#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// (API version 2) TIME WARP. Warp runs only the celestial layer - the rails, the bodies, the universe clock (GetTime) - never
/// the game's physics: inside a berth ships fly in real time, and the mod steers members so they keep their true relative
/// motion. Warp needs the local player on rails (in a frame, not in a planet's cell), and drops to x1 by itself: at an arrival's
/// shell crossing, ahead of a maneuver burn, a rendezvous, an entry, a sphere-of-influence change, the star's heat, or the
/// time asked for (SetWarpStopTime). It is one clock for the whole session (SE2 2.4: server and client in one process).
/// Threads: any (the clock reads the factor once per tick).
/// </summary>
public static partial class OrbitalApi
{
    /// <summary>(v2) The warp factors the mod steps through (1 first: none), into your buffer (as many as fit). Returns how many
    /// there are (v3: up to the world's maximum, the setting Warp.MaxLevel).</summary>
    public static int GetWarpLevels(double[] levels)
    {
        var l = WarpControl.Levels;
        int n = WarpControl.TopLevel + 1;
        if (levels != null) for (int i = 0; i < n && i < levels.Length; i++) levels[i] = l[i];
        return n;
    }

    /// <summary>(v2) Warp can go above x1 now: the solar system is built and the local player is on rails.</summary>
    public static bool CanWarp() => SystemHost.Built && FrameHost.PlayerFrame != null;

    /// <summary>(v2) Ask for a warp factor: the highest of GetWarpLevels at or under it is set - held lower when a ship moving in
    /// your frame could not be carried at it (as the keys are), and x1 when the player is not on rails. Takes effect on the next
    /// tick; the mod may drop it again at any time (see above). Returns the factor set; why: what held it ("" none).</summary>
    public static double RequestTimescale(double timescale, out string why)
    {
        why = "";
        var l = WarpControl.Levels;
        double want = 1;
        int top = WarpControl.TopLevel;   // (v3: the world's maximum, Warp.MaxLevel)
        if (!double.IsNaN(timescale)) for (int k = 0; k <= top; k++) if (l[k] <= timescale + 1e-9) want = l[k];
        if (want <= 1.0) { SystemHost.Timescale = 1.0; return 1.0; }
        if (!CanWarp()) { SystemHost.Timescale = 1.0; why = "not on rails: leave the planet's space first"; return 1.0; }
        double cur = SystemHost.Timescale;   // (read once: the server's limit writes it in parallel)
        double to = want;
        if (want > cur)
        {
            to = ServerFrames.ClampWarpRaise(want, cur, out string held);
            if (held != null) why = "a ship moving nearby: " + held;
        }
        SystemHost.Timescale = to;
        return to;
    }

    /// <summary>(v2) The universe time warp is asked to stop at (NaN: none) - the map's "Warp here".</summary>
    public static double GetWarpStopTime() => SystemHost.WarpStopAt;

    /// <summary>(v2) Ask warp to drop to x1 at a universe time (s; NaN: no such stop). Only while warping: the clock forgets the
    /// stop on any tick it runs at x1 (raise the warp first, then set it) and once it is reached. False: a time not in the future.</summary>
    public static bool SetWarpStopTime(double time)
    {
        if (double.IsNaN(time)) { SystemHost.WarpStopAt = double.NaN; return true; }
        if (double.IsInfinity(time) || time <= SystemHost.Now) return false;
        SystemHost.WarpStopAt = time;
        return true;
    }
}
