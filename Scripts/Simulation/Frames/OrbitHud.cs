using Keen.VRage.Core;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>A HUD panel in the game's UI font: dark box, accent rule, title, label / value rows.</summary>
public static class HudPanel
{
    /// <summary>The colour at another opacity (ColorSRGB's channels are bytes).</summary>
    public static ColorSRGB Alpha(ColorSRGB c, float a) => new ColorSRGB(c.R, c.G, c.B, (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255));

    static readonly ColorSRGB Bg = new ColorSRGB(0.02f, 0.045f, 0.07f, 0.80f);
    static readonly ColorSRGB Accent = new ColorSRGB(0.96f, 0.62f, 0.18f, 1f);
    static readonly ColorSRGB Title = new ColorSRGB(0.95f, 0.97f, 1f, 1f);
    static readonly ColorSRGB Label = new ColorSRGB(0.60f, 0.68f, 0.78f, 1f);
    static readonly ColorSRGB Value = new ColorSRGB(0.95f, 0.97f, 1f, 1f);
    public static readonly ColorSRGB Warn = new ColorSRGB(1f, 0.42f, 0.30f, 1f);

    public struct Row { public string Label, Value; public ColorSRGB? Color; public Row(string l, string v, ColorSRGB? c = null) { Label = l; Value = v; Color = c; } }

