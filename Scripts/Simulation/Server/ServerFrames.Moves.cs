using Keen.VRage.Core;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): every long move of a grid (joined groups together, deferred to the teleport phase) and the
// aero mod told of each (AeroApi.NoteMoved).
public static partial class ServerFrames
{
    /// <summary>
    /// Every long move of a grid goes through here (HavokMoves.Choose). A dynamic grid with no joints moves at once within
    /// HavokMoves.OrdinaryStep (100 m: what the engine treats as ordinary motion); a longer move is a teleport, queued for
    /// the teleport phase where the game's fast travel moves ships (before the physics step). A grid with joints (wheels,
    /// rotors, hinges, docked or locked ships) moves there with everything joined to it, all by the same offset: moved later
    /// in the frame, Havok cannot migrate the joints and the server dies. Anything static in the group (a base it is locked
    /// to) and it does not move: false. A STATIC grid is refused (false, logged once per grid) unless the caller says the move
    /// is meant (allowStatic: an arrival, its HighSpeed conic, a re-pin, a berth shift, a split, a spawn moved off a border);
    /// then it goes to the teleport phase too, where RunDeferredMoves checks its landing against the physics worlds
    /// (HavokMoves.StaticMoveSafe - the "still active bodies in native world" crash, 2026-10-17). setVel: its velocity after
    /// (all of the group); addVel: added to each instead. A group already moving this tick is not moved again (true).
    /// </summary>
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, Vector3D addVel = default, Quaternion? rotate = null, bool allowStatic = false)
        => MoveGrid(g, target, setVel, out _, addVel, rotate, allowStatic);

    /// <summary>
    /// A second move of a queued grid in the same tick (a re-pin, then an arrival, a merge or a split): added to the one
    /// already queued for the whole group - it returned "moved" and did nothing (the group stayed in the empty berth while
    /// the frame arrived, or landed 400 km off the host and split off on a wrong orbit). A second turn is not composed.
    /// single: a grid queued on its own (a static, or a long move with no joints): its own entry, no joint search (a static's
    /// search refuses it - it is static itself).
    /// </summary>
    static bool AddToQueuedMove(OrbitalGridComponent g, (Vector3D p, Vector3D v) queued, Vector3D target, Vector3D? setVel, Vector3D addVel,
                                Quaternion? rotate, List<OrbitalGridComponent> with, bool single)
    {
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel)) return false;
        var set = new List<Entity>();
        if (single) set.Add(g.Entity);
        else if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' second move refused: {why}"); return false; }
        var inSet = new HashSet<Entity>(set);
        Vector3D delta = target - queued.p;
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        if (single) byEntity[g.Entity] = g;
        else foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
        lock (_deferred)
            for (int i = 0; i < _deferred.Count; i++)
            {
                var d = _deferred[i];
                if (!inSet.Contains(d.Item1)) continue;
                var np = d.Item2 + delta; var nv = setVel ?? d.Item3 + addVel;
                if (!GridMembers.Finite(np) || !GridMembers.Finite(nv)) continue;
                _deferred[i] = (d.Item1, np, nv);
                if (byEntity.TryGetValue(d.Item1, out var og))
                {
                    lock (GridMembers.Pending) GridMembers.Pending[og.Id] = (np, nv);
                    if (og != g) with.Add(og);
                }
            }
        if (rotate.HasValue) Event($"grid {g.Id} '{g.DisplayName}': a second move this tick turned nothing (only the first move's turn is applied)");
        Event($"grid {g.Id} '{g.DisplayName}': a second move this tick, {delta.Length() / 1000:F1} km more, added to the queued one");
        return true;
    }

    /// <summary>As above; with: the other grids that move with it (its joined group), for the caller's frame bookkeeping.</summary>
    /// <summary>rotate: turns the grid (its orientation and spin; a joined set rigidly, about this grid) - crossing between
    /// a planet's rotating chart and the inertial frames, positions and velocities were turned and orientations not: the
    /// ship arrived rotated against its own motion (and its cockpit's target, FrameHost, with it).</summary>
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, out List<OrbitalGridComponent> with, Vector3D addVel = default, Quaternion? rotate = null, bool allowStatic = false)
    {
        with = new List<OrbitalGridComponent>();
        // (asked live: the motion cache - IsDynamic, 0.25 s - can still call a grid just made a station dynamic)
        bool isStatic = !GridMembers.IsDynamicNow(g);
        if (isStatic && !allowStatic)
        {
            // (a move would make the game turn it back: the cache refreshed, said as before)
            if (g.Dyn) { GridMembers.ForgetMotion(g); Event($"grid {g.Id} '{g.DisplayName}' not moved: it was just made static"); return false; }
            StaticRefused(g, "MoveGrid");
            return false;
        }
        (Vector3D p, Vector3D v) queued;
        bool isQueued;
        lock (GridMembers.Pending) isQueued = GridMembers.Pending.TryGetValue(g.Id, out queued);
        if (isQueued) return AddToQueuedMove(g, queued, target, setVel, addVel, rotate, with, single: isStatic || !GridMembers.IsConstrained(g));
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel) || (setVel.HasValue && !GridMembers.Finite(setVel.Value)))
        { GridMembers.NaNRefused++; FrameHost.Problem($"grid {g.Id} '{g.DisplayName}' not moved: non-finite target"); return false; }
        bool constrained = GridMembers.IsConstrained(g);
        Vector3D from = GridMembers.Position(g);
        var route = HavokMoves.Choose(isStatic, allowStatic, constrained, (target - from).Length());
        if (route == HavokMoves.Route.Direct)
        {
            Vector3D v = setVel ?? GridMembers.Velocity(g) + addVel;
            if (!GridMembers.MoveTo(g, target)) return false;   // (callers must not frame a grid that never got there)
            GridMembers.SetVelocity(g, v);
            if (rotate.HasValue) { GridMembers.Rotate(g.Entity, rotate.Value); NoteTargetTurn(g.Entity, from, target, rotate.Value); }
            HavokAudit.NoteMove(HavokAudit.MoveDirect, g.Id, from, target);
            return true;
        }
        // The teleport phase. On its own (a static, or a long move with no joints): just this grid, quietly and with no
        // list (a HighSpeed conic moves one every tick); its turn is about itself, so it lands on the target.
        if (!constrained)
        {
            Vector3D v1 = setVel ?? GridMembers.Velocity(g) + addVel;
            if (!GridMembers.Finite(v1)) return false;   // (never hand Havok a NaN)
            lock (_deferred)
            {
                _deferred.Add((g.Entity, target, v1)); _deferredQuiet++;
                if (rotate.HasValue) lock (_deferredRot) _deferredRot[g.Entity] = rotate.Value;
                lock (GridMembers.Pending) GridMembers.Pending[g.Id] = (target, v1);
            }
            return true;
        }
        // With joints: everything joined to it (a static in it, and nothing moves).
        var set = new List<Entity>();
        if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' not moved: {why}"); return false; }
        Vector3D delta = target - GridMembers.Position(g);
        Vector3D pivot = GridMembers.Position(g);
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
        lock (_deferred)
            foreach (var e in set)
            {
                Vector3D v0 = e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
                Vector3D v = setVel ?? v0 + addVel;
                Vector3D p = e.Data.GetWorldTransform().Position + delta;
                if (rotate.HasValue) p = target + Vector3D.Transform(e.Data.GetWorldTransform().Position - pivot, rotate.Value);   // (the set turns rigidly about this grid)
                if (!GridMembers.Finite(p) || !GridMembers.Finite(v)) continue;   // (never hand Havok a NaN)
                _deferred.Add((e, p, v));
                if (rotate.HasValue) lock (_deferredRot) _deferredRot[e] = rotate.Value;
                if (byEntity.TryGetValue(e, out var og))
                {
                    lock (GridMembers.Pending) GridMembers.Pending[og.Id] = (p, v);
                    if (og != g) with.Add(og);
                }
            }
        Event($"grid {g.Id} '{g.DisplayName}' and {set.Count - 1} joined entit(ies): moving {delta.Length() / 1000:F1} km before the next physics step");
        return true;
    }

    /// <summary>
    /// A per-tick placement (a fast member of a frame you are not in, put on its own orbit each tick): a dynamic grid's
    /// ordinary step (HavokMoves.OrdinaryStep) is written at once, with no allocation - the engine sees it as any motion; a
    /// longer step, a static, or a grid already queued goes through MoveGrid (the teleport phase; a static's landing
    /// checked). Its velocity is the caller's to set after (it reaches a queued move too - GridMembers.SetVelocity).
    /// </summary>
    internal static bool PlaceGrid(OrbitalGridComponent g, Vector3D p)
    {
        if (!GridMembers.Finite(p)) { GridMembers.NaNRefused++; return false; }
        bool queued = false;
        if (GridMembers.Pending.Count > 0) lock (GridMembers.Pending) queued = GridMembers.Pending.ContainsKey(g.Id);
        if (!queued && GridMembers.TryPosition(g, out Vector3D at) && (p - at).Length() <= HavokMoves.OrdinaryStep && GridMembers.IsDynamicNow(g))
        {
            if (!GridMembers.MoveTo(g, p)) return false;
            HavokAudit.NoteMove(HavokAudit.MoveDirect, g.Id, at, p);
            return true;
        }
        return MoveGrid(g, p, null, allowStatic: true);
    }

    /// <summary>A move refused because the grid is static (logged once per grid; HavokMoves). Count for the harness.</summary>
    public static int StaticMovesRefused;
    static readonly HashSet<long> _staticRefusedLogged = new HashSet<long>();
    internal static void StaticRefused(OrbitalGridComponent g, string by)
    {
        System.Threading.Interlocked.Increment(ref StaticMovesRefused);
        HavokAudit.NoteMove(HavokAudit.MoveRefused, g.Id, GridMembers.Position(g), GridMembers.Position(g));
        bool first; lock (_staticRefusedLogged) first = _staticRefusedLogged.Add(g.Id);
        if (first) Event($"grid {g.Id} '{g.DisplayName}' not moved: it is static ({by}; a static is moved only where it is meant to be - its landing checked against the physics worlds in the teleport phase)");
    }

    /// <summary>
    /// A velocity written after a queued move this tick (GridMembers.SetVelocity / AddVelocity): into the queued move's, which
    /// the teleport phase writes - it overwrote the later write before (a re-pinned anchor's drain to zero came back as its old
    /// velocity, drained twice). add: dv added to the queued velocity. Server thread; lock order _deferred, then Pending.
    /// </summary>
    internal static void RetargetVelocity(OrbitalGridComponent g, Vector3D v, bool add)
    {
        var e = g.Entity;
        if (e == null) return;
        lock (_deferred)
        {
            (Vector3D p, Vector3D v) q;
            lock (GridMembers.Pending) { if (!GridMembers.Pending.TryGetValue(g.Id, out q)) return; }
            Vector3D nv = add ? q.v + v : v;
            if (!GridMembers.Finite(nv)) return;
            for (int i = 0; i < _deferred.Count; i++)
                if (ReferenceEquals(_deferred[i].e, e)) _deferred[i] = (e, _deferred[i].p, nv);
            lock (GridMembers.Pending) GridMembers.Pending[g.Id] = (q.p, nv);
        }
    }

    /// <summary>An entity's move queued for the teleport phase by itself (no grid of ours: a warm berth's placeholder). Its
    /// landing is checked there as any static's would be.</summary>
    internal static void QueueEntityMove(Entity e, Vector3D p, Vector3D v)
    {
        if (e == null || !GridMembers.Finite(p) || !GridMembers.Finite(v)) return;
        lock (_deferred) { _deferred.Add((e, p, v)); _deferredQuiet++; }
    }

    /// <summary>Grid moves done where fast travel does them (ServerPlanetBeacon's teleport phase, before the physics step).</summary>
    private static readonly List<(Entity e, Vector3D p, Vector3D v)> _deferred = new List<(Entity, Vector3D, Vector3D)>();
    private static readonly Dictionary<Entity, Quaternion> _deferredRot = new Dictionary<Entity, Quaternion>();
    /// <summary>Entries of _deferred queued quietly (single grids - a HighSpeed conic queues one every tick): the teleport
    /// phase's "moved N grid(s)" line only for a batch with more than these (a joined set, the dev move). Under _deferred.</summary>
    private static int _deferredQuiet;
    /// <summary>Statics given a first hop clear of every world (HavokMoves.TryParkPoint): their real move is the next frame's.</summary>
    private static readonly HashSet<Entity> _parkedStatics = new HashSet<Entity>();
    private static BoundingBoxD[] _worldBoxes = new BoundingBoxD[16];
    /// <summary>Statics parked (two hops) and parks that found no clear place (moved straight), for the harness.</summary>
    public static int StaticParks, StaticParkFailed;

    /// <summary>Piloted grids the server just turned (crossing between a planet's rotating chart and the inertial frames): the
    /// client turns their cockpit's target with them (TargetControlData is the client's - what the mouse moves; left
    /// unturned, the gyros swung the ship back by the chart's angle: 80 deg within 2 s of the handover, seated - DevReentry).
    /// from / to: the grid's position before and after (the client's copy is near one of them).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(Vector3D from, Vector3D to, Quaternion rot)> TargetTurns = new System.Collections.Concurrent.ConcurrentQueue<(Vector3D, Vector3D, Quaternion)>();

    /// <summary>Grids moved across a planet's chart border (rails <-> the planet's frame), counted: on rails a grid sits
    /// still in its berth while its frame carries the orbit, so a crossing steps its speed (0 <-> ~1 km/s) in one tick -
    /// the client quiets the game's acceleration camera shake for it (FrameHost; it shook the cockpit 19 deg).</summary>
    public static int ChartCrossings;


    static void NoteTargetTurn(Entity e, Vector3D from, Vector3D to, Quaternion rot)
    {
        System.Threading.Interlocked.Increment(ref ChartCrossings);
        try { if (e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) TargetTurns.Enqueue((from, to, rot)); } catch { }
    }

    /// <summary>Tell the aero mod a grid was moved (AeroApi.NoteMoved: its structure ignores the physics contacts of the next
    /// steps - a teleport is no collision). Silent when the aero mod is absent or older.</summary>
    public static void AeroNoteMoved(Entity e)
    {
        if (e == null) return;
        GridEdits.Moved(e);   // (the move stamp - OrbitalApi.TryGetGridEdits: the pull side of this push)
        // (under FramesLock - a frame step's moves: told right after the lock, FlushAeroNoteMoved, still before the next physics
        //  step; never a call into aero while holding our lock - API.md's lock rule)
        if (System.Threading.Monitor.IsEntered(FramesLock)) { lock (_noteMovedLater) _noteMovedLater.Add(e); return; }
        CallNoteMoved(e);
    }

    /// <summary>The moves made under FramesLock, told to the aero mod (after the lock).</summary>
    public static void FlushAeroNoteMoved()
    {
        Entity[] todo;
        lock (_noteMovedLater) { if (_noteMovedLater.Count == 0) return; todo = _noteMovedLater.ToArray(); _noteMovedLater.Clear(); }
        foreach (var e in todo) CallNoteMoved(e);
    }
    static readonly List<Entity> _noteMovedLater = new List<Entity>();

    static void CallNoteMoved(Entity e)
    {
        // (bound once per aero assembly - AeroLink, the last loaded copy: an aero mod that loads later or is reloaded is bound
        //  again; bound once for good, a reload kept the stale copy, 2026-10-15. A typed delegate: no argument array, no
        //  Invoke per move)
        int gen = AeroLink.Generation;
        if (gen != System.Threading.Volatile.Read(ref _noteMovedGen))
        {
            var m = AeroLink.Find("AeroMod.AeroApi")?.GetMethod("NoteMoved", new[] { typeof(Entity) });   // (explicit parameter types: a later overload must not make it ambiguous)
            var bound = m == null ? null : (Action<Entity>)Delegate.CreateDelegate(typeof(Action<Entity>), m, false);   // (an unexpected signature: as absent)
            System.Threading.Volatile.Write(ref _noteMoved, bound);
            System.Threading.Volatile.Write(ref _noteMovedGen, gen);
        }
        var fn = System.Threading.Volatile.Read(ref _noteMoved);
        if (fn == null) return;
        try { fn(e); } catch { }
    }
    static Action<Entity> _noteMoved; static int _noteMovedGen = -1;

    /// <summary>The queued moves, all in one go (once a frame, whichever beacon's job comes first). A static's landing is
    /// checked against the physics worlds first (HavokMoves.StaticMoveSafe); one that would land beside a world that only
    /// overlaps it hops clear of every world now and lands next frame (its queued place kept for Position meanwhile).</summary>
    public static void RunDeferredMoves()
    {
        // (one run at a time - every planet's beacon calls this - so the scratch lists are reused: a HighSpeed conic queues a
        //  move every tick, two new lists a tick otherwise)
        if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try { RunDeferredMovesOnce(); }
        finally { System.Threading.Volatile.Write(ref _running, 0); }
    }
    static int _running;
    static readonly List<(Entity e, Vector3D p, Vector3D v)> _todo = new List<(Entity, Vector3D, Vector3D)>();
    static readonly List<long> _done = new List<long>();
    static readonly List<(Entity e, Vector3D p, Vector3D v)> _requeue = new List<(Entity, Vector3D, Vector3D)>();

    static void RunDeferredMovesOnce()
    {
        var todo = _todo; var done = _done; todo.Clear(); done.Clear(); _requeue.Clear();
        int quiet;
        lock (_deferred)
        {
            if (_deferred.Count == 0) return;
            todo.AddRange(_deferred);
            _deferred.Clear();
            quiet = _deferredQuiet; _deferredQuiet = 0;
            lock (GridMembers.Pending) done.AddRange(GridMembers.Pending.Keys);   // (queued with these moves, under this lock)
        }
        int worlds = -1;   // (the physics worlds' boxes, read once a run - at the first static)
        foreach (var (e, p, v) in todo)
        {
            // (deleted since it was queued: skipped before its transform is read - that threw, ~200 ms - 2026-10-15)
            if (!EntityAlive(e)) { lock (_deferredRot) _deferredRot.Remove(e); _parkedStatics.Remove(e); continue; }
            try
            {
                GridMembers.Unmanage(e);   // (out of its world area first: else the game unloads and respawns it)
                var wt = e.Data.GetWorldTransform();
                // A static: its landing checked (2026-10-17, the "still active bodies in native world" crash - HavokMoves).
                // Parked last frame: in no world now, it lands safely anywhere.
                bool parked = _parkedStatics.Count > 0 && _parkedStatics.Remove(e);
                if (!parked && IsStaticBody(e))
                {
                    if (worlds < 0) worlds = LoadWorldBoxes();
                    if (worlds > 0 && ParkFirst(e, wt, p, worlds, out Vector3D hop))
                    {
                        e.Data.SetWorldTransform(new WorldTransform(hop, wt.Orientation));
                        AeroNoteMoved(e);
                        _parkedStatics.Add(e);
                        _requeue.Add((e, p, v));   // (its turn, if any, stays queued in _deferredRot)
                        StaticParks++;
                        long pid = e.TryGet<OrbitalGridComponent>()?.Id ?? 0;
                        HavokAudit.NoteMove(HavokAudit.MovePark, pid, wt.Position, hop);
                        Event($"static grid {pid}: its landing {ServerPlanetBeacon.Fmt(p)} is beside a physics world that only overlaps it - " +
                              $"first clear of every world ({(hop - wt.Position).Length() / 1000:F0} km), there next frame (HavokMoves)");
                        continue;
                    }
                }
                Quaternion rot; bool turn; lock (_deferredRot) { turn = _deferredRot.TryGetValue(e, out rot); if (turn) _deferredRot.Remove(e); }
                e.Data.SetWorldTransform(new WorldTransform(p, turn ? Quaternion.Normalize(rot * wt.Orientation) : wt.Orientation));
                AeroNoteMoved(e);   // (its next physics contacts are the move's, not loads)
                HavokAudit.NoteMove(HavokAudit.MoveTeleport, e.TryGet<OrbitalGridComponent>()?.Id ?? 0, wt.Position, p);
                if (turn) NoteTargetTurn(e, wt.Position, p, rot);
                ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb))
                {
                    var was = rb.LinearVelocity;
                    rb.LinearVelocity = (Vector3)v; if (turn) rb.AngularVelocity = Vector3.Transform(rb.AngularVelocity, rot);
                    GridEdits.Wrote(e, rb.LinearVelocity - was);   // (OrbitalApi.TryGetGridEdits; a wheel or rotor part of the set: not recorded)
                }
            }
            catch (Exception ex) { FrameHost.Problem("deferred move failed: " + ex.Message); }
        }
        if (_requeue.Count > 0)
        {
            // (a parked static's real move: next frame; its queued place stays in Pending - Position answers it meanwhile)
            lock (_deferred) { _deferred.AddRange(_requeue); _deferredQuiet += _requeue.Count; }
            foreach (var r in _requeue) { long id = r.e.TryGet<OrbitalGridComponent>()?.Id ?? 0; if (id != 0) done.Remove(id); }
        }
        lock (GridMembers.Pending) foreach (long id in done) GridMembers.Pending.Remove(id);
        HavokAudit.SampleSoon();   // (the physics step right after this applies them: the audit looks after it)
        if (todo.Count > quiet) Event($"moved {todo.Count} grid(s) in the teleport phase");
    }

    /// <summary>A static body (as fast travel asks it: IPhysicsMotionProvider).</summary>
    static bool IsStaticBody(Entity e)
    {
        var mp = e.AsInterface<Keen.VRage.Physics.Components.IPhysicsMotionProvider>();
        return mp != null && mp.Motion == Keen.VRage.Physics.Data.BodyArgs.Motion.Static;
    }

    /// <summary>The physics worlds' boxes (IPhysicsWorldManager.GetWorlds, public) into _worldBoxes; their count, 0 when there
    /// is no manager to ask (then nothing is parked: moved as before).</summary>
    static int LoadWorldBoxes()
    {
        var mgr = ServerPlanetBeacon.ServerSession?.Get<Keen.VRage.Physics.IPhysics>()?.WorldManager;
        if (mgr == null) return 0;
        using (var buf = new Keen.VRage.Library.Memory.Buffer<Keen.VRage.Physics.IPhysicsWorldManager.WorldInfo>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalWorlds"))
        {
            mgr.GetWorlds(buf);
            int n = buf.Count;
            if (_worldBoxes.Length < n) _worldBoxes = new BoundingBoxD[Math.Max(n, _worldBoxes.Length * 2)];
            for (int i = 0; i < n; i++) _worldBoxes[i] = buf[i].Bounds;
            return n;
        }
    }

    /// <summary>True (and the first hop) when a static's move to p would land in HavokMoves' leaking case: its body as a
    /// sphere - its box's centre and half-diagonal (any turn queued with it applied to the centre's offset).</summary>
    static bool ParkFirst(Entity e, WorldTransform wt, Vector3D p, int n, out Vector3D hop)
    {
        hop = wt.Position;
        var box = BoundingBoxData.TryGetWorldAABB(e.Data);
        Vector3D c = box.HasValue ? box.Value.Center : wt.Position;
        double r = box.HasValue ? box.Value.HalfExtents.Length() : HavokMoves.OrdinaryStep;   // (no box: a generous guess)
        Vector3D off = c - wt.Position;
        Vector3D offTo = off;
        lock (_deferredRot) if (_deferredRot.TryGetValue(e, out var rot)) offTo = Vector3D.Transform(off, rot);
        Vector3D to = p + offTo;
        if (HavokMoves.StaticMoveSafe(c, to, r, _worldBoxes, n)) return false;
        if (!HavokMoves.TryParkPoint(to, r, _worldBoxes, n, out Vector3D park))
        {
            StaticParkFailed++;
            FrameHost.Problem($"static grid {e.TryGet<OrbitalGridComponent>()?.Id ?? 0}: its landing is beside a physics world, and no place clear of every world was found - moved straight");
            return false;
        }
        hop = park - off;
        return true;
    }
}
