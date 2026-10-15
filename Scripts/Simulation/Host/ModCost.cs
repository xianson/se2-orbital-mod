#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>What the mod's own code costs per frame (ms): the average and the worst over the last full second.</summary>
public sealed class ModCost
{
    public static readonly ModCost Client = new ModCost(), Server = new ModCost(), Map = new ModCost();
    // (kept in Stopwatch ticks and added with Interlocked, no lock: every planet's beacon job stops Server at once, in parallel,
    //  and waited on each other's lock - ModCost.Stop was 1.4 ms/s in a profile, 2026-10-15. The second that ends is published
    //  by the one caller whose exchange moves the window on; a sample racing it lands in the next second's average)
    private long _windowStart, _sumTicks, _maxTicks; private int _n;
    public volatile float AvgMs, MaxMs, PeakMs;   // (Peak: the worst frame ever, for one-off stalls)
    public static long Start()
    {
        if (FrameHost.AllocWatch) _allocMark = GC.GetAllocatedBytesForCurrentThread();
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }
    // (DEV, allocs on: a named section's bytes since the last Start - the map draws on one thread)
    [ThreadStatic] static long _allocMark;   // (per thread: the server's Start must not move the map's mark)
    string _name, _key;   // (the allocation watch's key, made once)
    public void Stop(long start)
    {
        if (_name != null && FrameHost.AllocWatch) FrameHost.AllocAdd(_key ??= "map:" + _name, GC.GetAllocatedBytesForCurrentThread() - _allocMark);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long d = now - start;
        if (System.Threading.Volatile.Read(ref _windowStart) == 0) System.Threading.Interlocked.CompareExchange(ref _windowStart, now, 0L);
        System.Threading.Interlocked.Add(ref _sumTicks, d); System.Threading.Interlocked.Increment(ref _n);
        long mx = System.Threading.Volatile.Read(ref _maxTicks);
        while (d > mx) { long seen = System.Threading.Interlocked.CompareExchange(ref _maxTicks, d, mx); if (seen == mx) break; mx = seen; }
        double ms = d * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > PeakMs) PeakMs = (float)ms;
        long ws = System.Threading.Volatile.Read(ref _windowStart);
        if (now - ws >= System.Diagnostics.Stopwatch.Frequency && System.Threading.Interlocked.CompareExchange(ref _windowStart, now, ws) == ws)
        {
            long sum = System.Threading.Interlocked.Exchange(ref _sumTicks, 0L), max = System.Threading.Interlocked.Exchange(ref _maxTicks, 0L);
            int n = System.Threading.Interlocked.Exchange(ref _n, 0);
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (n > 0) AvgMs = (float)(sum * toMs / n);
            MaxMs = (float)(max * toMs);
        }
    }
    public override string ToString() => $"{AvgMs:F2}/{MaxMs:F1}/{PeakMs:F0}";

    /// <summary>Named parts of a costly path (the map's draw), for finding where its time goes.</summary>
    public static readonly Dictionary<string, ModCost> Sections = new Dictionary<string, ModCost>();
    // (a known name read from a copy of Sections without the lock - the map asks ~30 a frame; the copy is replaced whole, under
    //  the lock, when a name is added: the same ModCost per name as ever)
    static Dictionary<string, ModCost> _sectionsRead = new Dictionary<string, ModCost>();
    public static ModCost Sec(string name)
    {
        if (System.Threading.Volatile.Read(ref _sectionsRead).TryGetValue(name, out var known)) return known;
        lock (Sections)
        {
            if (!Sections.TryGetValue(name, out var c))
            {
                Sections[name] = c = new ModCost { _name = name };
                System.Threading.Volatile.Write(ref _sectionsRead, new Dictionary<string, ModCost>(Sections));
            }
            return c;
        }
    }
    public static string SectionList()
    {
        var sb = new System.Text.StringBuilder();
        lock (Sections) foreach (var kv in Sections) sb.Append($"{kv.Key} {kv.Value}  ");
        return sb.ToString();
    }
}

/// <summary>Ticks per second over the last second (harness diagnostics).</summary>
public sealed class TickRate
{
    public static readonly TickRate Client = new TickRate();
    public static readonly TickRate Server = new TickRate();
    /// <summary>Client frames that actually draw (after the once-per-frame dedup).</summary>
    public static readonly TickRate Draw = new TickRate();
    private long _windowStart, _last; private int _n; private double _worst, _sum, _lastPause;
    /// <summary>Server tick gaps longer than this are logged ([HITCH]).</summary>
    public static double HitchLogMs = 150;
    public volatile float PerSecond;
    /// <summary>The longest gap between two ticks in the last full second (ms): a hitch shows here.</summary>
    public volatile float WorstMs, MeanMs;
    public void Count()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        if (_last != 0)
        {
            double gap = (now - _last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; _sum += gap; if (gap > _worst) _worst = gap;
            // (the server's hitches, each one, logged with the garbage collection pause during it: to line them up
            // with whatever else the log says happened then)
            if (ReferenceEquals(this, Server))
            {
                double pause = System.GC.GetTotalPauseDuration().TotalMilliseconds;
                if (gap > HitchLogMs)
                    Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[HITCH] server tick gap {gap:F0} ms (GC pause in it {pause - _lastPause:F0} ms, gen0/1/2 {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}/{System.GC.CollectionCount(2)})");
                _lastPause = pause;
            }
        }
        _last = now;
        _n++;
        double el = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (el >= 1.0) { PerSecond = (float)(_n / el); WorstMs = (float)_worst; MeanMs = (float)(_sum / Math.Max(1, _n)); _n = 0; _worst = 0; _sum = 0; _windowStart = now; }
    }
}
