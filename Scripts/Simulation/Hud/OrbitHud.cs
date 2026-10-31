using Keen.VRage.Core;
using SEAerospace.Orbital;
using static OrbitalMod.Num;
using HudText = SEAerospace.MapUi.HudText;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// The in-flight orbit display: what OrbitDisplay computes, as the orbit disc beside the game's SPD box and, with a target or
/// a ridden anchor, the relative plot beside it. Their words sit in fixed slots off the drawings (SEAerospace.MapUi.HudText).
/// </summary>
public static class OrbitHud
{
    static readonly List<Vector2> _orbitPts = new List<Vector2>(161), _relPts = new List<Vector2>(161);
    public sealed class Readout
    {
        public string Body, Mode;
        public double Alt, Speed, Pe, Ap, Period, IncDeg;
        /// <summary>Seconds to the next periapsis / apoapsis (NaN: none ahead, e.g. past Pe on an escape).</summary>
        public double PeIn = double.NaN, ApIn = double.NaN;
        public bool Escape;
        public Vector3D? PeWorld, ApWorld;
        /// <summary>The orbit in the world (a closed loop when elliptic), and the body it is about (world centre, radius).</summary>
        public Vector3D[] Path;
        public bool PathClosed;
        public Vector3D BodyWorld;
        public double BodyRadius;
        /// <summary>The orbit about the body in world axes (offsets from its centre): path, you, apsides.</summary>
        public Vector3D[] RelPath;
        public Vector3D PlayerRel, PeRel;
        public Vector3D? ApRel;
        /// <summary>Unit directions (world): prograde, orbit normal, radial out.</summary>
        public Vector3D Pro, Nor, Rad;
        /// <summary>Riding another's frame: your motion about its anchor (curvilinear: along-track, radial; m) for its next revolution, and now.</summary>
        public List<(double along, double radial)> Relative;
        public (double along, double radial) RelNow, RelVel;
        public double RelPeriod;
        public string AnchorName;
        /// <summary>The body's sphere of influence (m), and the next patch event on your path: where (world
        /// offset from the body), what ("Escape", "Palatine Entry"), in how long; for an entry the moon then
        /// (world offset) and its sphere.</summary>
        public double SoiRadius;
        public Vector3D? EventRel, MoonRel;
        public string EventLabel;
        public double EventIn, MoonSoi;
        public bool Holding;   // dampeners on: station-keeping with the anchor
        public int RelSamples;   // the prediction's samples over a full revolution (it may be cut short)
        public List<double> RelCross;   // out of the anchor's plane along the prediction (m, + along its normal)
        public double RelNowCross;
        public bool RelLeaves;   // the prediction leaves the frame (cut at its boundary)
        public bool RelIsTarget; // the plot is about your target (RendezvousView.HudRelative), not the anchor you ride
        public double RelBoundary = ServerFrames.CaptureRadius;   // the ring: where you leave the frame (riding) / merge with the target
        /// <summary>The rendezvous with your target on your path (offsets from the body): you then, the target then.</summary>
        public Vector3D? RvRel, RvTargetRel;
        public string RvLabel;
        public double RvIn, RvDist;
        /// <summary>Ring rocks met on your path (offsets from the body): where you are then, name, how close, how soon.</summary>
        public List<(Vector3D rel, string label, double dist, double inT)> Rocks;
    }

    public static Readout Current;
    /// <summary>The plot drawn last (harness: hudcheck) and when (wall seconds).</summary>
    public static SEAerospace.Frames.RendezvousPlot.Plot LastPlot;
    public static double LastPlotWall;

