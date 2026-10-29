using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// DevHarness (partial): Britannica's dev commands (2026-10-26) - the last group in Execute's chain (after .Systems).
public static partial class DevHarness
{
    private static string ExecuteBritannica(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line, string[] a, string verb)
    {
        switch (verb)
        {
            case "freespec":
            {
                // freespec <planet> ox oy oz lx ly lz: the PLAYER into the game's own free spectator (SpectatingModesSessionComponent
                // .SwitchToSpectator - flown with the usual controls), at a point offset (m) from the planet's displayed centre,
                // looking at another, level on its horizon. (`cam at` is the harness's fixed screenshot camera; this one is the user's.)
                var p = FindPlanet(a[1]);
                if (p == null) return "no such planet";
                Vector3D c = PlanetWorldPos(p);
                Vector3D pos = c + new Vector3D(D(a[2]), D(a[3]), D(a[4])), look = c + new Vector3D(D(a[5]), D(a[6]), D(a[7]));
                Vector3 up = (Vector3)Vector3D.Normalize(pos - c), fwd = (Vector3)Vector3D.Normalize(look - pos);
                if (MathF.Abs(Vector3.Dot(fwd, up)) > 0.98f) up = MathF.Abs(fwd.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
                var wt = new WorldTransform(pos, Quaternion.CreateFromForwardUp(fwd, up));
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = asm.GetType("Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent", false)) != null) break;
                if (t == null) return "no SpectatingModesSessionComponent";
                MethodInfo get = null;
                foreach (var m in session.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { get = m; break; }
                var comp = get?.MakeGenericMethod(t).Invoke(session, null);
                if (comp == null) return "no spectating component in this session";
                SpecCam.Off(session);   // (the screenshot camera's override would hide the spectator's view)
                t.GetMethod("SwitchToSpectator", new[] { typeof(WorldTransform) }).Invoke(comp, new object[] { wt });
                return $"free spectator at {(pos - c).Length() / 1000:F1} km from {PlanetName(p)}'s centre";
            }
            case "creative":
            {
                // creative on|off: the world's game mode (GameModeSessionComponent.CreativeGlobal, saved with the world) -
                // test worlds flown as a player in creative (the user, 2026-10-26: no spectator stills)
                bool on = a.Length < 2 || a[1] != "off";
                return GridGen.OnServerThread(() =>
                {
                    var ss = ServerPlanetBeacon.ServerSession;
                    if (ss == null) return "no server session";
                    var gm = ss.Get<Keen.Game2.Simulation.GameSystems.Player.GameModeSessionComponent>();
                    if (gm == null) return "no game mode component";
                    gm.CreativeGlobal = on;
                    return $"creative {(gm.CreativeGlobal ? "on" : "off")} (save the world to keep it)";
                });
            }
            case "srvtp":
            {
                // srvtp x y z lx ly lz: the player's character moved ON THE SERVER (DEntityContext.Teleport, the call
                // EntityAdmin.TeleportPlayer ends in) - without the admin check `tp` goes through: in a session that doesn't
                // count the player as admin `tp` did nothing, silently (2026-10-26, the user stuck on Verdure)
                var pos = new Vector3D(D(a[1]), D(a[2]), D(a[3])); var look = new Vector3D(D(a[4]), D(a[5]), D(a[6]));
                Vector3 fwd = (Vector3)Vector3D.Normalize(look - pos), up = Math.Abs(fwd.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
                var wt = new WorldTransform(pos, Quaternion.CreateFromForwardUp(fwd, up));
                return GridGen.OnServerThread(() =>
                {
                    var ss = ServerPlanetBeacon.ServerSession;
                    if (ss == null) return "no server session";
                    // (the PLAYER's character only: every server character also moved the NPCs - 2026-10-26, a shipwreck's
                    //  renegade stacked onto the player at Britannica, then a NaN transform crashed the render)
                    var me = PlayerCache.Of(ss);
                    if (me == null) return "no player character on the server";
                    me.Data.Teleport(in wt);
                    return $"server teleport: the player's character to {ServerPlanetBeacon.Fmt(pos)}";
                });
            }
            case "spawnplanet":
            {
                // spawnplanet x y z [radius m] [seed]: Britannica (its own planet definitions, Assets/Procedural/Planets/
                // BritannicaFloor) spawned on the server at a world position - once per world: the save keeps it. The game's
                // own path (the voxel debug screen's planet placer): the planet-spawner configurator sets radius and seed
                // through IConfiguratorProcessor, then the entity spawner.
                var pos = new Vector3D(D(a[1]), D(a[2]), D(a[3]));
                int radius = a.Length > 4 ? (int)D(a[4]) : BritannicaWorld.GameRadius, seed = a.Length > 5 ? (int)D(a[5]) : 1915;
                // (a 7th argument: another server prefab's Guid, through the same path - the A/B against a vanilla planet's)
                Guid prefabId = a.Length > 6 ? new Guid(a[6]) : BritannicaIslandsTest.ServerPrefab;
                return GridGen.OnServerThread(() =>
                {
                    var ss = ServerPlanetBeacon.ServerSession;
                    if (ss == null) return "no server session";
                    if (prefabId == BritannicaIslandsTest.ServerPrefab)
                        foreach (var pb in ss.GetEntitiesOfType<ServerPlanetBeacon>())
                            if (BritannicaIslandsTest.IsBritannica(pb)) return "Britannica is already in this world";
                    if (!Keen.VRage.Library.Definitions.DefinitionManager.Instance.TryGetDefinition(BritannicaIslandsTest.SpawnerPrefab, out Keen.VRage.Core.Game.Definitions.PrefabDefinition spawner) || spawner == null)
                        return "no planet-spawner configurator prefab";
                    if (!Keen.VRage.Library.Definitions.DefinitionManager.Instance.TryGetDefinition(prefabId, out Keen.VRage.Core.Game.Definitions.PrefabDefinition prefab) || prefab == null)
                        return $"no server prefab {prefabId} (content not built?)";
                    // (PlanetConfiguratorObjectBuilder is in Keen.VRage.Voxels - not whitelisted: the generic Process by reflection,
                    //  its fields set through an Action<object>, which Action<in T> accepts)
                    Type cfg = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        if ((cfg = asm.GetType("Keen.VRage.Voxels.ConfiguratorComponents.PlanetConfiguratorObjectBuilder", false)) != null) break;
                    if (cfg == null) return "no PlanetConfiguratorObjectBuilder type";
                    MethodInfo process = null;
                    foreach (var m in typeof(Keen.VRage.Core.Game.GameSystems.PrefabConfigurators.IConfiguratorProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "Process" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 4) { process = m; break; }
                    if (process == null) return "no IConfiguratorProcessor.Process<TIn>";
                    Action<object> fill = ob => { cfg.GetField("Radius").SetValue(ob, radius); cfg.GetField("Seed").SetValue(ob, seed); };
                    var eob = (Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder)process.MakeGenericMethod(cfg).Invoke(null, new object[] { ss.Scene, spawner, prefab, fill });
                    Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilderFunctions.OB<Keen.VRage.Core.Game.Components.WorldTransformComponent, Keen.VRage.Core.Game.Components.WorldTransformComponentObjectBuilder>(eob).Transform = new WorldTransform(pos, Quaternion.Identity);
                    if (prefabId == BritannicaIslandsTest.ServerPrefab) eob.DebugName = "Britannica";
                    // (staged and async between the server's ticks, as every harness spawn: a synchronous SpawnEntity inside the server's
                    //  job killed the server - GridGen.cs)
                    var spawnerSys = ss.Get<Keen.VRage.Core.Game.Systems.IEntitySpawner>();
                    var bundle = new Keen.VRage.Core.Game.Systems.EntityBundle();
                    bundle.Add(eob, null, true);
                    if (!GridGen.BetweenTicks(() => spawnerSys.SpawnBundleAsync(new Keen.VRage.Core.Game.Systems.GameEntitySerializer.BundleInitArgs { Bundle = bundle })))
                        return "the planet did not spawn: no engine update queue";
                    return $"{eob.DebugName} spawning (async) at {ServerPlanetBeacon.Fmt(pos)}, R {radius / 1000.0:F0} km, seed {seed}";
                });
            }
            case "bodies":
            {
                // bodies: the system's bodies and the planet beacon each was paired with, against the beacons the harness sees
                // (2026-10-26: `approach Kemik` answered "no such planet" - SystemHost.BodyNameOf found no body for Kemik's beacon)
                var sb = new System.Text.StringBuilder();
                foreach (var kv in SystemHost.BeaconOf)
                    sb.Append($"body {kv.Key}: {(kv.Value == null ? "null" : $"{PlanetName(kv.Value)} c={ServerPlanetBeacon.Fmt(kv.Value.Center)} hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(kv.Value)}")}; ");
                foreach (var pb in Planets())
                    sb.Append($"seen {PlanetName(pb)} c={ServerPlanetBeacon.Fmt(pb.Center)} hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pb)} -> {SystemHost.BodyNameOf(pb) ?? "NONE"}; ");
                return sb.ToString();
            }
            default:
                return "unknown command";
        }
    }
}
