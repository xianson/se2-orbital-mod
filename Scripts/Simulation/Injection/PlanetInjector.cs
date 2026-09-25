using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Adds <see cref="PlanetFrameComponent"/> to every prefab that is a discoverable planet.
/// Same entry mechanism as the aero mod: mods cannot ship .def content until the Mod SDK
/// catches up with 2.4.0.77, so components are injected into prefab compositions at load.
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
    private static int _planetCount;

    public static void Please(PrefabDefinition prefab)
    {
        foreach (var type in prefab.Composition.Types)
        {
            if (type != typeof(DiscoverablePlanetComponent)) continue;

            Add(prefab, typeof(PlanetFrameComponent));
            _planetCount++;
            Log.Default?.Info($"[ORBIT] Injected PlanetFrameComponent into planet prefab #{_planetCount}");
            return;
        }
    }
}
