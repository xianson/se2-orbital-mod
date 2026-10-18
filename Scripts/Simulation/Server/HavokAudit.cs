using System.Reflection;
using Keen.VRage.Library.Mathematics.Spatial;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// CLUSTER AUDIT (diagnostic, on by default, cheap): watches Havok's large-world bookkeeping for the state that ends in
/// "There are still active bodies in native world N" (HavokLargeWorldManager.RemoveCluster - the crash of 2026-10-17;
/// HavokMoves explains the mechanism). For each physics world: the native world's body count against what the engine can
/// account for when it removes that cluster - its dynamic entity list plus the statics its static tree finds in the
/// cluster's box (HavokMoves.Unaccounted). More bodies than that and the removal would leave them: logged once per
/// mismatch as
///   [ORBIT-HAVOK] cluster N world 0x..: bodies X, tracked Y (+ statics Z) - K unaccounted; last moves: ...
/// with the last few Orbital moves (direct, teleport-phase, static parks, refusals) and grids added / removed (a split's
/// pieces, spawns, deletes). Sampled once a second, and soon after every teleport phase and after grids come or go (at most
/// ten a second). A sample is a handful of reflected field reads per world, one native call each, a static-tree query into
/// a pooled buffer - no per-tick work when nothing is due.
///
/// Reflection (docs/REFLECTION_AND_WHITELIST.md, "Havok cluster audit"): HavokLargeWorldManager (internal, VRage.Physics) -
/// private fields _clusters (FreeList&lt;ClusterInfo&gt;; its private _list array) and _staticObjectTree (a public
/// DynamicBoundingVolumeTree3D&lt;DEntity&gt;), ClusterInfo's public field DynamicEntities, the public GetWorld(tag) (a
/// Ptr&lt;HknpWorld&gt;, read through its public explicit conversion to nint - mod code may not use unsafe, VRS1005),
/// and HknpWorld+__Internal.getNumBodies (internal static extern, called as HknpWorld.getNumBodies calls it: the world
/// pointer + 32). The worlds themselves come from the public IPhysicsWorldManager.GetWorlds (tag = cluster index).
/// Anything not found: the audit stops itself, says why in Status and the log, once.
/// </summary>
public static class HavokAudit
{
    /// <summary>DEV/diagnostic switch (default on).</summary>
    public static bool Enabled = true;
    public static string Status = "-";
    public static int Samples, Mismatches;
    /// <summary>The last sample's cost (ms) and the worlds it saw.</summary>
    public static double LastSampleMs;
    public static int LastWorlds;

    public const byte MoveDirect = 1, MoveTeleport = 2, MovePark = 3, MoveRefused = 4, GridAdded = 5, GridRemoved = 6;

    struct Ev { public double At; public byte Kind; public long Grid; public Vector3D From, To; }
    static readonly Ev[] _ring = new Ev[12];
    static int _ringNext, _ringCount;
    static readonly object _ringLock = new object();

