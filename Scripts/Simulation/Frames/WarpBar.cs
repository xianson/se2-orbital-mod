using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The time-warp gauge (KSP's top-left arrows): one arrow per warp level, lit up to the current one,
/// the level (x100), the game clock, and why warp is held (not on rails; stopping for a burn). Always
/// on screen: in flight at the top centre, in the map in its open area above the hint line. In the
/// map (where there is a cursor) the arrows are buttons: click one to warp at that level. The keys
/// ('.' ',' '/') work everywhere as before.
/// </summary>
public static class WarpBar
{
    static readonly ColorSRGB Fill = new ColorSRGB(0.02f, 0.05f, 0.07f, 0.72f);
    static readonly ColorSRGB On = new ColorSRGB(0.35f, 0.90f, 0.55f, 1f);
    static readonly ColorSRGB Off = new ColorSRGB(0.55f, 0.62f, 0.70f, 0.55f);
    static readonly ColorSRGB Hot = new ColorSRGB(0.96f, 0.62f, 0.18f, 1f);
    static readonly ColorSRGB TextC = new ColorSRGB(0.94f, 0.97f, 1.00f, 1f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.70f, 0.78f, 0.88f, 0.9f);

    /// <summary>In flight: its own HUD batch (the map draws it inside the map's batch).</summary>
    public static void DrawHud(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (MapView.Visible || !SystemHost.Built) return;
        if (FrameHost.PlayerFrame == null) return;   // on a planet (not on rails): no warp to show
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            var scr = MapPipeline.ScreenSize;
            Draw(new Vector2(scr.X * 0.5f, scr.Y * 0.02f), centred: true, mouse: null, click: false);
        }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>In the map (inside its batch): clickable.</summary>
    public static void DrawMap(Vector2 mouse)
    {
        if (FrameHost.PlayerFrame == null) return;   // on a planet (not on rails): no warp to show
        var scr = MapPipeline.ScreenSize;
        Draw(new Vector2(scr.X * 0.265f, scr.Y * 0.85f), centred: false, mouse: mouse,
             click: MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open);
    }

    static void Draw(Vector2 at, bool centred, Vector2? mouse, bool click)
    {
        var scr = MapPipeline.ScreenSize;
        float u = Math.Max(1f, scr.Y / 1080f);
        int n = WarpControl.Levels.Length;
        int cur = 0;
        for (int k = 0; k < n; k++) if (SystemHost.Timescale >= WarpControl.Levels[k] - 1e-6) cur = k;
        bool rails = FrameHost.PlayerFrame != null;

        float aw = 16f * u, ah = 20f * u, gap = 5f * u, pad = 10f * u;
        string level = $"×{WarpControl.Levels[cur]:N0}";
        string clock = Clock(SystemHost.Now);
        string why = !rails ? "on rails only" : null;
        double burn = Maneuvers.NextBurnStart(SystemHost.Now);
        if (why == null && !MapView.Visible && !double.IsNaN(burn) && burn > SystemHost.Now) why = "burn in " + Maneuvers.Clock(burn - SystemHost.Now);
        var lsz = MapPipeline.MeasureText(level, 1.0f);
        var csz = MapPipeline.MeasureText(clock, 0.85f);
        var wsz = why != null ? MapPipeline.MeasureText(why, 0.72f) : Vector2.Zero;
        float arrowsW = n * aw + (n - 1) * gap;
        float w = pad + arrowsW + pad + Math.Max(lsz.X, 60f * u) + pad + Math.Max(csz.X, wsz.X) + pad;
        float h = Math.Max(ah, Math.Max(lsz.Y, csz.Y + wsz.Y)) + 2 * pad * 0.75f;
        Vector2 min = centred ? new Vector2(at.X - w / 2, at.Y) : at;
        MapPipeline.ScreenRect(min, min + new Vector2(w, h), Fill);
        if (!centred) MapPipeline.Reserve(min, min + new Vector2(w, h));

        // The arrows: filled up to the current level; the one under the mouse (map) outlined in orange.
        float ax = min.X + pad, ay = min.Y + (h - ah) / 2;
        for (int k = 0; k < n; k++)
        {
            var a0 = new Vector2(ax + k * (aw + gap), ay);
            bool lit = k <= cur && (k == 0 || rails);
            bool hot = mouse.HasValue && new BoundingBox2(a0, a0 + new Vector2(aw, ah)).Contains(mouse.Value) == ContainmentType.Contains;
            Arrow(a0, aw, ah, lit ? On : Off, hot ? Hot : (lit ? On : Off), lit, u);
            if (hot && click) WarpControl.SetLevel(k);
        }
        float tx = ax + arrowsW + pad;
        MapPipeline.ScreenText(new Vector2(tx, min.Y + (h - lsz.Y) / 2), level, cur > 0 ? On : TextC, 1.0f);
        tx += Math.Max(lsz.X, 60f * u) + pad;
        float ty = min.Y + (h - (csz.Y + wsz.Y)) / 2;
        MapPipeline.ScreenText(new Vector2(tx, ty), clock, TextC, 0.85f);
        if (why != null) MapPipeline.ScreenText(new Vector2(tx, ty + csz.Y), why, Dim, 0.72f);
    }

    /// <summary>A right-pointing triangle, filled with horizontal strokes when lit.</summary>
    static void Arrow(Vector2 a0, float w, float h, ColorSRGB fill, ColorSRGB edge, bool filled, float u)
    {
        if (MapPipeline.ScreenIconBox(filled ? "tri" : "trio", a0, a0 + new Vector2(w, h), filled ? fill : edge)) return;
        Vector2 p0 = a0, p1 = a0 + new Vector2(0, h), p2 = a0 + new Vector2(w, h / 2);
        if (filled)
            for (float y = 1.5f * u; y < h - 1f * u; y += 1.5f * u)
            {
                float f = 1 - Math.Abs(y - h / 2) / (h / 2);
                MapPipeline.ScreenLine(a0 + new Vector2(0, y), a0 + new Vector2(w * f, y), fill, 1.6f * u);
            }
        MapPipeline.ScreenLine(p0, p1, edge, 1.4f * u);
        MapPipeline.ScreenLine(p1, p2, edge, 1.4f * u);
        MapPipeline.ScreenLine(p2, p0, edge, 1.4f * u);
    }

    /// <summary>The game clock as day and time (day 1 at the start).</summary>
    static string Clock(double t)
    {
        if (!(t >= 0)) return "";
        var ts = TimeSpan.FromSeconds(t);
        return $"Day {(int)ts.TotalDays + 1}  {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
