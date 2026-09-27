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

    public static void Tick()
    {
        bool up = MapInput.KeyPressed(KeyboardInputs.OemPeriod), down = MapInput.KeyPressed(KeyboardInputs.OemComma),
             stop = MapInput.KeyPressed(KeyboardInputs.OemForwardSlash);
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

    public static Keen.VRage.Core.Game.Systems.Session Session;
    public static void Say(string s) { Notice = s; _noticeUntil = Wall() + 5.0; if (Session != null) GameUi.Toast(Session, "warp", "Time warp", s, 3); }
    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
