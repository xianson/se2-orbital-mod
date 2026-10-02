using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Injected into SERVER grid compositions (CubeGridComponent). Registers the grid so the server-side
/// frame manager (<see cref="ServerFrames"/>) can frame it. Unpiloted grids are simulated on the
/// server in SE2 (the aero mod applies its forces there), so all grid physics access is server-side.
/// </summary>
public class OrbitalGridComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly CubeGridComponent _grid;

    internal long Id;
    internal Keen.VRage.Core.Game.Systems.Session Session;
    /// <summary>True for the server copy (SE2 builds grids from the same composition on both sides).</summary>
    internal bool IsServer => Session != null && ReferenceEquals(Session, ServerPlanetBeacon.ServerSession);

    void IInSceneListener.OnAddedToScene() { Session = Entity.GetSession(); Id = GridMembers.Register(this); }
    void IInSceneListener.OnBeforeRemovedFromScene() { GridMembers.Unregister(this); }

    internal string DisplayName
    {
        get { try { return _grid?.DisplayName.EvaluateText() ?? "grid"; } catch { return "grid"; } }
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

    internal static long Register(OrbitalGridComponent g)
    {
        long nid;
        lock (_lock) { nid = ++_next; _byId[nid] = g; }
        EncounterFrames.OnGridRegistered(nid);
        return nid;
    }

    internal static void Unregister(OrbitalGridComponent g)
    {
        lock (_lock) { _byId.Remove(g.Id); }
        ServerFrames.Forget(g.Id);   // (per-grid state dies with the grid)
    }

    public static List<OrbitalGridComponent> All()
    {
        lock (_lock) return new List<OrbitalGridComponent>(_byId.Values);
    }

    public static OrbitalGridComponent Get(long id)
    {
        lock (_lock) return _byId.TryGetValue(id, out var g) ? g : null;
    }

    // ── physics (server thread) ──

    /// <summary>Where a grid is, or where it is going this tick (a jointed group's move waits for the physics step).</summary>
    public static Vector3D Position(OrbitalGridComponent g)
    {
        lock (Pending) if (Pending.TryGetValue(g.Id, out var pm)) return pm.p;
        return g.Entity.Data.GetWorldTransform().Position;
    }

    /// <summary>Grids with a move queued for the next physics step (ServerFrames.MoveGrid): id -> target, velocity then.</summary>
    public static readonly Dictionary<long, (Vector3D p, Vector3D v)> Pending = new Dictionary<long, (Vector3D, Vector3D)>();

    public static bool IsDynamic(OrbitalGridComponent g)
    {
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
        try { return g.Entity.Data.TryGet<RigidBodyMassProperties>(out var mp) && mp.InvMass > 0 ? 1.0 / mp.InvMass : 0; }
        catch { return 0; }
    }

    public static Vector3D Velocity(OrbitalGridComponent g)
    {
        lock (Pending) if (Pending.TryGetValue(g.Id, out var pm)) return pm.v;
        try { return g.Entity.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero; }
        catch { return Vector3D.Zero; }
    }

    /// <summary>Set linear velocity, keep angular (the pilot's).</summary>
    /// <summary>A finite vector (NaN from the orbital math must never reach the engine).</summary>
    public static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

    public static bool SetVelocity(OrbitalGridComponent g, Vector3D v)
    {
        if (!Finite(v)) { NaNRefused++; return false; }
        try
        {
            ref RigidBodyData rb = ref g.Entity.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) return false;
            rb.LinearVelocity = (Vector3)v;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Add a velocity change (an acceleration × dt), keep angular.</summary>
    public static bool AddVelocity(OrbitalGridComponent g, Vector3D dv)
    {
        try
        {
            ref RigidBodyData rb = ref g.Entity.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) return false;
            rb.LinearVelocity = rb.LinearVelocity + (Vector3)dv;
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

    /// <summary>Move the grid (orientation kept), the way the engine's own admin teleport does.</summary>
    /// <summary>Writes refused for a non-finite value (a sign of bad orbital state upstream).</summary>
    public static int NaNRefused;

    public static bool SetPosition(OrbitalGridComponent g, Vector3D p)
    {
        if (!Finite(p)) { NaNRefused++; return false; }
        try
        {
            var wt = g.Entity.Data.GetWorldTransform();
            g.Entity.Data.SetWorldTransform(new WorldTransform(p, wt.Orientation));
            return true;
        }
        catch { return false; }
    }
}