    /// <summary>Draw at a top-left corner; returns the panel's bottom-right. Call inside a UI batch.</summary>
    public static Vector2 Draw(Vector2 at, float width, string title, string subtitle, List<Row> rows, string footer, float u, ColorSRGB? accent = null)
    {
        float pad = 10f * u, line = 21f * u, ts = 0.62f * u, rs = 0.52f * u;
        float h = pad * 2 + line * 1.2f + rows.Count * line + (footer != null ? line : 0);
        var max = at + new Vector2(width, h);
        MapPipeline.ScreenRect(at, max, Bg);
        MapPipeline.ScreenLine(at, new Vector2(max.X, at.Y), accent ?? Accent, 2f * u);
        float y = at.Y + pad;
        MapPipeline.ScreenText(new Vector2(at.X + pad, y), title, Title, ts);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var sw = MapPipeline.MeasureText(subtitle, rs);   // right-aligned in the header row
            MapPipeline.ScreenText(new Vector2(max.X - pad - sw.X, y + 2f * u), subtitle, Label, rs);
        }
        y += line * 1.2f;
        foreach (var r in rows)
        {
            MapPipeline.ScreenText(new Vector2(at.X + pad, y), r.Label, Label, rs);
            var vw = MapPipeline.MeasureText(r.Value, rs);
            MapPipeline.ScreenText(new Vector2(max.X - pad - vw.X, y), r.Value, r.Color ?? Value, rs);
            y += line;
        }
        if (footer != null) MapPipeline.ScreenText(new Vector2(at.X + pad, y), footer, Label, rs * 0.95f);
        return max;
    }

    /// <summary>A row of a Stats block: a small-caps label, a value, a dim note after it (a countdown, a unit's aside).</summary>
    public struct Stat { public string Label, Value, Note; public ColorSRGB? Color; public Stat(string l, string v, string n = null, ColorSRGB? c = null) { Label = l; Value = v; Note = n; Color = c; } }

    /// <summary>
    /// THE READOUT BLOCK (2026-10-11, the user: the orbit card and the build panel "badly presented", "like a badly
    /// formatted word text paragraph"): a light plate with an accent edge, an optional small title, then rows - the label
    /// in a dim small-caps column, the value lined up after it, a dimmer note after the value - and free lines under them.
    /// Sized to its text (measured: fixed pixels overlapped at 4K). anchor: the left edge (or the right, alignRight) and the
    /// top (or the middle, centreY). Returns the plate's (min, max). Call inside a UI batch.
    /// </summary>
    public static (Vector2 min, Vector2 max) Stats(Vector2 anchor, bool alignRight, bool centreY, string title, List<Stat> rows, List<string> lines, float u)
        => Stats(anchor, alignRight, centreY, title, rows, lines, u, 0f, 0f, out _);

    /// <summary>As Stats, at least minWidth wide, with reserveTop px left free under the title for the caller to draw in
    /// (its top-left: reserved).</summary>
    public static (Vector2 min, Vector2 max) Stats(Vector2 anchor, bool alignRight, bool centreY, string title, List<Stat> rows, List<string> lines, float u,
        float minWidth, float reserveTop, out Vector2 reserved)
    {
        const float LS = 0.40f, VS = 0.56f, NS = 0.44f, TS = 0.42f;
        float pad = 10f * u, gap = 12f * u;
        float hV = MapPipeline.MeasureText("0", VS * u).Y, hL = MapPipeline.MeasureText("A", LS * u).Y, hN = MapPipeline.MeasureText("0", NS * u).Y, hT = MapPipeline.MeasureText("A", TS * u).Y;
        float rowH = hV * 1.12f, lineH = hN * 1.2f;
        float labelW = 0f, valW = 0f, linesW = 0f;
        foreach (var r in rows)
        {
            labelW = Math.Max(labelW, MapPipeline.MeasureText(r.Label, LS * u).X);
            float w = MapPipeline.MeasureText(r.Value ?? "", VS * u).X;
            if (!string.IsNullOrEmpty(r.Note)) w += 6f * u + MapPipeline.MeasureText(r.Note, NS * u).X;
            valW = Math.Max(valW, w);
        }
        if (lines != null) foreach (var l in lines) linesW = Math.Max(linesW, MapPipeline.MeasureText(l, NS * u).X);
        float titleW = string.IsNullOrEmpty(title) ? 0f : MapPipeline.MeasureText(title, TS * u).X;
        float W = Math.Max(minWidth, pad * 2 + Math.Max(Math.Max(labelW + gap + valW, linesW), titleW));
        float H = pad * 2 + (string.IsNullOrEmpty(title) ? 0f : hT + 6f * u) + reserveTop + rows.Count * rowH + (lines != null && lines.Count > 0 ? 6f * u + lines.Count * lineH : 0f);
        var min = new Vector2(alignRight ? anchor.X - W : anchor.X, centreY ? anchor.Y - H * 0.5f : anchor.Y);
        var max = min + new Vector2(W, H);
        MapPipeline.ScreenRect(min, max, new ColorSRGB(0.02f, 0.045f, 0.07f, 0.62f));
        MapPipeline.ScreenLine(alignRight ? new Vector2(max.X, min.Y) : min, alignRight ? max : new Vector2(min.X, max.Y), Accent, 2f * u);
        float y = min.Y + pad, x = min.X + pad;
        if (!string.IsNullOrEmpty(title)) { MapPipeline.ScreenText(new Vector2(x, y), title, Accent, TS * u); y += hT + 6f * u; }
        reserved = new Vector2(x, y); y += reserveTop;
        foreach (var r in rows)
        {
            // (bottoms lined up: the small label and note sit on the value's baseline)
            MapPipeline.ScreenText(new Vector2(x, y + hV - hL), r.Label, Label, LS * u);
            float vx = x + labelW + gap;
            MapPipeline.ScreenText(new Vector2(vx, y), r.Value ?? "", r.Color ?? Value, VS * u);
            if (!string.IsNullOrEmpty(r.Note))
                MapPipeline.ScreenText(new Vector2(vx + MapPipeline.MeasureText(r.Value ?? "", VS * u).X + 6f * u, y + hV - hN), r.Note, Label, NS * u);
            y += rowH;
        }
        if (lines != null && lines.Count > 0)
        {
            y += 6f * u;
            foreach (var l in lines) { MapPipeline.ScreenText(new Vector2(x, y), l, Label, NS * u); y += lineH; }
        }
        return (min, max);
    }

    /// <summary>A flat button (dark plate, accent rule on hover, centred text). Returns true when the mouse is on it.</summary>
    public static bool Button(Vector2 min, Vector2 size, string text, Vector2 mouse, float u, bool enabled = true)
    {
        // The game's own button look (as its "Fast Travel"): a flat translucent slate, lighter on hover.
        var max = min + size;
        bool hot = enabled && mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
        MapPipeline.ScreenRect(min, max, hot ? new ColorSRGB(0.30f, 0.38f, 0.44f, 0.80f) : new ColorSRGB(0.14f, 0.20f, 0.25f, 0.62f));
        var ts = MapPipeline.MeasureText(text, 0.62f * u);
        MapPipeline.ScreenText(min + (size - ts) * 0.5f, text, enabled ? Title : new ColorSRGB(0.55f, 0.62f, 0.68f, 0.9f), 0.62f * u);
        return hot;
    }

    /// <summary>A small labelled tag at a world point (apsides, your position), when on screen.</summary>
    public static void Tag(Vector3D world, string text, ColorSRGB c, float u)
    {
        if (!MapPipeline.ToScreen(world, out var s)) return;
        var sz = MapPipeline.ScreenSize;
        if (s.X < 0 || s.Y < 0 || s.X > sz.X || s.Y > sz.Y) return;
        TagAt(s, text, c, u);
    }

    /// <summary>A small labelled tag at a screen point: a small dot on the point, quiet text beside it.</summary>
    public static void TagAt(Vector2 s, string text, ColorSRGB c, float u, bool diamond = true)
    {
        float r = 5f * u;
        if (!diamond) { LabelAt(s + new Vector2(9f * u, 0), text, c, u); return; }
        if (MapPipeline.ScreenIcon("dot", s, 3f * u, c)) { LabelAt(s + new Vector2(9f * u, 0), text, c, u); return; }
        MapPipeline.ScreenLine(s + new Vector2(0, -r), s + new Vector2(r, 0), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(r, 0), s + new Vector2(0, r), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(0, r), s + new Vector2(-r, 0), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(-r, 0), s + new Vector2(0, -r), c, 1.8f * u);
        LabelAt(s + new Vector2(9f * u, 0), text, c, u);
    }

    private static readonly List<(Vector2 min, Vector2 max)> _placed = new List<(Vector2, Vector2)>();
    /// <summary>Start of a frame's labels: later labels step down until clear of earlier ones.</summary>
    public static void BeginLabels() { _placed.Clear(); SkipWhenBlocked = false; }

    /// <summary>A label with no clear spot is left out (flight), instead of drawn over what is there.</summary>
    public static bool SkipWhenBlocked;

    /// <summary>
    /// Flight: the game's own HUD (objectives, the orbit card, the suit and ship panels, the toolbar)
    /// is off limits to our labels for this batch.
    /// </summary>
    public static void ReserveGameHud()
    {
        var s = MapPipeline.ScreenSize;
        MapPipeline.Reserve(Vector2.Zero, new Vector2(s.X * 0.25f, s.Y * 0.19f));                     // objectives
        MapPipeline.Reserve(new Vector2(s.X * 0.74f, 0), new Vector2(s.X, s.Y * 0.17f));             // cards
        MapPipeline.Reserve(new Vector2(0, s.Y * 0.76f), new Vector2(s.X * 0.18f, s.Y));             // suit / status
        MapPipeline.Reserve(new Vector2(s.X * 0.82f, s.Y * 0.76f), s);                             // ship panel
        MapPipeline.Reserve(new Vector2(s.X * 0.26f, s.Y * 0.78f), new Vector2(s.X * 0.8f, s.Y));    // toolbar, hints
        MapPipeline.Reserve(new Vector2(s.X * 0.39f, 0), new Vector2(s.X * 0.61f, s.Y * 0.08f));     // warp bar
        SkipWhenBlocked = true;
    }

    /// <summary>
    /// Text on a dark plate beside its point: right of it, else left, above or below, whichever is clear
    /// of the labels already placed (tags and the map's own names). When none is, it stays on the right:
    /// a label that wanders off (it used to step down until clear) names the wrong place.
    /// </summary>
    public static void LabelAt(Vector2 p, string text, ColorSRGB c, float u)
    {
        const float Scale = 0.36f;   // small and quiet (at 0.5 on a dark plate the tags shouted)
        var ts = MapPipeline.MeasureText(text, Scale * u);
        var pad = new Vector2(3f * u, 2f * u);
        Vector2 anchor = p - new Vector2(9f * u, 0);   // the point being named (callers pass it +9 px)
        var cands = new[]
        {
            p - new Vector2(0, ts.Y * 0.5f),                                          // right
            anchor - new Vector2(ts.X + 9f * u, ts.Y * 0.5f),                         // left
            anchor + new Vector2(-ts.X * 0.5f, -ts.Y - 9f * u),                       // above
            anchor + new Vector2(-ts.X * 0.5f, 9f * u),                               // below
        };
        Vector2 at = cands[0]; bool placed = false;
        string key = MapPipeline.LabelKey(text);
        int last = MapPipeline.ShownLately(key);
        float h = last >= 0 ? -3f * u : 3f * u;                 // lenient to stay, strict to appear
        var order = new List<int> { 0, 1, 2, 3 };
        if (last > 0) { order.Remove(last); order.Insert(0, last); }   // keep the side it was on
        int chosen = -1;
        foreach (int ci in order)
        {
            var cand = cands[ci];
            Vector2 mn = cand - pad - new Vector2(h, h), mx = cand + ts + pad + new Vector2(h, h);
            bool hit = false;
            foreach (var b in _placed)
                if (mn.X < b.max.X && mx.X > b.min.X && mn.Y < b.max.Y && mx.Y > b.min.Y) { hit = true; break; }
            if (!hit && MapPipeline.Free(mn, mx)) { at = cand; placed = true; chosen = ci; break; }
        }
        if (!placed) return;
        MapPipeline.MarkShown(key, chosen);   // no clear spot: left out (drawn anyway, labels landed on each other)
        _placed.Add((at - pad, at + ts + pad));
        MapPipeline.Reserve(at - pad, at + ts + pad);
        MapPipeline.ScreenText(at, text, c, Scale * u);   // no plate: the font's own shadow is enough
    }

    public static string Km(double m) => Math.Abs(m) >= 1e6 ? $"{m / 1000:N0} km" : Math.Abs(m) >= 1e4 ? $"{m / 1000:F0} km" : $"{m / 1000:F1} km";
}

