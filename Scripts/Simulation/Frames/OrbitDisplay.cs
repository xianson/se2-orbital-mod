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
    private const double VelocitySmoothing = 0.15;
    private const double DominanceMargin = 1.1;
    private const double NearCameraSkip = 1000.0; // m

    private static MeshBuilder _builder;
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
        var sv = _hasFrozen ? _frozen : new StateVector(camera.Position - center, _velocity);
        if (_hasFrozen) mu = _frozenMu;
        string fit = law.IsInverseSquare ? "exact (1/r²)" : $"local fit (falloff {law.Falloff:F1})";
        double speed = sv.Velocity.Length();

        if (speed < MinSpeed || mu <= 0)
        {
            LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km, v {speed:F1} m/s — no orbit (at rest) [{fit}]";
            DrawText(camera, LastReadout);
            _builder.Commit();
            _drewLastFrame = true;
            return;
        }

        KeplerianElements el = OrbitalMath.ToElements(sv, mu);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity))
        {
            Clear();
            return;
        }

        OrbitPath path = OrbitSampler.SamplePath(el, PathPoints, law.Reach);
        var color = el.IsElliptic ? ColorSRGB.Yellow : ColorSRGB.Red;
        var pts = path.Points;
        if (pts != null && pts.Length > 1)
        {
            // The orbit passes through the camera by construction. Thin (default) lines only, and
            // skip segments touching the camera's neighbourhood: a world-width line at the eye
            // fills the screen (seen in game with thickness 3).
            int n = pts.Length;
            int segments = path.IsClosed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3D p0 = center + pts[i];
                Vector3D p1 = center + pts[(i + 1) % n];
                if ((p0 - camera.Position).LengthSquared() < NearCameraSkip * NearCameraSkip ||
                    (p1 - camera.Position).LengthSquared() < NearCameraSkip * NearCameraSkip) continue;
                _builder.AddLine(p0, p1, color);
            }
        }

        double pe = el.PeriapsisRadius - radius;
        string ap = el.IsElliptic ? $"Ap {(el.ApoapsisRadius - radius) / 1000:F1} km  T {el.Period / 60:F1} min" : "escape";
        LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km  v {speed:F0} m/s\n" +
                      $"a {el.SemiMajorAxis / 1000:F1} km  e {el.Eccentricity:F3}  i {el.Inclination * 180 / Math.PI:F1}°\n" +
                      $"Pe {pe / 1000:F1} km{(pe < 0 ? " (IMPACT)" : "")}  {ap}\n{fit}";
        DrawText(camera, LastReadout);
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
        var parentOrg = parent.OriginInRoot(t).Position;
        OrbitPath path = OrbitSampler.SamplePath(el, PathPoints, parent.SoiRadius);
        var color = el.IsElliptic ? ColorSRGB.Yellow : ColorSRGB.Red;
        var pts = path.Points;
        if (pts != null && pts.Length > 1)
        {
            int n = pts.Length;
            int segments = path.IsClosed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3D p0 = SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + pts[i]);
                Vector3D p1 = SEAerospace.PlanetBerths.WorldFromCelestial(obs, parentOrg + pts[(i + 1) % n]);
                if ((p0 - camera.Position).LengthSquared() < NearCameraSkip * NearCameraSkip ||
                    (p1 - camera.Position).LengthSquared() < NearCameraSkip * NearCameraSkip) continue;
                _builder.AddLine(p0, p1, color);
            }
        }

        var def = reg.FindDefinition(frame.ParentBodyName);
        double radius = def != null ? def.RadiusMeters : 0;
        StateVector cur = OrbitPropagation.StateAt(el, t);
        string ap = el.IsElliptic ? $"Ap {(el.ApoapsisRadius - radius) / 1000:F1} km  T {el.Period / 60:F1} min" : "escape";
        LastReadout = $"frame #{frame.Id} around {frame.ParentBodyName}: alt {(cur.Position.Length() - radius) / 1000:F1} km  v {cur.Velocity.Length():F0} m/s\n" +
                      $"a {el.SemiMajorAxis / 1000:F1} km  e {el.Eccentricity:F3}  i {el.Inclination * 180 / Math.PI:F1}°\n" +
                      $"Pe {(el.PeriapsisRadius - radius) / 1000:F1} km  {ap}\nrails (warp x{SystemHost.Timescale:F0})";
        DrawText(camera, LastReadout);
        _builder.Commit();
        _drewLastFrame = true;
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

    private static void Clear()
    {
        if (_builder != null && _drewLastFrame) _builder.Commit();
        _drewLastFrame = false;
        LastReadout = null;
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
