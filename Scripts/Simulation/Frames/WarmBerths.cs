using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Mathematics;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// WARM BERTHS: no engine stall when a frame takes a berth. A grid landing in empty space makes the engine build a
/// physics world there (its own Havok world, server and client) and block the server tick on the procedural sectors
/// around it: ~230 ms at every split and stow (berths are 100+ km apart). Both stay alive while any dynamic body is in
/// the area. So a placeholder - a one-block grid named <see cref="Name"/>, never registered as a grid of ours
/// (OrbitalGridComponent), so no frame, contact, marker or bill ever sees it - is parked 500 m off a berth's centre
/// (a kept physics world is a 20 km cube about its first body, and procedural sectors are 4 km: a ship put at the centre lands inside both):
///  - every berth a frame holds gets one, while the frame's grids keep its area alive (no stall), so it stays warm
///    when the frame leaves (a merge, an arrival);
///  - at least <see cref="MinFree"/> free warm berths are kept: the allocator hands those out first; below that, the
///    next cold berth is warmed (that one stall, one at a time - most of them at the world's load);
///  - more than <see cref="MaxFree"/> free ones: the farthest let go (idle physics worlds do not pile up);
///  - one pushed out of place is put back (a move inside its own world: cheap);
///  - free warm berths are closed to encounters (SpawnGuard): a placeholder triggers them as any grid would.
/// Placeholders are grids, so the world saves them; a loaded one is found by its name and given its berth back
/// (one in no berth is removed).
/// </summary>
public static class WarmBerths
{
    public const string Name = "Orbital warm berth";
    public const int MinFree = 3, MaxFree = 6;
    public const double Offset = 500.0;   // m from the berth's centre (+Y): in the same procedural sectors as a ship put at the centre (4 km sectors)
    /// <summary>The block it is built of: the 0.5 m light armour cube's server prefab (Vanilla Armors/Cube/50/Light/
    /// ArmorCubeLight50_Server.def) - a full block, with its collision and mass. (A 0.25 m detailing cube has none: the
    /// physics engine could not make its body, and the half-made entity crashed Havok when its world was removed.)</summary>
    static readonly System.Guid Block = new System.Guid("632d7385-12b9-47a6-802a-a610d0cbd1e0");

    public static bool Enabled = true;
    public static string Status = "-";
    static readonly Dictionary<int, Entity> _parked = new Dictionary<int, Entity>();
    static object _session;
    static double _next, _nextCold;
    static bool _adopted;

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Its place in a berth.</summary>
    public static Vector3D PlaceOf(Vector3D berthCentre) => berthCentre + new Vector3D(0, Offset, 0);

    /// <summary>Free warm berths' centres (SpawnGuard closes them to encounters; it holds FramesLock).</summary>
    public static List<(int slot, Vector3D at)> FreeWarm()
    {
        var l = new List<(int, Vector3D)>();
        var alloc = SystemHost.Frames?.Allocator;
        if (alloc == null) return l;
        lock (ServerFrames.FramesLock) foreach (int s in alloc.WarmSlots()) if (!alloc.IsOccupied(s)) l.Add((s, alloc.SlotCenter(s)));
        return l;
    }

    // (the allocator is shared with the client thread - a stow allocates there: every use of it under FramesLock;
    //  a grid is never built while holding it)

