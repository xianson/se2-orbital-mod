using Keen.VRage.Core;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

// ServerFrames (partial): every long move of a grid (joined groups together, deferred to the teleport phase) and the
// aero mod told of each (AeroApi.NoteMoved).
public static partial class ServerFrames
{
    /// <summary>
    /// Every long move of a grid goes through here. A grid with no joints moves at once, as it always has. A
    /// grid with joints (wheels, rotors, hinges, docked or locked ships) moves with everything joined to it,
    /// all by the same offset, where the game's fast travel moves ships (before the physics step): moved
    /// later in the frame, Havok cannot migrate the joints and the server dies. Anything static in the group
    /// (a base it is locked to) and it does not move: false. setVel: its velocity after (all of the group);
    /// addVel: added to each instead. A group already moving this tick is not moved again (true).
    /// </summary>
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, Vector3D addVel = default, Quaternion? rotate = null)
        => MoveGrid(g, target, setVel, out _, addVel, rotate);

    /// <summary>
    /// A second move of a jointed group in the same tick (a re-pin, then an arrival, a merge or a split): added to the one
    /// already queued for the whole group - it returned "moved" and did nothing (the group stayed in the empty berth while
    /// the frame arrived, or landed 400 km off the host and split off on a wrong orbit). A second turn is not composed.
    /// </summary>
    static bool AddToQueuedMove(OrbitalGridComponent g, (Vector3D p, Vector3D v) queued, Vector3D target, Vector3D? setVel, Vector3D addVel,
                                Quaternion? rotate, List<OrbitalGridComponent> with)
    {
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel)) return false;
        var set = new List<Entity>();
        if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' second move refused: {why}"); return false; }
        var inSet = new HashSet<Entity>(set);
        Vector3D delta = target - queued.p;
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
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
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, out List<OrbitalGridComponent> with, Vector3D addVel = default, Quaternion? rotate = null)
    {
        with = new List<OrbitalGridComponent>();
        (Vector3D p, Vector3D v) queued;
        bool isQueued;
        lock (GridMembers.Pending) isQueued = GridMembers.Pending.TryGetValue(g.Id, out queued);
        if (isQueued) return AddToQueuedMove(g, queued, target, setVel, addVel, rotate, with);
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel) || (setVel.HasValue && !GridMembers.Finite(setVel.Value)))
        { GridMembers.NaNRefused++; Event($"grid {g.Id} '{g.DisplayName}' not moved: non-finite target"); return false; }
        // (the motion cache - IsDynamic, 0.25 s - can still call a grid just made a station dynamic: a move would make the game
        //  turn it back. Asked live here, where it matters)
        if (g.Dyn && !GridMembers.IsDynamicNow(g)) { GridMembers.ForgetMotion(g); Event($"grid {g.Id} '{g.DisplayName}' not moved: it was just made static"); return false; }
        if (!GridMembers.IsConstrained(g))
        {
            Vector3D v = setVel ?? GridMembers.Velocity(g) + addVel;
            Vector3D from = GridMembers.Position(g);
            if (!GridMembers.SetPosition(g, target)) return false;   // (callers must not frame a grid that never got there)
            GridMembers.SetVelocity(g, v);
            if (rotate.HasValue) { GridMembers.Rotate(g.Entity, rotate.Value); NoteTargetTurn(g.Entity, from, target, rotate.Value); }
            return true;
        }
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

    /// <summary>Grid moves done where fast travel does them (ServerPlanetBeacon's teleport phase, before the physics step).</summary>
    private static readonly List<(Entity e, Vector3D p, Vector3D v)> _deferred = new List<(Entity, Vector3D, Vector3D)>();
    private static readonly Dictionary<Entity, Quaternion> _deferredRot = new Dictionary<Entity, Quaternion>();

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

    /// <summary>The queued moves, all in one go (once a frame, whichever beacon's job comes first).</summary>
    public static void RunDeferredMoves()
    {
        List<(Entity e, Vector3D p, Vector3D v)> todo;
        List<long> done;
        lock (_deferred)
        {
            if (_deferred.Count == 0) return;
            todo = new List<(Entity, Vector3D, Vector3D)>(_deferred);
            _deferred.Clear();
            lock (GridMembers.Pending) done = new List<long>(GridMembers.Pending.Keys);   // (queued with these moves, under this lock)
        }
        foreach (var (e, p, v) in todo)
        {
            try
            {
                GridMembers.Unmanage(e);   // (out of its world area first: else the game unloads and respawns it)
                var wt = e.Data.GetWorldTransform();
                Quaternion rot; bool turn; lock (_deferredRot) { turn = _deferredRot.TryGetValue(e, out rot); if (turn) _deferredRot.Remove(e); }
                e.Data.SetWorldTransform(new WorldTransform(p, turn ? Quaternion.Normalize(rot * wt.Orientation) : wt.Orientation));
                AeroNoteMoved(e);   // (its next physics contacts are the move's, not loads)
                if (turn) NoteTargetTurn(e, wt.Position, p, rot);
                ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb))
                {
                    var was = rb.LinearVelocity;
                    rb.LinearVelocity = (Vector3)v; if (turn) rb.AngularVelocity = Vector3.Transform(rb.AngularVelocity, rot);
                    GridEdits.Wrote(e, rb.LinearVelocity - was);   // (OrbitalApi.TryGetGridEdits; a wheel or rotor part of the set: not recorded)
                }
            }
            catch (Exception ex) { Event("deferred move failed: " + ex.Message); }
        }
        lock (GridMembers.Pending) foreach (long id in done) GridMembers.Pending.Remove(id);
        Event($"moved {todo.Count} grid(s) in the teleport phase");
    }
}
