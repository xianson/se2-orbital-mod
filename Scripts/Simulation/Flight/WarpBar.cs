using System;
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
    // (the game's own HUD look - its cockpit panel and toggle buttons, as BuildHints' boxes: the dark fill, a thin pale
    //  outline bevelled at a corner, white text with a shadow, white and grey marks; 2026-10-13, the user: "make the warp box
    //  look more like the other gui")
    static readonly ColorSRGB Fill = new ColorSRGB(0f, 12f / 255f, 16f / 255f, 0.70f);
    static readonly ColorSRGB Edge = new ColorSRGB(0.62f, 0.72f, 0.76f, 1f);
    static readonly ColorSRGB Shadow = new ColorSRGB(0f, 0f, 0f, 0.85f);
    static readonly ColorSRGB On = new ColorSRGB(1f, 1f, 1f, 1f);
    static readonly ColorSRGB Off = new ColorSRGB(0.62f, 0.72f, 0.76f, 0.6f);
    static readonly ColorSRGB Hot = new ColorSRGB(0.96f, 0.62f, 0.18f, 1f);
    static readonly ColorSRGB TextC = new ColorSRGB(0.94f, 0.97f, 1.00f, 1f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.70f, 0.78f, 0.88f, 0.9f);

    /// <summary>In flight, at x1 longer than IdleSeconds it fades out over FadeSeconds (2026-10-12, the user: "don't show warp
    /// if at 1x for more than 5 seconds, fade it out"); any warp brings it back at once. The map's is always shown.</summary>
    public const double IdleSeconds = 5, FadeSeconds = 1;
    static double _idleSince = -1;
    static float _alpha = 1f;

    /// <summary>In flight: its own HUD batch (the map draws it inside the map's batch).</summary>
    public static void DrawHud(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (MapView.Visible || !SystemHost.Built) return;
        if (FrameHost.PlayerFrame == null) return;   // on a planet (not on rails): no warp to show
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (SystemHost.Timescale > 1.0 + 1e-6) _idleSince = -1;
        else if (_idleSince < 0) _idleSince = wall;
        double idle = _idleSince < 0 ? 0 : wall - _idleSince;
        _alpha = idle <= IdleSeconds ? 1f : (float)Math.Max(0, 1 - (idle - IdleSeconds) / FadeSeconds);
        if (_alpha <= 0f) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            var scr = MapPipeline.ScreenSize;
            // (2026-10-12, the user: "put the warp bar above those buttons instead of center screen": right-aligned on the
            //  toggle row at the bottom right - seated, the cockpit's; on foot, the character HUD's, in the same place)
            float u = MapPipeline.UiScale(scr.Y);
            Draw(AlignButton.RowTopRight - new Vector2(0f, 10f * u), centred: false, mouse: null, click: false, aboveLeft: true);
        }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>In the map (inside its batch): clickable.</summary>
    public static void DrawMap(Vector2 mouse)
    {
        _alpha = 1f;
        if (FrameHost.PlayerFrame == null) return;   // on a planet (not on rails): no warp to show
        var scr = MapPipeline.ScreenSize;
        Draw(MapLayout.P(0.265f, 0.82f), centred: false, mouse: mouse,   // (above the hints' two lines)
             click: MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open);
    }

    static void Draw(Vector2 at, bool centred, Vector2? mouse, bool click, bool aboveLeft = false)
    {
        var scr = MapPipeline.ScreenSize;
        float u = MapPipeline.UiScale(scr.Y);
        int n = WarpControl.TopLevel + 1;   // (up to the world's maximum, Warp.MaxLevel)
        int cur = 0;
        for (int k = 0; k < n; k++) if (SystemHost.Timescale >= WarpControl.Levels[k] - 1e-6) cur = k;
        bool rails = FrameHost.PlayerFrame != null;

        float aw = 9f * u, ah = 11f * u, gap = 3f * u, pad = 6f * u;
        string level = $"×{WarpControl.Levels[cur]:N0}";
        string clock = Clock(SystemHost.Now);
        string why = !rails ? "on rails only" : null;
        double burn = Maneuvers.NextBurnStart(SystemHost.Now);
        if (why == null && !MapView.Visible && !double.IsNaN(burn) && burn > SystemHost.Now) why = "burn in " + Maneuvers.Clock(burn - SystemHost.Now);
        // (text at the HUD's sizes - BuildHints' boxes 0.42 u: at 1.0 u it was 2.4x the rest of the HUD, 2026-10-13, the user:
        //  "warp is gigantic for no good reason")
        float tl = 0.48f * u, tc = 0.40f * u, tw = 0.34f * u;
        var lsz = MapPipeline.MeasureText(level, tl);
        var csz = MapPipeline.MeasureText(clock, tc);
        var wsz = why != null ? MapPipeline.MeasureText(why, tw) : Vector2.Zero;
        float arrowsW = n * aw + (n - 1) * gap;
        float w = pad + arrowsW + pad + Math.Max(lsz.X, 30f * u) + pad + Math.Max(csz.X, wsz.X) + pad;
        float h = Math.Max(ah, Math.Max(lsz.Y, csz.Y + wsz.Y)) + 2 * pad * 0.75f;
        Vector2 min = centred ? new Vector2(at.X - w / 2, at.Y) : aboveLeft ? new Vector2(at.X - w, at.Y - h) : at;   // (aboveLeft: at is its bottom-right)
        {
            // the outline bevelled on its top-left and bottom-right corners (the game's panel and buttons)
            float bev = 5f * u, x0 = min.X, y0 = min.Y, x1 = min.X + w, y1 = min.Y + h;
            var pts = new List<Vector2> { new(x0 + bev, y0), new(x1, y0), new(x1, y1 - bev), new(x1 - bev, y1), new(x0, y1), new(x0, y0 + bev) };
            MapPipeline.ScreenFill(pts, A(Fill));
            for (int k = 0; k < pts.Count; k++) MapPipeline.ScreenLine(pts[k], pts[(k + 1) % pts.Count], A(Edge), 1f * u);
        }
        if (!centred) MapPipeline.Reserve(min, min + new Vector2(w, h));

        // The arrows: filled up to the current level; the one under the mouse (map) outlined in orange.
        float ax = min.X + pad, ay = min.Y + (h - ah) / 2;
        for (int k = 0; k < n; k++)
        {
            var a0 = new Vector2(ax + k * (aw + gap), ay);
            bool lit = k <= cur && (k == 0 || rails);
            bool hot = mouse.HasValue && new BoundingBox2(a0, a0 + new Vector2(aw, ah)).Contains(mouse.Value) == ContainmentType.Contains;
            Arrow(a0, aw, ah, A(lit ? On : Off), A(hot ? Hot : (lit ? On : Off)), lit, u);
            if (hot && click) WarpControl.SetLevel(k);
        }
        float tx = ax + arrowsW + pad;
        Text(new Vector2(tx, min.Y + (h - lsz.Y) / 2), level, A(cur > 0 ? On : TextC), tl, u);
        tx += Math.Max(lsz.X, 30f * u) + pad;
        float ty = min.Y + (h - (csz.Y + wsz.Y)) / 2;
        Text(new Vector2(tx, ty), clock, A(TextC), tc, u);
        if (why != null) Text(new Vector2(tx, ty + csz.Y), why, A(Dim), tw, u);
    }

    /// <summary>White text with the HUD's drop shadow.</summary>
    static void Text(Vector2 at, string t, ColorSRGB c, float scale, float u)
    {
        MapPipeline.ScreenText(at + new Vector2(2f * u, 2f * u), t, A(Shadow), scale);
        MapPipeline.ScreenText(at, t, c, scale);
    }

    // (the BYTE constructor: R, G, B are 0..255 bytes - they went to the float one, 0..1, and every faded frame tripped the
    //  game's colour assertion with wrong colours; the in-game integration run, 2026-10-15)
    static ColorSRGB A(ColorSRGB c) => _alpha >= 1f ? c : new ColorSRGB(c.R, c.G, c.B, (byte)Math.Clamp((int)(c.A * _alpha + 0.5f), 0, 255));

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
