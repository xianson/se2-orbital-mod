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
            // In your suit with the jetpack off (walking, floating about): no disc, plot or markers.
            if (FrameHost.Seated || JetpackOn(session))
            {
                DrawMarkers(r, u);
                if (r.Relative != null) DrawRelative(r, u);   // riding another's frame: your motion about its anchor
                else DrawDisc(r, u);
            }
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
    /// The orbit disc, just left of the speedometer: your orbit seen from above its plane, turned so the way
    /// you look points up (heading-up, as a sat-nav). The body at its centre, your orbit, you with a
    /// prograde tick, Pe / Ap; a view wedge from you; and at its left edge how far above or below the
    /// orbit plane you are looking.
    /// </summary>
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
        bool showSoi = r.SoiRadius > 0 && (r.Escape || (r.EventRel.HasValue && !r.MoonRel.HasValue) || far > 0.5 * r.SoiRadius);
        if (showSoi) far = Math.Max(far, r.SoiRadius);
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
        // The next patch event: where you leave the sphere, or enter a moon's.
        if (r.EventRel.HasValue)
        {
            var es = P(r.EventRel.Value);
            var ec = new ColorSRGB(0.85f, 0.55f, 1f, 1f);
            if (!MapPipeline.ScreenIcon("ring", es, 5f * u, ec)) MapPipeline.ScreenCircle(es, 4f * u, ec, 1.6f * u);
            MapPipeline.TextScreen(es + new Vector2(0, 10f * u), $"{r.EventLabel} · {Maneuvers.Clock(r.EventIn)}", ec, 0.4f);
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
        double ext = Math.Max(500, Math.Max(maxX - minX, maxY - minY)) * 1.08;
        float span = Math.Min(a1.X - a0.X, a1.Y - a0.Y);
        double k = span / ext;
        double cx = 0.5 * (minX + maxX), cy = 0.5 * (minY + maxY);
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
        // Grid at a clean step (1, 2, 5 x 10^n m), labelled in km along the top (V bar) and left (R bar).
        double raw = ext / 4, mag = Math.Pow(10, Math.Floor(Math.Log10(raw))), step = mag * (raw / mag < 1.5 ? 1 : raw / mag < 3.5 ? 2 : 5);
        string Lab(double m) => Math.Abs(m) < 1 ? "0" : Math.Abs(m) >= 1000 ? $"{m / 1000:0.#}" : $"{m / 1000:0.##}";
        var origin = P((0, 0));
        for (int i = -40; i <= 40; i++)
        {
            float gx = origin.X + (float)(i * step * k), gy = origin.Y + (float)(i * step * k);
            if (gx >= a0.X && gx <= a1.X)
            {
                MapPipeline.ScreenLine(new Vector2(gx, a0.Y), new Vector2(gx, a1.Y), i == 0 ? dim : faint, 1f * u);
                if (i % 2 == 0) MapPipeline.TextScreen(new Vector2(gx, lo.Y + 7f * u), Lab(i * step), dim, 0.34f);   // behind: +
            }
            if (gy >= a0.Y && gy <= a1.Y)
            {
                MapPipeline.ScreenLine(new Vector2(a0.X, gy), new Vector2(a1.X, gy), i == 0 ? dim : faint, 1f * u);
                if (i % 2 == 0) MapPipeline.TextScreen(new Vector2(lo.X + 7f * u, gy), Lab(i * step), dim, 0.34f);   // below: +
            }
        }
        MapPipeline.TextScreen(new Vector2(a1.X - 16f * u, a1.Y - 8f * u), "-V km", dim, 0.34f);
        MapPipeline.TextScreen(new Vector2(a0.X + 16f * u, a1.Y - 8f * u), "+R", dim, 0.34f);
        // The frame's boundary round the anchor (past it you leave on your own orbit).
        {
            var arc = new List<Vector2>();
            double rb = ServerFrames.CaptureRadius;
            for (int i = 0; i <= 96; i++)
            {
                double a = 2 * Math.PI * i / 96;
                var s = P((rb * Math.Cos(a), rb * Math.Sin(a)));
                if (In(s)) arc.Add(s); else { MapStyle.Boundary(arc, false, HudPanel.Alpha(orange, 0.7f), MapStyle.Thin(u), u); arc.Clear(); }
            }
            MapStyle.Boundary(arc, false, HudPanel.Alpha(orange, 0.7f), MapStyle.Thin(u), u);
        }
        // Your next revolution: dashed, with quarter-revolution ticks (time from now) and its direction.
        var path = new List<Vector2>(r.Relative.Count);
        foreach (var q in r.Relative) path.Add(P(q));
        MapStyle.Plan(path, false, Orbit, 1.5f * u, u);
        int n = path.Count - 1;
        for (int qd = 1; qd <= 3 && n >= 4; qd++)
        {
            int i = n * qd / 4;
            MapPipeline.ScreenDisc(path[i], 2.2f * u, Orbit);
            MapPipeline.TextScreen(path[i] + new Vector2(0, -8f * u), "+" + Maneuvers.Clock(r.RelPeriod * qd / 4), HudPanel.Alpha(Orbit, 0.8f), 0.32f);
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
        // The anchor (a square) and you (a dot with your relative velocity).
        MapPipeline.ScreenFill(new List<Vector2> { origin + new Vector2(-3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, -3.5f * u), origin + new Vector2(3.5f * u, 3.5f * u), origin + new Vector2(-3.5f * u, 3.5f * u) }, orange);
        var me = P(r.RelNow);
        var vr = new Vector2((float)-r.RelVel.along, (float)-r.RelVel.radial);
        if (vr.LengthSquared() > 1e-6f) MapPipeline.ScreenLine(me, me + Vector2.Normalize(vr) * 12f * u, white, 1.6f * u);
        MapPipeline.ScreenDisc(me, 3.4f * u, white);
        // Readout: range, range rate, relative speed.
        double rng = Math.Sqrt(r.RelNow.along * r.RelNow.along + r.RelNow.radial * r.RelNow.radial);
        double rdot = rng > 1 ? (r.RelNow.along * r.RelVel.along + r.RelNow.radial * r.RelVel.radial) / rng : 0;
        double vrel = Math.Sqrt(r.RelVel.along * r.RelVel.along + r.RelVel.radial * r.RelVel.radial);
        MapPipeline.TextScreen(new Vector2(c.X, c.Y + R + 11f * u), r.Holding
            ? $"{r.AnchorName}   range {HudPanel.Km(rng)}   holding (dampeners)"
            : $"{r.AnchorName}   range {HudPanel.Km(rng)}   {(rdot < 0 ? "closing" : "opening")} {Math.Abs(rdot):0.0} m/s   rel {vrel:0.0} m/s", Orbit, 0.4f);
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