    /// <summary>Server tick (once a frame; acts once a second).</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session server)
    {
        if (server == null || !SystemHost.Built || SystemHost.Frames == null) return;
        if (!ReferenceEquals(server, _session)) { _session = server; _parked.Clear(); _pending.Clear(); _adopted = false; _next = _nextCold = 0; }
        double now = Wall();
        if (now < _next) return;
        _next = now + 1.0;
        if (!Enabled) { Status = "off" + (Why != null ? ": " + Why : ""); return; }
        try
        {
            var alloc = SystemHost.Frames.Allocator;
                        // placeholders spawned (staged) since the last tick: found by name, given their berths; one not come in 30 s: asked again
            Adopt(server, alloc);
            foreach (var kv in new List<KeyValuePair<int, double>>(_pending)) if (now - kv.Value > 30) _pending.Remove(kv.Key);
            // gone (closed by someone, a grinder): its berth is cold again
            foreach (var kv in new List<KeyValuePair<int, Entity>>(_parked))
                if (kv.Value == null || !Alive(kv.Value)) { _parked.Remove(kv.Key); lock (ServerFrames.FramesLock) alloc.SetWarm(kv.Key, false); }
            // what to do, decided under the lock
            var held = new List<(int slot, Vector3D centre)>();
            int cold = -1; Vector3D coldAt = default; int far = -1;
            var places = new List<(Entity e, Vector3D want)>();
            int free;
            lock (ServerFrames.FramesLock)
            {
                // 1) berths frames hold: a placeholder while their grids keep the area alive (no stall)
                foreach (var f in SystemHost.Frames.Frames)
                    if (f.BerthSlotId >= 0 && !f.IsEncounter && !_parked.ContainsKey(f.BerthSlotId) && f.Members.Count > 0) held.Add((f.BerthSlotId, alloc.SlotCenter(f.BerthSlotId)));
                // 2) at least MinFree free warm berths: the next cold one warmed (a stall: one at a time)
                free = alloc.FreeWarmCount;
                if (free < MinFree && now >= _nextCold) { cold = alloc.NextColdFree(); if (cold >= 0) coldAt = alloc.SlotCenter(cold); }
                // 3) more than MaxFree free: the farthest let go
                if (free > MaxFree)
                {
                    double fd = -1;
                    foreach (int s in alloc.WarmSlots()) if (!alloc.IsOccupied(s)) { double d = alloc.SlotCenter(s).LengthSquared(); if (d > fd) { fd = d; far = s; } }
                }
                foreach (var kv in _parked) places.Add((kv.Value, PlaceOf(alloc.SlotCenter(kv.Key))));
            }
            int added = 0;
            foreach (var h in held) if (Park(server, alloc, h.slot, h.centre)) added++;
            if (cold >= 0 && !_pending.ContainsKey(cold) && Park(server, alloc, cold, coldAt))
            {
                _nextCold = now + 3.0;
                Log.Default?.Info($"[ORBIT-FRAME] warm berths: slot {cold} being warmed");
            }
            if (far >= 0) Release(server, alloc, far);
            // 4) one pushed out of place: back (inside its own physics world)
            foreach (var (e, want) in places)
                if ((e.Data.GetWorldTransform().Position - want).Length() > 50) Pin(e, want);
            int warmN, freeN; lock (ServerFrames.FramesLock) { warmN = alloc.WarmSlots().Count; freeN = alloc.FreeWarmCount; }
            Status = $"warm berths: {warmN} warm ({freeN} free, at least {MinFree}), {_parked.Count} placeholder(s){(added > 0 ? $", {added} added to held berths" : "")}";
        }
        catch (System.Exception ex) { Status = "warm berths failed: " + ex.Message; Log.Default?.Info("[ORBIT-FRAME] warm berths failed: " + ex); }
    }

    static bool Alive(Entity e) { try { return e.GetSession() != null; } catch { return false; } }

    /// <summary>Slots whose placeholder was asked for and has not shown up yet (the spawn is staged): when asked.</summary>
    static readonly Dictionary<int, double> _pending = new Dictionary<int, double>();

