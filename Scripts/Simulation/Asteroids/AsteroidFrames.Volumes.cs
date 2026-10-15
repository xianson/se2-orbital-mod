using System.Reflection;
using Keen.Game2.Simulation.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.Volume;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// AsteroidFrames (partial): the generator's compositions this mod reserves for its rocks, and what AsteroidBridge
// (the generator's own asteroid path, server) asks of them.
public static partial class AsteroidFrames
{
    // ───────────────────────────── reserved compositions ─────────────────────────────

    private static FieldInfo _density, _spawnerField;

    /// <summary>A belt's composition: the first of its preferences the world does not use, given a density.</summary>
    private static VolumeDefinition CompositionFor(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen, string belt)
    {
        if (_beltComp.TryGetValue(belt, out var have) && !_givenBack.Contains(have)) return have;
        var used = UsedByGame(session, gen);
        var why = new System.Text.StringBuilder();
        foreach (var (name, id) in Preference(belt))
        {
            if (!DefinitionManager.Instance.TryGetDefinition(id, out VolumeDefinition v) || v == null) { why.Append($" {name}: not loaded;"); continue; }
            if (v.Entities.Length == 0) { why.Append($" {name}: empty;"); continue; }
            if (used.Contains(v)) { why.Append($" {name}: used by the world;"); continue; }
            if (_givenBack.Contains(v)) { why.Append($" {name}: given back;"); continue; }
            if (!Reserve(v)) return null;
            _beltComp[belt] = v;
            Event($"{belt}: composition {name} (reserved, density {RockDensity}/km3)");
            return v;
        }
        // None unused: SHARE the belt's first choice. The game's own volumes use it too (zeroed by the bridge),
        // so it has a density only while one of our volumes is out, i.e. only while you are at an asteroid;
        // any rock their volumes spawn meanwhile is outside ours and the bridge deletes it.
        foreach (var (name, id) in Preference(belt))
        {
            if (!DefinitionManager.Instance.TryGetDefinition(id, out VolumeDefinition v) || v == null || v.Entities.Length == 0) continue;
            if (!Reserve(v)) return null;
            // (shared by choice: no longer 'given back', which a saved volume of ours with it can have marked it at
            // load, before this choice; left marked, every rock put out with it was taken in again next tick)
            lock (_gate) { _shared.Add(v); _givenBack.Remove(v); }
            _beltComp[belt] = v;
            Event($"{belt}: composition {name} SHARED with the game's volumes (a density only while at an asteroid;{why})");
            return v;
        }
        Warn("comp " + belt, $"{belt}: no composition at all; no rocks ({why.ToString().Trim()})");
        return null;
    }