    /// <summary>Under this speed (m/s) and low (under the atmosphere's top) you are walking or flying about, not orbiting.</summary>
    public const double WalkSpeed = 200;
    /// <summary>
    /// Walking about (slow and low): no orbit stats, line or apsides anywhere (map included). On an
    /// ascent you pass 200 m/s quickly and your Ap is back.
    /// </summary>
    public static bool Walking
    {
        get
        {
            var r = Current;
            return r != null && r.Speed < WalkSpeed && r.Alt < Math.Max(2000.0, r.BodyRadius * SystemHost.AtmosphereFraction);
        }
    }
    static readonly ColorSRGB Orbit = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);   // your orbit: cyan, as on the map

    private static GameUi.Card _card;

    /// <summary>The words with no plot drawn (a burn with no orbit shown): the disc's block alone, where the disc would be.</summary>
    static void DrawInfo(float u)
    {
        _infoShown = false;
        if (!_hasText || _discLines.Count == 0) return;
        // (no plot - a burn with no orbit shown: the disc's block alone, centred where the disc would be; the same words and sizes,
        //  no panel - 2026-10-31 HUD text)
        DiscPlace(out var c, out _);
        float H = HudText.Height(_discLines, LineH(true, u), LineH(false, u));
        DrawBlock(_discLines, c.X, c.Y - H * 0.5f, true, u);
        _infoShown = true;
    }
    static bool _hasText;
    static bool _infoShown;
    /// <summary>The orbit readout is on screen (harness: hudcheck), and why not when it is not.</summary>
    public static bool CardOpen => _infoShown;
    public static string CardWhy = "-";
    private static double _nextUpdate;

    /// <summary>The orbit disc and the relative plot, their words remade twice a second (BuildText).</summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        GameNotes.Tick(session);   // (the game's HUD notifications: where they are, hidden over the map - audit C1)
        var r = Walking ? null : Current;   // walking about: no orbit stats
        double now = Wall();
        string burn = Maneuvers.BurnLine;
        // Not over the map: the card would sit on the terminal's close button, and the map's own title
        // says the same (orbit and next burn).
        // (nor over another screen: the terminal's other tabs, a dialog - it sat on the terminal's close button)
        bool covered = !MapView.Visible && GameUi.TopScreenNeedingInput(session) != null;
        // (in a planet's frame, an apoapsis inside its air - flying about, a hop, coming down: no orbit display at all)
        EntryHidden = LowArc(r);
        bool show = OrbitalConfig.ShowOrbit && ((r != null && !EntryHidden) || burn != null) && !MapView.Visible && !covered;
        if (_card != null) { GameUi.CloseCard(_card); _card = null; }   // (the old card: never shown again)
        if (!show)
        {
            CardWhy = !OrbitalConfig.ShowOrbit ? "orbit display off" : MapView.Visible ? "map open" : covered ? "a screen is open" : Walking ? "walking" : EntryHidden ? "apoapsis inside the air" : "no orbit";
            _hasText = false; _infoShown = false; TextDump = "-";
            return;
        }
        if (now >= _nextUpdate)
        {
            _nextUpdate = now + 0.5;
            BuildText(r, burn);   // (the words, made here twice a second - drawn every frame they were new strings every frame)
            CardWhy = "shown beside the plot";
        }
        if (r == null || MapView.Visible)
        {
            if (_hasText && FrameMarkers.BeginHud(session)) { try { DrawInfo(MapPipeline.UiScale(MapPipeline.ScreenSize.Y)); } finally { MapPipeline.UiEnd(); } }
            return;
        }
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            // (no side block in flight any more - 2026-10-13, the user, on it showing up in place of the plot: "that kind of gui
            //  is unacceptable": on foot with the jetpack off, or with the seat or the jetpack not detected, it took the plot's
            //  place. The plot, with its captions, is the orbit display everywhere in flight.)
            _infoShown = true;
            HudPanel.BeginLabels();
            HudPanel.ReserveGameHud();
            float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
            // No orbit line in the world (first or third person): the orbit disc shows it,
            // and the markers show its directions.
            // (seated, jetpacking or floating in your suit: the same display - Walking, slow and low, has no orbit at all)
            {
                DrawMarkers(r, u);
                // your motion about your target, or about the anchor you ride away from; else the disc (RendezvousPlot.Choose)
                var plot = SEAerospace.Frames.RendezvousPlot.Choose(new SEAerospace.Frames.RendezvousPlot.Who
                {
                    Flying = true, HasTarget = r.Relative != null && r.RelIsTarget, TargetSharesBody = true,
                    Riding = r.Relative != null && !r.RelIsTarget, AtAnchor = FrameHost.AtAnchor,
                });
                if (!EntryHidden)
                {
                    LastPlot = plot; LastPlotWall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                    // BOTH PLOTS (2026-10-18, UI audit C4: the relative plot replaced your own orbit on the card): your orbit's
                    // disc where it always is, the relative plot beside it on the left - or, with no room there (a narrow
                    // screen), in its place as before
                    bool relPlot = plot == SEAerospace.Frames.RendezvousPlot.Plot.Target || plot == SEAerospace.Frames.RendezvousPlot.Plot.Anchor;
                    _relBeside = relPlot && RelRoom(u);
                    if (!relPlot) { _oopReach = double.NaN; DrawDisc(r, u); }
                    else if (_relBeside && DrawDisc(r, u)) DrawRelative(r, u, true);
                    else { _relBeside = false; DrawRelative(r, u, false); }
                }
            }
        }
        finally { MapPipeline.UiEnd(); }
    }

    // ── THE HUD'S WORDS (2026-10-31, the user's approved text cleanup: SEAerospace.MapUi.HudText says what and where) ──
    // No text on the drawings; three slots: one block centred under the disc (the orbit, or the burn), the relative plot's
    // title on its top edge (the target's name left, its distance right), one block left-aligned under the plot (closing /
    // opening / holding, then the closest approach). One time format, two sizes, a soft halo in place of the per-line plates.
    static readonly List<HudText.Line> _discLines = new List<HudText.Line>(8), _plotLines = new List<HudText.Line>(6);
    static readonly List<string> _extra = new List<string>(6);
    static string _plotName, _plotDist; static int _plotNameRole;
    /// <summary>The words on screen now, one " | " between lines (harness: hudcheck).</summary>
    public static string TextDump = "-";
    /// <summary>The out-of-plane strip's reach (m) when the plot drew it last frame, else NaN (its words go in the plot's block).</summary>
    static double _oopReach = double.NaN;

    static void BuildText(Readout r, string burn)
    {
        double t = SystemHost.Now;
        string orbitBig = null, orbitSmall = null;
        if (r != null)
        {
            double rp = r.Pe + r.BodyRadius, ra = r.Ap + r.BodyRadius;
            double ecc = r.Escape ? 1 : ra + rp > 0 ? Math.Abs(ra - rp) / (ra + rp) : 0;
            HudText.Orbit(r.Pe, r.Ap, r.PeIn, r.ApIn, r.Period, ecc, r.IncDeg, r.Escape, out orbitBig, out orbitSmall);
        }
        // the burn: what is left, when (Maneuvers.BurnTag: "in 2m 43s", "now", "late 40s", "stopped: no thrust"), how long at full thrust
        string burnText = burn != null ? HudText.Burn(Maneuvers.BurnLeft, Maneuvers.BurnTag, Maneuvers.BurnSeconds) : null;
        // (the hold works seated only - AttitudeHold lets go on foot, "left the seat": the harness's "hold prograde" on foot
        //  answered "holding Prograde" and was let go the next tick, so no line - P3)
        string hold = AttitudeHold.Current != AttitudeHold.Mode.Off ? "hold " + AttitudeHold.Label(AttitudeHold.Current).ToLowerInvariant() : null;
        _extra.Clear();
        // what was written on the disc (the next patch event, the next ring rock) is a line here now
        if (r != null && r.EventRel.HasValue && r.EventLabel != null) _extra.Add($"{r.EventLabel} {HudText.In(r.EventIn) ?? ""}".TrimEnd());
        if (r?.Rocks != null && r.Rocks.Count > 0) { var k = r.Rocks[0]; _extra.Add($"{k.label} {HudText.Km(k.dist)} {HudText.In(k.inT) ?? ""}".TrimEnd()); }
        // re-entry: fx-flight's card tag, else, skimming above the air, how low it must go (UI audit U3)
        string et = EntryHost.CardTag(t);
        if (et != null) _extra.Add(et);
        else if (EntryHost.AirGapLine(t) is string gap) _extra.Add(gap);
        if (Contacts.LoudLine is string loud) _extra.Add(loud);   // (you are heard: a radar of yours transmits - fx-detect)
        HudText.DiscBlock(orbitBig, orbitSmall, burnText, hold, _extra, _discLines);
        BuildTarget(r, t);
        _hasText = true;
        var sb = new System.Text.StringBuilder();
        foreach (var l in _discLines) sb.Append(l.Text).Append(" | ");
        if (_plotName != null) sb.Append("[plot] ").Append(_plotName).Append(" / ").Append(_plotDist ?? "-").Append(" | ");
        foreach (var l in _plotLines) sb.Append(l.Text).Append(" | ");
        TextDump = sb.Length > 0 ? sb.ToString(0, sb.Length - 3) : "-";
    }

    /// <summary>
    /// The target's (or the ridden anchor's) words: the plot's title (name, distance) and its block (how the range changes, the
    /// closest approach - ONE value, Maneuvers' closest approach on your path, the one the disc's magenta ring marks: the plot
    /// worked its own out of its sampled revolution and the two disagreed, P6). A target with no relative plot (a planet or
    /// moon): its lines join the disc's block.
    /// </summary>
    static void BuildTarget(Readout r, double t)
    {
        _plotName = _plotDist = null; _plotLines.Clear(); _plotNameRole = HudText.White;
        bool plot = r?.Relative != null && r.Relative.Count > 0;
        string key = Maneuvers.Target;
        string name = null, dist = null, rate = null, closest = null;
        if (plot && !r.RelIsTarget)
        {
            // riding an anchor's frame: the plot's own numbers (there is no target to ask Maneuvers about)
            name = r.AnchorName;
            double rng = Math.Sqrt(r.RelNow.along * r.RelNow.along + r.RelNow.radial * r.RelNow.radial + r.RelNowCross * r.RelNowCross);
            double rng2 = Math.Sqrt(r.RelNow.along * r.RelNow.along + r.RelNow.radial * r.RelNow.radial);
            double rdot = rng2 > 1 ? (r.RelNow.along * r.RelVel.along + r.RelNow.radial * r.RelVel.radial) / rng2 : 0;
            dist = HudText.Km(rng);
            rate = r.Holding ? "holding station" : HudText.Rate(rdot);
            if (!r.Holding && r.Relative.Count > 2)
            {
                int ci = 0; double cmin = double.MaxValue;
                for (int i = 0; i < r.Relative.Count; i++) { var q = r.Relative[i]; double dd = q.along * q.along + q.radial * q.radial; if (dd < cmin) { cmin = dd; ci = i; } }
                closest = ci > 0 ? HudText.Closest(true, r.RelPeriod * ci / Math.Max(1, r.RelSamples - 1), Math.Sqrt(cmin), rng2, false, false) : null;
            }
        }
        else if (key != null)
        {
            name = Targets.Name(key);
            var ap = Maneuvers.TargetApproach(t);
            if (ap.Tracking) rate = "tracking " + ap.TrackingText;
            else if (ap.Here) rate = "you are there";
            else
            {
                TargetRange(t, out double rng, out double rdot);
                if (!double.IsNaN(rng)) dist = HudText.Km(rng);
                rate = HudText.Rate(rdot);
                closest = HudText.Closest(ap.Ok, ap.At - t, ap.Body ? ap.Alt : ap.Dist, rng, ap.Body, ap.Impact);
                if (closest != null && ap.Estimate) closest += " · estimate";
            }
            _plotNameRole = HudText.TargetRole;
        }
        if (name == null) return;
        if (name.Length > 18) name = name.Substring(0, 17) + "…";
        if (plot)
        {
            _plotName = name; _plotDist = dist;
            if (rate != null) _plotLines.Add(new HudText.Line(rate, false, HudText.White));
            if (closest != null) _plotLines.Add(new HudText.Line(closest, false, HudText.Dim));
            if (r.RelLeaves && r.Relative.Count > 1)
                _plotLines.Add(new HudText.Line("leaves frame " + (HudText.In(r.RelPeriod * (r.Relative.Count - 1) / Math.Max(1, r.RelSamples - 1)) ?? ""), false, HudText.Dim));
            if (!double.IsNaN(_oopReach)) _plotLines.Add(new HudText.Line("out of plane ±" + HudText.Km(_oopReach), false, HudText.Dim));
        }
        else
        {
            // no plot (a body target): the target's lines under the orbit's, in the disc's block
            _discLines.Add(new HudText.Line(dist != null ? $"{name}  {dist}" : name, false, HudText.TargetRole));
            string s = rate != null && closest != null ? rate + " · " + closest : rate ?? closest;
            if (s != null) _discLines.Add(new HudText.Line(s, false, HudText.Dim));
        }
    }

    static readonly ColorSRGB BurnCol = new ColorSRGB(1f, 0.64f, 0.24f, 1f), TgtCol = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);   // (the plan's orange, the map's target magenta)
    static ColorSRGB RoleColor(int role) => role == HudText.BurnRole ? BurnCol : role == HudText.TargetRole ? TgtCol : role == HudText.Dim ? CapDim : CapWhite;
    /// <summary>A line's height (px): a big line's or a small one's, with its leading.</summary>
    static float LineH(bool big, float u) => MapPipeline.MeasureText("0", (big ? HudText.Primary : HudText.Secondary) * u).Y * 1.12f;
    /// <summary>HUD text: a soft dark halo round it, no plate (MapPipeline.ScreenTextSoft).</summary>
    static void T(Vector2 at, string text, ColorSRGB col, float scale, float u) => MapPipeline.ScreenTextSoft(at, text, col, scale, 1.4f * u);
    static float BlockWidth(List<HudText.Line> lines, float u)
    {
        float w = 0;
        foreach (var l in lines) w = Math.Max(w, MapPipeline.MeasureText(l.Text, (l.Big ? HudText.Primary : HudText.Secondary) * u).X);
        return w;
    }
    /// <summary>A block of lines from its top (y): centred on x, or left-aligned at x. Returns the y under it.</summary>
    static float DrawBlock(List<HudText.Line> lines, float x, float y, bool centred, float u)
    {
        foreach (var l in lines)
        {
            float s = (l.Big ? HudText.Primary : HudText.Secondary) * u;
            float px = centred ? x - MapPipeline.MeasureText(l.Text, s).X * 0.5f : x;
            T(new Vector2(px, y), l.Text, RoleColor(l.Role), s, u);
            y += LineH(l.Big, u);
        }
        return y;
    }

    /// <summary>How far your target is now (m; a body's: to its surface) and how fast that changes (m/s, + receding): your
    /// orbit now and the target about the same body. NaN when either is unknown.</summary>
    static void TargetRange(double t, out double range, out double rate)
    {
        range = rate = double.NaN;
        string key = Maneuvers.Target;
        if (key == null || !Maneuvers.Base(t, out var body, out var el) || !Targets.About(key, body, t, out var rel)) return;
        var me = OrbitPropagation.StateAt(el, t);
        Vector3D d = rel.Position - me.Position, dv = rel.Velocity - me.Velocity;
        double l = d.Length();
        if (!IsFinite(l)) return;
        double rad = Targets.Radius(key);
        range = rad > 0 ? Math.Max(0, l - rad) : l;
        rate = l > 1e-6 ? Vector3D.Dot(d, dv) / l : 0;
    }

    /// <summary>The orbit display is hidden: in a planet's frame with the apoapsis inside its air (LowArc).</summary>
    public static bool EntryHidden;
    static bool _lowArc;
    /// <summary>
    /// The orbit display only when the orbit reaches out of the air (the user, 2026-10-08): in a planet's own frame it shows
    /// once the apoapsis is above the frame's border - the top of the air (an airless body: its shell) - by ArcShowMargin,
    /// and hides again only when the apoapsis falls back under the border (hysteresis: it flickered at the edge). An
    /// escape path shows; in an orbital frame it always shows.
    /// </summary>
    static bool LowArc(Readout r)
    {
        if (r == null || FrameHost.PlayerFrame != null || FrameHost.ObserverPlanet == null) return _lowArc = false;
        if (r.Escape) return _lowArc = false;
        var def = SystemHost.Registry?.FindDefinition(FrameHost.ObserverPlanet);
        if (def == null || !(def.RadiusMeters > 0)) return _lowArc = false;
        double border = SEAerospace.PlanetBerths.ShellRadius(def) - def.RadiusMeters;   // (altitude of the frame's edge)
        if (!(border > 0)) return _lowArc = false;
        double showAbove = border + Math.Max(ArcShowMarginMin, ArcShowMargin * border);
        if (_lowArc) { if (r.Ap > showAbove) _lowArc = false; }
        else if (r.Ap < border) _lowArc = true;
        return _lowArc;
    }
    /// <summary>The apoapsis must clear the border by this fraction of it (or ArcShowMarginMin m) to show again.</summary>
    public const double ArcShowMargin = 0.2, ArcShowMarginMin = 2000;

    static readonly ColorSRGB ProCol = new ColorSRGB(0.95f, 0.85f, 0.25f, 1f), NorCol = new ColorSRGB(0.82f, 0.45f, 1f, 1f),
                              RadCol = new ColorSRGB(0.35f, 0.85f, 1f, 1f);

    /// <summary>
    /// The orbit's directions on screen (a navball's markers): prograde / retrograde, normal /
    /// anti-normal, radial out / in, each where it points; full near where you look, faded away from it.
    /// </summary>
    static void DrawMarkers(Readout r, float u)
    {
        var cp = MapPipeline.CameraPosition;
        if (!cp.HasValue) return;
        Vector3D fwd = (QuaternionD)MapPipeline.CameraOrientation * Vector3D.Forward;
        void M(Vector3D dir, string icon, ColorSRGB c)
        {
            if (dir.LengthSquared() < 0.5) return;
            double ang = Math.Acos(Math.Clamp(Vector3D.Dot(dir, fwd), -1, 1)) * 180 / Math.PI;
            if (ang > 80) return;   // (behind or far off: the HUD edge would pile them up)
            if (!MapPipeline.HudPoint(cp.Value + dir * 1e4, out var s, out bool edge) || edge) return;
            float a = (float)Math.Clamp(1.0 - (ang - 12.0) / 40.0, 0.25, 1.0);
            var col = HudPanel.Alpha(c, a);
            if (!MapPipeline.ScreenIcon(icon, s, 11f * u, col)) MapPipeline.ScreenCircle(s, 8f * u, col, 2f * u);
        }
        M(r.Pro, "prograde", ProCol); M(-r.Pro, "retrograde", ProCol);
        M(r.Nor, "normal", NorCol); M(-r.Nor, "antinormal", NorCol);
        M(r.Rad, "radialout", RadCol); M(-r.Rad, "radialin", RadCol);
    }

    private static Vector3D _discUp;

    /// <summary>
    /// A plot's scale that holds still (the user, 2026-10-10: "very twitchy from constantly resizing the axis scale to fit"):
    /// the shown extent sits on clean steps (1, 2, 5 x 10^n), GROWS at once when the content reaches GrowAt of it, SHRINKS
    /// only after the content has stayed under ShrinkAt of it for ShrinkHold s, and any change is eased (Tau, in log
    /// space - a zoom, not a slide). It was the content's exact extent every frame.
    /// </summary>
    internal sealed class ScaleLatch
    {
        public const double GrowAt = 0.92, ShrinkAt = 0.38, ShrinkHold = 1.5, Tau = 0.25, Margin = 1.12;
        double _shown = -1, _target = -1, _shrinkSince, _at;
        public double Target => _target;
        public static double Nice(double x)
        {
            if (!(x > 0)) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(x))), f = x / mag;
            return mag * (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
        }
        /// <summary>The extent to draw this frame for content needing <paramref name="need"/>.</summary>
        public double Update(double need, double now)
        {
            need = Math.Max(need, 1e-6);
            double dt = _at > 0 ? Math.Clamp(now - _at, 0, 0.25) : 0; _at = now;
            if (_target < 0 || !(_shown > 0)) { _target = _shown = Nice(need * Margin); return _shown; }
            if (need > _target * GrowAt) { _target = Nice(need * Margin); _shrinkSince = 0; }
            else if (need < _target * ShrinkAt)
            {
                if (_shrinkSince == 0) _shrinkSince = now;
                else if (now - _shrinkSince > ShrinkHold) { _target = Nice(need * Margin); _shrinkSince = 0; }
            }
            else _shrinkSince = 0;
            // (content past the drawn extent: snapped out at once - eased, a frame drawing the overflow (a dotted circle
            //  millions of px round) took 10 s, dt is clamped to 0.25 s a frame, and the ease crept: minutes frozen, 36 GB
            //  of UI commands, 2026-10-12. Only the way in is eased.)
            if (need > _shown) { _shown = _target; return _shown; }
            double a = dt > 0 ? 1 - Math.Exp(-dt / Tau) : 1;
            _shown = Math.Exp(Math.Log(_shown) + (Math.Log(_target) - Math.Log(_shown)) * a);
            return _shown;
        }
        public void Reset() { _shown = _target = -1; _shrinkSince = 0; _at = 0; }
    }
    static readonly ScaleLatch _discScale = new(), _relScale = new(), _oopScale = new();
    static double _relCx, _relCy, _relCxT, _relCyT, _relCenterAt; static bool _relCenterSet; static string _relFor;

    /// <summary>
    /// The orbit disc, just left of the speedometer: your orbit seen from above its plane, turned so the way
    /// you look points up (heading-up, as a sat-nav). The body at its centre, your orbit, you with a
    /// prograde tick, Pe / Ap; a view wedge from you; and at its left edge how far above or below the
    /// orbit plane you are looking.
    /// </summary>
    /// <summary>The disc's centre and radius (px): just left of the game's SPD box.</summary>
    static void DiscPlace(out Vector2 c, out float R)
    {
        var scr = MapPipeline.ScreenSize;
        R = scr.Y * 0.085f;
        // Just left of the game's speed box: measured on screen at 0.409 screen-heights left of centre
        // (the game's HUD keeps its offsets in screen heights from the centre), level with its middle.
        c = new Vector2(scr.X * 0.5f - scr.Y * 0.43f - R, scr.Y * 0.493f);
        // the game's own SPD box when known (HudGeom: any size and shape of screen - at 640x480 the disc sat on the build
        // boxes): sized to it (its height 0.052 screen-heights at 16:9), a small gap left of it, level with its middle
        var gs = HudGeom.Spd;
        if (gs != null && gs[3] > 4f) { R = gs[3] * (0.085f / 0.052f); c = new Vector2(gs[0] - gs[3] * 0.12f - R, gs[1] + gs[3] * 0.5f); }
    }

    /// <summary>The relative plot beside the disc (its centre), and whether the screen has room for it on the left.</summary>
    static Vector2 RelBesideCentre(Vector2 disc, float R, float u) => disc - new Vector2(2f * R + 22f * u, 0);
    static bool RelRoom(float u) { DiscPlace(out var c, out float R); return RelBesideCentre(c, R, u).X - R > 8f * u; }
    static bool _relBeside;

    static bool DrawDisc(Readout r, float u)
    {
        if (r.RelPath == null || r.RelPath.Length < 2 || r.Nor.LengthSquared() < 0.5) return false;
        DiscPlace(out var c, out float R);
        Vector3D n = r.Nor;
        Vector3D fwd = (QuaternionD)MapPipeline.CameraOrientation * Vector3D.Forward;
        Vector3D up = fwd - n * Vector3D.Dot(fwd, n);
        if (up.LengthSquared() > 1e-4) _discUp = Vector3D.Normalize(up);   // looking along the normal: keep the last heading
        else if (_discUp.LengthSquared() < 0.5) _discUp = Vector3D.Normalize(r.Pro);
        Vector3D e1 = _discUp - n * Vector3D.Dot(_discUp, n);
        if (e1.LengthSquared() < 1e-6) return false;
        e1 = Vector3D.Normalize(e1);
        Vector3D e2 = Vector3D.Cross(e1, n);   // screen right, seen from above the plane
        // Scale: the orbit's far point (or the body) fills the disc.
        double far = r.BodyRadius;
        foreach (var q in r.RelPath) if (IsFinite(q)) far = Math.Max(far, q.Length());
        if (r.EventRel.HasValue) far = Math.Max(far, r.EventRel.Value.Length());
        if (r.MoonRel.HasValue) far = Math.Max(far, r.MoonRel.Value.Length() + r.MoonSoi);
        if (r.RvRel.HasValue) far = Math.Max(far, Math.Max(r.RvRel.Value.Length(), r.RvTargetRel.Value.Length()));
        if (r.Rocks != null) foreach (var rk in r.Rocks) far = Math.Max(far, rk.rel.Length());
        bool showSoi = r.SoiRadius > 0 && (r.Escape || (r.EventRel.HasValue && !r.MoonRel.HasValue) || far > 0.5 * r.SoiRadius);
        if (showSoi) far = Math.Max(far, r.SoiRadius);
        double k = (R * 0.86) / _discScale.Update(Math.Max(1.0, far), Wall());   // (held on clean steps, eased: it refit every frame)
        Vector2 P(Vector3D q) => c + new Vector2((float)(Vector3D.Dot(q, e2) * k), (float)(-Vector3D.Dot(q, e1) * k));
        // Panel: a dark disc, a thin rim (as the game's HUD).
        var rim = new List<Vector2>(64);
        for (int i = 0; i < 64; i++) { double a = 2 * Math.PI * i / 64; rim.Add(c + new Vector2((float)Math.Cos(a) * R, (float)Math.Sin(a) * R)); }
        MapPipeline.ScreenFill(rim, new ColorSRGB(0.02f, 0.04f, 0.06f, 0.45f));
        MapPipeline.ScreenPath(rim, true, new ColorSRGB(0.9f, 0.95f, 1f, 0.55f), 1.2f * u);
        // The body (never under a few px), its atmosphere as a thin ring.
        float br = Math.Max(3f * u, (float)(r.BodyRadius * k));
        MapPipeline.ScreenDisc(c, br, new ColorSRGB(0.55f, 0.6f, 0.68f, 0.9f));
        float ar = (float)(r.BodyRadius * (1 + SystemHost.AtmosphereFraction) * k);
        bool air = SystemHost.Registry?.FindDefinition(r.Body)?.HasAtmosphere ?? true;   // (airless bodies drew one too: 2026-10-15)
        if (air && ar > br + 1.5f * u) MapStyle.BoundaryCircle(c, ar, new ColorSRGB(0.55f, 0.85f, 1f, 0.45f), MapStyle.Thin(u), u);
        // The body's sphere of influence (a boundary: dotted), when your path reaches toward it.
        if (showSoi) MapStyle.BoundaryCircle(c, (float)(r.SoiRadius * k), HudPanel.Alpha(new ColorSRGB(0.75f, 0.8f, 0.88f, 1f), 0.55f), MapStyle.Thin(u), u);
        // A moon you will meet: where it will be then, and its sphere.
        if (r.MoonRel.HasValue)
        {
            var ms = P(r.MoonRel.Value);
            MapPipeline.ScreenDisc(ms, 2.5f * u, new ColorSRGB(0.8f, 0.82f, 0.86f, 0.9f));
            float mr = (float)(r.MoonSoi * k);
            if (mr > 3f * u) MapStyle.BoundaryCircle(ms, mr, HudPanel.Alpha(new ColorSRGB(0.75f, 0.8f, 0.88f, 1f), 0.55f), MapStyle.Thin(u), u);
        }
        // Your orbit.
        var path = _orbitPts; path.Clear();   // (reused: drawn here, every frame)
        foreach (var q in r.RelPath) if (IsFinite(q)) path.Add(P(q));
        MapPipeline.ScreenPath(path, r.PathClosed, Orbit, 1.6f * u);
        // Pe / Ap: their markers only - the values are in the block under the disc (2026-10-31 HUD text: "Ap 300 km · 57:44" and
        // "Pe 300 km · 1h 55m" on the rim of a circular orbit, P1)
        void Apsis(Vector3D q) => MapPipeline.ScreenDisc(P(q), 2.2f * u, Orbit);
        if (r.Pe > -r.BodyRadius) Apsis(r.PeRel);
        if (r.ApRel.HasValue && !r.Escape) Apsis(r.ApRel.Value);
        // The next patch event: where you leave the sphere, or enter a moon's.
        if (r.EventRel.HasValue)
        {
            var es = P(r.EventRel.Value);
            var ec = new ColorSRGB(0.85f, 0.55f, 1f, 1f);
            if (!MapPipeline.ScreenIcon("ring", es, 5f * u, ec)) MapPipeline.ScreenCircle(es, 4f * u, ec, 1.6f * u);
            // (its words: a line of the block under the disc)
        }
        // The rendezvous with your target: marked as the escape / entry is, the target then a ring, joined.
        if (r.RvRel.HasValue)
        {
            var rs = P(r.RvRel.Value);
            var rc = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);   // (the map's target colour)
            var ts = P(r.RvTargetRel.Value);
            if ((ts - rs).Length() > 3f * u)
            {
                MapPipeline.ScreenCircle(ts, 3f * u, rc, 1.2f * u);
                MapPipeline.ScreenDashed(rs, ts, HudPanel.Alpha(rc, 0.6f), 1f * u);
            }
            if (!MapPipeline.ScreenIcon("ring", rs, 5f * u, rc)) MapPipeline.ScreenCircle(rs, 4f * u, rc, 1.6f * u);
            // (no label: the closest approach is told once, under the relative plot - P7, P9, P10)
        }
        // Ring rocks you will meet: each marked as the rendezvous is (the next one's words: a line of the block under the disc).
        if (r.Rocks != null)
        {
            var kc = Maneuvers.RockColor;
            for (int i = 0; i < r.Rocks.Count; i++)
            {
                var ks = P(r.Rocks[i].rel);
                MapPipeline.ScreenCircle(ks, 3.5f * u, kc, 1.4f * u);
                MapPipeline.ScreenDisc(ks, 1.2f * u, kc);
            }
        }
        // You: a dot, a prograde tick, and your view as a wedge (straight up: heading-up).
        var me = P(r.PlayerRel);
        var pro2 = new Vector2((float)Vector3D.Dot(r.Pro, e2), (float)-Vector3D.Dot(r.Pro, e1));
        if (pro2.LengthSquared() > 1e-6) MapPipeline.ScreenLine(me, me + Vector2.Normalize(pro2) * 9f * u, ProCol, 1.6f * u);
        float wl = R * 0.28f;
        var wedge = HudPanel.Alpha(new ColorSRGB(1f, 1f, 1f, 1f), 0.35f);
        MapPipeline.ScreenLine(me, me + new Vector2(-0.45f, -1f) * wl, wedge, 1f * u);
        MapPipeline.ScreenLine(me, me + new Vector2(0.45f, -1f) * wl, wedge, 1f * u);
        MapPipeline.ScreenDisc(me, 3.2f * u, new ColorSRGB(1f, 1f, 1f, 1f));
        // How far above / below the orbit plane you look: a notch on the disc's left edge.
        double tilt = Math.Asin(Math.Clamp(Vector3D.Dot(fwd, n), -1, 1));
        float ty = (float)(-tilt / (Math.PI / 2)) * R * 0.8f;
        MapPipeline.ScreenLine(c + new Vector2(-R - 6f * u, -R * 0.8f), c + new Vector2(-R - 6f * u, R * 0.8f), HudPanel.Alpha(new ColorSRGB(1f, 1f, 1f, 1f), 0.3f), 1f * u);
        MapPipeline.ScreenLine(c + new Vector2(-R - 10f * u, ty), c + new Vector2(-R - 2f * u, ty), NorCol, 2f * u);
        DiscText(c, R, u);
        return true;
    }

    static readonly ColorSRGB CapDim = new ColorSRGB(0.72f, 0.8f, 0.9f, 0.9f), CapWhite = new ColorSRGB(0.95f, 0.97f, 1f, 1f);
    static readonly List<SEAerospace.MapUi.Rect> _avoid = new List<SEAerospace.MapUi.Rect>(2);

    /// <summary>
    /// Slot (a): the block centred under the disc - the orbit's next apsis (big) and the other with the period (small), or the
    /// burn (big, orange) with the hold; then the extra lines (a patch event, a ring rock, entry, transmitting). Kept right of the
    /// relative plot beside it. When the game's own HUD notification ("Press P to Park/Lock/Connect", GameNotes) would sit on it,
    /// it goes above the disc instead (UI audit C1).
    /// </summary>
    static void DiscText(Vector2 c, float R, float u)
    {
        var lines = _discLines;
        CaptionsBelow = true;
        if (lines.Count == 0) return;
        float H = HudText.Height(lines, LineH(true, u), LineH(false, u)), wide = BlockWidth(lines, u);
        float cx = c.X;
        if (_relBeside)
        {
            float minX = RelBesideCentre(c, R, u).X + R + 8f * u;   // (the plot's right edge)
            if (cx - wide * 0.5f < minX) cx = minX + wide * 0.5f;
        }
        _avoid.Clear();
        var nr = GameNotes.Rect;
        if (nr != null) _avoid.Add(SEAerospace.MapUi.Rect.FromXywh(nr));
        float top = c.Y + R + 6f * u, aboveBottom = c.Y - R - 6f * u;
        bool below = SEAerospace.MapUi.CardLayout.Below(cx - wide * 0.5f - 4f * u, cx + wide * 0.5f + 4f * u, top, aboveBottom, H, _avoid);
        DrawBlock(lines, cx, below ? top : aboveBottom - H, true, u);
        CaptionsBelow = below;
    }
    /// <summary>The card's lines are under the plot (false: moved above it, clear of the game's notification).</summary>
    public static bool CaptionsBelow = true;

    /// <summary>
    /// Riding another's frame (a station, an asteroid): your motion about its anchor, as a rendezvous
    /// plot in its curvilinear local frame. The anchor at the panel's top-left; NASA axes: behind (-V)
    /// to the right, toward the planet (+R) down. Your next revolution dashed, where you are now, and
    /// the frame's boundary (past it you split off on your own orbit).
    /// </summary>
    // the scale label, remade four times a second (drawn every frame it was a new string every frame)
    static string _gridText; static double _gridAt, _gridFor = double.NaN;
    static string GridText(double step)
    {
        double w = Wall();
        if (_gridText != null && step == _gridFor && w - _gridAt < 0.25) return _gridText;
        _gridAt = w; _gridFor = step;
        return _gridText = "grid " + HudText.Km(step);
    }

    static void DrawRelative(Readout r, float u, bool beside)
    {
        var scr = MapPipeline.ScreenSize;
        DiscPlace(out var c, out float R);
        if (beside) c = RelBesideCentre(c, R, u);   // (left of your orbit's disc: both shown - UI audit C4)
        float S = 2 * R, padL = 14f * u, padT = 14f * u, pad = 6f * u;
        var lo = new Vector2(c.X - R, c.Y - R);                       // panel top-left
        var a0 = new Vector2(lo.X + padL, lo.Y + padT);               // plot area
        var a1 = new Vector2(c.X + R - pad, c.Y + R - pad);
        // Screen axes (NASA): x = behind (-V bar), y = toward the planet (+R bar).
        (double x, double y) Ax((double along, double radial) q) => (-q.along, -q.radial);
        // Fit: your next revolution, you and the anchor, equal scale; the anchor lands top-left when you
        // are behind and below it (the usual approach), inward otherwise (nothing clipped).
        double minX = 0, maxX = 0, minY = 0, maxY = 0;
        void Inc((double x, double y) p) { minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y); }
        foreach (var q in r.Relative) Inc(Ax(q));
        Inc(Ax(r.RelNow));
        // (a steady view: the extent on clean steps, grown at once, shrunk late, eased - ScaleLatch; the centre held where it is
        //  until the content would leave the view, then eased onto it. Both were refit to the content every frame: twitchy)
        double now = Wall();
        if (_relFor != r.AnchorName) { _relFor = r.AnchorName; _relScale.Reset(); _relCenterSet = false; }
        double target0 = _relScale.Target;
        double ext = _relScale.Update(Math.Max(500, Math.Max(maxX - minX, maxY - minY)), now);
        float span = Math.Min(a1.X - a0.X, a1.Y - a0.Y);
        double k = span / ext;
        {
            double bx = 0.5 * (minX + maxX), by = 0.5 * (minY + maxY), half = 0.5 * _relScale.Target * 0.96;
            bool leaves = maxX - _relCxT > half || _relCxT - minX > half || maxY - _relCyT > half || _relCyT - minY > half;
            if (!_relCenterSet) { _relCx = _relCxT = bx; _relCy = _relCyT = by; _relCenterSet = true; }
            else if (leaves || _relScale.Target != target0) { _relCxT = bx; _relCyT = by; }
            double dt = _relCenterAt > 0 ? Math.Clamp(now - _relCenterAt, 0, 0.25) : 0; _relCenterAt = now;
            double a = dt > 0 ? 1 - Math.Exp(-dt / ScaleLatch.Tau) : 1;
            _relCx += (_relCxT - _relCx) * a; _relCy += (_relCyT - _relCy) * a;
        }
        double cx = _relCx, cy = _relCy;
        var mid = 0.5f * (a0 + a1);
        Vector2 P((double along, double radial) q) { var p = Ax(q); return mid + new Vector2((float)((p.x - cx) * k), (float)((p.y - cy) * k)); }
        bool In(Vector2 s) => s.X >= a0.X - 1 && s.X <= a1.X + 1 && s.Y >= a0.Y - 1 && s.Y <= a1.Y + 1;
        var white = new ColorSRGB(1f, 1f, 1f, 1f);
        var faint = HudPanel.Alpha(white, 0.12f);
        var dim = HudPanel.Alpha(white, 0.5f);
        var orange = new ColorSRGB(1f, 0.6f, 0.3f, 1f);
        // Panel.
        var box = new List<Vector2> { lo, new Vector2(c.X + R, c.Y - R), new Vector2(c.X + R, c.Y + R), new Vector2(c.X - R, c.Y + R) };
        MapPipeline.ScreenFill(box, new ColorSRGB(0.02f, 0.04f, 0.06f, 0.55f));
        for (int i = 0; i < 4; i++) MapPipeline.ScreenLine(box[i], box[(i + 1) % 4], HudPanel.Alpha(white, 0.55f), 1.2f * u);
        // Grid at a clean step (the shown extent's: it moves with the eased zoom, never jumps), unlabelled - ONE scale bar
        // says what a square is (the user, 2026-10-10: "too much text... measurement spans, markers on the plot"; it was a
        // number on every other line, top and left).
        double step = ScaleLatch.Nice(_relScale.Target / 4.5);
        var origin = P((0, 0));
        for (int i = -40; i <= 40; i++)
        {
            float gx = origin.X + (float)(i * step * k), gy = origin.Y + (float)(i * step * k);
            if (gx >= a0.X && gx <= a1.X) MapPipeline.ScreenLine(new Vector2(gx, a0.Y), new Vector2(gx, a1.Y), i == 0 ? dim : faint, 1f * u);
            if (gy >= a0.Y && gy <= a1.Y) MapPipeline.ScreenLine(new Vector2(a0.X, gy), new Vector2(a1.X, gy), i == 0 ? dim : faint, 1f * u);
        }
        {
            // the scale bar, bottom right: one grid square, end ticks, its length
            float bl = (float)(step * k), by0 = a1.Y - 7f * u, bx1 = a1.X - 6f * u, bx0 = bx1 - bl;
            MapPipeline.ScreenLine(new Vector2(bx0, by0), new Vector2(bx1, by0), dim, 1.2f * u);
            MapPipeline.ScreenLine(new Vector2(bx0, by0 - 3f * u), new Vector2(bx0, by0 + 3f * u), dim, 1.2f * u);
            MapPipeline.ScreenLine(new Vector2(bx1, by0 - 3f * u), new Vector2(bx1, by0 + 3f * u), dim, 1.2f * u);
        }
        // The frame's boundary round the anchor (past it you leave on your own orbit).
        {
            var arc = new List<Vector2>();
            double rb = r.RelBoundary;
            for (int i = 0; i <= 96; i++)
            {
                double a = 2 * Math.PI * i / 96;
                var s = P((rb * Math.Cos(a), rb * Math.Sin(a)));
                if (In(s)) arc.Add(s); else { MapStyle.Boundary(arc, false, HudPanel.Alpha(orange, 0.7f), MapStyle.Thin(u), u); arc.Clear(); }
            }
            MapStyle.Boundary(arc, false, HudPanel.Alpha(orange, 0.7f), MapStyle.Thin(u), u);
        }
        // Your next revolution: dashed, with quarter-revolution ticks (time from now) and its direction.
        var path = _relPts; path.Clear();   // (reused: drawn here, every frame)
        foreach (var q in r.Relative) path.Add(P(q));
        MapStyle.Plan(path, false, Orbit, 1.5f * u, u);
        int n = path.Count - 1, full = Math.Max(1, r.RelSamples - 1);
        for (int qd = 1; qd <= 3 && n >= 4; qd++)
        {
            int i = full * qd / 4;
            if (i > n) break;   // (cut short where it leaves the frame)
            MapPipeline.ScreenDisc(path[i], 2.2f * u, Orbit);
        }
        if (n >= 2)
        {
            int i = Math.Min(n, n / 8 + 1);
            var d = path[i] - path[i - 1];
            if (d.LengthSquared() > 1e-4f)
            {
                d = Vector2.Normalize(d); var nrm = new Vector2(-d.Y, d.X);
                MapPipeline.ScreenLine(path[i], path[i] - d * 6f * u + nrm * 3.5f * u, Orbit, 1.4f * u);
                MapPipeline.ScreenLine(path[i], path[i] - d * 6f * u - nrm * 3.5f * u, Orbit, 1.4f * u);
            }
        }
        // Where the prediction leaves the frame: from there you are on your own orbit.
        if (r.RelLeaves && path.Count > 0)
        {
            var ex = path[path.Count - 1];
            MapPipeline.ScreenCircle(ex, 4f * u, orange, 1.6f * u);
        }
        // The range as a measured span from the anchor to you, your velocity arrow, the closest approach marked on the path - their
        // words are in the block under the plot (2026-10-31 HUD text: no text on the drawing).
        var me = P(r.RelNow);
        // the range span: anchor to you, end ticks, its length at the middle
        if (In(me) && In(origin) && (me - origin).Length() > 18f * u)
        {
            var d = Vector2.Normalize(me - origin); var nrm = new Vector2(-d.Y, d.X);
            var sc = HudPanel.Alpha(white, 0.45f);
            MapPipeline.ScreenLine(origin + d * 6f * u, me - d * 6f * u, sc, 1f * u);
            MapPipeline.ScreenLine(origin + d * 6f * u - nrm * 3f * u, origin + d * 6f * u + nrm * 3f * u, sc, 1f * u);
            MapPipeline.ScreenLine(me - d * 6f * u - nrm * 3f * u, me - d * 6f * u + nrm * 3f * u, sc, 1f * u);
        }
        // the closest approach on the predicted revolution (marked where it happens: its distance and when)
        if (!r.Holding && r.Relative.Count > 2)
        {
            int ci = 0; double cmin = double.MaxValue;
            for (int i = 0; i < r.Relative.Count; i++) { var q = r.Relative[i]; double dd = q.along * q.along + q.radial * q.radial; if (dd < cmin) { cmin = dd; ci = i; } }
            var cs = P(r.Relative[ci]);
            if (ci > 0 && In(cs))
            {
                MapPipeline.ScreenCircle(cs, 4.5f * u, Orbit, 1.3f * u);
            }
        }
        // The anchor (a square, its name by it) and you (a dot, your relative velocity with its rate on it).
        MapPipeline.ScreenFill(new List<Vector2> { origin + new Vector2(-3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, 3.5f * u), origin + new Vector2(-3.5f * u, 3.5f * u) }, orange);
        var vr = new Vector2((float)-r.RelVel.along, (float)-r.RelVel.radial);
        if (vr.LengthSquared() > 1e-6f)
        {
            var tip = me + Vector2.Normalize(vr) * 14f * u;
            MapPipeline.ScreenLine(me, tip, white, 1.6f * u);
        }
        MapPipeline.ScreenDisc(me, 3.4f * u, white);
        // Out of plane: a strip under the plot (same along-track axis), only when there is real motion out of it (its words - "out
        // of plane ±2.0 km" - in the block under it).
        float stripBottom = float.NaN; double oopShownReach = double.NaN;
        if (r.RelCross != null && r.RelCross.Count >= 2)
        {
            double oop = Math.Abs(r.RelNowCross);
            foreach (var x in r.RelCross) oop = Math.Max(oop, Math.Abs(x));
            if (oop > Math.Max(200, 0.1 * ext))
            {
                float sh = R * 0.55f, top = c.Y + R + 26f * u;
                var sb = new List<Vector2> { new Vector2(c.X - R, top), new Vector2(c.X + R, top), new Vector2(c.X + R, top + sh), new Vector2(c.X - R, top + sh) };
                MapPipeline.ScreenFill(sb, new ColorSRGB(0.02f, 0.04f, 0.06f, 0.55f));
                for (int i = 0; i < 4; i++) MapPipeline.ScreenLine(sb[i], sb[(i + 1) % 4], HudPanel.Alpha(white, 0.55f), 1.2f * u);
                float midY = top + sh * 0.5f;
                double oopShown = _oopScale.Update(oop, Wall());   // (held and eased as the plot's: it refit every frame)
                double kc = (sh * 0.5 - 6 * u) / oopShown;
                MapPipeline.ScreenLine(new Vector2(a0.X, midY), new Vector2(a1.X, midY), dim, 1f * u);   // the anchor's plane
                stripBottom = top + sh; oopShownReach = oop;
                {
                    // the out-of-plane reach as a measured span at the left edge: from the plane to the furthest point
                    float sx = a0.X + 6f * u, sy = midY - (float)(oop * kc);
                    MapPipeline.ScreenLine(new Vector2(sx, midY), new Vector2(sx, sy), dim, 1f * u);
                    MapPipeline.ScreenLine(new Vector2(sx - 3f * u, sy), new Vector2(sx + 3f * u, sy), dim, 1f * u);
                }
                var sp = new List<Vector2>(r.Relative.Count);
                for (int i = 0; i < r.Relative.Count && i < r.RelCross.Count; i++)
                    sp.Add(new Vector2(P(r.Relative[i]).X, midY - (float)(r.RelCross[i] * kc)));
                MapStyle.Plan(sp, false, Orbit, 1.4f * u, u);
                var ax = P((0, 0)).X;
                MapPipeline.ScreenFill(new List<Vector2> { new Vector2(ax - 3f * u, midY - 3f * u), new Vector2(ax + 3f * u, midY - 3f * u), new Vector2(ax + 3f * u, midY + 3f * u), new Vector2(ax - 3f * u, midY + 3f * u) }, orange);
                MapPipeline.ScreenDisc(new Vector2(P(r.RelNow).X, midY - (float)(r.RelNowCross * kc)), 3f * u, white);
            }
        }
        _oopReach = oopShownReach;
        // THE PLOT'S WORDS (2026-10-31 HUD text), in their slots (HudText.Plot): the name (left, magenta for a target) and its
        // distance (right) on the box's top edge - "Relative to X" was a title on its own (b); "ahead" and "behind" in the box's
        // top corners, off the axis and the target (P13); the scale label bottom-right inside the box, always drawn (it was a
        // collision-tested label and lost to the others: P8); the block under the box (c). Nothing else inside the box (P5).
        {
            float ss = HudText.Secondary * u, ps = HudText.Primary * u;
            string grid = GridText(step);
            var slots = HudText.Plot(new SEAerospace.MapUi.Rect(c.X - R, c.Y - R, c.X + R, c.Y + R),
                _plotName != null ? MapPipeline.MeasureText(_plotName, ps) : Vector2.Zero, _plotDist != null ? MapPipeline.MeasureText(_plotDist, ss) : Vector2.Zero,
                MapPipeline.MeasureText("ahead", ss), MapPipeline.MeasureText("behind", ss), MapPipeline.MeasureText(grid, ss),
                5f * u, 17f * u, 4f * u, stripBottom);
            if (_plotName != null) T(slots.Name, _plotName, RoleColor(_plotNameRole), ps, u);
            if (_plotDist != null) T(slots.Distance, _plotDist, CapWhite, ss, u);
            T(slots.Ahead, "ahead", CapDim, ss, u);
            T(slots.Behind, "behind", CapDim, ss, u);
            T(slots.Scale, grid, CapDim, ss, u);
            float y = DrawBlock(_plotLines, slots.Block.X, slots.Block.Y, false, u);
            // (no room beside the disc: the plot in its place, the disc's block under the plot's)
            if (!beside) DrawBlock(_discLines, slots.Block.X, y + 4f * u, false, u);
        }
    }

    /// <summary>
    /// Your orbit in flight: a smooth screen-space curve (as on the map), hidden behind the planet.
    /// (It was 3D line geometry: widths guessed from the distance, jagged, through the globe.)
    /// </summary>
    static void DrawPath(Readout r, float u)
    {
        var pts = r.Path;
        if (pts == null || pts.Length < 2) return;
        MapPipeline.Occlude(r.BodyWorld, r.BodyRadius);
        var col = HudPanel.Alpha(Orbit, 0.75f);
        var run = new List<Vector2>();
        bool whole = true;
        int n = pts.Length, segs = r.PathClosed ? n : n - 1;
        void Flush() { if (run.Count > 1) MapPipeline.ScreenPath(run, false, col, 1.6f * u); run = new List<Vector2>(); }
        for (int i = 0; i <= segs; i++)
        {
            Vector3D w = pts[i % n];
            if (double.IsNaN(w.X) || MapPipeline.Occluded(w) || !MapPipeline.ToScreen(w, out var s)) { whole = false; Flush(); continue; }   // (NaN: a gap, at the camera)
            run.Add(s);
        }
        if (whole && r.PathClosed && run.Count > 2) { run.RemoveAt(run.Count - 1); MapPipeline.ScreenPath(run, true, col, 1.6f * u); }
        else Flush();
    }

}
