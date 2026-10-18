using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// Injected into SERVER grid compositions (CubeGridComponent). Registers the grid so the server-side
/// frame manager (<see cref="ServerFrames"/>) can frame it. Unpiloted grids are simulated on the
/// server in SE2 (the aero mod applies its forces there), so all grid physics access is server-side.
/// </summary>
public class OrbitalGridComponent : Component, IInSceneListener
{
    /// <summary>GridMembers.IsDynamic's cache: the answer and when (Stopwatch ticks; 0 none).</summary>
    internal bool Dyn; internal long DynAt;
    /// <summary>GridMembers.Position's last live read: its answer once this grid is dead (LastKnownPosition).</summary>
    internal LastKnownPosition Pos;
    /// <summary>EncounterFrames.Kind's copy of this grid's entry (read without its lock).</summary>
    internal EncounterFrames.KindMemo KindMemo;
    [Keen.VRage.DCS.Annotations.Component]
    private readonly CubeGridComponent _grid;

    internal long Id;
    internal Keen.VRage.Core.Game.Systems.Session Session;
    /// <summary>True for the server copy (SE2 builds grids from the same composition on both sides).</summary>
    internal bool IsServer => Session != null && ReferenceEquals(Session, ServerPlanetBeacon.ServerSession);

    void IInSceneListener.OnAddedToScene()
    {
        Session = Entity.GetSession();
        // (a warm berth's placeholder is no grid of ours: never framed, listed, marked or billed - WarmBerths)
        if (DisplayName == WarmBerths.Name) { Id = 0; return; }
        Id = GridMembers.Register(this);
    }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        GridEdits.Forget(Entity);   // (what we wrote into its physics: OrbitalApi.TryGetGridEdits - a warm berth's placeholder too)
        if (Id != 0) GridMembers.Unregister(this);
    }

    /// <summary>The evaluated name, the raw text it came from and when - one object, swapped whole (both threads read it).</summary>
    sealed class NameMemo { public string Raw, Name; public long At; }
    NameMemo _nameMemo;
    /// <summary>Seconds the evaluated name is kept while the grid's own text is unchanged (a rename shows at once: it is a new
    /// text, compared by reference - LocalizableText.RawText).</summary>
    public static double NameSeconds = 5.0;
    /// <summary>The name evaluated now (not the cache): what a tombstone keeps to relink by.</summary>
    internal string LiveName() { System.Threading.Volatile.Write(ref _nameMemo, null); return DisplayName; }
    internal string DisplayName
    {
        // (the game's text evaluated at most once a second - a renamed grid shows within it: asked several times per grid per
        //  tick - kinds, encounters, events - each a fresh string and builder, 0.7 MB/s in a profile, 2026-10-14)
        // (2026-10-15: still 78 KB/s - every grid evaluated once a second by the views' snapshot. Now kept NameSeconds while the
        //  grid's raw text is the same string object: a rename sets a new one (CubeGridComponent.DisplayName's setter), seen on
        //  the next read - sooner than the second before. What the same raw text evaluates to changes only with the language:
        //  within NameSeconds.)
        get
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            string raw = null;
            try { raw = _grid?.DisplayName.RawText; } catch { }
            var m = System.Threading.Volatile.Read(ref _nameMemo);
            if (m != null && ReferenceEquals(raw, m.Raw) && now - m.At < (long)(NameSeconds * System.Diagnostics.Stopwatch.Frequency)) return m.Name;
            string n;
            try { n = _grid?.DisplayName.EvaluateText() ?? "grid"; } catch { n = "grid"; }
            System.Threading.Volatile.Write(ref _nameMemo, new NameMemo { Raw = raw, Name = n, At = now });
            return n;
        }
    }
}

/// <summary>Server-side registry of grids and the physics access the frame manager needs.</summary>
public static class GridMembers
{
    /// <summary>Grid ids live in their own range so they can never collide with character ids.</summary>
    public const long IdBase = 1_000_000_000L;

