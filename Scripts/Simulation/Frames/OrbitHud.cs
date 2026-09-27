using Keen.VRage.Core;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>A HUD panel in the game's UI font: dark box, accent rule, title, label / value rows.</summary>
public static class HudPanel
{
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
        var max = min + size;
        bool hot = enabled && mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
        MapPipeline.ScreenRect(min, max, hot ? new ColorSRGB(0.06f, 0.11f, 0.16f, 0.92f) : Bg);
        MapPipeline.ScreenLine(new Vector2(min.X, max.Y), max, hot ? Accent : new ColorSRGB(Accent.R, Accent.G, Accent.B, 0.45f), 2f * u);
        var ts = MapPipeline.MeasureText(text, 0.55f * u);
        MapPipeline.ScreenText(min + (size - ts) * 0.5f, text, enabled ? Title : Label, 0.55f * u);
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

    /// <summary>Text on a dark plate, vertically centred on the point (moved down clear of earlier labels).</summary>
    public static void LabelAt(Vector2 p, string text, ColorSRGB c, float u)
    {
        var ts = MapPipeline.MeasureText(text, 0.5f * u);
        var at = p - new Vector2(0, ts.Y * 0.5f);
        for (int tries = 0; tries < 8; tries++)
        {
            bool hit = false;
            foreach (var b in _placed)
                if (at.X < b.max.X && at.X + ts.X > b.min.X && at.Y < b.max.Y && at.Y + ts.Y > b.min.Y) { hit = true; break; }
            if (!hit) break;
            at.Y += ts.Y * 1.35f + 4f * u;
        }
        _placed.Add((at - new Vector2(4f * u, 3f * u), at + ts * new Vector2(1f, 1.25f) + new Vector2(4f * u, 3f * u)));
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
    static readonly ColorSRGB Orbit = new ColorSRGB(1f, 0.84f, 0.25f, 1f);

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        var r = Current;
        if (MapView.Visible || OrbitalMap.Active || !OrbitalConfig.ShowOrbit) return;
        if (r == null && WarpControl.Notice == null) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            HudPanel.BeginLabels();
            var sz = MapPipeline.ScreenSize;
            float u = Math.Max(1f, sz.Y / 1080f);
            if (r == null)
            {
                // Just the notice (no orbit to show).
                HudPanel.Draw(new Vector2(sz.X - 340f * u - 24f * u, sz.Y * 0.30f), 340f * u, "WARP", null, new List<HudPanel.Row>(), WarpControl.Notice, u);
                return;
            }
            var rows = new List<HudPanel.Row>
            {
                new HudPanel.Row("Altitude", HudPanel.Km(r.Alt)),
                new HudPanel.Row("Speed", $"{r.Speed:N0} m/s"),
                new HudPanel.Row("Periapsis", r.Pe < 0 ? "impact" : HudPanel.Km(r.Pe), r.Pe < 0 ? HudPanel.Warn : (ColorSRGB?)null),
                new HudPanel.Row("Apoapsis", r.Escape ? "escape" : HudPanel.Km(r.Ap)),
            };
            if (!r.Escape) rows.Add(new HudPanel.Row("Period", Maneuvers.Clock(r.Period)));
            rows.Add(new HudPanel.Row("Inclination", $"{r.IncDeg:F1}°"));
            HudPanel.Draw(new Vector2(sz.X - 300f * u - 24f * u, sz.Y * 0.30f), 300f * u, "ORBIT", r.Body, rows, WarpControl.Notice ?? r.Mode, u);
            if (r.PeWorld.HasValue) HudPanel.Tag(r.PeWorld.Value, "Pe " + HudPanel.Km(r.Pe), Orbit, u);
            if (r.ApWorld.HasValue && !r.Escape) HudPanel.Tag(r.ApWorld.Value, "Ap " + HudPanel.Km(r.Ap), Orbit, u);
        }
        finally { MapPipeline.UiEnd(); }
    }
}
