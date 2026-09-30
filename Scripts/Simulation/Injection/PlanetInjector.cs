using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Injects the orbital components into planet prefabs. Same entry mechanism as the aero mod:
/// mods cannot ship .def content until the Mod SDK catches up with 2.4.0.77, so components are
/// added to prefab compositions at definition load.
///
/// SE2 keeps SEPARATE client and server compositions per entity (bound by
/// BindingsBasedClientServerInfo in VRage.Multiplayer, which mods cannot reference):
///  - server planet: has DiscoverablePlanetComponent  -> gets <see cref="ServerPlanetBeacon"/>
///  - client planet: has PlanetEnvironmentRenderComponent -> gets <see cref="PlanetFrameComponent"/>
/// Keen's own validator rejects client types on server compositions and vice versa, which is
/// why the first version (everything on the server composition) never ran on the client.
/// </summary>
[DefinitionPostProcessor(ForceInstantiation = true)]
public class PlanetInjector : SimpleDefinitionPostProcessor<PrefabDefinition>
{
    public override void PostProcess(PrefabDefinition definition)
    {
        InjectPlanetComponents.Please(definition);
    }
}

public class InjectPlanetComponents : Injections
{
    private static int _serverCount;
    private static int _clientCount;
    private static int _gridCount;

    public static void Please(PrefabDefinition prefab)
    {
        bool isServerGrid = false;
        bool isServerPlanet = false;
        bool isClientPlanet = false;
        bool isClientVolume = false;
        foreach (var type in prefab.Composition.Types)
        {
            if (type == typeof(DiscoverablePlanetComponent)) isServerPlanet = true;
            if (type == typeof(PlanetEnvironmentRenderComponent)) isClientPlanet = true;
            if (type == typeof(Keen.Game2.Client.GameSystems.Render.ProceduralVolumeRenderComponent)) isClientVolume = true;
            if (type == typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent)) isServerGrid = true;
        }

        if (isServerPlanet)
        {
            // Fixed component id + an object builder: the beacon carries the saved orbital state, so
            // its id must be stable across loads (not derived from the composition size, which
            // depends on what other mods inject).
            Add(prefab, new Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo
            {
                Type = typeof(ServerPlanetBeacon),
                ComponentId = new System.Guid("0a7b17a1-5eed-4f00-8a11-0000000000b1"),
                ObjectBuilder = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder(),
            });
            _serverCount++;
            Log.Default?.Info($"[ORBIT] server planet prefab #{_serverCount}: '{prefab.DebugName}' +ServerPlanetBeacon");
        }

        if (isServerGrid)
        {
            // Grids: the server-side frame manager frames them (GridMembers). CubeGridComponent is
            // on the server composition (the aero mod injects the same way).
            Add(prefab, typeof(OrbitalGridComponent));
            _gridCount++;
            if (_gridCount <= 3) Log.Default?.Info($"[ORBIT] grid prefab #{_gridCount}: '{prefab.DebugName}' +OrbitalGridComponent");
        }

        if (isClientPlanet)
        {
            Add(prefab, typeof(PlanetFrameComponent));
            _clientCount++;
            Log.Default?.Info($"[ORBIT] client planet prefab #{_clientCount}: '{prefab.DebugName}' +PlanetFrameComponent");
        }

        if (isClientVolume)
        {
            // Procedural volumes (the planets' rings): followed by PlanetRings (hidden with the real planet).
            Add(prefab, typeof(OrbitalRingComponent));
            Log.Default?.Info($"[ORBIT] client procedural volume prefab: '{prefab.DebugName}' +OrbitalRingComponent");
        }

        // The sensor blocks (Orbital Mod Blocks): their server prefabs, by GUID, get their sensor.
        System.Guid id = default;
        try { id = prefab.Guid; } catch { }
        if (id == SensorBlocks.TelescopePrefab)
        {
            Add(prefab, typeof(TelescopeComponent));
            Log.Default?.Info($"[ORBIT] sensor block prefab '{prefab.DebugName}' +TelescopeComponent");
        }
        else if (id == SensorBlocks.RadarPrefab)
        {
            Add(prefab, typeof(RadarComponent));
            Log.Default?.Info($"[ORBIT] sensor block prefab '{prefab.DebugName}' +RadarComponent");
        }
    }
}
