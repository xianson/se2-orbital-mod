using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// SENSOR BLOCKS (the "Orbital Mod Blocks" companion mod defines them: copies of vanilla blocks, see
/// tools/blocks/gen_blocks.py; InjectPlanetComponents adds these components to their server prefabs).
///  - Telescope (an ore detector's body): passive. Optical: sunlit targets, by size, albedo and phase,
///    none in a planet's shadow, none near the sun. Infrared: a body's own warmth, day or night.
///  - Radar (an antenna's body): active. Its own echo, range by (power x cross-section)^1/4: every contact
///    in reach at once. It gives you away (Contacts.Loud).
/// A sensor counts when its block works (built, switched on, powered). Contacts does the seeing.
/// </summary>
public static class SensorBlocks
{
    public static readonly System.Guid TelescopePrefab = new System.Guid("0b170001-5e50-4000-8000-000000000002");
    public static readonly System.Guid RadarPrefab = new System.Guid("0b170002-5e50-4000-8000-000000000002");

    internal static readonly List<TelescopeComponent> Telescopes = new List<TelescopeComponent>();
    internal static readonly List<RadarComponent> Radars = new List<RadarComponent>();

    /// <summary>Harness: are the blocks defined, named, in the catalogue, unlocked for you?</summary>
    public static string Describe(Keen.VRage.Core.Game.Systems.Session session)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (name, n) in new[] { ("Telescope", 1), ("Radar", 2) })
        {
            System.Guid G(int i) => new System.Guid($"0b17{n:x4}-5e50-4000-8000-{i:x12}");
            bool kind = DefinitionManager.Instance.TryGetDefinition(G(7), out Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinition kd) && kd != null;
            string label = "-";
            try { if (kind) label = PlanetRenderBridge.GetMember(kd, "Name")?.ToString() ?? kd.DebugName; } catch (Exception e) { label = "? " + e.Message; }
            string unlocked = "-";
            try
            {
                if (DefinitionManager.Instance.TryGetDefinition(G(3), out Keen.VRage.DCS.Definitions.EntityCompositeDefinition comp) && comp != null)
                {
                    var prog = ServerPlanetBeacon.ServerSession?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Progression.ProgressionSessionComponent>();
                    var players = session.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>();
                    unlocked = prog == null || players == null ? "?" : prog.IsBlockUnlocked(comp, players.LocalPlayerIdentity).ToString();
                }
                else unlocked = "no composition";
            }
            catch (Exception e) { unlocked = "? " + e.Message; }
            int live; lock (n == 1 ? (object)Telescopes : Radars) live = n == 1 ? Telescopes.Count : Radars.Count;
            sb.Append($"{name}: kind {(kind ? "yes" : "NO")}, name '{label}', unlocked {unlocked}, {live} in the world; ");
        }
        return sb.ToString();
    }

    internal static bool Works(PowerableBlockComponent p)
    {
        try { return p != null && p.Functional && p.Enabled && p.Supplied; } catch { return false; }
    }
}

public class TelescopeComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly PowerableBlockComponent _power;

    public bool Working => SensorBlocks.Works(_power);
    void IInSceneListener.OnAddedToScene() { lock (SensorBlocks.Telescopes) SensorBlocks.Telescopes.Add(this); }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (SensorBlocks.Telescopes) SensorBlocks.Telescopes.Remove(this); }
}

public class RadarComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly PowerableBlockComponent _power;

    /// <summary>Transmit power, 0..1 of full (the terminal slider): range goes as its fourth root.</summary>
    public float Power = 1f;

    public bool Working => Power > 0.01f && SensorBlocks.Works(_power);
    void IInSceneListener.OnAddedToScene() { lock (SensorBlocks.Radars) SensorBlocks.Radars.Add(this); }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (SensorBlocks.Radars) SensorBlocks.Radars.Remove(this); }
}

/// <summary>The radar's terminal slider: its transmit power (host / single player: mod settings do not replicate).</summary>
[BlockDetailProvider]
public class RadarBlockDetailModel : BlockDetailModel
{
    [Keen.VRage.DCS.Annotations.Component]
    private RadarComponent _radar;

    [Slider(null)]
    [Range<float>(0f, 1f, RangeMode.InIn)]
    [Label("Transmit Power")]
    [Increment(0.05f, 0.1f, 0.25f)]
    public float Power
    {
        get => _radar?.Power ?? 1f;
        set { if (_radar != null) _radar.Power = System.Math.Clamp(value, 0f, 1f); }
    }
}
