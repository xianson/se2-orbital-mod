using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

// FrameHost (partial): the guarded client systems (a fault logged and skipped, never thrown into the game's job), their
// cost (ModCost) and the DEV allocation watch.
public static partial class FrameHost
{
    /// <summary>What the guarded client systems need each frame (passed, not captured).</summary>
    public readonly struct TickArgs
    {
        public readonly Keen.VRage.Core.Game.Systems.Session Session; public readonly WorldTransform Camera; public readonly double T;
        public TickArgs(Keen.VRage.Core.Game.Systems.Session s, WorldTransform c, double t) { Session = s; Camera = c; T = t; }
    }

    /// <summary>
    /// Runs one part of the frame so that a fault in it is logged and skipped, not thrown into the
    /// game's job (which would crash the game and send a crash report). Each fault is logged with its
    /// stack the first few times, then counted.
    /// </summary>
    public static void Guard(string name, Action<TickArgs> a, in TickArgs args)
    {
        bool aw = AllocWatch;
        long m0 = aw ? GC.GetAllocatedBytesForCurrentThread() : 0, o0 = _allocOverhead;
        long t0 = ModCost.Start();
        try { a(args); }
        catch (Exception ex) { Fault(name, ex); }
        GuardEnd(name, t0, aw, m0, o0);
    }

    // (each guard's ModCost by its name's reference - the names are literals - kept per thread, in the order the frame runs
    //  them: ModCost.Sec took a lock and hashed the name, ~30 times a frame. A name not seen yet, or one made anew each call,
    //  goes to ModCost.Sec as before.)
    [ThreadStatic] static string[] _gcNames; [ThreadStatic] static ModCost[] _gcCosts; [ThreadStatic] static int _gcNext;
    static ModCost GuardCost(string name)
    {
        string[] names = _gcNames ??= new string[64]; ModCost[] costs = _gcCosts ??= new ModCost[64];
        int i = _gcNext;
        if (i < names.Length && ReferenceEquals(names[i], name)) { _gcNext = i + 1; return costs[i]; }
        for (int k = 0; k < names.Length && names[k] != null; k++) if (ReferenceEquals(names[k], name)) { _gcNext = k + 1; return costs[k]; }
        var c = ModCost.Sec(name);
        for (int k = 0; k < names.Length; k++) if (names[k] == null) { names[k] = name; costs[k] = c; _gcNext = k + 1; break; }
        return c;
    }

    public static void Guard(string name, Action a)
    {
        bool aw = AllocWatch;
        long m0 = aw ? GC.GetAllocatedBytesForCurrentThread() : 0, o0 = _allocOverhead;
        long t0 = ModCost.Start();
        try { a(); }
        catch (Exception ex) { Fault(name, ex); }
        GuardEnd(name, t0, aw, m0, o0);
    }

    // (each guarded system's time: the status line's mapcost list). Its bytes are read BEFORE the timer stops: with allocs
    // on, ModCost.Stop builds its "map:" + name key (a new string every call) - ~80 bytes a frame that was counted to every
    // guarded system: the ~4 KB/s floor across all of them was this measuring, not the systems (2026-10-14). A guard nested
    // in another (MapView.Sectorless in MapView.Tick) leaves its own measuring out of the outer one's bytes the same way.
    [ThreadStatic] static long _allocOverhead;
    static void GuardEnd(string name, long t0, bool aw, long m0, long o0)
    {
        if (!aw || !AllocWatch) { GuardCost(name).Stop(t0); return; }
        long m1 = GC.GetAllocatedBytesForCurrentThread(), inner = _allocOverhead - o0;
        GuardCost(name).Stop(t0);
        _allocOverhead += GC.GetAllocatedBytesForCurrentThread() - m1;
        lock (_allocs) { _allocs.TryGetValue(name, out long b); _allocs[name] = b + (m1 - m0) - inner; }
    }

    /// <summary>DEV (harness: allocs on|off|show): bytes allocated per guarded system, since the last show.</summary>
    public static bool AllocWatch;
    static readonly Dictionary<string, long> _allocs = new Dictionary<string, long>();
    static long _allocsSince = System.Diagnostics.Stopwatch.GetTimestamp();
    public static void AllocAdd(string name, long bytes) { lock (_allocs) { _allocs.TryGetValue(name, out long b); _allocs[name] = b + bytes; } }
    public static string AllocReport()
    {
        double secs = (System.Diagnostics.Stopwatch.GetTimestamp() - _allocsSince) / (double)System.Diagnostics.Stopwatch.Frequency;
        var sb = new System.Text.StringBuilder($"allocs over {secs:F0} s (KB/s):");
        lock (_allocs)
        {
            var l = new List<KeyValuePair<string, long>>(_allocs);
            l.Sort((x, y) => y.Value.CompareTo(x.Value));
            foreach (var kv in l) sb.Append($" {kv.Key} {kv.Value / 1024.0 / Math.Max(secs, 0.001):F0}");
            _allocs.Clear();
        }
        _allocsSince = System.Diagnostics.Stopwatch.GetTimestamp();
        return sb.ToString();
    }

    private static readonly Dictionary<string, int> _faults = new Dictionary<string, int>();
    public static string LastFault = "";

    public static void Fault(string name, Exception ex)
    {
        string key = name + ":" + ex.GetType().Name + ":" + ex.TargetSite?.Name;
        int n;
        lock (_faults) { _faults.TryGetValue(key, out n); _faults[key] = ++n; }   // (both threads fault here)
        LastFault = $"{DateTime.Now:HH:mm:ss} {name}: {ex.GetType().Name}: {ex.Message} (x{n})";
        if (n <= 3 || n % 1000 == 0) Log.Default?.Error($"[ORBIT-FAULT] {name} (x{n}): {ex}");
    }
}
