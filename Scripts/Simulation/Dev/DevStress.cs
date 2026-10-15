using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// HARNESS: load tests for big ships (the aero mod's cost on giant grids). gridhold keeps a grid moving forward at
/// a speed (so per-frame aero runs on it); breakblocks destroys its blocks at a steady rate through the game's own
/// damage (as combat or a crash does). Server side; read with the status line 'frame' (worst / mean tick).
/// </summary>
public static class DevStress
{
    static readonly Dictionary<long, double> _hold = new Dictionary<long, double>();
    static (long id, int left, double every, double next) _break;
    static double _breakRadius;          // > 0: break near one spot (a hit), not anywhere
    static Vector3D? _breakCentre;
    static readonly Random _rng = new Random(7);
    public static string Status = "-";

    public static string Hold(long id, double speed)
    {
        lock (_hold) { if (speed <= 0) _hold.Remove(id); else _hold[id] = speed; }
        return speed <= 0 ? $"grid {id} released" : $"grid {id} held at {speed:F0} m/s forward";
    }

    /// <summary>Set a grid's angular velocity (world, rad/s): a knock, to see it recover.</summary>
    public static string Spin(long id, Vector3 w)
    {
        var g = GridMembers.Get(id);
        if (g == null || !g.IsServer) return "no such grid";
        ref var rb = ref g.Entity.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref rb)) return "no rigid body";
        rb.AngularVelocity = w;
        return $"grid {id} spun to {w}";
    }

    /// <summary>A grid's orientation and spin, for the harness.</summary>
    public static string Attitude(long id)
    {
        var g = GridMembers.Get(id);
        if (g == null || !g.IsServer) return "no such grid";
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        var w = g.Entity.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? rb.AngularVelocity : Vector3.Zero;
        return $"grid {id} q=({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) w=({w.X:F3},{w.Y:F3},{w.Z:F3}) |w|={w.Length():F4}";
    }

    static (long id, int count, double spacing) _clone;

    /// <summary>gridclone: copies of a grid (serialized and spawned as the game spawns split grids), in rows of
    /// ten, `spacing` metres apart, above it: many big grids at once, for the aero mod's frame budget.</summary>
    public static string Clone(long id, int count, double spacing)
    {
        _clone = (id, count, spacing);
        return $"cloning grid {id} x{count}, {spacing:F0} m apart (server, next tick)";
    }

    static void CloneTick()
    {
        var c = _clone; _clone = default;
        var g = GridMembers.Get(c.id);
        if (g == null || !g.IsServer) { Status = "clone: no such grid"; return; }
        var top = g.Entity;
        var session = top.GetSession();
        var ser = session.EntitySerializer;
        var spawner = session.Get<Keen.VRage.Core.Game.Systems.IEntitySpawner>();
        var wt = top.Data.GetWorldTransform();
        var q = (QuaternionD)wt.Orientation;
        Vector3D right = q * Vector3D.Right, up = q * Vector3D.Up;
        int made = 0;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cgrid = top.TryGet<CubeGridComponent>();
        // serialized once, as the game's undo of a grid deletion keeps a grid: the grid and its own blocks only -
        // nothing else (inventories' and owners' entities stay out: the blocks' components make their own); each
        // copy is a clone of that (new entities throughout)
        var source = new Keen.VRage.Core.Game.Systems.EntityBundle();
        ser.Serialize(top, source, (e, _) =>
        {
            var cg = e.TryGet<CubeGridComponent>();
            if (cg != null) return e == top;
            var cb = e.TryGet<CubeBlockComponent>();
            return cb != null && cb.Grid == cgrid;
        }, new Keen.VRage.Core.Game.Systems.GameEntitySerializer.BundleSerializationParameters());
        for (int i = 1; i <= c.count; i++)
        {
            var bundle = Keen.VRage.Core.Game.Systems.EntityBundleFunctions.CloneBundleCollectClonedInstances(source).Bundle;
            var offset = right * ((i % 10) * c.spacing) + up * ((i / 10 + 1) * c.spacing);
            Keen.VRage.Core.Game.Systems.EntityBundleFunctions.TransformBundle(bundle, new WorldTransform(offset, Quaternion.Identity));
            // (staged and async, as the game spawns encounters: the synchronous SpawnBundle builds the grid before its
            //  blocks' own data exists - "Data not found" in an inventory's mass - on every grid tried)
            spawner.SpawnBundleAsync(new Keen.VRage.Core.Game.Systems.GameEntitySerializer.BundleInitArgs { Bundle = bundle });
            made++;
        }
        Status = $"cloned grid {c.id} x{made} in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms";
    }

    static (long id, double speed) _dive;

    /// <summary>gridvel: a grid sent straight down (along the gravity where it is) at a speed, its hold released -
    /// a crash into the planet, for the aero mod under real impact damage.</summary>
    public static string Dive(long id, double speed)
    {
        lock (_hold) _hold.Remove(id);
        _dive = (id, speed);
        return $"grid {id} sent down at {speed:F0} m/s (server, next tick)";
    }

    static void DiveTick()
    {
        var d = _dive; _dive = default;
        var g = GridMembers.Get(d.id);
        if (g == null || !g.IsServer) { Status = "dive: no such grid"; return; }
        var grav = g.Entity.Data.TryGet<Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData>(out var ge) ? ge.GravitySum : Vector3.Zero;
        if (grav.LengthSquared() < 1e-4f) { Status = "dive: no gravity here"; return; }
        GridMembers.SetVelocity(g, (Vector3D)Vector3.Normalize(grav) * d.speed);
        Status = $"grid {d.id} diving at {d.speed:F0} m/s";
    }

    static (long id, double thickness) _cut;

    /// <summary>cutgrid: every block within thickness/2 of the grid's middle (along its longest axis), in one tick -
    /// the grid falls in two (a split, for the aero mod's broken-off pieces).</summary>
    public static string Cut(long id, double thickness)
    {
        _cut = (id, thickness);
        return $"cutting grid {id} through its middle, {thickness:F1} m thick (server, next tick)";
    }

    static void CutTick()
    {
        var c = _cut; _cut = default;
        var g = GridMembers.Get(c.id);
        if (g == null || !g.IsServer) { Status = "cut: no such grid"; return; }
        var grid = g.Entity.TryGet<CubeGridComponent>();
        var recv = g.Entity.TryGet<GridDamageReceiverComponent>();
        var all = new List<CubeBlockComponent>();
        grid?.VisitAllBlocksWithComponent<CubeBlockComponent>(b => { if (b != null) all.Add(b); }, false);
        if (all.Count == 0) { Status = "cut: no blocks"; return; }
        var gwt = g.Entity.Data.GetWorldTransform();
        var local = all.Select(b => (b, p: WorldTransform.TransformDirectionInv((Vector3)(b.Entity.Data.GetWorldTransform().Position - gwt.Position), gwt))).ToList();
        var lo = local.Aggregate(new Vector3(float.MaxValue), (m, t) => Vector3.Min(m, t.p)); var hi = local.Aggregate(new Vector3(float.MinValue), (m, t) => Vector3.Max(m, t.p));
        var ext = hi - lo; int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
        float mid = axis == 0 ? (lo.X + hi.X) / 2 : axis == 1 ? (lo.Y + hi.Y) / 2 : (lo.Z + hi.Z) / 2;
        int done = 0;
        foreach (var (b, p) in local)
        {
            float a = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
            if (MathF.Abs(a - mid) <= c.thickness / 2) { recv?.DealDamage(b, 1e9f); done++; }
        }
        Status = $"cut {done} block(s) of '{g.DisplayName}' across axis {axis}";
    }

    public static string Break(long id, int n, double every, double radius = 0)
    {
        _break = (id, n, Math.Max(0.0, every), 0);
        _breakRadius = radius; _breakCentre = null;
        return $"breaking {n} block(s) of grid {id}, one every {every:F2} s";
    }

    /// <summary>Server tick.</summary>
    public static void Tick()
    {
        if (_dive.id != 0) { try { DiveTick(); } catch (Exception e) { Status = "dive failed: " + e.Message; } }
        if (_cut.id != 0) { try { CutTick(); } catch (Exception e) { Status = "cut failed: " + e.Message; } }
        if (_clone.count > 0) { try { CloneTick(); } catch (Exception e) { var m = ""; for (var x = e; x != null; x = x.InnerException) m += " <- " + x.GetType().Name + ": " + x.Message; Status = "clone failed:" + m; Keen.VRage.Library.Diagnostics.Log.Default?.Info("[STRESS] clone failed:" + m + " | " + e); } }
        lock (_hold)
            foreach (var kv in _hold)
            {
                var g = GridMembers.Get(kv.Key);
                if (g == null || !g.IsServer) continue;
                var q = (QuaternionD)g.Entity.Data.GetWorldTransform().Orientation;
                GridMembers.SetVelocity(g, q * Vector3D.Forward * kv.Value);
            }
        if (_break.left <= 0) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _break.next) return;
        var bg = GridMembers.Get(_break.id);
        if (bg == null || !bg.IsServer) { Status = "break: no such grid"; _break.left = 0; return; }
        int per = _break.every <= 0 ? _break.left : 1;   // (every 0: all at once)
        int done = 0;
        try
        {
            var grid = bg.Entity.TryGet<CubeGridComponent>();
            var recv = bg.Entity.TryGet<GridDamageReceiverComponent>();
            var all = new List<CubeBlockComponent>();
            grid?.VisitAllBlocksWithComponent<CubeBlockComponent>(b => { if (b != null) all.Add(b); }, false);
            for (int i = 0; i < per && all.Count > 0; i++)
            {
                int k = _rng.Next(all.Count);
                if (_breakRadius > 0)
                {
                    // a hit: around one spot, nearest first (sorted once a tick: a scan per break was the harness's own hitch)
                    if (i == 0)
                    {
                        _breakCentre ??= all[k].Entity.Data.GetWorldTransform().Position;
                        var c0 = _breakCentre.Value;
                        all = all.Select(b => (b, d: (b.Entity.Data.GetWorldTransform().Position - c0).LengthSquared())).Where(t => t.d <= _breakRadius * _breakRadius).OrderByDescending(t => t.d).Select(t => t.b).ToList();
                    }
                    if (all.Count == 0) { _break.left = 0; break; }
                    k = all.Count - 1;   // (nearest last: removed from the end)
                }
                recv?.DealDamage(all[k], 1e9f);
                all.RemoveAt(k);
                done++;
            }
            Status = $"broke {done} (left {_break.left - done}) of '{bg.DisplayName}', {all.Count} block(s) remain";
        }
        catch (Exception e) { Status = "break failed: " + e.Message; }
        _break.left -= Math.Max(done, 1);
        _break.next = now + _break.every;
    }
}