    static bool Reserve(VolumeDefinition v)
    {
        if (_reserved.ContainsKey(v)) { SetDensity(v, RockDensity); return true; }
        _density ??= typeof(VolumeDefinition).GetField("<Density>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_density == null) { Warn("density", "VolumeDefinition density field not found"); return false; }
        float d0 = v.Density;
        lock (_gate) _reserved[v] = d0 < 0 ? 0.1f : d0;   // (one zeroed by a previous session's bridge: a sane value back)
        SetDensity(v, RockDensity);
        return true;
    }

    static void SetDensity(VolumeDefinition v, float d) { try { _density?.SetValue(v, d); } catch { } }

    /// <summary>Every composition the game's own generation uses in this world.</summary>
    static HashSet<VolumeDefinition> UsedByGame(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen)
    {
        var used = new HashSet<VolumeDefinition>();
        var def = gen.GenerationData?.Definition;
        if (def != null)
        {
            if (def.InfiniteArea != null) used.Add(def.InfiniteArea);
            foreach (var f in def.EllipsoidFields) if (f.Definition != null) used.Add(f.Definition);
        }
        try
        {
            var col = session.SessionComponents.TryGet<ColonizationSectorProceduralGenerationDataSessionComponent>();
            if (col?.Definition != null) foreach (var kv in col.Definition.SectorVolumes) if (kv.Value != null) used.Add(kv.Value);
        }
        catch { }
        using (var buf = new Buffer<IProceduralVolume>(Allocator.Pool, "OrbitalRoidsUsed"))
        {
            var all = new BoundingBoxD(new Vector3D(-1e13, -1e13, -1e13), new Vector3D(1e13, 1e13, 1e13));
            gen.QueryManualVolumes(in all, buf);
            var en = ((BufferReference<IProceduralVolume>)buf).GetEnumerator();
            while (en.MoveNext()) if (!IsOurVolume(en.Current) && en.Current.Composition != null) used.Add(en.Current.Composition);
            en.Dispose();
        }
        return used;
    }

    // ───────────────────────────── AsteroidBridge (server thread, every pass) ─────────────────────────────

    /// <summary>Find every volume of ours in the world (live or from a save). Called before the bridge zeroes and deletes.</summary>
    public static void Scan(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!Enabled || session == null) return;
        try
        {
            var found = new List<Owned>();
            foreach (var e in session.GetEntitiesOfType<ProceduralVolumeComponent>())
            {
                var pv = e.TryGet<ProceduralVolumeComponent>();
                if (pv?.Name == null || !pv.Name.StartsWith(VolumePrefix) || pv.ProceduralVolume == null) continue;
                var box = pv.ProceduralVolume.GetOrientedBoundingBox().GetAABB();
                found.Add(new Owned { E = e, Name = pv.Name, Vol = pv.ProceduralVolume, C = box.Center, R = 0.5 * (box.Max - box.Min).Length() + ProtectMargin });
                // A saved one's composition (a reserved one last session) is not zeroed meanwhile.
                var c = pv.ProceduralVolume.Composition;
                if (c != null && !_givenBack.Contains(c) && !_reserved.ContainsKey(c)) lock (_gate) _reserved[c] = c.Density < 0 ? 0.1f : c.Density;
            }
            lock (_gate)
            {
                // Just spawned and not listed yet: kept.
                foreach (var o in _owned) if (!found.Exists(x => ReferenceEquals(x.E, o.E)) && _roids.Exists(r => ReferenceEquals(r.Volume, o.E) && Wall() - r.VolumeSince < 6)) found.Add(o);
                _owned = found;
            }
            foreach (var o in found) Undiscoverable(session, o.E);
        }
        catch (Exception e) { Warn("scan", "volume scan: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>A manual volume that is one of ours.</summary>
    public static bool IsOurVolume(IProceduralVolume v)
    {
        if (!Enabled || v == null) return false;
        lock (_gate) foreach (var o in _owned) if (ReferenceEquals(o.Vol, v)) return true;
        return false;
    }

    /// <summary>A reserved composition (ours): the bridge must not zero it.</summary>
    public static bool IsReserved(VolumeDefinition v)
    {
        if (!Enabled || v == null) return false;
        lock (_gate)
            return _reserved.ContainsKey(v) && !_givenBack.Contains(v)
                   && (!_shared.Contains(v) || _roids.Exists(r => ReferenceEquals(r.Comp, v) && (r.Volume != null || Wall() - r.LastWanted < 30)));   // a shared one: only while ours are (being) put out
    }

    /// <summary>The game uses a composition we reserved (one of its own volumes has it): it is the game's again.</summary>
    public static void GameUses(VolumeDefinition v)
    {
        if (!IsReserved(v)) return;
        lock (_gate)
        {
            if (_shared.Contains(v)) return;   // (shared by choice: the game's volumes are zeroed by the bridge meanwhile)
            _givenBack.Add(v);
            if (_reserved.TryGetValue(v, out float d0)) SetDensity(v, d0);
        }
        Warn("givenback " + Name(v), $"the game uses composition {Name(v)}: given back (asteroid volumes with it are put out again with another)");
    }

    /// <summary>A generator body inside one of our volumes (a rock of an asteroid frame): kept. (Counted in CountRocks.)</summary>
    public static bool Protects(Vector3D p)
    {
        if (!Enabled) return false;
        lock (_gate)
            foreach (var o in _owned)
                if ((p - o.C).Length() <= o.R) return true;
        return false;
    }

    /// <summary>The session ends: reserved compositions get their densities back (definitions are shared by the process).</summary>
    public static void Restore()
    {
        lock (_gate)
        {
            foreach (var kv in _reserved) SetDensity(kv.Key, kv.Value);
            _reserved.Clear(); _beltComp.Clear(); _givenBack.Clear(); _owned.Clear(); _shared.Clear(); _hidden.Clear();
        }
    }

    /// <summary>A new system: everything from scratch.</summary>
    public static void Reset()
    {
        Restore();
        lock (_gate) { _roids.Clear(); _byName.Clear(); }
        lock (_left) _left.Clear();
        _built = false; Status = "not built";
    }
}