    private static readonly Dictionary<long, OrbitalGridComponent> _byId = new Dictionary<long, OrbitalGridComponent>();
    private static long _next = IdBase;
    private static readonly object _lock = new object();

    public static bool IsGridId(long id) => id >= IdBase;

    // ── identity across a re-add ──
    // A server grid that leaves the scene and comes back (the game's managed world areas unload and respawn their
    // grids: a scenario ship teleported out of its area came back as new entities) is the same ship: it takes its old
    // id back, so its frame, anchor role, orbit and contact state stay valid. A grid gone for good is forgotten after
    // TombstoneSeconds (its per-grid state then, and the frames prune it).
    public const double TombstoneSeconds = 60.0, RelinkMeters = 100.0;
    // (Session: the scene it left - only a grid of the same session takes its id back. 2026-10-15: the tombs outlived the
    //  world, and a grid of the next world with the same name within 100 m inside 60 s took the last world's id - and with
    //  it its id-keyed frame and contact state)
    struct Tomb { public string Name; public Vector3D At; public double Wall; public Keen.VRage.Core.Game.Systems.Session Session; }
    private static readonly Dictionary<long, Tomb> _tombs = new Dictionary<long, Tomb>();
    /// <summary>A grid id that left the scene lately and may come back (frames keep it, anchors wait for it).</summary>
    // (no tombs - nearly always: no lock. _tombN is _tombs.Count, written under _lock after every change: a reader that sees 0
    //  ran before the tomb's lock was let go. Asked per member per second by Prune, the invariants and the loose-grid adoption)
    public static bool Tombstoned(long id) { if (System.Threading.Volatile.Read(ref _tombN) == 0) return false; lock (_lock) return _tombs.ContainsKey(id); }
    static int _tombN;
    static void TombsChanged() => System.Threading.Volatile.Write(ref _tombN, _tombs.Count);
    public static int Relinked;

    internal static long Register(OrbitalGridComponent g)
    {
        long nid = 0;
        string relinked = null;
        if (g.IsServer)
        {
            string name = g.DisplayName; Vector3D at = default; bool hasAt = false;
            try { at = g.Entity.Data.GetWorldTransform().Position; hasAt = true; g.Pos.Seen(at); } catch { }   // (seeded: one deleted before any Position read answers here, not the origin)
            lock (_lock)
            {
                if (hasAt)
                {
                    long best = 0; double bd = RelinkMeters;
                    foreach (var kv in _tombs)
                    {
                        if (kv.Value.Name != name || !ReferenceEquals(kv.Value.Session, g.Session) || _byId.ContainsKey(kv.Key)) continue;
                        double d = (kv.Value.At - at).Length();
                        if (d <= bd) { bd = d; best = kv.Key; }
                    }
                    if (best != 0) { nid = best; _tombs.Remove(best); TombsChanged(); _byId[nid] = g; _allSnap = null; System.Threading.Volatile.Write(ref _byIdSnap, null); Relinked++; relinked = $"{bd:F0} m"; }
                }
            }
        }
        if (nid != 0) { Log.Default?.Info($"[ORBIT-FRAME] grid {nid} '{g.DisplayName}' back in the scene ({relinked} from where it left): its old id kept"); HavokAudit.NoteGrid(true, nid, g.Pos.At); return nid; }
        lock (_lock) { nid = ++_next; _byId[nid] = g; _allSnap = null; System.Threading.Volatile.Write(ref _byIdSnap, null); }
        EncounterFrames.OnGridRegistered(nid);
        if (g.IsServer) HavokAudit.NoteGrid(true, nid, g.Pos.At);   // (a split's piece, a spawn: the cluster audit samples soon)
        return nid;
    }

