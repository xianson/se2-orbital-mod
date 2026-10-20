using Keen.Game2.Simulation.GameSystems;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE PLAYERS' BODIES CARRY THE WORLD'S SETTINGS (2026-10-19, planetless worlds): injected into the SERVER character and
/// spectator compositions (PlanetInjector: CharacterMovementServerComponent / ServerSpectatorComponent - in no client
/// composite), with a fixed component id and the engine's EntityNameSessionComponentObjectBuilder, as the planets' beacon.
/// The world's settings were saved only by the planets (ServerPlanetBeacon): a world with no planet (the game's Empty World -
/// its save holds one spectator and one character, nothing else) saved none, and the world's end (WorldReset.Unloaded, run
/// by "the last planet left") never came. Which text is applied, and when the world is over, is Save/WorldCarriers.cs
/// (OrbitalSettings.Carriers): a body's text wins over a planet's (older saves: planets only - still read); a body leaving
/// mid-session (a death, a respawn) is not the world's end. Only the settings ride here - the orbital state (frames, clock,
/// contacts) stays the planets' (no planet, no system).
/// The key's value is the body's root entity (a seated character's grid: the 4.0 save refuses a reference to an entity it
/// does not hold as its own).
/// </summary>
public partial class OrbitalWorldBody : Component, IInSceneListener
{
    /// <summary>The fixed component id (saved with the body: the same every load). ...b1 is the planets' beacon.</summary>
    internal static readonly System.Guid Id = new System.Guid("0a7b17a1-5eed-4f00-8a11-0000000000b2");

    [Keen.VRage.DCS.Annotations.Init]
    private void InitSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        EnsureHooks();
        OrbitalSettings.Load(ob, WorldCarriers.Kind.Body, SessionOf(Entity));
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        ob.NamedEntities = new Dictionary<string, Entity>();   // (a fresh map: the builder is a complete snapshot)
        OrbitalSettings.Save(ob, RootOf(Entity));
    }

    void IInSceneListener.OnAddedToScene()
    {
        EnsureHooks();
        var s = SessionOf(Entity);
        WorldReset.ServerSession(s);   // (another world's session, its unload not seen: the last world's statics dropped)
        OrbitalSettings.CarrierAdded(s);
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        var s = SessionOf(Entity);
        OrbitalSettings.CarrierRemoved(s, s == null || s.IsUnloading);
    }

    /// <summary>The world's end beyond the settings: at its unload, every per-world static (WorldReset.Unloaded). A world
    /// whose unload was not seen is WorldReset.ServerSession's (each carrier's OnAddedToScene).</summary>
    static void Hooks(bool unloading) { if (unloading) WorldReset.Unloaded(); }
    internal static void EnsureHooks() => OrbitalSettings.WorldHooks ??= Hooks;

    /// <summary>The entity's session; null while it does not know its scene (a carrier's [Init] may run before).</summary>
    internal static Session SessionOf(Entity e)
    {
        try { return e?.Scene?.UserObject as Session; }
        catch { return null; }   // (not a hot path: once per carrier at load and at its scene's changes)
    }

    /// <summary>The top of the entity's hierarchy (a bundle root of the save: a seated character's grid); itself if none.</summary>
    internal static Entity RootOf(Entity e)
    {
        var top = e;
        for (int up = 0; top != null && up < 8; up++)
        {
            var parent = top.TryGet<HierarchyComponent>()?.Parent;
            if (parent == null) break;
            top = parent;
        }
        return top ?? e;
    }
}
