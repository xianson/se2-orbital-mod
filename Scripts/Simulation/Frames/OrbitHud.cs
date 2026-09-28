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

    /// <summary>A small labelled tag at a screen point: diamond, text on a dark plate.</summary>
    public static void TagAt(Vector2 s, string text, ColorSRGB c, float u, bool diamond = true)
    {
        float r = 5f * u;
        if (!diamond) { LabelAt(s + new Vector2(9f * u, 0), text, c, u); return; }
        MapPipeline.ScreenLine(s + new Vector2(0, -r), s + new Vector2(r, 0), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(r, 0), s + new Vector2(0, r), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(0, r), s + new Vector2(-r, 0), c, 1.8f * u);
        MapPipeline.ScreenLine(s + new Vector2(-r, 0), s + new Vector2(0, -r), c, 1.8f * u);
        LabelAt(s + new Vector2(9f * u, 0), text, c, u);
    }

    private static readonly List<(Vector2 min, Vector2 max)> _placed = new List<(Vector2, Vector2)>();
    /// <summary>Start of a frame's labels: later labels step down until clear of earlier ones.</summary>
    public static void BeginLabels() => _placed.Clear();

    /// <summary>
    /// Text on a dark plate beside its point: right of it, else left, above or below, whichever is clear
    /// of the labels already placed (tags and the map's own names). When none is, it stays on the right:
    /// a label that wanders off (it used to step down until clear) names the wrong place.
    /// </summary>
    public static void LabelAt(Vector2 p, string text, ColorSRGB c, float u)
    {
        var ts = MapPipeline.MeasureText(text, 0.5f * u);
        var pad = new Vector2(4f * u, 3f * u);
        Vector2 anchor = p - new Vector2(9f * u, 0);   // the point being named (callers pass it +9 px)
        var cands = new[]
        {
            p - new Vector2(0, ts.Y * 0.5f),                                          // right
            anchor - new Vector2(ts.X + 9f * u, ts.Y * 0.5f),                         // left
            anchor + new Vector2(-ts.X * 0.5f, -ts.Y - 9f * u),                       // above
            anchor + new Vector2(-ts.X * 0.5f, 9f * u),                               // below
        };
        Vector2 at = cands[0];
        foreach (var cand in cands)
        {
            Vector2 mn = cand - pad, mx = cand + ts + pad;
            bool hit = false;
            foreach (var b in _placed)
                if (mn.X < b.max.X && mx.X > b.min.X && mn.Y < b.max.Y && mx.Y > b.min.Y) { hit = true; break; }
            if (!hit && MapPipeline.Free(mn, mx)) { at = cand; break; }
        }
        _placed.Add((at - pad, at + ts + pad));
        MapPipeline.Reserve(at - pad, at + ts + pad);
        MapPipeline.ScreenRect(at - new Vector2(4f * u, 1f * u), at + ts + new Vector2(4f * u, 1f * u), new ColorSRGB(0.02f, 0.045f, 0.07f, 0.65f));
        MapPipeline.ScreenText(at, text, c, 0.5f * u);
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
    }

    public static Readout Current;
    static readonly ColorSRGB Orbit = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);   // your orbit: cyan, as on the map

    private static GameUi.Card _card;
    private static double _nextUpdate, _nextCheck;

    /// <summary>
    /// The readout lives in the game's own notification card (updated in place, twice a second at most),
    /// and the apsides are tagged on the drawn orbit.
    /// </summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        var r = Current;
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
            string title = r != null ? $"Orbit  ·  {r.Body}" : "Maneuver";
            var sb = new System.Text.StringBuilder();
            if (r != null)
            {
                sb.Append($"Altitude {HudPanel.Km(r.Alt)}   ·   {r.Speed:N0} m/s\n");
                sb.Append($"Periapsis {(r.Pe < 0 ? "impact" : HudPanel.Km(r.Pe))}   ·   Apoapsis {(r.Escape ? "escape" : HudPanel.Km(r.Ap))}\n");
                if (!r.Escape) sb.Append($"Period {Maneuvers.Clock(r.Period)}   ·   Inclination {r.IncDeg:F1}°\n");
                sb.Append(r.Mode);
            }
            if (burn != null) sb.Append((sb.Length > 0 ? "\n" : "") + burn);
            string content = sb.ToString();
            if (_card == null || (now >= _nextCheck && !GameUi.IsOpen(_card)))
            {
                _nextCheck = now + 2;
                if (_card != null) GameUi.CloseCard(_card);
                _card = GameUi.ShowCard(session, title, content);
            }
            else GameUi.UpdateCard(_card, title, content);
        }
        // Pe / Ap tags on the drawn orbit (world HUD annotations, like the game's markers).
        if (r == null || MapView.Visible || OrbitalMap.Active) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            HudPanel.BeginLabels();
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            if (r.PeWorld.HasValue) HudPanel.Tag(r.PeWorld.Value, "Pe " + HudPanel.Km(r.Pe), Orbit, u);
            if (r.ApWorld.HasValue && !r.Escape) HudPanel.Tag(r.ApWorld.Value, "Ap " + HudPanel.Km(r.Ap), Orbit, u);
        }
        finally { MapPipeline.UiEnd(); }
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
