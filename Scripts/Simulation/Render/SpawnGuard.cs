using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.SpawnPrevention;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// No encounter spawns on a planet's border. The game's procedural encounters are placed at fixed world
/// spots (its encounter sectors) and spawn when you come near; our frames' berths are world spots too, so a
/// frame orbiting low about a planet would meet them there (and one moved away is spawned again at its
/// spot). The generator skips any encounter whose box touches a spawn-prevention zone, so every frame on a
/// planet's border (periapsis under the keep radius) gets one over its berth, moved with it and taken away
/// when it leaves. A frame nobody is in gets one over its own bubble too. They are the generator's local (dynamic)
/// zones: never saved.
/// </summary>
public static class SpawnGuard
{
    public static string Status = "-";

    /// <summary>frame id -> (zone leaf id, the centre it was placed at).</summary>
    private static readonly Dictionary<long, (int leaf, Vector3D at, double r, bool border)> _zones = new Dictionary<long, (int, Vector3D, double, bool)>();
    private static MethodInfo _add, _remove;
    private static ProceduralGeneratorSessionComponent _gen;

    private static FieldInfo _encounters;
    private static MethodInfo _delete;

    /// <summary>
    /// Delete the game's procedural encounters whose area is within r of a world point (one that spawned on a
    /// border before its zone was up). Through the generator, as it despawns its own: the grids and the rock
    /// go together, and the zone keeps it from spawning there again. Out: how many.
    /// </summary>
    public static int DeleteEncountersNear(Vector3D at, double r)
    {
        var gen = _gen;
        if (gen == null) return 0;
        try
        {
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _encounters ??= gen.GetType().GetField("_encounters", bf);
            _delete ??= gen.GetType().GetMethod("DeleteEncounter", bf);
            if (!(_encounters?.GetValue(gen) is System.Collections.IDictionary all) || _delete == null) return 0;
            var ids = new List<object>();
            foreach (System.Collections.DictionaryEntry kv in all)
            {
                if (!(kv.Value is ProceduralSpaceEncounter e) || e.SpawnPreventionArea == null) continue;
                if (AsteroidBridge.BoxDistance(e.SpawnPreventionArea.Bounds, at) <= r) ids.Add(kv.Key);
            }
            foreach (var id in ids) _delete.Invoke(gen, new object[] { id });
            return ids.Count;
        }
        catch (System.Exception e) { Status = "spawn guard: delete failed: " + (e.InnerException ?? e).Message; return 0; }
    }

    /// <summary>Now, not on the next tick: a frame just made (a split) - the game spawned an encounter round its new berth
    /// in the very next tick, before the guard's own tick came round.</summary>
    public static void Refresh() { if (_gen != null) Tick(_gen); }

    public static void Tick(ProceduralGeneratorSessionComponent gen)
    {
        if (gen == null) return;
        if (!ReferenceEquals(gen, _gen)) { _zones.Clear(); _gen = gen; }   // (a new session: the old leaves went with it)
        try
        {
            if (_add == null)
            {
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _add = typeof(ProceduralGeneratorSessionComponent).GetMethod("RegisterLocalSpawnPreventionShape", bf);
                _remove = typeof(ProceduralGeneratorSessionComponent).GetMethod("RemoveLocalSpawnPreventionShape", bf);
                if (_add == null || _remove == null) { Status = "spawn guard: no prevention zone API"; return; }
            }
            var want = new Dictionary<long, (Vector3D at, double r, bool border)>();
            var reg = SystemHost.Frames;
            var mine = FrameHost.PlayerFrame;
            if (reg != null)
                foreach (var f in reg.Frames)
                {
                    if (EncounterFrames.IsSite(f)) continue;
                    if (EncounterFrames.OnPlanetBorder(f, out _)) { want[f.Id] = (f.BerthCenter, EncounterFrames.FarReach, true); continue; }
                    // A frame nobody is in (the world's own ships, parked in a fresh berth by a split): the game spawned an
                    // encounter round them there - a 230 ms hitch at each split, and rocks and wrecks in empty berths that
                    // then captured frames. Its bubble only (slots are 100+ km apart: never over yours).
                    bool seen = mine != null && mine.Id == f.Id;
                    if (!seen) foreach (long m in f.Members) if (!GridMembers.IsGridId(m)) { seen = true; break; }   // (a character in it)
                    if (!seen) want[f.Id] = (f.BerthCenter, ServerFrames.SlotRadius, false);
                }
            // free warm berths: their placeholders would trigger encounters as any grid (keys below every frame id)
            foreach (var w in WarmBerths.FreeWarm()) want[-1000L - w.slot] = (w.at, ServerFrames.SlotRadius, false);

            foreach (var id in new List<long>(_zones.Keys))
            {
                var z = _zones[id];
                if (want.TryGetValue(id, out var w) && (w.at - z.at).Length() < 1000 && Math.Abs(w.r - z.r) < 1) continue;
                _remove.Invoke(gen, new object[] { z.leaf });
                _zones.Remove(id);
            }
            foreach (var kv in want)
            {
                if (_zones.ContainsKey(kv.Key)) continue;
                var sphere = new BoundingSphereD(kv.Value.at, kv.Value.r);   // (whatever spawns this near is framed with it)
                var area = new SpawnPreventionAreaSphere(in sphere);
                int leaf = (int)_add.Invoke(gen, new object[] { area });
                _zones[kv.Key] = (leaf, kv.Value.at, kv.Value.r, kv.Value.border);
                if (kv.Value.border) Log.Default?.Info($"[ORBIT-ENC] spawn guard: frame #{kv.Key} is on a planet's border: no encounters within {kv.Value.r / 1000:F0} km of its berth (zone {leaf}, dyn test {gen.AnyPreventionZoneContains(kv.Value.at)}, box test {gen.CanSpawnInArea(new BoundingBoxD(kv.Value.at - new Vector3D(100), kv.Value.at + new Vector3D(100)))})");
            }
            int border = 0; foreach (var z in _zones.Values) if (z.border) border++;
            Status = $"spawn guard: {border} border berth(s) closed to encounters, {_zones.Count - border} berth(s) nobody is in";
        }
        catch (System.Exception e) { Status = "spawn guard failed: " + (e.InnerException ?? e).Message; }
    }
}