/// <summary>
/// The in-flight orbit readout: what OrbitDisplay computes, shown as a HUD panel (right side) with
/// Pe / Ap tags on the drawn orbit. Replaces the old 3D debug text.
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
    private static double _refusedUntil;
    private static string _lastTitle, _lastContent;
    private static (string title, string content)? _fallback;

    /// <summary>
    /// THE ORBIT READOUT (2026-10-11, the user: "kill the orbit gui in the top right - make it into cleanly presented orb
    /// element info next to the orbit plot and the rel plot"): no card any more; the elements in a HudPanel.Stats block
    /// hugging the plot's left side, level with its middle - altitude and speed, Pe and Ap with how long until each,
    /// period, inclination - and under them what the card also carried: the mode, the next burn, the target, the ring,
    /// the attitude hold, reentry, transmitting. Also drawn with no plot (a burn alone, over the map's absence).
    /// </summary>
    static void DrawInfo(float u)
    {
        _infoShown = false;
        if (_info == null) return;
        var scr = MapPipeline.ScreenSize;
        float R = scr.Y * 0.085f;
        var c = new Vector2(scr.X * 0.5f - scr.Y * 0.43f - R, scr.Y * 0.493f);   // (the plot's centre: DrawDisc / DrawRelative)
        HudPanel.Stats(new Vector2(c.X - R - 16f * u, c.Y), true, true, _info.Value.title, _info.Value.rows, _info.Value.lines, u);
        _infoShown = true;
    }
    static (string title, List<HudPanel.Stat> rows, List<string> lines)? _info;
    static bool _infoShown;
    /// <summary>The orbit readout is on screen (harness: hudcheck), and why not when it is not.</summary>
    public static bool CardOpen => _infoShown;
    public static string CardWhy = "-";
    private static double _nextUpdate, _nextCheck;

    /// <summary>
    /// The readout lives in the game's own notification card (updated in place, twice a second at most),
    /// and the apsides are tagged on the drawn orbit.
    /// </summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
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
        _fallback = null;
        if (_card != null) { GameUi.CloseCard(_card); _card = null; }   // (the old card: never shown again)
        if (!show)
        {
            CardWhy = !OrbitalConfig.ShowOrbit ? "orbit display off" : MapView.Visible ? "map open" : covered ? "a screen is open" : Walking ? "walking" : EntryHidden ? "apoapsis inside the air" : "no orbit";
            _info = null; _infoShown = false;
            return;
        }
        if (now >= _nextUpdate)
        {
            _nextUpdate = now + 0.5;
            string title = r != null ? $"ORBIT  ·  {SystemHost.DisplayName(r.Body).ToUpperInvariant()}" : "MANEUVER";
            var rows = new List<HudPanel.Stat>();
            var lines = new List<string>();
            if (r != null)
            {
                // Each apsis with how long until you are there (the one you reach first is the one to act on).
                // (just there, or just past it: "now", not a whole revolution away)
                string In(double s) => double.IsNaN(s) || s < 0 ? null : s < 10 || (r.Period > 0 && r.Period - s < 10) ? "now" : $"in {Maneuvers.Clock(s)}";
                rows.Add(new HudPanel.Stat("ALT", HudPanel.Km(r.Alt), $"{r.Speed:N0} m/s"));
                rows.Add(r.Pe < 0 ? new HudPanel.Stat("PE", "impact", null, HudPanel.Warn) : new HudPanel.Stat("PE", HudPanel.Km(r.Pe), In(r.PeIn)));
                rows.Add(r.Escape ? new HudPanel.Stat("AP", "escape") : new HudPanel.Stat("AP", HudPanel.Km(r.Ap), In(r.ApIn)));
                if (!r.Escape)
                {
                    rows.Add(new HudPanel.Stat("PERIOD", Maneuvers.Clock(r.Period)));
                    rows.Add(new HudPanel.Stat("INC", $"{r.IncDeg:F1}°"));
                }
                if (!string.IsNullOrEmpty(r.Mode)) lines.Add(r.Mode);
            }
            // SHORT TAGS (2026-10-12, the user: the lines under the plot were "a full sentence instead of a helpful gui"): a
            // word and its numbers each - the next burn, the target's approach, reentry, the attitude hold; the mode, the
            // ring crossings and transmitting are left to the map
            double now2 = SystemHost.Now;
            if (burn != null) lines.Add($"BURN  {Maneuvers.BurnLeft:F1} m/s  ·  {(Maneuvers.BurnIn > 0.5 ? Maneuvers.Clock(Maneuvers.BurnIn) : "now")}");
            var ap = Maneuvers.TargetApproach(now2);
            if (Maneuvers.Target != null)
            {
                string nm = Targets.Name(Maneuvers.Target);
                if (nm != null && nm.Length > 14) nm = nm.Substring(0, 13) + "…";
                lines.Add(ap.Tracking ? $"◆ {nm}  ·  tracking" : ap.Here ? $"◆ {nm}  ·  here" : !ap.Ok ? $"◆ {nm}  ·  no approach"
                    : ap.Impact ? $"◆ {nm}  ·  impact {Maneuvers.Clock(ap.At - now2)}" : $"◆ {nm}  ·  {HudPanel.Km(ap.Body ? ap.Alt : ap.Dist)}  ·  {Maneuvers.Clock(ap.At - now2)}");
            }
            var pf = FrameHost.PlayerFrame;
            if (pf != null && EntryHost.Braking(pf.Id, out double decel)) lines.Add($"ENTRY  {decel / 9.81:F1} g  ·  heat {SEAerospace.Entry.Reentry.Share(EntryHost.HeatOf(pf.Id), false):P0}");
            else if (EntryHost.Prediction is var pp && pp != null && !double.IsNaN(pp.EnterTime))
                lines.Add($"ENTRY  {(pp.EnterTime - now2 <= 10 ? "now" : Maneuvers.Clock(pp.EnterTime - now2))}  ·  heat {SEAerospace.Entry.Reentry.Share(pp.PeakHeat, false):P0}");
            if (AttitudeHold.Current != AttitudeHold.Mode.Off) lines.Add($"HOLD  {AttitudeHold.Label(AttitudeHold.Current)}");
            _info = (title, rows, lines);
            _lastTitle = title; _lastContent = string.Join("\n", lines);
            CardWhy = "shown beside the plot";
        }
        // Pe / Ap tags on the drawn orbit (world HUD annotations, like the game's markers).
        if (r == null || MapView.Visible)
        {
            if (_info != null && FrameMarkers.BeginHud(session)) { try { DrawInfo(Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f)); } finally { MapPipeline.UiEnd(); } }
            return;
        }
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            // (the readout's own block only with no plot to carry it: on foot with the jetpack off, or the plot hidden)
            if (!(FrameHost.Seated || JetpackOn(session)) || EntryHidden) DrawInfo(Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f));
            else _infoShown = true;
            HudPanel.BeginLabels();
            HudPanel.ReserveGameHud();
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            // No orbit line in the world (first or third person): the orbit disc shows it,
            // and the markers show its directions.
            // In your suit with the jetpack off (walking, floating about): no disc, plot or markers.
            if (FrameHost.Seated || JetpackOn(session))
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
                    if (plot == SEAerospace.Frames.RendezvousPlot.Plot.Target || plot == SEAerospace.Frames.RendezvousPlot.Plot.Anchor) DrawRelative(r, u);
                    else DrawDisc(r, u);
                }
            }
        }
        finally { MapPipeline.UiEnd(); }
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
    /// The orbit disc, just left of the speedometer: your orbit seen from above its plane, turned so the way
    /// you look points up (heading-up, as a sat-nav). The body at its centre, your orbit, you with a
    /// prograde tick, Pe / Ap; a view wedge from you; and at its left edge how far above or below the
    /// orbit plane you are looking.
    /// </summary>
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
            double a = dt > 0 ? 1 - Math.Exp(-dt / Tau) : 1;
            _shown = Math.Exp(Math.Log(_shown) + (Math.Log(_target) - Math.Log(_shown)) * a);
            return _shown;
        }
        public void Reset() { _shown = _target = -1; _shrinkSince = 0; _at = 0; }
    }
    static readonly ScaleLatch _discScale = new(), _relScale = new(), _oopScale = new();
    static double _relCx, _relCy, _relCxT, _relCyT, _relCenterAt; static bool _relCenterSet; static string _relFor;

    static void DrawDisc(Readout r, float u)
    {
        if (r.RelPath == null || r.RelPath.Length < 2 || r.Nor.LengthSquared() < 0.5) return;
        var scr = MapPipeline.ScreenSize;
        float R = scr.Y * 0.085f;
        // Just left of the game's speed box: measured on screen at 0.409 screen-heights left of centre
        // (the game's HUD keeps its offsets in screen heights from the centre), level with its middle.
        var c = new Vector2(scr.X * 0.5f - scr.Y * 0.43f - R, scr.Y * 0.493f);
        Vector3D n = r.Nor;
        Vector3D fwd = (QuaternionD)MapPipeline.CameraOrientation * Vector3D.Forward;
        Vector3D up = fwd - n * Vector3D.Dot(fwd, n);
        if (up.LengthSquared() > 1e-4) _discUp = Vector3D.Normalize(up);   // looking along the normal: keep the last heading
        else if (_discUp.LengthSquared() < 0.5) _discUp = Vector3D.Normalize(r.Pro);
        Vector3D e1 = _discUp - n * Vector3D.Dot(_discUp, n);
        if (e1.LengthSquared() < 1e-6) return;
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
        if (ar > br + 1.5f * u) MapStyle.BoundaryCircle(c, ar, new ColorSRGB(0.55f, 0.85f, 1f, 0.45f), MapStyle.Thin(u), u);
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
        // Pe / Ap.
        void Apsis(Vector3D q, string label)
        {
            var s = P(q);
            MapPipeline.ScreenDisc(s, 2.2f * u, Orbit);
            // (out from the disc's centre past the apsis, aligned away from the orbit: above the point both labels sat on
            //  the orbit and on each other - 2026-10-12)
            var d = s - c; float dl = d.Length();
            var dir = dl > 1e-3f ? d / dl : new Vector2(0, -1);
            var sz = MapPipeline.MeasureText(label, 0.42f * u);
            var at = s + dir * 8f * u;
            float x = dir.X < -0.3f ? at.X - sz.X : dir.X > 0.3f ? at.X : at.X - sz.X * 0.5f;
            float y = dir.Y < -0.3f ? at.Y - sz.Y : dir.Y > 0.3f ? at.Y : at.Y - sz.Y * 0.5f;
            MapPipeline.ScreenText(new Vector2(x + 1.5f * u, y + 1.5f * u), label, new ColorSRGB(0f, 0f, 0f, 0.7f), 0.42f * u);
            MapPipeline.ScreenText(new Vector2(x, y), label, Orbit, 0.42f * u);
        }
        if (r.Pe > -r.BodyRadius) Apsis(r.PeRel, "Pe " + (r.Pe < 0 ? "impact" : HudPanel.Km(r.Pe) + ApsisIn(r, r.PeIn)));
        if (r.ApRel.HasValue && !r.Escape) Apsis(r.ApRel.Value, "Ap " + HudPanel.Km(r.Ap) + ApsisIn(r, r.ApIn));
        // The next patch event: where you leave the sphere, or enter a moon's.
        if (r.EventRel.HasValue)
        {
            var es = P(r.EventRel.Value);
            var ec = new ColorSRGB(0.85f, 0.55f, 1f, 1f);
            if (!MapPipeline.ScreenIcon("ring", es, 5f * u, ec)) MapPipeline.ScreenCircle(es, 4f * u, ec, 1.6f * u);
            MapPipeline.TextScreen(es + new Vector2(0, 10f * u), $"{r.EventLabel} · {Maneuvers.Clock(r.EventIn)}", ec, 0.4f * u);
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
            MapPipeline.TextScreen(rs + new Vector2(0, 10f * u), $"{r.RvLabel} · {HudPanel.Km(r.RvDist)} · {Maneuvers.Clock(r.RvIn)}", rc, 0.4f * u);
        }
        // Ring rocks you will meet: each marked as the rendezvous is, the next one named.
        if (r.Rocks != null)
        {
            var kc = Maneuvers.RockColor;
            for (int i = 0; i < r.Rocks.Count; i++)
            {
                var ks = P(r.Rocks[i].rel);
                MapPipeline.ScreenCircle(ks, 3.5f * u, kc, 1.4f * u);
                MapPipeline.ScreenDisc(ks, 1.2f * u, kc);
                if (i == 0) MapPipeline.TextScreen(ks + new Vector2(0, 10f * u), $"{r.Rocks[i].label} · {HudPanel.Km(r.Rocks[i].dist)} · {Maneuvers.Clock(r.Rocks[i].inT)}", kc, 0.4f * u);
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
        PlotCaptions(c, R, r, u, true);
    }

    /// <summary>An apsis' countdown for its label on the plot: " · now" just there, " · 2:13" until it.</summary>
    static string ApsisIn(Readout r, double s) => double.IsNaN(s) || s < 0 ? "" : s < 10 || (r.Period > 0 && r.Period - s < 10) ? " · now" : $" · {Maneuvers.Clock(s)}";

    /// <summary>
    /// THE ORBIT ON ITS PLOT (2026-10-11, the user: "the orbit info thing might be superseded by the plot - add info to the
    /// plot itself"): the apsides carry their altitudes and countdowns on the orbit (DrawDisc); over the panel the period
    /// and inclination; under it your altitude and speed, then what the old card also said - the mode, the next burn, the
    /// target, the ring, the hold, reentry, transmitting - one short line each, centred. Both plots (the disc and the
    /// relative one). The side block (DrawInfo) only when no plot is drawn.
    /// </summary>
    static void PlotCaptions(Vector2 c, float R, Readout r, float u, bool top)
    {
        var dim = new ColorSRGB(0.72f, 0.8f, 0.9f, 0.9f);
        void Centre(Vector2 at, string text, ColorSRGB col, float scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sz = MapPipeline.MeasureText(text, scale);
            MapPipeline.ScreenText(at - new Vector2(sz.X * 0.5f, 0), text, col, scale);
        }
        float hs = MapPipeline.MeasureText("0", 0.42f * u).Y;
        if (top && !r.Escape) Centre(c + new Vector2(0, -R - 6f * u - hs), $"{Maneuvers.Clock(r.Period)}  ·  {r.IncDeg:F1}°", dim, 0.42f * u);
        float y = c.Y + R + 6f * u;
        Centre(new Vector2(c.X, y), $"{HudPanel.Km(r.Alt)}  ·  {r.Speed:N0} m/s", new ColorSRGB(0.95f, 0.97f, 1f, 1f), 0.46f * u);
        y += MapPipeline.MeasureText("0", 0.46f * u).Y * 1.15f;
        if (_info != null)
            foreach (var l in _info.Value.lines) { Centre(new Vector2(c.X, y), l, dim, 0.4f * u); y += MapPipeline.MeasureText("0", 0.4f * u).Y * 1.15f; }
    }

    /// <summary>
    /// Riding another's frame (a station, an asteroid): your motion about its anchor, as a rendezvous
    /// plot in its curvilinear local frame. The anchor at the panel's top-left; NASA axes: behind (-V)
    /// to the right, toward the planet (+R) down. Your next revolution dashed, where you are now, and
    /// the frame's boundary (past it you split off on your own orbit).
    /// </summary>
    static void DrawRelative(Readout r, float u)
    {
        var scr = MapPipeline.ScreenSize;
        float R = scr.Y * 0.085f;
        var c = new Vector2(scr.X * 0.5f - scr.Y * 0.43f - R, scr.Y * 0.493f);
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
            MapPipeline.TextScreen(new Vector2(0.5f * (bx0 + bx1), by0 - 8f * u), HudPanel.Km(step), dim, 0.32f * u);
        }
        // axis hints at the axes' ends (NASA: behind is +V to the right, the planet down)
        if (origin.Y > a0.Y + 8f * u && origin.Y < a1.Y - 8f * u) MapPipeline.TextScreen(new Vector2(a1.X - 10f * u, origin.Y - 7f * u), "−V", dim, 0.32f * u);
        if (origin.X > a0.X + 8f * u && origin.X < a1.X - 8f * u) MapPipeline.TextScreen(new Vector2(origin.X + 9f * u, a1.Y - 20f * u), "+R", dim, 0.32f * u);
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
            MapPipeline.TextScreen(path[i] + new Vector2(0, -8f * u), "+" + Maneuvers.Clock(r.RelPeriod * qd / 4), HudPanel.Alpha(Orbit, 0.8f), 0.32f * u);
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
            MapPipeline.TextScreen(ex + new Vector2(0, 9f * u), "leaves frame", orange, 0.34f * u);
        }
        // ON THE PLOT, not in a line under it (the user, 2026-10-10): the range as a measured span from the anchor to you, the
        // closing / opening rate on your velocity arrow, the anchor's name by its square, the closest approach marked on the path.
        double rng = Math.Sqrt(r.RelNow.along * r.RelNow.along + r.RelNow.radial * r.RelNow.radial);
        double rdot = rng > 1 ? (r.RelNow.along * r.RelVel.along + r.RelNow.radial * r.RelVel.radial) / rng : 0;
        double vrel = Math.Sqrt(r.RelVel.along * r.RelVel.along + r.RelVel.radial * r.RelVel.radial);
        var me = P(r.RelNow);
        // the range span: anchor to you, end ticks, its length at the middle
        if (In(me) && In(origin) && (me - origin).Length() > 18f * u)
        {
            var d = Vector2.Normalize(me - origin); var nrm = new Vector2(-d.Y, d.X);
            var sc = HudPanel.Alpha(white, 0.45f);
            MapPipeline.ScreenLine(origin + d * 6f * u, me - d * 6f * u, sc, 1f * u);
            MapPipeline.ScreenLine(origin + d * 6f * u - nrm * 3f * u, origin + d * 6f * u + nrm * 3f * u, sc, 1f * u);
            MapPipeline.ScreenLine(me - d * 6f * u - nrm * 3f * u, me - d * 6f * u + nrm * 3f * u, sc, 1f * u);
            var mid0 = 0.5f * (origin + me) + nrm * 8f * u;
            MapPipeline.TextScreen(mid0, HudPanel.Km(rng), HudPanel.Alpha(white, 0.8f), 0.34f * u);
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
                double tca = r.RelPeriod * ci / Math.Max(1, r.RelSamples - 1);
                MapPipeline.TextScreen(cs + new Vector2(0, 11f * u), $"closest {HudPanel.Km(Math.Sqrt(cmin))} · +{Maneuvers.Clock(tca)}", HudPanel.Alpha(Orbit, 0.9f), 0.32f * u);
            }
        }
        // The anchor (a square, its name by it) and you (a dot, your relative velocity with its rate on it).
        MapPipeline.ScreenFill(new List<Vector2> { origin + new Vector2(-3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, 3.5f * u), origin + new Vector2(-3.5f * u, 3.5f * u) }, orange);
        if (In(origin) && !string.IsNullOrEmpty(r.AnchorName)) MapPipeline.TextScreen(origin + new Vector2(0, -10f * u), r.AnchorName, orange, 0.34f * u);
        var vr = new Vector2((float)-r.RelVel.along, (float)-r.RelVel.radial);
        if (vr.LengthSquared() > 1e-6f)
        {
            var tip = me + Vector2.Normalize(vr) * 14f * u;
            MapPipeline.ScreenLine(me, tip, white, 1.6f * u);
            MapPipeline.TextScreen(tip + Vector2.Normalize(vr) * 10f * u, r.Holding ? "holding" : $"{(rdot < 0 ? "closing" : "opening")} {Math.Abs(rdot):0.0} m/s", white, 0.32f * u);
        }
        else if (r.Holding) MapPipeline.TextScreen(me + new Vector2(0, -10f * u), "holding", white, 0.32f * u);
        MapPipeline.ScreenDisc(me, 3.4f * u, white);
        _ = vrel;
        // Out of plane: a strip under the plot (same along-track axis), only when there is real motion out of it.
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
                MapPipeline.TextScreen(new Vector2(c.X + R - 12f * u, top + 8f * u), "+N", dim, 0.34f * u);
                {
                    // the out-of-plane reach as a measured span at the left edge: from the plane to the furthest point
                    float sx = a0.X + 6f * u, sy = midY - (float)(oop * kc);
                    MapPipeline.ScreenLine(new Vector2(sx, midY), new Vector2(sx, sy), dim, 1f * u);
                    MapPipeline.ScreenLine(new Vector2(sx - 3f * u, sy), new Vector2(sx + 3f * u, sy), dim, 1f * u);
                    MapPipeline.TextScreen(new Vector2(sx + 22f * u, 0.5f * (midY + sy)), "±" + HudPanel.Km(oop), dim, 0.32f * u);
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
        // (the readout line that was here - name, range, closing rate, relative speed - is on the plot now, above)
        PlotCaptions(c, R, r, u, false);   // (under it: your altitude and speed, the burn, the target...; its header is its own)
    }

    /// <summary>Your suit's jetpack is on (unknown: treated as on).</summary>
    static bool JetpackOn(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var st = ch?.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.CharacterStatusProviderComponent>();
            return st == null || st.IsJetpackEnabled;
        }
        catch { return true; }
    }

    static bool IsFinite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

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

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
