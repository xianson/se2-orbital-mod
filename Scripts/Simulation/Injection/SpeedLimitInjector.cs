using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The world's speed caps raised to 1000 m/s, grids and characters alike (the game's are 300 / 310):
/// orbital motion near a planet (about 1 km/s in low orbit) and relative motion in warp (N times
/// the true speed) otherwise hit the cap and lost velocity. Set at definition load, before the
/// physics world is built with them; past the cap the game caps as it always does.
/// </summary>
[DefinitionPostProcessor(ForceInstantiation = true)]
public class SpeedLimitInjector : SimpleDefinitionPostProcessor<PhysicsSessionConfiguration>
{
    public const float SpeedCap = 1000f;

    public override void PostProcess(PhysicsSessionConfiguration definition)
    {
        try
        {
            var t = typeof(PhysicsSessionConfiguration);
            float was = definition.MaximumSpeedLinear, wasChar = definition.MaximumCharacterSpeedLinear;
            t.GetProperty("MaximumSpeedLinear")?.SetValue(definition, SpeedCap);
            t.GetProperty("MaximumCharacterSpeedLinear")?.SetValue(definition, SpeedCap);
            Log.Default?.Info($"[ORBIT] speed caps: grids {was} -> {definition.MaximumSpeedLinear}, characters {wasChar} -> {definition.MaximumCharacterSpeedLinear} m/s");
        }
        catch (System.Exception e) { Log.Default?.Warning("[ORBIT] speed caps not raised: " + e.Message); }
    }
}

/// <summary>The character's own cap (its physics definition's MaxVelocity, 310) raised with the world's.</summary>
[DefinitionPostProcessor(ForceInstantiation = true)]
public class CharacterSpeedLimitInjector : SimpleDefinitionPostProcessor<Keen.Game2.Simulation.WorldObjects.Characters.CharacterPhysicsEntityComponentDefinition>
{
    public override void PostProcess(Keen.Game2.Simulation.WorldObjects.Characters.CharacterPhysicsEntityComponentDefinition definition)
    {
        try
        {
            var p = typeof(Keen.VRage.Physics.Components.CharacterPhysicsComponentDefinition).GetProperty("MaxVelocity",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            object was = p?.GetValue(definition);
            if (p != null) p.SetValue(definition, System.Convert.ChangeType(SpeedLimitInjector.SpeedCap, p.PropertyType));
            Log.Default?.Info($"[ORBIT] character MaxVelocity {was} -> {p?.GetValue(definition)} m/s");
        }
        catch (System.Exception e) { Log.Default?.Warning("[ORBIT] character speed cap not raised: " + e.Message); }
    }
}
