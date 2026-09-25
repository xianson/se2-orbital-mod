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

    public static void Please(PrefabDefinition prefab)
    {
        bool isServerPlanet = false;
        bool isClientPlanet = false;
        foreach (var type in prefab.Composition.Types)
        {
            if (type == typeof(DiscoverablePlanetComponent)) isServerPlanet = true;
            if (type == typeof(PlanetEnvironmentRenderComponent)) isClientPlanet = true;
        }

        if (isServerPlanet)
        {
            Add(prefab, typeof(ServerPlanetBeacon));
            _serverCount++;
            Log.Default?.Info($"[ORBIT] server planet prefab #{_serverCount}: '{prefab.DebugName}' +ServerPlanetBeacon");
        }

        if (isClientPlanet)
        {
            Add(prefab, typeof(PlanetFrameComponent));
            _clientCount++;
            Log.Default?.Info($"[ORBIT] client planet prefab #{_clientCount}: '{prefab.DebugName}' +PlanetFrameComponent");
        }
    }
}
