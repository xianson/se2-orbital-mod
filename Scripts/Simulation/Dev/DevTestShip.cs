using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.ObjectBuilders;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// HARNESS: a small test ship in front of you, fully built (as the game spawns an encounter's grids, not as a
/// player places blocks, which in survival makes projections to weld): a charged battery, a telescope and a
/// radar on one grid, yours. It exists so the real sensor blocks (their power, their draw, their settings,
/// what they see) and the warp bill's dry stop can be tested without building by hand.
/// Built like the game's block placer builds a new grid: its default server grid with ONE block as its child
/// at the default transform (blocks placed by hand at chosen offsets on one grid crashed the server's block
/// octree: they must connect, which only the game's validated placement ensures). So each block is a grid
/// of its own, 20 m apart; the sensors then have no battery: SensorBlocks.DevIgnorePower counts them.
/// </summary>
public static class DevTestShip
{
    static readonly System.Guid ServerGrid = new System.Guid("672d6382-207a-498f-a51c-58b370aa96cb");   // (BlockPlacer.def: DefaultPrefabServerGrid)
    static readonly System.Guid Battery = new System.Guid("7046873e-c9fe-408c-96c8-53a7f0e62bcd");      // Battery50_Server
    static readonly System.Guid AeroSurface = new System.Guid("ae200001-ae00-c501-0011-000000000001");   // (Aerodynamics Mod: ControlSurface_1x1_Server)
    public static string Status = "none";
    /// <summary>Where the last test radar was spawned (its grid's origin; the block sits on it): the harness finds the
    /// radar's CLIENT copy near it (radarpanel - the client's blocks carry no server composition to match).</summary>
    public static Vector3D? RadarAt;

    /// <summary>Spawn it 60 m ahead of the camera. Server side (the spawner), owned by the local player.
    /// 2026-10-19 (final-check): spawned ON THE SERVER THREAD (GridGen.OnServerThread), STAGED and async
    /// (IEntitySpawner.SpawnBundleAsync, as WarmBerths / DevStress / the game's encounters), the owner set in the builders.
    /// Before, it spawned synchronously from the harness's client frame job: it raced the server's tick ("Concurrent access
    /// (write) GetOrAddArchetype", then a native crash in HkWorld.SetBodyInfo / HavokSessionComponent.OnRigidBodyAdded - the
    /// game died at `testship` in two runs out of two); a synchronous SpawnEntity on the server thread instead was fatal at
    /// once ("Data not found" in the grid's anchoring test, ConvertGridToDynamicWhenNotAnchoredComponent). The grids exist a
    /// tick or two later (sensorblocks / findgrids).</summary>
    public static string Spawn(Keen.VRage.Core.Game.Systems.Session client, WorldTransform camera, double batteryCharge = 1.0, bool aeroSurface = false)
    {
        var me = client.Get<ClientPlayersSessionComponent>().LocalPlayerIdentity;   // (the client's scene: read here)
        return GridGen.OnServerThread(() => SpawnOnServer(me, camera, batteryCharge, aeroSurface));
    }

    private static string SpawnOnServer(IdentityId me, WorldTransform camera, double batteryCharge, bool aeroSurface)
    {
        var server = ServerPlanetBeacon.ServerSession;
        if (server == null) return "no server session";
        var spawner = server.Get<Keen.VRage.Core.Game.Systems.IEntitySpawner>();
        if (spawner == null) return "no spawner";
        if (!DefinitionManager.Instance.TryGetDefinition(ServerGrid, out PrefabDefinition gridPrefab) || gridPrefab == null) return "no default grid prefab";
        Vector3D fwd = (QuaternionD)camera.Orientation * Vector3D.Forward, right = (QuaternionD)camera.Orientation * Vector3D.Right;
        string why = "";
        Vector3D first = default;
        int k = 0;
        var set = aeroSurface   // (testship surface: one NEW aero control surface - a block placed this session, as a player places one)
            ? new[] { (AeroSurface, "surface") }
            : new[] { (Battery, "battery"), (SensorBlocks.TelescopePrefab, "telescope"), (SensorBlocks.RadarPrefab, "radar") };
        foreach (var (prefab, name) in set)
        {
            if (!DefinitionManager.Instance.TryGetDefinition(prefab, out PrefabDefinition p) || p == null) { why += $" no {name} prefab;"; continue; }
            var at = new WorldTransform(camera.Position + fwd * 60 + right * (20 * (k++ - 1)), camera.Orientation);
            if (k == 1) first = at.Position;
            // built as WarmBerths builds its placeholder (GridBuilder: the block at cell 0 by its child transform). Spawn it
            // with open space ahead: in the UIAudit Flight world's start view (a mountain) the grids were lost within seconds;
            // in orbit (orbit Verdure 300 280 first) they stay - gridorbit the radar onto your orbit to stand next to it.
            var gb = new GridBuilder(server, gridPrefab.Get());
            gb.SetName(name == "radar" ? "Test Radar" : name == "telescope" ? "Test Telescope" : name == "surface" ? "Test Surface" : "Test Battery");
            gb.SetTransform(at);
            gb.SetOwner(me);
            var b = p.Get();
            if (name == "battery")
                try { b.OB<ResourceContainerComponent, ResourceContainerObjectBuilder>().CurrentChargeLevel = (Keen.VRage.Library.Mathematics.FixedPoint)batteryCharge; } catch (Exception ce) { why += " charge: " + ce.Message; }
            var bo = b.TryGetOB<OwnershipComponent, OwnershipObjectBuilder>();   // (the block's own owner, if it has one)
            if (bo != null) bo.Owner = me;
            gb.AddBlock(Vector3I.Zero, new IntegerOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up), b);
            try
            {
                var bundle = new Keen.VRage.Core.Game.Systems.EntityBundle();
                bundle.Add(gb.CompileObjectBuilder(), null, true);
                spawner.SpawnBundleAsync(new Keen.VRage.Core.Game.Systems.GameEntitySerializer.BundleInitArgs { Bundle = bundle });
            }
            catch (Exception ex) { why += $" {name} spawn failed: {(ex.InnerException ?? ex).Message};"; continue; }
            if (name == "radar") RadarAt = at.Position;
            why += $" {name} (yours{(bo != null ? ", block too" : "")});";
        }
        Status = $"test blocks spawning (async) near {ServerPlanetBeacon.Fmt(first)}:{why}";
        return Status;
    }
}
