using Keen.VRage.Core;
using Keen.VRage.Core.Input;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV: a debug menu in the map (F8, dev builds only) that puts you into set situations: an orbit
/// about any body (low circular, high circular, eccentric), a sector's site, a seat, and a few
/// resets. Each entry runs the same harness command a test would.
/// </summary>
public static class DebugPanel
{
    /// <summary>DEV: open as if F8 were pressed.</summary>
    public static bool DevOpen;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, Vector2 mouse)
    {
        if (!OrbitalConfig.DevHarness) return;
        if (!MapInput.KeyPressed(KeyboardInputs.F8) && !DevOpen) return;
        DevOpen = false;
        if (MapMenu.Open) { MapMenu.Close(); return; }
        // In the open middle of the map, clear of the game's side panels (which draw over ours).
        var scr = MapPipeline.ScreenSize;
        MapMenu.Show(new Vector2(scr.X * 0.27f, scr.Y * 0.21f), "Debug", Items(session));
    }

    private static List<MapMenu.Item> Items(Keen.VRage.Core.Game.Systems.Session session)
    {
        var items = new List<MapMenu.Item>();
        void Cmd(string text, string line) => items.Add(new MapMenu.Item(text, () => Run(session, text, line)));
        var reg = SystemHost.Registry;
        if (reg != null)
            foreach (var b in reg.Bodies)
            {
                if (b.IsRoot) continue;
                double r = reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
                if (!(r > 0)) continue;
                double soi = double.IsInfinity(b.SoiRadius) ? 50 * r : b.SoiRadius;
                double low = Math.Max(20000, 0.3 * r) / 1000, high = Math.Max(low * 2, (0.3 * soi - r) / 1000), far = Math.Max(low * 3, (0.6 * soi - r) / 1000);
                Cmd($"{b.Name}: low orbit ({low:F0} km)", $"orbit {b.Name} {low:F0} {low:F0}");
                Cmd($"{b.Name}: high orbit ({high:F0} km)", $"orbit {b.Name} {high:F0} {high:F0}");
                Cmd($"{b.Name}: eccentric ({low:F0} x {far:F0} km)", $"orbit {b.Name} {far:F0} {low:F0}");
            }
        int n = 0;
        lock (ServerFrames.FramesLock) foreach (var f in SystemHost.Frames.Frames) if (f.IsEncounter) n++;
        for (int i = 0; i < Math.Min(n, 4); i++) Cmd($"Next to encounter {i + 1} (20 km behind)", $"gotosite {i} 20");
        Cmd("Sit in the nearest ship", "seat");
        Cmd("Clear maneuvers", "node clear");
        Cmd("Warp: stop", "warp 1");
        return items;
    }

    private static void Run(Keen.VRage.Core.Game.Systems.Session session, string text, string line)
    {
        string r = DevHarness.Run(session, line);
        GameUi.Toast(session, "debug", text, r.Length > 120 ? r.Substring(0, 120) : r, 4);
    }
}
