using Keen.VRage.Core.Input;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Time warp from the keyboard (KSP's keys): '.' faster, ',' slower, '/' back to x1. Warp only
/// advances the rails, so it needs your orbit on the rails (a frame); otherwise a notice says so.
/// Warp still stops by itself at arrivals and ahead of maneuver burns.
/// </summary>
public static class WarpControl
{
    public static readonly double[] Levels = { 1, 5, 10, 50, 100, 1000, 10000 };
    public static string Notice; private static double _noticeUntil;

    /// <summary>A notice from the server thread (said on the next client tick).</summary>
    public static volatile string PendingSay;

    public static void Tick()
    {
        var pend = PendingSay;
        if (pend != null) { PendingSay = null; Say(pend); }
        // Not while typing: the keys act in flight and on the map, never in a dialog, another terminal
        // tab or a menu (a '.' typed into a name changed the warp).
        string top = Session != null ? GameUi.TopScreenNeedingInput(Session) : null;
        bool keysLive = top == null || (MapView.Visible && top.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) >= 0);
        bool up = keysLive && MapInput.KeyPressed(KeyboardInputs.OemPeriod), down = keysLive && MapInput.KeyPressed(KeyboardInputs.OemComma),
             stop = keysLive && MapInput.KeyPressed(KeyboardInputs.OemForwardSlash);
        up |= MapInput.DevKeys.Remove("period"); down |= MapInput.DevKeys.Remove("comma"); stop |= MapInput.DevKeys.Remove("slash");
        if (!up && !down && !stop) { if (Wall() > _noticeUntil) Notice = null; return; }
        if (stop) { SystemHost.Timescale = 1; Say("Warp ×1"); return; }
        if (FrameHost.PlayerFrame == null) { SystemHost.Timescale = 1; Say("Needs rails: leave the planet's space first"); return; }
        int i = 0;
        for (int k = 0; k < Levels.Length; k++) if (SystemHost.Timescale >= Levels[k] - 1e-6) i = k;
        i = Math.Clamp(i + (up ? 1 : -1), 0, Levels.Length - 1);
        SystemHost.Timescale = Levels[i];
        Say($"Warp ×{Levels[i]:N0}");
    }

    /// <summary>Warp at level i (the warp bar's arrows): x1 always; faster only on rails.</summary>
    public static void SetLevel(int i)
    {
        i = Math.Clamp(i, 0, Levels.Length - 1);
        if (i > 0 && FrameHost.PlayerFrame == null) { SystemHost.Timescale = 1; Say("Needs rails: leave the planet's space first"); return; }
        SystemHost.Timescale = Levels[i];
        Say($"Warp ×{Levels[i]:N0}");
    }

    public static Keen.VRage.Core.Game.Systems.Session Session;
    // (No toast over the map: its warp bar shows the level, and the toast sat on the close button.)
    public static void Say(string s) { Notice = s; _noticeUntil = Wall() + 5.0; if (Session != null && !MapView.Visible) GameUi.Toast(Session, "warp", "Time warp", s, 3); }
    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
