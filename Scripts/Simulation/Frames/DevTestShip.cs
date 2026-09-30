using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.ObjectBuilders;

#pragma warning disable
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
    public static string Status = "none";
    public static Entity Last;

    /// <summary>Spawn it 60 m ahead of the camera. Server side (the spawner), owned by the local player.</summary>
    public static string Spawn(Keen.VRage.Core.Game.Systems.Session client, WorldTransform camera, double batteryCharge = 1.0)
    {
        var server = ServerPlanetBeacon.ServerSession;
        if (server == null) return "no server session";
        var gen = AsteroidBridge.Generator(server);
        var spawner = gen != null ? AsteroidFrames.Spawner(gen) : null;
        if (spawner == null) return "no spawner";
        if (!DefinitionManager.Instance.TryGetDefinition(ServerGrid, out PrefabDefinition gridPrefab) || gridPrefab == null) return "no default grid prefab";
        Vector3D fwd = (QuaternionD)camera.Orientation * Vector3D.Forward, right = (QuaternionD)camera.Orientation * Vector3D.Right;
        string why = "";
        var own = server.SessionComponents.TryGet<OwnershipSessionComponent>();
        var me = client.Get<ClientPlayersSessionComponent>().LocalPlayerIdentity;
        Vector3D first = default;
        int k = 0;
        foreach (var (prefab, name) in new[] { (Battery, "battery"), (SensorBlocks.TelescopePrefab, "telescope"), (SensorBlocks.RadarPrefab, "radar") })
        {
            if (!DefinitionManager.Instance.TryGetDefinition(prefab, out PrefabDefinition p) || p == null) { why += $" no {name} prefab;"; continue; }
            var grid = gridPrefab.Get();
            var at = new WorldTransform(camera.Position + fwd * 60 + right * (20 * (k++ - 1)), camera.Orientation);
            if (k == 1) first = at.Position;
            grid.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = at;
            var b = p.Get();
            if (name == "battery")
                try { b.OB<ResourceContainerComponent, ResourceContainerObjectBuilder>().CurrentChargeLevel = (Keen.VRage.Library.Mathematics.FixedPoint)batteryCharge; } catch (Exception ce) { why += " charge: " + ce.Message; }
            grid.OB<Keen.VRage.Core.Game.Components.HierarchyComponent, Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilder>().AddChild(b);
            Entity e;
            try { e = spawner.SpawnEntity(grid); }
            catch (Exception ex) { why += $" {name} spawn failed: {(ex.InnerException ?? ex).Message};"; continue; }
            if (e == null) { why += $" {name}: nothing spawned;"; continue; }
            Last = e;
            int n = 0;
            try
            {
                if (own != null)
                {
                    if (own.TryTransferOwnership(e, me)) n++;
                    var hc = e.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>();
                    if (hc != null) foreach (var c in hc.Children) if (own.TryTransferOwnership(c, me)) n++;
                }
            }
            catch (Exception ex) { why += $" {name} ownership: {ex.Message};"; }
            string owner = "?";
            try { owner = own != null ? (own.TryGetOwnerOf(e).Equals(me) ? "yours" : "someone else's") : "?"; } catch { }
            why += $" {name} ({owner}, transferred x{n});";
        }
        var atFirst = new WorldTransform(first, camera.Orientation);
        Status = $"test blocks spawned near {ServerPlanetBeacon.Fmt(atFirst.Position)}:{why}";
        return Status;
    }
}
