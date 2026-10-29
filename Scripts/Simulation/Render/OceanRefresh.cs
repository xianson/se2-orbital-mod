using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.Core.Game.Systems;

namespace OrbitalMod;

/// <summary>
/// A planet's ocean drawn (2026-10-24, Earth on Verdure: the ocean existed - its domain, 272 ocean tiles, buoyancy's
/// queries said "in the ocean" - and nothing was drawn). The game's own load order: PlanetaryWaterComponent.OnAddedToScene
/// announces the ocean to the renderer (WaterDomainComponent.AddOceans -> OnPlanetaryWaterChanged) BEFORE it makes the
/// ocean's tiles (GenerateOceanTiles), and nothing announces it again - the renderer (WaterRenderComponent, in the session
/// the configurator calls the server's) got no tiles. No vanilla planet has an ocean, so the game never shows it.
/// Fix: once a session's planetary water has tiles while its renderer holds no ocean grid, WaterSessionComponent.
/// RefreshRenderData() (public; unused by the game) - every ocean handed to the renderer again. Once per session.
/// Reflection: VRage.Water is not a script reference (docs/REFLECTION_AND_WHITELIST.md §4.x, 2026-10-24).
/// </summary>
internal static class OceanRefresh
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly HashSet<Session> _done = new();
    static int _frame;

    /// <summary>Client tick: both sessions of this process (the client's, the in-process server's), every ~2 s until done.</summary>
    internal static void Tick(Session client)
    {
        if (++_frame % 120 != 0) return;
        Check(client);
        var server = ServerPlanetBeacon.ServerSession;
        if (server != null && !ReferenceEquals(server, client)) Check(server);
    }

    static void Check(Session session)
    {
        if (session == null || _done.Contains(session)) return;
        object water = null, render = null;
        foreach (var c in session.SessionComponents.Components)
        {
            if (c == null) continue;
            string n = c.GetType().Name;
            if (n == "WaterSessionComponent") water = c;
            else if (n == "WaterRenderComponent") render = c;
        }
        if (water == null) return;
        var planets = PlanetRenderBridge.GetMember(water, "_planetaryWaterComponents") as IList;
        if (planets == null) { PlanetRenderBridge.WarnOnce("ocean-refresh-list", "[ORBIT] ocean refresh: WaterSessionComponent._planetaryWaterComponents not found"); _done.Add(session); return; }
        if (planets.Count == 0) return;
        foreach (var p in planets)
            if (!((PlanetRenderBridge.GetMember(p, "_tiles") as ICollection)?.Count > 0)) return;   // (its tiles not made yet)
        if (render != null && (PlanetRenderBridge.GetMember(render, "_gridsPerDomain") as ICollection)?.Count > 0) { _done.Add(session); return; }
        var m = water.GetType().GetMethod("RefreshRenderData", F, null, System.Type.EmptyTypes, null);
        if (m == null) { PlanetRenderBridge.WarnOnce("ocean-refresh-method", "[ORBIT] ocean refresh: WaterSessionComponent.RefreshRenderData not found"); _done.Add(session); return; }
        m.Invoke(water, null);
        _done.Add(session);
        Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[ORBIT] ocean refresh: {planets.Count} planetary ocean(s) handed to the renderer again ({(render != null ? "renderer here" : "no renderer in this session")})");
    }

    /// <summary>A session ended: forget it (a reloaded world is a new session).</summary>
    internal static void Reset() => _done.Clear();
}