    internal static void Unregister(OrbitalGridComponent g)
    {
        bool tomb = false;
        if (g.IsServer)
            try
            {
                var at = g.Entity.Data.GetWorldTransform().Position;
                lock (_lock)
                {
                    string n = g.LiveName(); double w = Wall();
                    _tombs[g.Id] = new Tomb { Name = n, At = at, Wall = w, Session = g.Session }; TombsChanged();
                    if (w + TombstoneSeconds < _tombDue) System.Threading.Volatile.Write(ref _tombDue, w + TombstoneSeconds);
                }
                tomb = true;
            }
            catch { }
        lock (_lock) { _byId.Remove(g.Id); _allSnap = null; System.Threading.Volatile.Write(ref _byIdSnap, null); }
        lock (GridMembers.Pending) GridMembers.Pending.Remove(g.Id);
        if (g.IsServer) HavokAudit.NoteGrid(false, g.Id, g.Pos.At);
        if (!tomb) ServerFrames.Forget(g.Id);   // (per-grid state dies with the grid - after its tombstone, if it may come back)
    }

    /// <summary>Tombstones past their time, removed (caller holds _lock): returned, to be forgotten AFTER the lock is let go
    /// (Forget takes FramesLock; elsewhere FramesLock is held while this lock is taken - never the other way round).</summary>
    static List<long> ExpireTombs()
    {
        double now = Wall();
        List<long> dead = null;
        double due = double.PositiveInfinity;
        foreach (var kv in _tombs)
        {
            if (now - kv.Value.Wall > TombstoneSeconds) (dead ??= new List<long>()).Add(kv.Key);
            else if (kv.Value.Wall + TombstoneSeconds < due) due = kv.Value.Wall + TombstoneSeconds;
        }
        if (dead != null) { foreach (long id in dead) _tombs.Remove(id); TombsChanged(); }
        System.Threading.Volatile.Write(ref _tombDue, due);
        return dead;
    }
    // THE SWEEP, ONLY WHEN A TOMB IS DUE (2026-10-15, a profile: SweepTombs took _lock every tick to walk the tombs - 1.3 ms/s,
    // most of it waiting on the lock Get took thousands of times a tick). _tombDue is the earliest wall time any tomb can expire
    // (its Wall + TombstoneSeconds; +inf: none): lowered as a tomb is made, recomputed by each sweep - never later than the
    // true earliest (a relink removing the earliest only leaves it early: one walk that expires nothing). A tomb is "past its
    // time" only when now - Wall > TombstoneSeconds, i.e. now > its due: nothing expires before _tombDue.
    static double _tombDue = double.PositiveInfinity;
    /// <summary>A new world (WorldReset): the last one's tombstones dropped (none of its grids comes back), their grids forgotten.
    /// Ids are not restarted: they stay unique in the process (the next world's grids may have registered already).</summary>
    public static void ResetWorld()
    {
        List<long> dead = null;
        lock (_lock)
        {
            if (_tombs.Count > 0) { dead = new List<long>(_tombs.Keys); _tombs.Clear(); TombsChanged(); }
            System.Threading.Volatile.Write(ref _tombDue, double.PositiveInfinity);
        }
        if (dead != null) foreach (long id in dead) ServerFrames.Forget(id);   // (after the lock, as SweepTombs)
    }

    /// <summary>From the server tick: grids gone for good forgotten.</summary>
    public static void SweepTombs()
    {
        if (!(Wall() > System.Threading.Volatile.Read(ref _tombDue))) return;
        List<long> dead; lock (_lock) dead = ExpireTombs();
        if (dead != null) foreach (long id in dead) ServerFrames.Forget(id);
    }

    /// <summary>
    /// Out of the game's managed world area before a move: a scenario ship (and each of its sub-grids) stays managed by
    /// the area it was placed in until its own seat is piloted, and a teleport out of the area made the game unload it and
    /// respawn it as new entities. (The game's own call, as when a seat is first piloted.)
    /// </summary>
    public static void Unmanage(Entity e)
    {
        try
        {
            if (!Alive(e) || !e.Data.Has<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedByWorldAreaTag>()) return;   // (a dead one's Has threw, ~200 ms - 2026-10-15)
            var areas = e.GetSession()?.SessionComponents.TryGet<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldAreaSessionComponent>();
            if (areas != null && areas.TryUnmanage(e)) Log.Default?.Info($"[ORBIT-FRAME] an entity released from its world area before a move");
        }
        catch { }
    }

