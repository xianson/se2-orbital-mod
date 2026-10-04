#pragma warning disable
using System.IO;
using Keen.VRage.Core;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Library.Mathematics;
using Keen.Game2.Simulation.GameSystems.Ownership;

namespace OrbitalMod;

/// <summary>
/// DEV (harness 'gen'): GENERATED GRIDS - vehicles built by code from a block list (the offline generator writes it:
/// a mesh or a parametric shape voxelised to a hollow shell, batteries placed for the centre of mass, thrusters,
/// hinged control surfaces). Uses the game's own GridBuilder (Game2.Simulation.Utils, public) - as the hinge's own
/// RequestSpawnTop does - and HingeComponent.RequestSpawnTop + GridBuilder.AddBlockToGrid for the flaps.
///
/// Spec file (text, one item a line; # comments; positions in 0.25 m cells, the grid's frame; directions Forward
/// Backward Left Right Up Down):
///   name TEXT              the grid's name
///   ahead M / up M         where: metres ahead of / above the camera (default 120 / 0)
///   static 1               spawn static (else dynamic)
///   B PREFAB X Y Z FWD UP  a block of the main grid (PREFAB: the block's server prefab Guid)
///   H INDEX MIN MAX        block INDEX (0-based among the B lines) is a hinge: spawn its head; limits in degrees
///   F HINGE PREFAB X Y Z FWD UP   a flap block on hinge number HINGE's head grid (its frame: the head at 0 0 0)
///
/// gen spawn FILE | gen status | gen clear | gen measure FILE (each Guid line: a lone block 30 m apart, static)
/// | gen report (their mass and cell bounds -> %TEMP%\OrbitalMod\gen\catalog.txt)
/// </summary>
public static class GridGen
{
    sealed class Hinge { public bool LimitsSet; public float Target = float.NaN, Gain = 4f, Last = float.NaN, ErrSum, ErrPeak, SwingSum, SwingPeak; public int Samples; public int Block; public float Min, Max; public List<(Guid p, Vector3I at, IntegerOrientation o)> Flap = new(); public Entity Entity; public bool Spawned, Done; public int Wait; public string Why = ""; }
    static readonly List<Entity> _spawned = new List<Entity>();
    static readonly List<Hinge> _hinges = new List<Hinge>();
    static readonly List<(Guid prefab, Entity grid)> _measured = new List<(Guid, Entity)>();
    public static string Status = "none";
    static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

    static string Dir => Path.Combine(Path.GetTempPath(), "OrbitalMod", "gen");

    // ── the SERVER thread owns the server's entities: every spawn, hinge command and read runs there (the harness runs on
    //    the client thread; a hinge's SetVelocity from it raced the server's entity storage and crashed the game) ──
    static readonly System.Collections.Concurrent.ConcurrentQueue<(Func<string> work, System.Threading.ManualResetEventSlim done, string[] result)> _server = new();

    /// <summary>Run on the server thread (ServerPlanetBeacon's job) and wait for it (3 s); the answer, or why not.</summary>
    internal static string RunOnServer(Func<string> work) => OnServer(work);
    static string OnServer(Func<string> work)
    {
        var done = new System.Threading.ManualResetEventSlim(false); var result = new string[1];
        _server.Enqueue((work, done, result));
        return done.Wait(3000) ? result[0] : "timed out waiting for the server thread";
    }

    /// <summary>Server thread (ServerPlanetBeacon's job): queued hinge/steer commands, the hinges held.</summary>
    public static void ServerTick()
    {
        while (_server.TryDequeue(out var job))
        {
            try { job.result[0] = job.work(); } catch (Exception e) { var x = e.InnerException ?? e; job.result[0] = "failed: " + x.Message + " @ " + string.Join(" < ", (x.StackTrace ?? "").Split((char)10).Take(4).Select(l => l.Trim())); }
            job.done.Set();
        }
        Hold();
        HingeRig.Tick();
    }

    /// <summary>Client tick (the harness's thread): hinge heads spawned and flaps grown - as spawning, it works here.</summary>
    public static void ClientTick() => Tick();

