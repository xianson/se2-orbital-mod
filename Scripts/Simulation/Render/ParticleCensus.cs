using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV: EVERY PARTICLE EFFECT THE GAME IS PLAYING (2026-10-12, the user: "are we able to somehow log what particles are
/// playing?" - puffs of smoke and sparks hanging in the sky; ours or the game's?). The client's ParticleEffectsSessionComponent
/// keeps its live effects in two sets (_activeLoopedEffects, _activeVolatileEffects); each ParticleEffectHandle has its
/// spawner's DebugTag (the aero mod's flight field spawns with "AeroFlight"), its ResolvedDefinition, whether it is emitting
/// (IsActive) and waiting to be disposed (IsWaitingForDispose). Grouped: tag | effect | looped or not | state : count.
/// All by reflection (VRage.Game.Client is not referenceable). Read off the render thread's sets: a dev snapshot.
/// </summary>
public static class ParticleCensus
{
    public static string Report(Keen.VRage.Core.Game.Systems.Session session, bool log, Vector3D camera)
    {
        object comp = null;
        foreach (var c in session.SessionComponents.Components)
            if (c != null && c.GetType().Name == "ParticleEffectsSessionComponent") { comp = c; break; }
        if (comp == null) return "no ParticleEffectsSessionComponent (client only)";
        var counts = new Dictionary<string, int>();
        int total = 0;
        // (its _active*Effects sets live in native memory behind a ref-struct enumerator - reflection cannot walk them: "Specified
        //  method is not supported". The effect pools' maps are managed: _effectPoolManager._pools[*]._entityMap, each value
        //  (chunk, index, handle) - every pooled effect's handle; the flight field's spawns are pooled, as are most)
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var mgr = comp.GetType().GetField("_effectPoolManager", F)?.GetValue(comp);
        var pools = mgr?.GetType().GetField("_pools", F)?.GetValue(mgr) as System.Collections.IDictionary;
        if (pools == null) return "no effect pools (_effectPoolManager._pools)";
        int unattached = 0;
        foreach (var pool in pools.Values)
        {
            var map = pool?.GetType().GetField("_entityMap", F)?.GetValue(pool) as System.Collections.IDictionary;
            if (map == null) continue;
            foreach (var v in map.Values)
            {
                var h = v?.GetType().GetField("Item3")?.GetValue(v);
                if (h == null) { unattached++; continue; }
                total++;
                var t = h.GetType();
                string tag = t.GetProperty("DebugTag")?.GetValue(h) as string ?? "(no tag)";
                var def = t.GetProperty("ResolvedDefinition")?.GetValue(h);
                string name = DefName(def);
                bool looped = def?.GetType().GetProperty("IsLooping")?.GetValue(def) is bool lp && lp;
                bool active = t.GetProperty("IsActive")?.GetValue(h) is bool a && a;
                bool dying = t.GetProperty("IsWaitingForDispose")?.GetValue(h) is bool w && w;
                bool gone = t.GetProperty("IsDisposed")?.GetValue(h) is bool g && g;
                // its owner: the grid (or other root) whose renderer it hangs on (ParticleEffectHandle.RootEntity, the render
                // parent's RootEntityOwner), its name and how far it is from the camera; an owner no longer in the scene: "gone"
                string owner = Owner(comp, t.GetProperty("RootEntity")?.GetValue(h), camera);
                string key = $"{tag} | {name} | {(looped ? "looped" : "one-shot")} | {(gone ? "disposed" : dying ? "dying" : active ? "emitting" : "stopped")} | owner {owner}";
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        if (unattached > 0) counts["(pooled entities with no handle: returning or idle)"] = unattached;
        var lines = counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,4} x {kv.Key}").ToList();
        string r = $"{total} live effects:\n" + string.Join("\n", lines);
        if (log) Log.Default?.Info("[ORBIT-PARTICLES] " + r.Replace("\n", " || "));
        return r;
    }

    static string Owner(object comp, object dentity, Vector3D camera)
    {
        if (dentity == null) return "none (world)";
        try
        {
            var scene = (comp as Keen.VRage.DCS.Components.Component)?.Entity?.Scene;
            if (scene == null) return "?";
            var de = (Keen.VRage.DCS.Accessors.DEntity)dentity;
            if (!scene.IsEntityAlive(de)) return "gone";   // (a deleted owner's transform threw: see EffectCull)
            var e = Keen.VRage.DCS.Components.Entity.TryGetFromDataEntity(new Keen.VRage.DCS.Accessors.DEntityContext(scene, de));
            if (e == null) return "gone";
            double d = (e.Data.GetWorldTransform().Position - camera).Length();
            string band = d < 1e3 ? "<1 km" : d < 1e4 ? "1-10 km" : d < 1e5 ? "10-100 km" : d < 1e6 ? "100-1000 km" : ">1000 km";
            // (its server grid: the nearest server copy to it - for 'cam grid <id>')
            long sid = 0; double sd = 200;
            var pos = e.Data.GetWorldTransform().Position;
            // (the server grids' places as the server publishes them, ServerFrames.GridPositions - their entities read from here, the
            //  client's frame, were the cross-thread read the game's DataGuard asserts on, 2026-10-15)
            lock (ServerFrames.GridPositions) foreach (var kv in ServerFrames.GridPositions) { double dd = (kv.Value - pos).Length(); if (dd < sd) { sd = dd; sid = kv.Key; } }
            return $"'{e.DebugName}' {band}{(sid != 0 ? $" (server {sid})" : "")}";
        }
        catch (Exception ex) { return "gone (" + (ex.InnerException ?? ex).GetType().Name + ")"; }
    }

    static string DefName(object def)
    {
        if (def == null) return "?";
        foreach (var p in new[] { "Name", "DisplayName", "DebugName", "Id" })
        {
            try { var v = def.GetType().GetProperty(p)?.GetValue(def); if (v != null && v.ToString().Length > 0) return v.ToString(); } catch { }
        }
        return def.ToString();
    }

    /// <summary>The engine's Set&lt;T&gt; (its own struct enumerator, by reflection: GetEnumerator / MoveNext / Current).</summary>
    static IEnumerable<object> Enumerate(object set)
    {
        if (set == null) yield break;
        // (its own GetEnumerator first: the IEnumerable one threw "Specified method is not supported")
        var ge = set.GetType().GetMethod("GetEnumerator", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        if (ge == null && set is System.Collections.IEnumerable e) { foreach (var x in e) if (x != null) yield return x; yield break; }
        object en = ge?.Invoke(set, null);
        if (en == null) yield break;
        // (a struct enumerator, boxed once: MoveNext advances the box)
        var mn = en.GetType().GetMethod("MoveNext"); var cur = en.GetType().GetProperty("Current");
        var items = new List<object>();
        try { int guard = 0; while (guard++ < 100000 && (bool)mn.Invoke(en, null)) { var x = cur.GetValue(en); if (x != null) items.Add(x); } }
        catch (Exception ex) { items.Add("enumeration failed: " + (ex.InnerException ?? ex).Message); }
        foreach (var x in items) yield return x;
    }
}