    /// <summary>
    /// Every grid - a SHARED SNAPSHOT, rebuilt only when grids come or go: never change it (AllCopy for a list of your own).
    /// (A profile, 2026-10-14: a new list per call - ~50 callers, several per tick and per frame - was 5.8 MB/s, more than half
    /// of everything the game allocated, and the 2.5 GB heap's full collections pause it 230-400 ms.)
    /// </summary>
    public static List<OrbitalGridComponent> All()
    {
        var s = System.Threading.Volatile.Read(ref _allSnap);
        if (s != null) return s;
        lock (_lock)
        {
            s = _allSnap;
            if (s == null) { s = new List<OrbitalGridComponent>(_byId.Values); System.Threading.Volatile.Write(ref _allSnap, s); }
            return s;
        }
    }
    /// <summary>The server's static grids (stations, grids on rocks) - a shared snapshot, at most DynamicCacheSeconds old
    /// (StaticAnchor asked every grid of the world, per frame, per update: a profile, 2026-10-14). Never change it.</summary>
    public static List<OrbitalGridComponent> Statics()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = System.Threading.Volatile.Read(ref _statics);
        if (s != null && now - System.Threading.Volatile.Read(ref _staticsAt) < (long)(DynamicCacheSeconds * System.Diagnostics.Stopwatch.Frequency)
            && ReferenceEquals(_staticsOf, System.Threading.Volatile.Read(ref _allSnap))) return s;
        var all = All(); var l = new List<OrbitalGridComponent>();
        foreach (var g in all) if (g.IsServer && g.Entity != null && !IsDynamic(g)) l.Add(g);
        _staticsOf = all; System.Threading.Volatile.Write(ref _statics, l); System.Threading.Volatile.Write(ref _staticsAt, now);
        return l;
    }
    private static List<OrbitalGridComponent> _statics, _staticsOf; private static long _staticsAt;
    /// <summary>A grid's motion switched by the mod: the statics list is built anew.</summary>
    static void StaticsStale() => System.Threading.Volatile.Write(ref _staticsAt, 0L);

    /// <summary>Every grid, in a list of the caller's own (to sort or filter).</summary>
    public static List<OrbitalGridComponent> AllCopy() => new List<OrbitalGridComponent>(All());
    private static List<OrbitalGridComponent> _allSnap;

    // GET WITHOUT THE LOCK (2026-10-15, a profile: Get was 5.6 ms/s of the server tick on its own, and its lock held up
    // SweepTombs and the client's readers). A copy of _byId, published whole and never changed after, rebuilt on the first Get
    // after a grid comes or goes (each change, under _lock, drops it). A reader holding the old copy answers as it would have
    // had it taken the lock just before the change.
    private static Dictionary<long, OrbitalGridComponent> _byIdSnap;
    public static OrbitalGridComponent Get(long id)
    {
        var s = System.Threading.Volatile.Read(ref _byIdSnap);
        if (s == null)
            lock (_lock)
            {
                s = _byIdSnap;
                if (s == null) { s = new Dictionary<long, OrbitalGridComponent>(_byId); System.Threading.Volatile.Write(ref _byIdSnap, s); }
            }
        return s.TryGetValue(id, out var g) ? g : null;
    }

    // ── physics (server thread) ──
    // (2026-10-15: each accessor asks Alive first - a grid deleted before its Unregister ran (another thread, or later this
    //  tick) threw from its data read, and an exception costs ~200 ms; these run per grid per tick. The try stays for one
    //  deleted between the check and the read: the scene changes concurrently with the simulation.)

    /// <summary>Where a grid is, or where it is going this tick (a jointed group's move waits for the physics step). A dead
    /// grid (deleted, its Unregister not run yet): where it last was - never a throw, never a NaN (LastKnownPosition). Whoever
    /// moves, frames or anchors on the answer asks <see cref="TryPosition"/>.</summary>
    // (2026-10-15: no guard here - a dead grid threw, ~200 ms, into whatever handler each of ~70 callers had)
    public static Vector3D Position(OrbitalGridComponent g) { TryPosition(g, out var p); return p; }

    /// <summary>As Position; false for a dead grid (p then its last known place): the movers' and framers' question - a
    /// dead grid is never moved, framed, anchored on or stowed by a stale place.</summary>
    public static bool TryPosition(OrbitalGridComponent g, out Vector3D p)
    {
        // (no move queued - nearly always: no lock. A profile, 2026-10-14: this lock taken per grid per frame update was the
        //  server tick's single largest item)
        if (Pending.Count > 0) lock (Pending) if (Pending.TryGetValue(g.Id, out var pm)) return LastKnownPosition.Pick(true, pm.p, Alive(g.Entity), false, default, ref g.Pos, out p);
        bool alive = Alive(g.Entity), read = false; Vector3D at = default;
        if (alive) try { at = g.Entity.Data.GetWorldTransform().Position; read = true; } catch { }   // (deleted between the check and the read)
        return LastKnownPosition.Pick(false, default, alive, read, at, ref g.Pos, out p);
    }

    /// <summary>Grids with a move queued for the next physics step (ServerFrames.MoveGrid): id -> target, velocity then.</summary>
    public static readonly Dictionary<long, (Vector3D p, Vector3D v)> Pending = new Dictionary<long, (Vector3D, Vector3D)>();

    /// <summary>Dynamic (a ship) or not (a station, a grid on a rock), as of at most DynamicCacheSeconds ago: asked ~30 places
    /// per grid per tick, and each answer was two component lookups and an interface cast (a profile, 2026-10-14). Whoever
    /// switches a grid's motion calls ForgetMotion.</summary>
    public static bool IsDynamic(OrbitalGridComponent g)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long at = System.Threading.Volatile.Read(ref g.DynAt);
        if (at != 0 && now - at < (long)(DynamicCacheSeconds * System.Diagnostics.Stopwatch.Frequency)) return g.Dyn;
        bool d = IsDynamicNow(g);
        g.Dyn = d; System.Threading.Volatile.Write(ref g.DynAt, now);
        return d;
    }
    public static double DynamicCacheSeconds = 0.25;
    public static void ForgetMotion(OrbitalGridComponent g) { if (g != null) { System.Threading.Volatile.Write(ref g.DynAt, 0L); StaticsStale(); } }

    internal static bool IsDynamicNow(OrbitalGridComponent g)
    {
        if (!Alive(g.Entity)) return false;
        try
        {
            var rbc = g.Entity.TryGet<RigidBodyComponent>();
            if (rbc != null && rbc.CurrentMotionType != BodyArgs.Motion.Dynamic) return false;
            var motion = g.Entity.AsInterface<IPhysicsMotionProvider>();
            if (motion != null && motion.Motion != BodyArgs.Motion.Dynamic) return false;
            return g.Entity.Data.TryGet<RigidBodyData>(out _);
        }
        catch { return false; }
    }

    public static double Mass(OrbitalGridComponent g)
    {
        if (!Alive(g.Entity)) return 0;
        try { return g.Entity.Data.TryGet<RigidBodyMassProperties>(out var mp) && mp.InvMass > 0 ? 1.0 / mp.InvMass : 0; }
        catch { return 0; }
    }

    public static Vector3D Velocity(OrbitalGridComponent g)
    {
        // (no move queued - nearly always: no lock, as Position)
        if (Pending.Count > 0) lock (Pending) if (Pending.TryGetValue(g.Id, out var pm)) return pm.v;
        if (!Alive(g.Entity)) return Vector3D.Zero;
        try { return g.Entity.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero; }
        catch { return Vector3D.Zero; }
    }

    /// <summary>Set linear velocity, keep angular (the pilot's).</summary>
    /// <summary>A finite vector (NaN from the orbital math must never reach the engine).</summary>
    public static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

    public static bool SetVelocity(OrbitalGridComponent g, Vector3D v)
    {
        if (!Finite(v)) { NaNRefused++; return false; }
        if (!Alive(g.Entity)) return false;
        // (a move queued for the teleport phase writes its velocity there: one set after the move this tick - the drain's zero
        //  after a re-pin, a placement's zero - goes into the queued one, or the queued one overwrote it. 2026-10-17: single
        //  grids' long moves queue now too)
        if (Pending.Count > 0) ServerFrames.RetargetVelocity(g, v, add: false);
        try
        {
            ref RigidBodyData rb = ref g.Entity.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) return false;
            var was = rb.LinearVelocity;
            rb.LinearVelocity = (Vector3)v;
            GridEdits.Wrote(g, rb.LinearVelocity - was);   // (OrbitalApi.TryGetGridEdits: written after the physics step - no force)
            return true;
        }
        catch { return false; }
    }

    /// <summary>Add a velocity change (an acceleration × dt), keep angular.</summary>
    public static bool AddVelocity(OrbitalGridComponent g, Vector3D dv)
    {
        if (!Alive(g.Entity)) return false;
        if (Pending.Count > 0 && Finite(dv)) ServerFrames.RetargetVelocity(g, dv, add: true);   // (as SetVelocity: into a queued move's)
        try
        {
            ref RigidBodyData rb = ref g.Entity.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) return false;
            var was = rb.LinearVelocity;
            rb.LinearVelocity = was + (Vector3)dv;
            GridEdits.Wrote(g, rb.LinearVelocity - was);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// This frame's thrust delta-v (the thrust component's per-frame impulse / mass), world space,
    /// ONLY while a pilot commands thrust (VoluntaryThrustData non-zero). Without input the thrust
    /// is the inertial dampeners, and in HighSpeed they would fight the engine's own gravity step
    /// (the velocity is zeroed every tick): folding that would cancel gravity in the conic (seen in
    /// game: an unpiloted wreck climbed from a=167 to 217 km). In orbit, dampeners hold the orbit.
    /// </summary>
    public static Vector3D ThrustDv(OrbitalGridComponent g)
    {
        if (!Alive(g.Entity)) return Vector3D.Zero;
        try
        {
            if (!g.Entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt) ||
                vt.VoluntaryThrust.LengthSquared() < 1e-6f)
                return Vector3D.Zero;
            if (g.Entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ActiveThrustData>(out var at) &&
                g.Entity.Data.TryGet<RigidBodyMassProperties>(out var mp) && mp.InvMass > 0)
                return (Vector3D)WorldTransform.TransformDirection(at.ComputedThrustPerFrame, g.Entity.Data.GetWorldTransform()) * mp.InvMass;
        }
        catch { }
        return Vector3D.Zero;
    }

    /// <summary>
    /// True when the grid is held by a physics constraint (landing gear, connector, rotor, merge...).
    /// Teleporting one body of a constrained pair breaks Havok's constraint migration (seen in game:
    /// "Body was not migrated properly" assertion and a hung server thread), so such grids are not
    /// moved into frames on their own.
    /// </summary>
    public static bool IsConstrained(OrbitalGridComponent g)
    {
        if (!Alive(g.Entity)) return true;   // (gone: as unknown - not moved)
        try
        {
            var physics = g.Session?.Get<Keen.VRage.Physics.IPhysics>();
            if (physics == null || !physics.HasBody(g.Entity.DEntity)) return false;
            using (var buf = new Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalConstrained"))
            {
                physics.GetDirectlyConnectedBodies(g.Entity.DEntity, buf);
                return buf.Count > 0;
            }
        }
        catch { return true; }   // unknown: be safe, do not move it
    }


    /// <summary>
    /// Everything that must move with a grid (as the game's fast travel collects it): the grid and every
    /// entity joined to it (wheels, rotor and hinge parts, docked or locked ships), not characters (the
    /// player moves on its own). False, with nothing to move, when it is joined to something static.
    /// </summary>
    public static bool MoveSet(OrbitalGridComponent g, List<Entity> set, out string why)
    {
        why = null;
        set.Add(g.Entity);
        if (!Alive(g.Entity)) { why = "grid gone"; set.Clear(); return false; }
        try
        {
            var svc = g.Session?.Get<Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities>();
            if (svc == null) { if (IsConstrained(g)) { why = "no connection search"; set.Clear(); return false; } return true; }
            if (svc.IsConnectedToStaticEntity(g.Entity)) { why = "joined to something static"; set.Clear(); return false; }
            using (var buf = new Keen.VRage.Library.Memory.Buffer<Entity>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalMoveSet"))
            {
                var args = new Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities.SearchArguments { IncludeRoot = false, IncludeConstrained = true };
                svc.FindConnectedEntities(g.Entity, (Keen.VRage.Library.Memory.BufferReference<Entity>)buf, args);
                var en = ((Keen.VRage.Library.Memory.BufferReference<Entity>)buf).GetEnumerator();
                while (en.MoveNext()) if (en.Current != null && !set.Contains(en.Current)) set.Add(en.Current);
                en.Dispose();
            }
            // As fast travel: anything static in it (a base it is joined to) and it does not move at all.
            foreach (var e in set)
            {
                var mp = e.AsInterface<Keen.VRage.Physics.Components.IPhysicsMotionProvider>();
                if (mp != null && mp.Motion == Keen.VRage.Physics.Data.BodyArgs.Motion.Static) { why = "joined to something static"; set.Clear(); return false; }
            }
            return true;
        }
        catch (Exception e) { why = "connection search failed: " + e.Message; set.Clear(); return false; }
    }

    /// <summary>Writes refused for a non-finite value (a sign of bad orbital state upstream).</summary>
    public static int NaNRefused;

    /// <summary>Turns an entity in place by a rotation: its orientation and its spin.</summary>
    public static void Rotate(Entity e, Quaternion rot)
    {
        if (!Alive(e)) return;
        try
        {
            var wt = e.Data.GetWorldTransform();
            e.Data.SetWorldTransform(new WorldTransform(wt.Position, Quaternion.Normalize(rot * wt.Orientation)));
            ServerFrames.AeroNoteMoved(e);
            ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb)) rb.AngularVelocity = Vector3.Transform(rb.AngularVelocity, rot);
        }
        catch { }
    }

    /// <summary>Moves a DYNAMIC grid at once (orientation kept), the way the engine's own admin teleport does. A static one is
    /// refused (false; logged once per grid): a static body moved outside the teleport phase's checks can leave Havok's
    /// cluster bookkeeping stale - the "still active bodies in native world" crash (HavokMoves, 2026-10-17). A static moves
    /// through ServerFrames.MoveGrid(allowStatic: true).</summary>
    public static bool SetPosition(OrbitalGridComponent g, Vector3D p)
    {
        if (!Finite(p)) { NaNRefused++; return false; }
        if (!Alive(g.Entity)) return false;
        if (!IsDynamicNow(g)) { ServerFrames.StaticRefused(g, "SetPosition"); return false; }
        return MoveTo(g, p);
    }

    /// <summary>SetPosition's write, for a caller that asked the motion itself (ServerFrames.MoveGrid, PlaceGrid).</summary>
    internal static bool MoveTo(OrbitalGridComponent g, Vector3D p)
    {
        if (!Finite(p)) { NaNRefused++; return false; }
        if (!Alive(g.Entity)) return false;
        Unmanage(g.Entity);
        try
        {
            var wt = g.Entity.Data.GetWorldTransform();
            g.Entity.Data.SetWorldTransform(new WorldTransform(p, wt.Orientation));
            ServerFrames.AeroNoteMoved(g.Entity);
            return true;
        }
        catch { return false; }
    }
}
