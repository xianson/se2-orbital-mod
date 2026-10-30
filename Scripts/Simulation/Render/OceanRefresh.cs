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
///
/// The unload crash (2026-10-30, Concordia with Earth on Verdure, exit to the main menu): the renderer counts an ocean's
/// tiles per domain in a SET of tile corners (WaterRenderComponent._gridsPerDomain, IncrementGridsCount) but takes one off
/// per TILE when the ocean goes (PlanetaryWaterRemoved -> DecrementGridsCount) and drops the domain at zero - tiles that
/// share a corner empty the set early, and the next tile's lookup throws KeyNotFoundException: the game crashes leaving
/// the world. Guard: one corner no tile has (<see cref="Sentinel"/>) in each ocean domain's set, so the count never
/// reaches zero while the tiles go; the renderer clears every set and disposes every water entity when it leaves the scene
/// (OnBeforeRemovedFromScene). Checked every ~2 s for every ocean the renderer holds, whoever handed it over.
/// </summary>
internal static class OceanRefresh
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const int Sentinel = int.MinValue;
    static readonly HashSet<Session> _done = new(), _guardLogged = new();
    static int _frame;
    /// <summary>The sessions the last tick saw (for <see cref="GuardAtUnload"/>: the world may unload before a tick guarded them).</summary>
    static Session _client, _server;

    /// <summary>Client tick: both sessions of this process (the client's, the in-process server's), every ~2 s until done.</summary>
    internal static void Tick(Session client)
    {
        var server = ServerPlanetBeacon.ServerSession;
        _client = client; _server = server;
        if (++_frame % 120 != 0) return;
        Check(client);
        Guard(client);
        if (server != null && !ReferenceEquals(server, client)) { Check(server); Guard(server); }
    }

    /// <summary>The world unloads (WorldReset, before its entities are destroyed - the ocean's tiles go then): guard both
    /// sessions now, a world left within ~2 s of its ocean's tiles was not guarded by a tick yet (2026-10-30: crashed 3 s in).</summary>
    internal static void GuardAtUnload()
    {
        // (the server's first: its renderer is the one the ocean's removal reaches, and the client's session is already
        //  torn down when the server's unloads - its components gone; 2026-10-30)
        // (every domain the renderer still holds: the planets have left the water session's list by now - 2026-10-30, the
        //  planet-list guard found nothing at the unload and the game crashed; the renderer disposes them all right after)
        string s = Guard(_server, all: true);
        if (!ReferenceEquals(_server, _client)) s += " | " + Guard(_client, all: true);
        Keen.VRage.Library.Diagnostics.Log.Default?.Info("[ORBIT] ocean unload guard at the unload: " + s);
    }

    /// <summary>The unload crash guard (above): the sentinel corner in every ocean domain's set of this session's renderer.</summary>
    static string Guard(Session session, bool all = false)
    {
        var comps = session?.SessionComponents?.Components;
        if (comps == null) return session == null ? "no session" : "session torn down";
        object water = null, render = null;
        foreach (var c in comps)
        {
            if (c == null) continue;
            string n = c.GetType().Name;
            if (n == "WaterSessionComponent") water = c;
            else if (n == "WaterRenderComponent") render = c;
        }
        if (render == null) return "no renderer";
        if (!(PlanetRenderBridge.GetMember(render, "_gridsPerDomain") is IDictionary perDomain)) return "no _gridsPerDomain";
        string dropped = all ? DropUncounted(render, perDomain) : "";
        if (perDomain.Count == 0) return "renderer counts no water" + dropped;
        var domains = new List<object>();
        if (all) foreach (DictionaryEntry e in perDomain) domains.Add(e.Key);
        else
        {
            if (water == null || !(PlanetRenderBridge.GetMember(water, "_planetaryWaterComponents") is IList planets)) return "no planetary water";
            foreach (var p in planets)
            {
                object d = PlanetRenderBridge.GetMember(p, "Domain");
                if (d != null && perDomain.Contains(d)) domains.Add(d);
            }
        }
        int added = 0;
        foreach (var domain in domains)
        {
            object set = perDomain[domain];
            var t = set?.GetType();
            var add = t?.GetMethod("Add", F);   // (HashSet<Vector3I>.Add: the corner set is a VRage.Water.Client private)
            if (add == null || t.GetGenericArguments().Length != 1 || t.GetGenericArguments()[0] != typeof(Keen.VRage.Library.Mathematics.Vector3I))
            {
                PlanetRenderBridge.WarnOnce("ocean-guard-shape", "[ORBIT] ocean unload guard: WaterRenderComponent._gridsPerDomain is not a set of corners (" + (t?.Name ?? "null") + ")");
                return "not a set of corners";
            }
            object s = new Keen.VRage.Library.Mathematics.Vector3I(Sentinel, Sentinel, Sentinel);
            if ((bool)add.Invoke(set, new[] { s })) added++;
        }
        if (added > 0 && !all && _guardLogged.Add(session))
            Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[ORBIT] ocean unload guard: {added} ocean domain(s) kept from emptying while their tiles unload");
        return $"{domains.Count} domain(s), {added} newly guarded" + dropped;
    }

    /// <summary>At the unload: a water entity the renderer made for a domain whose tiles it never counted (the ocean announced
    /// before its tiles were made - the load-order bug above - and not refreshed yet: 2026-10-30, a world left 3 s after its
    /// load) - PlanetaryWaterRemoved would take one off per tile from a count that does not exist and throw. Disposed and
    /// dropped, as DecrementGridsCount does at zero: the removal then skips that domain. No count means no grid of it is
    /// drawn (GridAdded counts every one), so nothing else looks it up.</summary>
    static string DropUncounted(object render, IDictionary perDomain)
    {
        if (!(PlanetRenderBridge.GetMember(render, "_waterRenderEntities") is IDictionary entities)) return ", no _waterRenderEntities";
        var drop = new List<object>();
        foreach (DictionaryEntry e in entities) if (!perDomain.Contains(e.Key)) drop.Add(e.Key);
        foreach (var d in drop)
        {
            (entities[d] as System.IDisposable)?.Dispose();
            entities.Remove(d);
        }
        return drop.Count > 0 ? $", {drop.Count} uncounted water entit{(drop.Count == 1 ? "y" : "ies")} dropped" : "";
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
    internal static void Reset() { _done.Clear(); _guardLogged.Clear(); _client = _server = null; }
}
