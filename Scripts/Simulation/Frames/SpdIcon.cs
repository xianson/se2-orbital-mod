using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE COCKPIT SPD BOX'S SPEEDOMETER (2026-10-12, the user: "spd is supposed to have the speedometer, it lost it"): the game's
/// on-foot speedometer has an icon by its SPD label (HUDSpeedometer.axaml, PART_TachometerIcon); its cockpit template (the
/// Flight class) has none. Seated, our speedometer icon (MapIcons "speed") is drawn where it would be - left of the right-
/// aligned SPD label, in the SPD box (0.327-0.424 screen-heights left of centre, +-0.026 about the middle), as the build
/// readout's boxes carry theirs. Not over the map or another screen.
/// </summary>
public static class SpdIcon
{
    public static bool On = true;

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!On || !FrameHost.Seated || MapView.Visible) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            var scr = MapPipeline.ScreenSize;
            float H = scr.Y, u = MapPipeline.UiScale(H);
            float x1 = scr.X * 0.5f - 0.327f * H, hh = 0.026f * H, top = scr.Y * 0.5f - hh;
            // (the game's own SPD box when known - HudGeom: any size and shape of screen; our sizes scaled to it)
            var g = HudGeom.Spd;
            if (g != null && g[3] > 4f) { x1 = g[0] + g[2]; hh = g[3] * 0.5f; top = g[1]; u = hh / (0.026f * 1080f); }
            float ts = 0.42f * u, padX = 4f * u;
            var lsz = MapPipeline.MeasureText("SPD", ts);
            float y = top + (2f * hh - 2f * lsz.Y) * 0.5f - 2.5f * u;   // (the label row as the game places it - measured at 4K)
            float isz = 20f * u * 0.5f;
            MapPipeline.ScreenIcon("speed", new Vector2(x1 - 7f * u - lsz.X - 6f * u - isz, y + lsz.Y * 0.5f), isz, new ColorSRGB(1f, 1f, 1f, 1f));
        }
        finally { MapPipeline.UiEnd(); }
    }
}