    static bool Park(Keen.VRage.Core.Game.Systems.Session server, SEAerospace.Frames.BerthAllocator alloc, int slot, Vector3D centre)
    {
        if (_parked.ContainsKey(slot)) { lock (ServerFrames.FramesLock) alloc.SetWarm(slot, true); return true; }
        if (_pending.ContainsKey(slot)) return false;
        if (!DefinitionManager.Instance.TryGetDefinition(Block, out PrefabDefinition pd) || pd == null) { Stop("no block prefab"); return false; }
        // (a block - its model or cube-block component - and a plain one: no hinge, rotor or other mechanical part)
        bool isBlock = false, mech = false;
        foreach (var ty in pd.Composition.Types)
        {
            if (ty.Name == "BlockModelComponent" || ty.Name == "CubeBlockComponent" || ty.Name == "ArmorBlockComponent") isBlock = true;
            if (ty.Name.Contains("Hinge") || ty.Name.Contains("Rotor") || ty.Name.Contains("Piston") || ty.Name.Contains("Mechanical") || ty.Name.Contains("Powerable")) mech = true;
        }
        if (!isBlock || mech) { Stop("the prefab is no plain block"); return false; }
        var gb = new GridBuilder(server);
        gb.SetName(Name); gb.SetDebugName(Name);
        gb.SetMotionType(Keen.VRage.Physics.Data.BodyArgs.Motion.Dynamic);   // (a static body keeps no physics world alive)
        gb.SetTransform(new WorldTransform(PlaceOf(centre), Quaternion.Identity));
        gb.AddBlock(Vector3I.Zero, new IntegerOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up), pd);
        // Spawned STAGED and async, as the game spawns encounters (and DevStress clones grids): GridBuilder.BuildOne's
        // synchronous spawn built the grid before its blocks' data existed - "Data not found" in the physics engine and in
        // the grid's anchoring test, a half-made body and two crashes. Found by its name next tick (Adopt).
        try
        {
            var bundle = new Keen.VRage.Core.Game.Systems.EntityBundle();
            bundle.Add(gb.CompileObjectBuilder(), null, true);
            server.Get<Keen.VRage.Core.Game.Systems.IEntitySpawner>().SpawnBundleAsync(new Keen.VRage.Core.Game.Systems.GameEntitySerializer.BundleInitArgs { Bundle = bundle });
        }
        catch (System.Exception ex) { Stop("spawning a placeholder threw: " + ex.Message); return false; }
        _pending[slot] = Wall();
        return true;
    }

    static void Release(Keen.VRage.Core.Game.Systems.Session server, SEAerospace.Frames.BerthAllocator alloc, int slot)
    {
        if (_parked.TryGetValue(slot, out var e)) { try { server.MarkEntityForClose(e); } catch { } _parked.Remove(slot); }
        int fw; lock (ServerFrames.FramesLock) { alloc.SetWarm(slot, false); fw = alloc.FreeWarmCount; }
        Log.Default?.Info($"[ORBIT-FRAME] warm berths: slot {slot} let go ({fw} free warm)");
    }

    static void Pin(Entity e, Vector3D p)
    {
        try
        {
            var wt = e.Data.GetWorldTransform();
            e.Data.SetWorldTransform(new WorldTransform(p, wt.Orientation));
            ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb)) { rb.LinearVelocity = default; rb.AngularVelocity = default; }
        }
        catch { }
    }

    /// <summary>A loaded world: its placeholders (saved, as grids are) given their berths back; one in no berth removed.</summary>
    static void Adopt(Keen.VRage.Core.Game.Systems.Session server, SEAerospace.Frames.BerthAllocator alloc)
    {
        int kept = 0, removed = 0;
        foreach (var e in server.GetEntitiesOfType<CubeGridComponent>())
        {
            if (!IsPlaceholder(e) || _parked.ContainsValue(e)) continue;
            Vector3D at = e.Data.GetWorldTransform().Position;
            int slot; lock (ServerFrames.FramesLock) slot = alloc.SlotNear(at - new Vector3D(0, Offset, 0), 1000);
            if (slot >= 0 && !_parked.ContainsKey(slot)) { _parked[slot] = e; _pending.Remove(slot); lock (ServerFrames.FramesLock) alloc.SetWarm(slot, true); kept++; }
            else { try { server.MarkEntityForClose(e); } catch { } removed++; }
        }
        if (kept + removed > 0) Log.Default?.Info($"[ORBIT-FRAME] warm berths: {kept} placeholder(s) in their berths{(_adopted ? "" : " (from the save)")}, {removed} in none removed");
        _adopted = true;
    }

    /// <summary>A grid entity that is one of ours (by its name): never registered (OrbitalGridComponent).</summary>
    public static bool IsPlaceholder(Entity e)
    {
        try { var g = e?.TryGet<CubeGridComponent>(); return g != null && g.DisplayName.EvaluateText() == Name; }
        catch { return false; }
    }

    public static string Why;
    static void Stop(string why) { Why = why; Enabled = false; Status = "off: " + why; Log.Default?.Info("[ORBIT-FRAME] warm berths stopped: " + why); }

    /// <summary>DEV: drop every placeholder (the berths cold again).</summary>
    public static string Clear()
    {
        var server = ServerPlanetBeacon.ServerSession; var alloc = SystemHost.Frames?.Allocator;
        if (server == null || alloc == null) return "no server";
        int n = 0;
        foreach (int s in new List<int>(_parked.Keys)) { Release(server, alloc, s); n++; }
        return $"warm berths: {n} let go";
    }
}
