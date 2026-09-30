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
            int live, working;
            if (n == 1) lock (Telescopes) { live = Telescopes.Count; working = Telescopes.FindAll(c => c.Working).Count; }
            else lock (Radars) { live = Radars.Count; working = Radars.FindAll(c => c.Working).Count; }
            sb.Append($"{name}: kind {(kind ? "yes" : "NO")}, name '{label}', unlocked {unlocked}, {live} in the world ({working} working); ");
        }
        lock (Radars)
            foreach (var r in Radars) sb.Append($"radar power {r.Power:P0} draw {r.Draw}; ");
        return sb.ToString();
    }

    /// <summary>Harness: count a sensor as working without power (the test blocks stand alone, with no battery).</summary>
    public static bool DevIgnorePower;

    internal static bool Works(PowerableBlockComponent p)
    {
        try { return p != null && p.Functional && p.Enabled && (p.Supplied || DevIgnorePower); } catch { return false; }
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

public class RadarComponent : Component, IInSceneListener,
    Keen.Game2.Simulation.WorldObjects.CubeBlocks.BlockStateModifiers.IConsumedResourceModifier
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly PowerableBlockComponent _power;

    /// <summary>What transmitting at full power adds to the block's own small draw (the block's units; its
    /// definition asks 20 for the electronics, so a full-power radar draws 400).</summary>
    public const float TransmitDraw = 380f;

    float _p = 1f;
    /// <summary>Transmit power, 0..1 of full (the terminal slider): range goes as its fourth root, and the
    /// block's power draw with it. Saved with the world (SavedState).</summary>
    public float Power
    {
        get => _p;
        set
        {
            float v = System.Math.Clamp(value, 0f, 1f);
            if (v == _p) return;
            _p = v;
            try { _power?.UpdateConsumedResource(); } catch { }   // (the block asks for its new draw)
        }
    }

    Keen.VRage.Library.Mathematics.FixedPoint Keen.Game2.Simulation.WorldObjects.CubeBlocks.BlockStateModifiers.IConsumedResourceModifier.RecomputeConsumedResource()
        => (Keen.VRage.Library.Mathematics.FixedPoint)(TransmitDraw * _p);
    void Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.IResourceNode.GetResources(
        Keen.VRage.Library.Memory.BufferReference<Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.ResourceNodeData> resources) { }

    public bool Working => Power > 0.01f && SensorBlocks.Works(_power);
    /// <summary>Harness: what the block asks of the grid now (its applied consumption).</summary>
    public string Draw { get { try { return PlanetRenderBridge.GetMember(_power, "_appliedConsumption")?.ToString() ?? "?"; } catch { return "?"; } } }
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
        set { if (_radar != null) _radar.Power = value; }
    }
}
