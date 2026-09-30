using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// First in-game use of the ported orbital core: the predicted orbit of the camera around the
/// planet whose gravity dominates, drawn as a line with an element readout.
///
/// State vector: position relative to the planet centre, velocity from differentiating the
/// camera position (smoothed). Elements: SEAerospace.Orbital.OrbitalMath.ToElements. Path:
/// OrbitSampler.SamplePath. Drawn with a UI3D MeshBuilder, which the culling shader treats as a
/// Default (world-view) entity, so it is visible without debug draw and needs no reflection.
///
/// SE2 planet gravity is g0·(r0/d)^p with a hard cutoff; unless p == 2 the conic is only the
/// local Keplerian fit (see <see cref="GravityLaw.MuAt"/>), and the readout says so.
/// </summary>
public static class OrbitDisplay
{
    private const int PathPoints = 160;
    private const double MinSpeed = 0.5;      // m/s; below this there is no meaningful orbit
    private const double VelocitySmoothing = 0.05;   // (the fallback estimate: seated / HighSpeed)
    private const double DominanceMargin = 1.1;
    private const double NearCameraSkip = 1000.0; // m

    private static MeshBuilder _builder;
    /// <summary>Line width as a fraction of the distance to the camera (the thick-line width is in world metres).</summary>
    public static float LineThickness = 0.0025f;
    static readonly ColorSRGB You = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);

    /// <summary>
    /// A world line whose width is a constant angle from the camera: split where it passes near the
    /// camera, so each piece is as wide as ITS distance calls for (a segment kilometres long passing
    /// beside the camera, sized by its midpoint, was a wide ribbon across the view), and the piece at
    /// the camera itself is skipped.
    /// </summary>
    static void Line(Vector3D a, Vector3D b, Vector3D cam, ColorSRGB color, int depth = 0)
    {
        Vector3D ab = b - a;
        double len = ab.Length();
        if (len < 1e-6) return;
        double k = Math.Clamp(Vector3D.Dot(cam - a, ab) / (len * len), 0, 1);
        double near = (a + ab * k - cam).Length();                  // closest approach to the camera
        if (near < NearCameraSkip && len < NearCameraSkip * 2) return;
        if (len > near * 0.5 && depth < 12)
        {
            Vector3D m = a + ab * 0.5;
            Line(a, m, cam, color, depth + 1);
            Line(m, b, cam, color, depth + 1);
            return;
        }
        _builder.AddLine(a, b, color, (float)Math.Max(0.05, near * LineThickness), true);
    }
    private static bool IsFiniteV(Vector3D v) => !double.IsNaN(v.X + v.Y + v.Z) && !double.IsInfinity(v.X + v.Y + v.Z);
    private static object _dominant;
    private static double _dominantG;
    private static bool _hasPrev;
    private static Vector3D _prevCamera;
    private static long _prevTicks;
    private static Vector3D _velocity;
    private static bool _drewLastFrame;

    public static string LastReadout;

    /// <summary>Smoothed camera velocity (m/s), before any override. Updated while a planet dominates.</summary>
    public static Vector3D MeasuredVelocity => _measured;
    private static Vector3D _measured;

    /// <summary>DEV: when set, replaces the measured velocity (m/s, world axes). Harness `fakevel`.</summary>
    public static Vector3D? VelocityOverride;

    /// <summary>DEV: pin the state (planet-relative position, velocity, mu) so the conic can be viewed from elsewhere. Harness `fakevel freeze`.</summary>
    public static bool Freeze;
    private static StateVector _frozen;
    private static double _frozenMu;
    private static bool _hasFrozen;

    /// <summary>
    /// Called by every client planet each frame, under PlanetRenderBridge.Lock. The planet with the
    /// strongest gravity at the camera takes over (with a margin) and draws.
    /// </summary>
    public static void Consider(object owner, Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera,
                                Vector3D center, double radius, GravityLaw law, string name)
    {
        double d = (camera.Position - center).Length();
        double g = law.At(d);

        if (_hasFrozen)
        {
            // A frozen orbit stays with its planet wherever the camera goes.
            if (!ReferenceEquals(owner, _dominant)) return;
        }
        else if (_dominant == null || ReferenceEquals(owner, _dominant))
        {
            _dominant = g > 0 ? owner : null;
            _dominantG = g;
        }
        else if (g > _dominantG * DominanceMargin)
        {
            _dominant = owner;
            _dominantG = g;
            _hasPrev = false;
        }
        if (!ReferenceEquals(owner, _dominant))
        {
            // Nobody under gravity: clear once.
            if (_dominant == null && _drewLastFrame) Clear();
            return;
        }

        UpdateVelocity(camera.Position);
        _measured = _velocity;
        if (VelocityOverride.HasValue) _velocity = VelocityOverride.Value;
        if (!OrbitalConfig.ShowOrbit || (g <= 0 && !_hasFrozen)) { Clear(); return; }

        _builder ??= CreateBuilder(session);
        if (_builder == null) return;

        double mu = law.MuAt(d);
        if (Freeze && !_hasFrozen) { _frozen = new StateVector(camera.Position - center, _velocity); _frozenMu = mu; _hasFrozen = true; }
        if (!Freeze) _hasFrozen = false;
        // On foot: the character's own position and physics velocity (exact; the camera-differenced estimate
        // was noisy: a third-person camera moves on its own). Seated / HighSpeed: the smoothed estimate.
        var sv = _hasFrozen ? _frozen
               : FrameHost.FootState(out Vector3D fp, out Vector3D fv) ? new StateVector(fp - center, fv)
               : new StateVector(camera.Position - center, _velocity);
        if (_hasFrozen) mu = _frozenMu;
        string fit = law.IsInverseSquare ? "exact (1/r²)" : $"local fit (falloff {law.Falloff:F1})";
        double speed = sv.Velocity.Length();

        if (speed < MinSpeed || mu <= 0)
        {
            LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km, v {speed:F1} m/s — no orbit (at rest) [{fit}]";
            OrbitHud.Current = null;   // at rest: no orbit to show
            _builder.Commit();
            _drewLastFrame = true;
            return;
        }

        Chart chart = Chart.Of(name, SystemHost.Now);
        KeplerianElements el = OrbitalMath.ToElements(new StateVector(chart.ToInertial(sv.Position), chart.VelToInertial(sv.Position, sv.Velocity)), mu);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity))
        {
            Clear();
            return;
        }

        OrbitPath path = OrbitSampler.SamplePath(el, PathPoints, law.Reach);
        // For the orbit disc and the direction markers: the orbit about the body in world axes.
        Vector3D pI = chart.ToInertial(sv.Position), vI = chart.VelToInertial(sv.Position, sv.Velocity);
        Vector3D[] rel = null;
        if (path.Points != null) { rel = new Vector3D[path.Points.Length]; for (int i = 0; i < rel.Length; i++) rel[i] = chart.FromInertial(path.Points[i]); }
        Vector3D Dir(Vector3D v) => v.LengthSquared() > 0 ? Vector3D.Normalize(chart.FromInertial(v)) : Vector3D.Zero;
        // Drawn by the HUD (OrbitHud.DrawPath: a smooth screen curve, hidden behind the planet); the
        // orbit passes through the camera by construction, so the points near it are left out.
        var pts = path.Points;
        Vector3D[] world = null;
        if (pts != null && pts.Length > 1)
        {
            var list = new List<Vector3D>(pts.Length);
            foreach (var q in pts)
            {
                Vector3D w = center + chart.FromInertial(q);
                list.Add((w - camera.Position).LengthSquared() < NearCameraSkip * NearCameraSkip ? new Vector3D(double.NaN, 0, 0) : w);
            }
            world = list.ToArray();
        }

        double pe = el.PeriapsisRadius - radius;
        string ap = el.IsElliptic ? $"Ap {(el.ApoapsisRadius - radius) / 1000:F1} km  T {el.Period / 60:F1} min" : "escape";
        LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km  v {speed:F0} m/s\n" +
                      $"a {el.SemiMajorAxis / 1000:F1} km  e {el.Eccentricity:F3}  i {el.Inclination * 180 / Math.PI:F1}°\n" +
                      $"Pe {pe / 1000:F1} km{(pe < 0 ? " (IMPACT)" : "")}  {ap}\n{fit}";
        OrbitHud.Current = new OrbitHud.Readout
        {
            Body = name, Mode = SystemHost.Timescale > 1 ? $"Free flight   warp ×{SystemHost.Timescale:F0}" : "Free flight",
            Alt = d - radius, Speed = speed, Pe = pe, Ap = el.IsElliptic ? el.ApoapsisRadius - radius : 0,
            Period = el.IsElliptic ? el.Period : 0, IncDeg = el.Inclination * 180 / Math.PI, Escape = !el.IsElliptic,
            PeWorld = center + chart.FromInertial(OrbitSampler.PositionAtTrueAnomaly(el, 0)),
            ApWorld = el.IsElliptic ? center + chart.FromInertial(OrbitSampler.PositionAtTrueAnomaly(el, Math.PI)) : (Vector3D?)null,
            Path = world, PathClosed = path.IsClosed, BodyWorld = center, BodyRadius = radius,
            RelPath = rel, PlayerRel = chart.FromInertial(pI), Pro = Dir(vI), Nor = Dir(Vector3D.Cross(pI, vI)), Rad = Dir(pI),
            PeRel = chart.FromInertial(OrbitSampler.PositionAtTrueAnomaly(el, 0)),
            ApRel = el.IsElliptic ? chart.FromInertial(OrbitSampler.PositionAtTrueAnomaly(el, Math.PI)) : (Vector3D?)null,
        };
        PatchEvent(OrbitHud.Current, name, v => chart.FromInertial(v));
        RendezvousView.HudRelative(SystemHost.Now, OrbitHud.Current);   // a target: your motion about it
        _builder.Commit();
        _drewLastFrame = true;
    }

    /// <summary>
    /// Conjunction frame: draw the frame's RAILS orbit (its elements, not the camera's motion) around
    /// the parent body, mapped into the observer's window (the berth). Since the observer is the
    /// frame, the orbit passes through the camera; segments near it are skipped as usual.
    /// </summary>
    public static void DrawFrameOrbit(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera,
        SEAerospace.ObserverFrame obs, SEAerospace.Frames.ProximityFrame frame, SEAerospace.SystemDef.SystemRegistry reg, double t)
    {
        if (!OrbitalConfig.ShowOrbit) { Clear(); return; }
        var parent = reg.Find(frame.ParentBodyName);
        if (parent == null) return;
        _builder ??= CreateBuilder(session);
        if (_builder == null) return;

        var el = frame.Elements;
        var anchorEl = el;
        bool riding = false;
        var parentOrg = parent.OriginInRoot(t).Position;
        // Riding a frame anchored by something else (a station, an asteroid): your own orbit is the
        // frame's plus your offset and velocity in it (your jetpack changes it, not the frame's).
        if ((FrameHost.RiderFrame == frame.Id || FrameHost.DevRider) && !EncounterFrames.IsSite(frame))
        {
            var fc = OrbitPropagation.StateAt(el, t);
            var mine = new StateVector(fc.Position + SEAerospace.PlanetBerths.SpinToCelestial(obs, FrameHost.RiderOffset),
                                       fc.Velocity + SEAerospace.PlanetBerths.SpinToCelestial(obs, FrameHost.RiderVelocity));
            var own = CaptureMath.CaptureElements(mine, el.Mu, t);
            if (IsFinite(own.SemiMajorAxis)) { el = own; riding = true; }
            // A static anchor (a station, an asteroid) sits where it is in the frame, not at its centre:
            // the plot is about it, so its own orbit is the frame's plus its offset.
            if (ServerFrames.StaticAnchorOf(frame, out Vector3D anchorAt))
            {
                var ae = CaptureMath.CaptureElements(new StateVector(fc.Position + SEAerospace.PlanetBerths.SpinToCelestial(obs, anchorAt - frame.BerthCenter), fc.Velocity), anchorEl.Mu, t);
                if (IsFinite(ae.SemiMajorAxis)) anchorEl = ae;
            }
        }
        OrbitPath path = OrbitSampler.SamplePath(el, PathPoints, parent.SoiRadius);
        // The orbit itself is drawn by the HUD (OrbitHud.DrawPath: a smooth screen curve, behind the
        // planet hidden); here only its points in the world.
        var pts = path.Points;
        Vector3D[] world = null;
        if (pts != null && pts.Length > 1)
        {
            world = new Vector3D[pts.Length];
            for (int i = 0; i < pts.Length; i++) world[i] = SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + pts[i]);
        }
        var def = reg.FindDefinition(frame.ParentBodyName);
        double radius = def != null ? def.RadiusMeters : 0;
        StateVector cur = OrbitPropagation.StateAt(el, t);
        string ap = el.IsElliptic ? $"Ap {(el.ApoapsisRadius - radius) / 1000:F1} km  T {el.Period / 60:F1} min" : "escape";
        LastReadout = $"frame #{frame.Id} around {frame.ParentBodyName}: alt {(cur.Position.Length() - radius) / 1000:F1} km  v {cur.Velocity.Length():F0} m/s\n" +
                      $"a {el.SemiMajorAxis / 1000:F1} km  e {el.Eccentricity:F3}  i {el.Inclination * 180 / Math.PI:F1}°\n" +
                      $"Pe {(el.PeriapsisRadius - radius) / 1000:F1} km  {ap}\nrails (warp x{SystemHost.Timescale:F0})";
        OrbitHud.Current = new OrbitHud.Readout
        {
            Body = frame.ParentBodyName, Mode = null,   // (no 'On rails': the warp bar shows warp)
            Alt = cur.Position.Length() - radius, Speed = cur.Velocity.Length(),
            Pe = el.PeriapsisRadius - radius, Ap = el.IsElliptic ? el.ApoapsisRadius - radius : 0,
            Period = el.IsElliptic ? el.Period : 0, IncDeg = el.Inclination * 180 / Math.PI, Escape = !el.IsElliptic,
            PeWorld = SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + OrbitSampler.PositionAtTrueAnomaly(el, 0)),
            ApWorld = el.IsElliptic ? SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + OrbitSampler.PositionAtTrueAnomaly(el, Math.PI)) : (Vector3D?)null,
            Path = world, PathClosed = path.IsClosed,
            BodyWorld = SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg), BodyRadius = radius,
        };
        if (riding)
        {
            var rc = OrbitHud.Current;
            rc.RelCross = new List<double>(161);
            rc.Relative = Curvilinear(anchorEl, el, t, out rc.RelNow, cross: rc.RelCross);
            rc.RelNowCross = rc.RelCross.Count > 0 ? rc.RelCross[0] : 0;
            // Past the frame's boundary you leave it on your own orbit: the prediction stops there.
            rc.RelSamples = rc.Relative.Count;
            for (int i = 0; i < rc.Relative.Count; i++)
            {
                var q = rc.Relative[i];
                if (Math.Sqrt(q.along * q.along + q.radial * q.radial) > ServerFrames.CaptureRadius)
                {
                    rc.Relative.RemoveRange(i + 1, rc.Relative.Count - i - 1);
                    if (rc.RelCross.Count > i + 1) rc.RelCross.RemoveRange(i + 1, rc.RelCross.Count - i - 1);
                    rc.RelLeaves = true; break;
                }
            }
            Curvilinear(anchorEl, el, t + 1.0, out var ahead, samples: 0);
            rc.RelVel = (ahead.along - rc.RelNow.along, ahead.radial - rc.RelNow.radial);   // per second
            // Dampeners on: the jetpack keeps station with the anchor: you hold where you are.
            rc.Holding = FrameHost.Dampeners && !EncounterFrames.IsSite(frame);
            if (rc.Holding) { rc.Relative = new List<(double, double)> { rc.RelNow, rc.RelNow }; rc.RelCross = new List<double> { rc.RelNowCross, rc.RelNowCross }; rc.RelVel = (0, 0); }
            rc.RelPeriod = anchorEl.IsElliptic && IsFinite(anchorEl.Period) ? anchorEl.Period : 3600;
            rc.AnchorName = AnchorName(frame);
            // DEV check: where the plot said you would be 30 s later, against where you are.
            if (_predList != null && t - _predT >= 30 && _predPeriod > 0)
            {
                double fi = (t - _predT) / _predPeriod * _predN;
                if (fi > _predList.Count - 1) { _predList = null; goto predDone; }   // (past where it left the frame)
                int i0 = Math.Min(_predList.Count - 2, (int)fi); double w = fi - i0;
                double pa = _predList[i0].along * (1 - w) + _predList[i0 + 1].along * w, pr = _predList[i0].radial * (1 - w) + _predList[i0 + 1].radial * w;
                double err = Math.Sqrt((pa - rc.RelNow.along) * (pa - rc.RelNow.along) + (pr - rc.RelNow.radial) * (pr - rc.RelNow.radial));
                PredDiag = $"prediction after {t - _predT:F0} s: predicted ({pa:F0}, {pr:F0}) m, actual ({rc.RelNow.along:F0}, {rc.RelNow.radial:F0}) m, error {err:F0} m{(rc.Holding ? " (holding)" : "")}";
                _predList = null;
            }
            predDone:
            if (_predList == null && !rc.Holding && rc.Relative.Count > 1) { _predList = rc.Relative; _predT = t; _predPeriod = rc.RelPeriod; _predN = Math.Max(1, rc.RelSamples - 1); }
        }
        {
            // For the orbit disc and the direction markers: the orbit about the body in world axes.
            var rd = OrbitHud.Current;
            Vector3D M(Vector3D x) => SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + x) - rd.BodyWorld;
            Vector3D here = M(cur.Position);
            Vector3D D(Vector3D v) => v.LengthSquared() > 0 ? Vector3D.Normalize(M(cur.Position + Vector3D.Normalize(v) * 1000) - here) : Vector3D.Zero;
            if (pts != null) { rd.RelPath = new Vector3D[pts.Length]; for (int i = 0; i < pts.Length; i++) rd.RelPath[i] = M(pts[i]); }
            rd.PlayerRel = here; rd.Pro = D(cur.Velocity); rd.Nor = D(Vector3D.Cross(cur.Position, cur.Velocity)); rd.Rad = D(cur.Position);
            rd.PeRel = M(OrbitSampler.PositionAtTrueAnomaly(el, 0));
            rd.ApRel = el.IsElliptic ? M(OrbitSampler.PositionAtTrueAnomaly(el, Math.PI)) : (Vector3D?)null;
            PatchEvent(rd, frame.ParentBodyName, M);
        }
        RendezvousView.HudRelative(SystemHost.Now, OrbitHud.Current);   // a target you chose comes before the frame you ride
        _builder.Commit();
        _drewLastFrame = true;
    }

    /// <summary>
    /// Your motion about the frame's anchor in its curvilinear local frame, for the next revolution of
    /// the anchor: along-track as arc length on the anchor's orbit (+ ahead), radial as the height
    /// difference (+ up). Exact: both orbits propagated, not a linearisation.
    /// </summary>
    /// <summary>
    /// The disc's escape / entry: the next change of body on your patched path (the planner's trajectory),
    /// where it happens (offset from the body, world axes via toRel), and for an entry the moon then.
    /// </summary>
    static void PatchEvent(OrbitHud.Readout r, string body, Func<Vector3D, Vector3D> toRel)
    {
        var reg = SystemHost.Registry;
        var b = reg?.Find(body);
        r.SoiRadius = b != null && !b.IsRoot && IsFinite(b.SoiRadius) ? b.SoiRadius : 0;
        // The rendezvous with your target, when it is about this same body (the disc's).
        try
        {
            if (Maneuvers.Rendezvous(SystemHost.Now, out var rb, out double rt, out double rd, out var ry, out var rtg) && rb == b
                && IsFiniteV(ry) && IsFiniteV(rtg))
            {
                r.RvRel = toRel(ry); r.RvTargetRel = toRel(rtg);
                r.RvIn = Math.Max(0, rt - SystemHost.Now); r.RvDist = rd;
                r.RvLabel = $"{Maneuvers.Target} Rendezvous";
            }
            // The ring rocks met on the path about this body.
            if (Maneuvers.Trajectory(SystemHost.Now, out var rl, out _))
                foreach (var p in RingRocks.Passes(rl, SystemHost.Now))
                {
                    if (p.Leg.Body != b) continue;
                    var at = OrbitPropagation.StateAt(p.Leg.El, p.T).Position;
                    if (!IsFiniteV(at)) continue;
                    (r.Rocks ??= new List<(Vector3D, string, double, double)>()).Add((toRel(at), p.Label, p.D, Math.Max(0, p.T - SystemHost.Now)));
                }
        }
        catch { }
        try
        {
            double t = SystemHost.Now;
            if (!Maneuvers.Trajectory(t, out var legs, out _) || legs.Count < 2 || legs[0].Body?.Name != body) return;
            for (int i = 1; i < legs.Count; i++)
            {
                if (legs[i].Body == legs[0].Body) continue;
                double T = legs[i].T0;
                var at = OrbitPropagation.StateAt(legs[0].El, T).Position;
                if (!IsFiniteV(at)) return;
                r.EventRel = toRel(at);
                r.EventIn = Math.Max(0, T - t);
                var nb = legs[i].Body;
                if (nb == legs[0].Body.Parent) r.EventLabel = "Escape";
                else
                {
                    r.EventLabel = $"{SystemHost.DisplayName(nb.Name)} Entry";
                    if (nb.Parent == legs[0].Body) { r.MoonRel = toRel(nb.StateInParentAt(T).Position); r.MoonSoi = nb.SoiRadius; }
                }
                return;
            }
        }
        catch { }
    }

    /// <summary>DEV: the rendezvous prediction check.</summary>
    public static string PredDiag = "-";
    private static List<(double along, double radial)> _predList;
    private static double _predT, _predPeriod;
    private static int _predN = 160;

    /// <summary>What anchors a frame, for the plot's title.</summary>
    static string AnchorName(SEAerospace.Frames.ProximityFrame f)
    {
        if (f.AnchorEntityId == ServerFrames.AsteroidAnchorId) return "Asteroid";
        var g = GridMembers.IsGridId(f.AnchorEntityId) ? GridMembers.Get(f.AnchorEntityId) : null;
        return g?.DisplayName ?? "Anchor";
    }

    static List<(double along, double radial)> Curvilinear(KeplerianElements anchor, KeplerianElements you, double t, out (double along, double radial) now, int samples = 160, List<double> cross = null)
    {
        (double, double) At(double tk) => At3(tk, out _);
        (double, double) At3(double tk, out double oop)
        {
            oop = 0;
            var a = OrbitPropagation.StateAt(anchor, tk); var b = OrbitPropagation.StateAt(you, tk);
            Vector3D h = Vector3D.Cross(a.Position, a.Velocity);
            if (h.LengthSquared() < 1e-12) return (0, 0);
            h = Vector3D.Normalize(h);
            double r0 = a.Position.Length();
            oop = Vector3D.Dot(b.Position, h);   // out of the anchor's plane (+ along its orbit normal)
            Vector3D bp = b.Position - h * oop;
            double th = Math.Atan2(Vector3D.Dot(h, Vector3D.Cross(a.Position, bp)), Vector3D.Dot(a.Position, bp));
            return (th * r0, b.Position.Length() - r0);
        }
        now = At(t);
        var list = new List<(double, double)>(samples + 1);
        double P = anchor.IsElliptic && IsFinite(anchor.Period) ? anchor.Period : 3600;
        for (int k = 0; k <= samples && samples > 0; k++) { list.Add(At3(t + P * k / samples, out double o)); cross?.Add(o); }
        return list;
    }

    private static void UpdateVelocity(Vector3D cam)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (now - _prevTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_hasPrev && dt > 1e-4 && dt < 0.5)
        {
            Vector3D inst = (cam - _prevCamera) * (1.0 / dt);
            // A teleport shows up as an absurd jump; drop it instead of smearing it in.
            if (inst.Length() < 20000) _velocity = _velocity + (inst - _velocity) * VelocitySmoothing;
            else _velocity = Vector3D.Zero;
        }
        else if (!_hasPrev)
        {
            _velocity = Vector3D.Zero;
        }
        _prevCamera = cam;
        _prevTicks = now;
        _hasPrev = true;
    }

    private static void DrawText(WorldTransform camera, string text)
    {
        Vector3D fwd = (Vector3D)Vector3.Transform(Vector3.Forward, camera.Orientation);
        Vector3D up = (Vector3D)Vector3.Transform(Vector3.Up, camera.Orientation);
        Vector3D left = (Vector3D)Vector3.Transform(-Vector3.Right, camera.Orientation);
        _builder.AddText(camera.Position + fwd * 10.0 + up * 3.0 + left * 4.0, text, ColorSRGB.Yellow, 0.7f);
    }

    internal static void Clear()
    {
        if (_builder != null && _drewLastFrame) _builder.Commit();
        _drewLastFrame = false;
        LastReadout = null;
        OrbitHud.Current = null;
    }

    private static MeshBuilder CreateBuilder(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            return session.Get<IDebugDraw>().MeshBuilderFactory.CreateMeshBuilder(MeshBuilder.Type.UI3D, 0L, 512, "OrbitalModOrbit");
        }
        catch (Exception e)
        {
            Log.Default?.Warning($"[ORBIT] orbit display unavailable: {e.Message}");
            return null;
        }
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
