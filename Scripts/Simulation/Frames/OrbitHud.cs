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
    public sealed class Readout
    {
        public string Body, Mode;
        public double Alt, Speed, Pe, Ap, Period, IncDeg;
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
    }

    public static Readout Current;

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
        bool show = OrbitalConfig.ShowOrbit && (r != null || burn != null) && !MapView.Visible;
        if (!show)
        {
            if (_card != null) { GameUi.CloseCard(_card); _card = null; }
            return;
        }
        if (now >= _nextUpdate)
        {
            _nextUpdate = now + 0.5;
            string title = r != null ? $"Orbit  ·  {SystemHost.DisplayName(r.Body)}" : "Maneuver";
            var sb = new System.Text.StringBuilder();
            if (r != null)
            {
                sb.Append($"Altitude {HudPanel.Km(r.Alt)}   ·   {r.Speed:N0} m/s\n");
                sb.Append($"Periapsis {(r.Pe < 0 ? "impact" : HudPanel.Km(r.Pe))}   ·   Apoapsis {(r.Escape ? "escape" : HudPanel.Km(r.Ap))}\n");
                if (!r.Escape) sb.Append($"Period {Maneuvers.Clock(r.Period)}   ·   Inclination {r.IncDeg:F1}°\n");
                if (!string.IsNullOrEmpty(r.Mode)) sb.Append(r.Mode);
            }
            if (burn != null) sb.Append((sb.Length > 0 ? "\n" : "") + burn);
            string tgt = Maneuvers.TargetLine(SystemHost.Now);
            if (tgt != null) sb.Append((sb.Length > 0 ? "\n" : "") + tgt);
            string content = sb.ToString().TrimEnd('\n').Replace("\n\n", "\n");   // no blank lines
            if (_card == null || (now >= _nextCheck && !GameUi.IsOpen(_card)))
            {
                _nextCheck = now + 2;
                if (_card != null) GameUi.CloseCard(_card);
                _card = GameUi.ShowCard(session, title, content);
            }
            else GameUi.UpdateCard(_card, title, content);
        }
        // Pe / Ap tags on the drawn orbit (world HUD annotations, like the game's markers).
        if (r == null || MapView.Visible) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            HudPanel.BeginLabels();
            HudPanel.ReserveGameHud();
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            // No orbit line in the world (first or third person): the orbit disc shows it,
            // and the markers show its directions.
            DrawMarkers(r, u);
            DrawDisc(r, u);
        }
        finally { MapPipeline.UiEnd(); }
    }

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
    /// The orbit disc, mid-height left of centre: your orbit seen from above its plane, turned so the way
    /// you look points up (heading-up, as a sat-nav). The body at its centre, your orbit, you with a
    /// prograde tick, Pe / Ap; a view wedge from you; and at its left edge how far above or below the
    /// orbit plane you are looking.
    /// </summary>
    static void DrawDisc(Readout r, float u)
    {
        if (r.RelPath == null || r.RelPath.Length < 2 || r.Nor.LengthSquared() < 0.5) return;
        var scr = MapPipeline.ScreenSize;
        float R = scr.Y * 0.085f;
        var c = new Vector2(scr.X * 0.30f, scr.Y * 0.5f);   // mid-height, left of centre (inside the game's left bracket)
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
        double k = (R * 0.86) / Math.Max(1.0, far);
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
        // Your orbit.
        var path = new List<Vector2>(r.RelPath.Length);
        foreach (var q in r.RelPath) if (IsFinite(q)) path.Add(P(q));
        MapPipeline.ScreenPath(path, r.PathClosed, Orbit, 1.6f * u);
        // Pe / Ap.
        void Apsis(Vector3D q, string label)
        {
            var s = P(q);
            MapPipeline.ScreenDisc(s, 2.2f * u, Orbit);
            MapPipeline.TextScreen(s + new Vector2(0, -9f * u), label, Orbit, 0.42f);
        }
        if (r.Pe > -r.BodyRadius) Apsis(r.PeRel, "Pe " + (r.Pe < 0 ? "impact" : HudPanel.Km(r.Pe)));
        if (r.ApRel.HasValue && !r.Escape) Apsis(r.ApRel.Value, "Ap " + HudPanel.Km(r.Ap));
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