    /// <summary>A grid move (ServerFrames: MoveGrid's direct write, the teleport phase's write, a static's park, a refusal).
    /// Kept in a fixed ring - no allocation; formatted only when a mismatch is logged.</summary>
    public static void NoteMove(byte kind, long grid, Vector3D from, Vector3D to)
    {
        if (!Enabled) return;
        lock (_ringLock)
        {
            _ring[_ringNext] = new Ev { At = Num.Wall(), Kind = kind, Grid = grid, From = from, To = to };
            _ringNext = (_ringNext + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
        }
    }

    /// <summary>A server grid came into the scene or left it (GridMembers.Register / Unregister): noted, and a sample soon.</summary>
    public static void NoteGrid(bool added, long grid, Vector3D at)
    {
        if (!Enabled) return;
        NoteMove(added ? GridAdded : GridRemoved, grid, at, at);
        SampleSoon();
    }

    static int _soon;
    /// <summary>A sample at the next server tick (rate-capped): after a teleport phase, a grid added or removed.</summary>
    public static void SampleSoon() { if (Enabled) System.Threading.Volatile.Write(ref _soon, 1); }

    static double _nextDue, _lastAt;
    const double Period = 1.0, MinGap = 0.1;

    /// <summary>The server tick (ServerFrames.TickBody, server thread, after the physics step).</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session server)
    {
        if (!Enabled || server == null) return;
        double now = Num.Wall();
        bool soon = System.Threading.Volatile.Read(ref _soon) != 0;
        if (now < _nextDue && !(soon && now - _lastAt >= MinGap)) return;
        System.Threading.Volatile.Write(ref _soon, 0);
        _lastAt = now; _nextDue = now + Period;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { Sample(server); }
        catch (Exception ex) { Stop("sample threw: " + ex.GetType().Name + ": " + ex.Message); }
        LastSampleMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── the reflected engine members (bound per manager instance) ──
    static object _mgr;
    static FieldInfo _fClusters, _fTree, _fList, _fDyn;
    static MethodInfo _getWorld, _ptrToNint;
    static Func<nint, uint> _numBodies;
    /// <summary>cluster index -> the last mismatch logged for it (bodies, tracked): logged once per mismatch.</summary>
    static readonly Dictionary<int, long> _logged = new Dictionary<int, long>();

    static bool Bind(object mgr)
    {
        if (ReferenceEquals(mgr, _mgr)) return _ptrToNint != null;
        _mgr = mgr; _fClusters = _fTree = _fList = _fDyn = null; _getWorld = _ptrToNint = null; _numBodies = null; _logged.Clear();
        var t = mgr.GetType();
        if (t.Name != "HavokLargeWorldManager") { Stop("the physics world manager is " + t.Name + " (no clusters to audit)"); return false; }
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var fc = t.GetField("_clusters", inst);
        var ft = t.GetField("_staticObjectTree", inst);
        var ci = t.GetNestedType("ClusterInfo", BindingFlags.Public | BindingFlags.NonPublic);
        var fl = fc?.FieldType.GetField("_list", inst);
        var fd = ci?.GetField("DynamicEntities", inst);
        var gw = t.GetMethod("GetWorld", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
        MethodInfo toNint = null;
        if (gw != null) foreach (var m in gw.ReturnType.GetMethods(BindingFlags.Static | BindingFlags.Public)) if (m.Name == "op_Explicit" && m.ReturnType == typeof(nint)) { toNint = m; break; }
        var hk = t.Assembly.GetType("Keen.VRage.Physics.Havok.Core.HknpWorld+__Internal");
        var nb = hk?.GetMethod("getNumBodies", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(nint) }, null);
        Func<nint, uint> call = nb == null ? null : (Func<nint, uint>)Delegate.CreateDelegate(typeof(Func<nint, uint>), nb, false);
        string miss = fc == null ? "_clusters" : ft == null ? "_staticObjectTree" : ci == null ? "ClusterInfo" : fl == null ? "FreeList._list"
                    : fd == null ? "ClusterInfo.DynamicEntities" : gw == null ? "GetWorld" : toNint == null ? "Ptr<HknpWorld> to nint" : call == null ? "HknpWorld.getNumBodies" : null;
        if (miss != null) { Stop("engine member not found: " + miss + " (game version changed?)"); return false; }
        if (!(ft.FieldType == typeof(DynamicBoundingVolumeTree3D<DEntity>))) { Stop("_staticObjectTree is " + ft.FieldType.Name); return false; }
        _fClusters = fc; _fTree = ft; _fList = fl; _fDyn = fd; _numBodies = call; _getWorld = gw; _ptrToNint = toNint;
        return true;
    }

    /// <summary>A cluster's native world address (GetWorld, then Ptr&lt;T&gt; to nint: a few boxes, once a second).</summary>
    static nint WorldAddress(object mgr, int tag) => _ptrToNint.Invoke(null, new[] { _getWorld.Invoke(mgr, new object[] { tag }) }) is nint a ? a : 0;

    static void Sample(Keen.VRage.Core.Game.Systems.Session server)
    {
        var mgr = server.Get<Keen.VRage.Physics.IPhysics>()?.WorldManager;
        if (mgr == null || !Bind(mgr)) return;
        var list = _fList.GetValue(_fClusters.GetValue(mgr)) as Array;
        var tree = _fTree.GetValue(mgr) as DynamicBoundingVolumeTree3D<DEntity>;
        if (list == null || tree == null) return;
        Samples++;
        using (var worlds = new Keen.VRage.Library.Memory.Buffer<Keen.VRage.Physics.IPhysicsWorldManager.WorldInfo>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalHavokAudit"))
        using (var statics = new Keen.VRage.Library.Memory.Buffer<DEntity>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalHavokAuditStatics"))
        {
            mgr.GetWorlds(worlds);
            LastWorlds = worlds.Count;
            int bad = 0;
            for (int i = 0; i < worlds.Count; i++)
            {
                int tag = worlds[i].Tag;
                if (tag < 0 || tag >= list.Length) continue;
                object info = list.GetValue(tag);   // (a boxed ClusterInfo: one small box per world per sample)
                nint w = WorldAddress(mgr, tag);
                if (w == 0) continue;
                int dyn = (_fDyn.GetValue(info) as HashSet<DEntity>)?.Count ?? 0;
                statics.Clear();
                var box = worlds[i].Bounds;
                tree.Query(in box, statics);
                int st = statics.Count;
                long native = _numBodies(w + 32);   // (HknpWorld.getNumBodies: the native call on the world's reader, +32)
                long un = HavokMoves.Unaccounted(native, dyn, st);
                if (un <= 0) { _logged.Remove(tag); continue; }
                bad++;
                long sig = native * 1_000_003L + dyn + st;
                if (_logged.TryGetValue(tag, out long was) && was == sig) continue;
                _logged[tag] = sig;
                Mismatches++;
                Log.Default?.Info($"[ORBIT-HAVOK] cluster {tag} world 0x{(long)w:X}: bodies {native}, tracked {dyn} (+ statics {st}) - {un} unaccounted " +
                                  $"(box {Fmt(box.Center)} +-{box.HalfExtents.X / 1000:F1} km); last moves: {RecentMoves()}");
            }
            Status = $"havok audit: {worlds.Count} world(s), {(bad == 0 ? "consistent" : bad + " with bodies unaccounted")}; {Samples} samples, {Mismatches} mismatch line(s), last {LastSampleMs:F2} ms";
        }
    }

    static string Fmt(Vector3D v) => $"({v.X / 1000:F1}, {v.Y / 1000:F1}, {v.Z / 1000:F1}) km";

    /// <summary>The ring's moves, oldest first (only when a mismatch is logged).</summary>
    static string RecentMoves()
    {
        var sb = new System.Text.StringBuilder();
        double now = Num.Wall();
        lock (_ringLock)
        {
            int start = (_ringNext - _ringCount + _ring.Length) % _ring.Length;
            for (int k = 0; k < _ringCount; k++)
            {
                var e = _ring[(start + k) % _ring.Length];
                string what = e.Kind switch
                {
                    MoveDirect => "direct", MoveTeleport => "teleport-phase", MovePark => "static park", MoveRefused => "static refused",
                    GridAdded => "grid added", GridRemoved => "grid removed", _ => "?"
                };
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{now - e.At:F1} s ago {what} grid {e.Grid}");
                if (e.Kind == MoveDirect || e.Kind == MoveTeleport || e.Kind == MovePark) sb.Append($" {(e.To - e.From).Length() / 1000:F2} km to {Fmt(e.To)}");
                else sb.Append($" at {Fmt(e.From)}");
            }
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    static void Stop(string why)
    {
        Enabled = false; Status = "havok audit off: " + why;
        Log.Default?.Info("[ORBIT-HAVOK] cluster audit off: " + why);
    }

    /// <summary>A new world (WorldReset): the bound manager and the logged mismatches dropped, the moves forgotten.</summary>
    public static void ResetWorld()
    {
        _mgr = null; _ptrToNint = null; _logged.Clear();
        lock (_ringLock) { _ringNext = 0; _ringCount = 0; }
        _nextDue = 0; _lastAt = 0;
    }
}