    public static string Command(Keen.VRage.Core.Game.Systems.Session client, WorldTransform camera, string[] a)
    {
        IdentityId me = default;
        try { me = client.Get<ClientPlayersSessionComponent>().LocalPlayerIdentity; } catch { }
        // (where each runs, as measured: spawning inside the server's job fails - a new battery's inventory is not readable
        //  there, "Data not found" - and works from here, the client thread, as DevTestShip always has; a hinge's
        //  SetVelocity from here raced the server's entity storage and crashed the game - hinge and steering writes,
        //  and the hold loop, run in the server's job)
        string sub = a.Length > 1 ? a[1] : "status";
        // (clear too: deleting from here raced the server's own access to those grids - "Concurrent access", then a crash;
        //  the asteroid frames delete from the server's job the same way)
        if (sub == "hold" || sub == "free" || sub == "steer" || sub == "clear") return OnServer(() => CommandOnServer(me, camera, a));
        return CommandOnServer(me, camera, a);
    }

    static string CommandOnServer(IdentityId me, WorldTransform camera, string[] a)
    {
        string sub = a.Length > 1 ? a[1] : "status";
        try
        {
            switch (sub)
            {
                case "spawn": return a.Length > 2 ? Spawn(me, camera, Path.IsPathRooted(a[2]) ? a[2] : Path.Combine(Dir, a[2])) : "gen spawn FILE";
                case "clear": return Clear();
                case "measure": return a.Length > 2 ? Measure(camera, Path.IsPathRooted(a[2]) ? a[2] : Path.Combine(Dir, a[2])) : "gen measure FILE";
                case "report": return Report();
                case "mounts": return Mounts();
                case "probe": return Probe();
                case "steer":
                {
                    // gen steer N PITCH YAW ROLL (rad/s, grid frame: about X, Y, Z): the rotation a pilot asks for, held
                    var e = Grid(a, 2); if (e == null) return "no grid " + (a.Length > 2 ? a[2] : "");
                    var w = new Vector3(float.Parse(a[3], CI), float.Parse(a[4], CI), float.Parse(a[5], CI));
                    e.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = w });
                    return $"steering grid {IdOf(e)}: {V(w)} rad/s";
                }
                case "aero": return AeroReadout(Grid(a, 2));
                case "air":
                {
                    // gen air N x,y,z | gen air off: the test airflow along a direction in grid N's frame, set as ONE world
                    // direction for every grid (AeroMod.AeroEntryFx.TestWorld) - the flap grids then see the plane's air
                    if (a.Length > 2 && a[2] == "off") { PlanetRenderBridge.ForeignValue("AeroMod.AeroEntryFx", "TestWorld", ""); return "test air: per grid"; }
                    var e = Grid(a, 2); if (e == null || a.Length < 4) return "gen air N x,y,z | gen air off";
                    var p = a[3].Split(',');
                    var local = Vector3.Normalize(new Vector3(float.Parse(p[0], CI), float.Parse(p[1], CI), float.Parse(p[2], CI)));
                    var w = Vector3.Transform(local, e.Data.GetWorldTransform().Orientation);
                    string val = string.Format(CI, "{0:R},{1:R},{2:R}", w.X, w.Y, w.Z);
                    PlanetRenderBridge.ForeignValue("AeroMod.AeroEntryFx", "TestWorld", val);
                    return "test air (world) " + val;
                }
                case "id": { var e = Grid(a, 2); return "id " + (e != null ? IdOf(e) : 0); }   // (gen id [N]: a spawned grid's harness id; 0 until registered)
                case "hinges": return string.Join(" || ", _hinges.Select((h, i) => HingeLine(i)));
                case "hold":
                {
                    // gen hold N|all DEG [GAIN]: P control toward DEG (velocity = gain x error, rad/s), stats reset;
                    // gen hold DEG: every hinge
                    if (a.Length == 3) a = new[] { a[0], a[1], "all", a[2] };
                    if (a.Length < 4) return "gen hold N|all DEG [GAIN]";
                    float deg = float.Parse(a[3], CI), gain = a.Length > 4 ? float.Parse(a[4], CI) : 4f;
                    for (int i = 0; i < _hinges.Count; i++)
                        if (a[2] == "all" || a[2] == i.ToString(CI))
                        { var h = _hinges[i]; h.Target = deg * MathF.PI / 180f; h.Gain = gain; h.ErrSum = h.ErrPeak = h.SwingSum = h.SwingPeak = 0f; h.Samples = 0; h.Last = float.NaN; }
                    return "holding " + a[2] + " at " + deg + "°";
                }
                case "free":
                    foreach (var h in _hinges) { h.Target = float.NaN; h.Entity?.TryGet<HingeComponent>()?.SetVelocity(0f); }
                    return "free";
                default: return Status + $" | grids {string.Join(",", _spawned.Select(IdOf))}; hinges " + string.Join(" ", _hinges.Select((h, i) => $"#{i}:{(h.Done ? "flap ok" : h.Spawned ? "head..." : "pending")}{(h.Why != "" ? " " + h.Why : "")}"));
            }
        }
        catch (Exception e) { var x = e.InnerException ?? e; return "gen failed: " + x.Message + " @ " + string.Join(" < ", (x.StackTrace ?? "").Split((char)10).Take(4).Select(l => l.Trim())); }
    }

    static IntegerOrientation Orient(string f, string u) =>
        new IntegerOrientation(Enum.Parse<Base6Directions.Direction>(f, true), Enum.Parse<Base6Directions.Direction>(u, true));

    static Vector3I Cell(string x, string y, string z) => new Vector3I(int.Parse(x, CI), int.Parse(y, CI), int.Parse(z, CI));

    static string Spawn(IdentityId me, WorldTransform camera, string file)
    {
        var server = ServerPlanetBeacon.ServerSession;
        if (server == null) return "no server session";
        if (!File.Exists(file)) return "no file " + file;
        string name = Path.GetFileNameWithoutExtension(file); double ahead = 120, up = 0; bool isStatic = false;
        var blocks = new List<(Guid p, Vector3I at, IntegerOrientation o)>();
        var hinges = new List<Hinge>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "name": name = line.Substring(5).Trim(); break;
                case "ahead": ahead = double.Parse(t[1], CI); break;
                case "up": up = double.Parse(t[1], CI); break;
                case "static": isStatic = t[1] != "0"; break;
                case "B": blocks.Add((Guid.Parse(t[1]), Cell(t[2], t[3], t[4]), Orient(t[5], t[6]))); break;
                case "H": hinges.Add(new Hinge { Block = int.Parse(t[1], CI), Min = float.Parse(t[2], CI), Max = float.Parse(t[3], CI) }); break;
                case "F": hinges[int.Parse(t[1], CI)].Flap.Add((Guid.Parse(t[2]), Cell(t[3], t[4], t[5]), Orient(t[6], t[7]))); break;
            }
        }
        if (blocks.Count == 0) return "no blocks in " + file;
        var gb = new GridBuilder(server);
        gb.SetName(name); gb.SetDebugName(name);
        gb.SetMotionType(isStatic ? Keen.VRage.Physics.Data.BodyArgs.Motion.Static : Keen.VRage.Physics.Data.BodyArgs.Motion.Dynamic);
        Vector3D fwd = (QuaternionD)camera.Orientation * Vector3D.Forward, upv = (QuaternionD)camera.Orientation * Vector3D.Up;
        gb.SetTransform(new WorldTransform(camera.Position + fwd * ahead + upv * up, camera.Orientation));
        gb.SetOwner(me);
        int missing = 0;
        foreach (var (p, at, o) in blocks)
        {
            if (!DefinitionManager.Instance.TryGetDefinition(p, out PrefabDefinition pd) || pd == null) { missing++; continue; }
            gb.AddBlock(at, o, pd);
        }
        var e = gb.BuildOne();
        if (e == null) return "nothing spawned";
        _spawned.Add(e);
        // the hinges: the main grid's children in the order added (missing prefabs shift it - none allowed with hinges)
        if (hinges.Count > 0 && missing == 0)
        {
            var kids = e.TryGet<HierarchyComponent>()?.Children;
            foreach (var h in hinges)
            {
                h.Entity = kids != null && h.Block < kids.Count ? kids[h.Block] : null;
                if (h.Entity?.TryGet<HingeComponent>() == null) { h.Why = "block " + h.Block + " is not a hinge"; continue; }
                _hinges.Add(h);
            }
        }
        Status = $"spawned '{name}': {blocks.Count - missing} blocks{(missing > 0 ? $" ({missing} unknown prefabs)" : "")}, {hinges.Count} hinges";
        return Status;
    }

    /// <summary>Server tick: every held hinge toward its target (P control), error and swing recorded.</summary>
    static void Hold()
    {
        foreach (var h in _hinges)
        {
            if (!h.Done || h.Entity == null || !HingeAngle(h, out float ang)) continue;
            if (!h.LimitsSet && ang >= h.Min * MathF.PI / 180f && ang <= h.Max * MathF.PI / 180f)
            {
                // (each limit is clamped by the other: max, min, then max again - whatever the defaults were)
                var hc = h.Entity.TryGet<HingeComponent>();
                float lo = h.Min * MathF.PI / 180f, hi = h.Max * MathF.PI / 180f;
                if (hc != null) { hc.SetEnableLocalLimits(true); hc.SetLocalMaxLimit(hi); hc.SetLocalMinLimit(lo); hc.SetLocalMaxLimit(hi); }
                h.LimitsSet = true;
            }
            if (float.IsNaN(h.Target)) continue;
            float err = h.Target - ang;
            h.Entity.TryGet<HingeComponent>()?.SetVelocity(h.Gain * err);
            h.ErrSum += MathF.Abs(err); h.ErrPeak = MathF.Max(h.ErrPeak, MathF.Abs(err));
            if (!float.IsNaN(h.Last)) h.SwingPeak = MathF.Max(h.SwingPeak, MathF.Abs(ang - h.Last));
            h.Last = ang; h.Samples++;
        }
    }

    static bool HingeAngle(Hinge h, out float a)
    {
        a = 0f;
        return h.Entity != null && h.Entity.Data.TryGet<HingeComponent.HingeData>(out var d) && (a = d.CurrentAngle) == a;
    }

    static string HingeLine(int i)
    {
        var h = _hinges[i]; var hc = h.Entity?.TryGet<HingeComponent>();
        string ang = HingeAngle(h, out float a) ? $"{a * 180f / MathF.PI:F2}°" : "?";
        string st = h.Samples > 0 ? $" err mean {h.ErrSum / h.Samples * 180f / MathF.PI:F3}° peak {h.ErrPeak * 180f / MathF.PI:F3}° swing peak {h.SwingPeak * 180f / MathF.PI:F4}° ({h.Samples})" : "";
        string lim = hc != null ? $" limits {hc.LocalMinLimit * 180f / MathF.PI:F0}..{hc.LocalMaxLimit * 180f / MathF.PI:F0}° (def {(hc.MinLimit < -1e6f ? "-inf" : (hc.MinLimit * 180f / MathF.PI).ToString("F0"))}..{(hc.MaxLimit > 1e6f ? "inf" : (hc.MaxLimit * 180f / MathF.PI).ToString("F0"))})" : "";
        return $"#{i} {(h.Done ? "flap" : "...")}{lim}{(h.Why != "" ? " " + h.Why : "")} angle {ang} vel {hc?.Velocity:F3} working {hc?.Entity?.TryGet<FunctionalBlockComponent>()?.Working}{(float.IsNaN(h.Target) ? " free" : $" target {h.Target * 180f / MathF.PI:F1}°")}{st}";
    }

    /// <summary>Server tick: hinge heads spawned one tick after their grid, flaps grown once the head is connected.</summary>
    static void Tick()
    {
        if (_hinges.Count == 0) return;
        try
        {
            foreach (var h in _hinges)
            {
                if (h.Done || h.Entity == null) continue;
                var hc = h.Entity.TryGet<HingeComponent>();
                if (hc == null) { h.Why = "hinge gone"; h.Done = true; continue; }
                if (!h.Spawned)
                {
                    hc.RequestSpawnTop(); h.Spawned = true; continue;
                }
                var head = hc.ConnectedEntity;
                if (head == null) { if (++h.Wait > 600) { h.Why = "no head after 10 s"; h.Done = true; } continue; }
                var grid = GridOf(head);
                if (grid == null) { if (++h.Wait > 600) { h.Why = "head's grid not found"; h.Done = true; } continue; }
                foreach (var (p, at, o) in h.Flap)
                    if (DefinitionManager.Instance.TryGetDefinition(p, out PrefabDefinition pd) && pd != null)
                        GridBuilder.AddBlockToGrid(grid, at, o, pd.Get());
                h.Done = true;
                // (held at its neutral - 90, the flap trailing, when the limits hold it, else their middle: a new head hangs
                //  at 0; the limits are set only once it is inside them - set now, the game widens them to include 0)
                h.Target = (h.Min <= 90f && h.Max >= 90f ? 90f : 0.5f * (h.Min + h.Max)) * MathF.PI / 180f;
            }
        }
        catch (Exception e) { Status = "tick: " + e.Message; }
    }

    /// <summary>Spawned grid number a[i] (0-based, default 0).</summary>
    static Entity Grid(string[] a, int i)
    {
        int n = a.Length > i && int.TryParse(a[i], out int k) ? k : 0;
        return n >= 0 && n < _spawned.Count ? _spawned[n] : null;
    }

    static System.Reflection.MethodInfo _flight, _forces, _lift, _ground;
    static bool _apiLooked;

    /// <summary>The grid's aero, from the aerodynamics mod's API (AeroMod.AeroApi, by reflection: the mods compile apart).</summary>
    static string AeroReadout(Entity e)
    {
        if (e == null) return "no grid";
        if (!_apiLooked)
        {
            _apiLooked = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("AeroMod.AeroApi"); if (t == null) continue;
                _flight = t.GetMethod("TryGetFlight"); _forces = t.GetMethod("TryGetForces"); _lift = t.GetMethod("TryGetLift"); _ground = t.GetMethod("TryGetGroundHeight"); break;
            }
        }
        if (_flight == null) return "aero mod's API not found";
        var f = new object[] { e, null, null, null, null, null };
        if (!(bool)_flight.Invoke(null, f)) return "not flying (no published flow)";
        var g = new object[] { e, null, null, null, null, null };
        _forces.Invoke(null, g);
        var l = new object[] { e, null, null };
        _lift.Invoke(null, l);
        var gh = new object[] { e, null };
        if (_ground != null) _ground.Invoke(null, gh);
        return $"M {(float)f[3]:F2} v {(float)f[2]:F0} m/s rho {(float)f[4]:F2} q {(float)f[5]:F0} Pa travel {V((Vector3)f[1])} | force {V((Vector3)g[1])} N torque {V((Vector3)g[2])} N m drag {(float)g[3]:F0} lift {(float)g[4]:F0} frontal {(float)g[5]:F1} m2 | lift dir {V((Vector3)l[1])} CL(frontal) {(float)l[2]:F3} | ground {(gh[1] is float gm ? gm : -1f):F1} m";
    }

    /// <summary>The harness id of a grid entity (its OrbitalGridComponent), 0 while not registered.</summary>
    static long IdOf(Entity e) { foreach (var g in GridMembers.All()) if (g.IsServer && g.Entity == e) return g.Id; return 0; }

    /// <summary>The grid whose children include this block entity.</summary>
    static Entity GridOf(Entity block)
    {
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || g.Entity == null) continue;
            var kids = g.Entity.TryGet<HierarchyComponent>()?.Children;
            if (kids == null) continue;
            foreach (var k in kids) if (k == block) return g.Entity;
        }
        return null;
    }

    /// <summary>A world point in a grid's frame.</summary>
    static Vector3 Inv(Vector3D p, WorldTransform wt) => Vector3.Transform((Vector3)(p - wt.Position), Quaternion.Inverse(wt.Orientation));
    static string V(Vector3D v) => $"({v.X:F2} {v.Y:F2} {v.Z:F2})";
    static string V(Vector3 v) => $"({v.X:F2} {v.Y:F2} {v.Z:F2})";

    /// <summary>Each spawned grid's and hinge head grid's transform, every hinge block's and head block's local transform
    /// and AABB - expressed in the MAIN grid's frame - to calibrate the generator (where a head lands, its axes).</summary>
    static string Probe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in _spawned)
        {
            var wt = e.Data.GetWorldTransform();
            sb.Append($"grid at {V(wt.Position)} fwd {V(wt.Orientation * Vector3.Forward)} up {V(wt.Orientation * Vector3.Up)} | ");
            var kids = e.TryGet<HierarchyComponent>()?.Children;
            if (kids == null) continue;
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i]?.TryGet<HingeComponent>() == null) continue;
                var cb = kids[i].TryGet<CubeBlockComponent>();
                var bt = kids[i].Data.GetWorldTransform();
                sb.Append($"hinge #{i} aabb {cb?.AABB.Min}..{cb?.AABB.Max} world {V(bt.Position)} local {V(Inv(bt.Position, wt))} fwd {V(Vector3.Transform(bt.Orientation * Vector3.Forward, Quaternion.Inverse(wt.Orientation)))} up {V(Vector3.Transform(bt.Orientation * Vector3.Up, Quaternion.Inverse(wt.Orientation)))} | ");
                var head = kids[i].TryGet<HingeComponent>().ConnectedEntity;
                if (head == null) { sb.Append("no head | "); continue; }
                var hg = GridOf(head);
                var ht = head.Data.GetWorldTransform();
                var hcb = head.TryGet<CubeBlockComponent>();
                sb.Append($"head aabb {hcb?.AABB.Min}..{hcb?.AABB.Max} local {V(Inv(ht.Position, wt))} fwd {V(Vector3.Transform(ht.Orientation * Vector3.Forward, Quaternion.Inverse(wt.Orientation)))} up {V(Vector3.Transform(ht.Orientation * Vector3.Up, Quaternion.Inverse(wt.Orientation)))}");
                if (hg != null)
                {
                    var gt = hg.Data.GetWorldTransform();
                    sb.Append($" headgrid origin local {V(Inv(gt.Position, wt))} fwd {V(Vector3.Transform(gt.Orientation * Vector3.Forward, Quaternion.Inverse(wt.Orientation)))} up {V(Vector3.Transform(gt.Orientation * Vector3.Up, Quaternion.Inverse(wt.Orientation)))} blocks {hg.TryGet<HierarchyComponent>()?.Children?.Count}");
                }
                sb.Append(" | ");
            }
        }
        return sb.Length > 0 ? sb.ToString() : "nothing spawned";
    }

    static string Clear()
    {
        var server = ServerPlanetBeacon.ServerSession;
        var gen = server != null ? AsteroidBridge.Generator(server) : null;
        var spawner = gen != null ? AsteroidFrames.Spawner(gen) : null;
        int n = 0;
        // (a flap's grid goes with its hinge's: delete them too)
        foreach (var h in _hinges) { var head = h.Entity?.TryGet<HingeComponent>()?.ConnectedEntity; var g = head != null ? GridOf(head) : null; if (g != null) try { spawner?.DeleteEntity(g); n++; } catch { } }
        foreach (var e in _spawned) try { spawner?.DeleteEntity(e); n++; } catch { }
        foreach (var (_, e) in _measured) try { spawner?.DeleteEntity(e); n++; } catch { }
        _spawned.Clear(); _hinges.Clear(); _measured.Clear();
        Status = $"cleared {n} grids";
        return Status;
    }

    /// <summary>gen mounts: every catalogue prefab's MOUNT FACES as the game defines them (CubeBlockDefinition
    /// .MountPointsGroupsPerDirection - generated from the block's collider: what CanConnectBlock checks), local axes,
    /// mount cells (0.25 m) per face, into gen/mounts.txt - the generator's mount data, measured instead of assumed.</summary>
    static string Mounts()
    {
        string cat = Path.Combine(Dir, "catalog.txt");
        if (!File.Exists(cat)) return "no catalog.txt";
        var sb = new System.Text.StringBuilder("# prefab name: face=mount cells (local axes; identity orientation)" + (char)10);
        int n = 0, bad = 0;
        foreach (var raw in File.ReadAllLines(cat))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var tok = line.Split(' ');
            if (!Guid.TryParse(tok[0], out var g)) continue;
            try
            {
                if (!DefinitionManager.Instance.TryGetDefinition(g, out PrefabDefinition pd) || pd == null) { bad++; continue; }
                var ob = pd.Get();
                // (the game's own query - the definition's mount groups turned by the builder's transform: identity here)
                var per = Keen.Game2.Simulation.GameSystems.BlockPlacement.ComplexTopologyAggregator.GetMountPointsGroupsPerDirection(ob);
                sb.Append(g).Append(' ').Append((pd.ToString() ?? "?").Replace(' ', '_')).Append(':');
                foreach (var kv in per)
                {
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Count);
                    kv.Value.Dispose();
                }
                sb.Append((char)10);
                n++;
            }
            catch (Exception e) { bad++; sb.Append(g).Append(" failed: ").Append(e.Message).Append((char)10); }
        }
        File.WriteAllText(Path.Combine(Dir, "mounts.txt"), sb.ToString());
        return $"mounts: {n} prefabs, {bad} failed -> gen/mounts.txt";
    }

    /// <summary>Each prefab Guid in the file (first token of a line) as a lone static block, 30 m apart.</summary>
    static string Measure(WorldTransform camera, string file)
    {
        var server = ServerPlanetBeacon.ServerSession;
        if (server == null) return "no server session";
        Vector3D fwd = (QuaternionD)camera.Orientation * Vector3D.Forward, right = (QuaternionD)camera.Orientation * Vector3D.Right;
        int k = 0, bad = 0;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0 || !Guid.TryParse(line.Split(' ')[0], out var g)) continue;
            if (!DefinitionManager.Instance.TryGetDefinition(g, out PrefabDefinition pd) || pd == null) { bad++; continue; }
            var gb = new GridBuilder(server);
            gb.SetMotionType(Keen.VRage.Physics.Data.BodyArgs.Motion.Dynamic);
            gb.SetTransform(new WorldTransform(camera.Position + fwd * 300 + right * (30 * (k++ % 20)) + (QuaternionD)camera.Orientation * Vector3D.Up * (30 * (k / 20)), camera.Orientation));
            gb.AddBlock(Vector3I.Zero, IntegerOrientation.Identity, pd);
            var e = gb.BuildOne();
            if (e != null) _measured.Add((g, e)); else bad++;
        }
        return Status = $"measuring {_measured.Count} blocks ({bad} failed): 'gen report' after a second";
    }

    static string Report()
    {
        Directory.CreateDirectory(Dir);
        var sb = new System.Text.StringBuilder("# prefab mass_kg aabb_min(x y z) aabb_max(x y z) - cells of 0.25 m, the block at 0 0 0 identity\n");
        foreach (var (g, e) in _measured)
        {
            var og = GridMembers.All().FirstOrDefault(x => x.IsServer && x.Entity == e);
            double mass = og != null ? GridMembers.Mass(og) : -1;
            var kids = e.TryGet<HierarchyComponent>()?.Children;
            var cb = kids != null && kids.Count > 0 ? kids[0].TryGet<CubeBlockComponent>() : null;
            string box = cb != null ? $"{cb.AABB.Min.X} {cb.AABB.Min.Y} {cb.AABB.Min.Z} {cb.AABB.Max.X} {cb.AABB.Max.Y} {cb.AABB.Max.Z}" : "? ? ? ? ? ?";
            sb.Append($"{g} {mass.ToString("F1", CI)} {box}\n");
        }
        File.WriteAllText(Path.Combine(Dir, "catalog.txt"), sb.ToString());
        return Status = $"catalog of {_measured.Count} -> {Path.Combine(Dir, "catalog.txt")}";
    }
}
