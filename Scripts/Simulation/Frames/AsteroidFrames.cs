using System.Reflection;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.ProceduralGeneration;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.Volume;
using Keen.VRage.DCS.ObjectBuilders;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// ASTEROIDS ARE ENCOUNTER FRAMES. Every belt (a Ring sector home: Zarkon and Pyrethra about the star,
/// Oblivara round Verdure) holds a fixed set of asteroid frames, single rocks and small clusters, each on
/// its own near-circular orbit through the band. You reach one as you reach a site: by rendezvous.
///
///  - POPULATION: deterministic from the belt's name (12-20 each, about a third clusters): radius, phase,
///    a small inclination within the band's thickness. Each is a site (EncounterFrames) with a home of its
///    own (a one-orbit ring), so targeting, closest approach, route planning and the merge all work as for
///    a sector. Its berth is a lattice slot of the frame allocator reserved for it (far past the few the
///    frames use: slot ids from <see cref="SlotBase"/>), so it is the same slot every load and nothing
///    else is ever put there. Nothing is saved: sites are rebuilt at build, as the authored ones are.
///  - ROCKS ON DEMAND: while your frame is merged with one, or near its orbit, a small manual procedural
///    volume (a ProceduralField: an ellipsoid sized to the rock or cluster) sits at its berth, and the
///    game's generator spawns its rocks there as you come close, as it does anywhere. When you have left
///    for a while the volume goes, and with it every rock you did not touch (AsteroidBridge deletes
///    them: the generator keeps player-edited ones, marked manual).
///  - COMPOSITION: the game resolves a volume's composition by its guid (in the save, and on the client,
///    whose own generator spawns what you see), so a copy of a shared definition would come back as the
///    shared one, zeroed. A volume uses instead a RESERVED definition: one the game ships but this world
///    never uses (none of the generator's data, the colonization sectors' volumes or the game's manual
///    volumes), given a density here. AsteroidBridge leaves reserved ones alone; should the game ever use
///    one, it is given back (and zeroed like the rest).
///  - SAVE / LOAD: the game saves manual volumes with the world. Ours are named "OrbitalRoid_..."; on load
///    one at its asteroid's berth with a reserved composition is taken back, any other is deleted.
/// </summary>
public static class AsteroidFrames
{
    /// <summary>Off by default: not yet run in the game (see "roids on" in the harness).</summary>
    public static bool Enabled = false;
    public static string Status = "not built";

    public const int MinPerBelt = 12, MaxPerBelt = 20;
    public const double ClusterShare = 0.35;
    /// <summary>First lattice slot id tried for an asteroid's berth (frames take the lowest free ones).</summary>
    public const int SlotBase = 150;
    /// <summary>Rocks are put out when your frame is this close to the asteroid's orbit position (m), or merged with it.</summary>
    public const double MaterializeRange = 50000.0;
    /// <summary>...and taken away this long after neither holds (s).</summary>
    public const double LeaveSeconds = 20.0;
    /// <summary>The volumes' half-sizes (m): a rock (a few boulders), a cluster.</summary>
    public static readonly Vector3D RockRadii = new Vector3D(1500, 1000, 1500), ClusterRadii = new Vector3D(2600, 1600, 2600);
    /// <summary>Density of a reserved composition (entities per km^3): above the generator's sample rate, so every sample in a volume spawns.</summary>
    public const float RockDensity = 1.0f;
    /// <summary>A cluster member can land this far past its volume (m): still ours.</summary>
    public const double ProtectMargin = 2500.0;
    public const string VolumePrefix = "OrbitalRoid_";

    /// <summary>The server prefab of a procedural volume (what the game's voxel debug screen spawns).</summary>
    static readonly Guid VolumePrefab = new Guid("8a4dba37-1cf7-435f-868c-86daadc3725b");

    // Compositions the game ships but uses nowhere (its VS2_2 field and ring generators), by preference per belt.
    static readonly (string name, Guid id) IronRich = ("IronRichBoulderField", new Guid("61afe307-3cf4-48f1-b501-6a077190d497")),
        RareOre = ("RareOreBoulderField", new Guid("faa11151-22dd-4ae7-b5a8-a71dc2a64cac")),
        IceShatter = ("IceShatterField", new Guid("0a2fa936-66d1-489a-b38c-b6da0de43aba")),
        KemikRing = ("KemikRingField", new Guid("11fde5c7-d247-4965-b5b8-7d90ac83b980")),
        VerdureRing = ("VerdureRingField", new Guid("066a07aa-8d81-469f-ab86-3b057dae6ef1"));
    static (string name, Guid id)[] Preference(string belt) => belt switch
    {
        "Pyrethra" => new[] { IceShatter, RareOre, IronRich, KemikRing, VerdureRing },
        "Oblivara" => new[] { VerdureRing, IceShatter, KemikRing, RareOre, IronRich },
        _ => new[] { IronRich, RareOre, KemikRing, IceShatter, VerdureRing },
    };

    public sealed class Roid
    {
        public int Index;
        public string Belt, Label, Name;
        public bool Cluster;
        public SectorHomes.Home Home;
        public EncounterFrames.Site Site;
        public long FrameId;
        public int Slot;
        public Vector3D Berth, Radii;
        public Entity Volume;                 // the live manual volume, or null
        public VolumeDefinition Comp;         // its composition
        public double VolumeSince, LastWanted, NextTry;
        public int Missing;                   // scans in a row the volume was not found
        public bool Forced;
        public int Rocks;                     // generator bodies in it (server), last scan
    }

    private static readonly object _gate = new object();
    private static readonly List<Roid> _roids = new List<Roid>();
    private static readonly Dictionary<string, Roid> _byName = new Dictionary<string, Roid>();
    private static bool _built;

    /// <summary>Every volume of ours in the world (live or saved), refreshed each AsteroidBridge pass.</summary>
    private sealed class Owned { public Entity E; public string Name; public IProceduralVolume Vol; public Vector3D C; public double R; public int Rocks; }
    private static List<Owned> _owned = new List<Owned>();

    /// <summary>Reserved compositions: the definition and the density it came with.</summary>
    private static readonly Dictionary<VolumeDefinition, float> _reserved = new Dictionary<VolumeDefinition, float>();
    private static readonly Dictionary<string, VolumeDefinition> _beltComp = new Dictionary<string, VolumeDefinition>();
    private static readonly HashSet<VolumeDefinition> _givenBack = new HashSet<VolumeDefinition>();

    private static readonly ConcurrentQueue<(int index, bool on)> _force = new ConcurrentQueue<(int, bool)>();
    private static readonly HashSet<string> _warned = new HashSet<string>();
    private static readonly List<string> _errors = new List<string>();

    // ───────────────────────────── server tick (holds FramesLock; from EncounterFrames) ─────────────────────────────

    public static void ServerTick(Keen.VRage.Core.Game.Systems.Session session, double t, int tick)
    {
        if (!Enabled || session == null) return;
        try
        {
            if (!_built) Build(session, t);
            if (tick % 30 == 0) Update(session, t);
        }
        catch (Exception e) { Warn("tick", "asteroid frames: " + (e.InnerException ?? e).Message); }
    }

    // ───────────────────────────── population ─────────────────────────────

    private static void Build(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        _built = true;
        var reg = SystemHost.Registry;
        var alloc = SystemHost.Frames?.Allocator;
        if (reg == null || alloc == null) { Status = "no system"; return; }

        // The belts: the Ring homes of the chart's sectors (or the known ones, without a chart).
        var belts = new List<SectorHomes.Home>();
        try
        {
            var sectors = session.SessionComponents.TryGet<SectorsSessionComponent>();
            if (sectors != null) foreach (var h in GameMap.HomesBySector(sectors, reg).Values) if (h.Kind == SectorHomes.Kind.Ring) belts.Add(h);
        }
        catch (Exception e) { Warn("homes", "belts from the chart: " + e.Message); }
        if (belts.Count == 0)
            foreach (var name in new[] { "Zarkon", "Pyrethra", "Oblivara" }) belts.Add(SectorHomes.For(name, null, 0, 0, reg));
        belts.RemoveAll(h => h.Kind != SectorHomes.Kind.Ring || reg.Find(h.Host) == null || !(h.Outer > h.Inner));
        belts.Sort((a, b) => string.CompareOrdinal(a.Sector, b.Sector));

        int slot = SlotBase, made = 0;
        var list = new List<Roid>();
        foreach (var belt in belts)
        {
            ulong seed = Hash(belt.Sector);
            int n = MinPerBelt + (int)(Next(ref seed) % (ulong)(MaxPerBelt - MinPerBelt + 1));
            double half = BeltHalfThickness(belt);
            int rocks = 0, clusters = 0;
            for (int i = 0; i < n; i++)
            {
                bool cluster = Unit(ref seed) < ClusterShare;
                double r = belt.Inner + (belt.Outer - belt.Inner) * (0.08 + 0.84 * Unit(ref seed));
                double phase = 2 * Math.PI * (i + 0.8 * (Unit(ref seed) - 0.5)) / n + (belt.Phase);
                double inc = Math.Atan2(half, r) * (2 * Unit(ref seed) - 1);
                double node = 2 * Math.PI * Unit(ref seed);
                string label = $"{belt.Sector} {(cluster ? "cluster" : "rock")} {(cluster ? ++clusters : ++rocks)}";
                var home = new SectorHomes.Home
                {
                    Sector = label, Kind = SectorHomes.Kind.Ring, Host = belt.Host,
                    Inner = r, Outer = r, Phase = phase, Tilt = inc, Node = node,
                };
                if (!ReserveBerth(alloc, reg, ref slot, out int sid, out Vector3D berth)) { Warn("slots", "no free lattice slot for an asteroid"); break; }
                var site = new EncounterFrames.Site { Sector = label, Host = belt.Host, Home = home, World = berth, Label = label, Anchor = true };
                var f = EncounterFrames.AddSite(site, t);
                if (f == null) { Warn("site " + label, $"{label}: no orbit"); continue; }
                list.Add(new Roid
                {
                    Index = list.Count, Belt = belt.Sector, Label = label, Name = VolumePrefix + label.Replace(' ', '_'),
                    Cluster = cluster, Home = home, Site = site, FrameId = f.Id, Slot = sid, Berth = berth,
                    Radii = cluster ? ClusterRadii : RockRadii,
                });
                made++;
            }
        }
        lock (_gate)
        {
            _roids.Clear(); _roids.AddRange(list);
            _byName.Clear(); foreach (var r in list) _byName[r.Name] = r;
        }
        Status = $"{made} asteroid frame(s) in {belts.Count} belt(s)";
        Event($"BUILT {Status}: " + string.Join(", ", belts.ConvertAll(b => $"{b.Sector} {list.Count(r => r.Belt == b.Sector)}")));
    }

    /// <summary>A band's half-thickness (m): the game's torus for a planet's ring, else a share of its width.</summary>
    static double BeltHalfThickness(SectorHomes.Home h)
    {
        if (SystemHost.BeaconOf.TryGetValue(h.Host ?? "", out var b))
            lock (AsteroidBridge.Rings)
                foreach (var ring in AsteroidBridge.Rings)
                    if ((ring.C - b.Center).Length() < Math.Max(5000.0, 0.05 * ring.Out) && ring.Half > 0) return ring.Half;
        return (h.Outer - h.Inner) * (SystemHost.BeaconOf.ContainsKey(h.Host ?? "") ? 0.02 : 0.15);
    }

    /// <summary>
    /// The next lattice slot from <paramref name="slot"/> that no frame holds and no planet cell is near, reserved
    /// on the allocator (so no frame is ever given it). The same slot every load: frames take the lowest free ids.
    /// </summary>
    static bool ReserveBerth(BerthAllocator alloc, SystemRegistry reg, ref int slot, out int id, out Vector3D centre)
    {
        id = -1; centre = default;
        for (int tries = 0; tries < 4096; tries++, slot++)
        {
            if (alloc.IsOccupied(slot)) continue;
            Vector3D c = alloc.SlotCenter(slot);
            if (VoxelBerthRegistry.TryCellContaining(c, reg, out _, out _)) continue;
            bool clear = true;
            foreach (var kv in VoxelBerthRegistry.PinnedCells) if ((kv.Value - c).Length() < 2 * alloc.SlotRadius) { clear = false; break; }
            if (clear) foreach (var f in SystemHost.Frames.Frames) if ((f.BerthCenter - c).Length() < alloc.SlotRadius) { clear = false; break; }
            if (!clear) continue;
            alloc.Reserve(slot);
            id = slot++; centre = c;
            return true;
        }
        return false;
    }

    // ───────────────────────────── rocks on demand ─────────────────────────────

    private static void Update(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        double now = Wall();
        while (_force.TryDequeue(out var fr))
            lock (_gate) if (fr.index >= 0 && fr.index < _roids.Count) { _roids[fr.index].Forced = fr.on; if (fr.on) _roids[fr.index].LastWanted = now; }

        var gen = AsteroidBridge.Generator(session);
        if (gen == null) { Status = "no generator"; return; }
        Reconcile(session, gen, now);

        var pf = FrameHost.PlayerId != 0 ? SystemHost.Frames.FindByMember(FrameHost.PlayerId) : null;
        StateVector ps = default; bool havePs = false;
        if (pf != null) { ps = OrbitPropagation.StateAt(pf.Elements, t); havePs = IsFinite(ps.Position); }
        List<Roid> roids;
        lock (_gate) roids = new List<Roid>(_roids);
        int live = 0;
        foreach (var r in roids)
        {
            var f = SystemHost.Frames.Get(r.FrameId);
            bool wanted = r.Forced;
            if (f != null && !wanted)
            {
                if (EncounterFrames.HasNonNpc(f)) wanted = true;   // you (or your grid) are in it
                else if (havePs && pf != f && pf.ParentBodyName == f.ParentBodyName)
                {
                    var rs = OrbitPropagation.StateAt(f.Elements, t);
                    wanted = IsFinite(rs.Position) && (rs.Position - ps.Position).Length() < MaterializeRange;
                }
            }
            if (wanted) r.LastWanted = now;
            try
            {
                if (r.Volume != null && r.Comp != null && _givenBack.Contains(r.Comp)) Dematerialize(gen, r, "its composition is the game's again");
                if (wanted && r.Volume == null && now >= r.NextTry) { r.NextTry = now + 10; Materialize(session, gen, r, now); }
                else if (!wanted && r.Volume != null && now - r.LastWanted > LeaveSeconds) Dematerialize(gen, r, "left");
            }
            catch (Exception e) { Warn("vol " + r.Label, $"{r.Label}: {(e.InnerException ?? e).Message}"); }
            if (r.Volume != null) live++;
        }
        Status = $"{roids.Count} asteroid frame(s), {live} with rocks out{(gen.IsActive ? "" : " (the game's asteroid generation is OFF: no rocks)")}";
    }

    private static void Materialize(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen, Roid r, double now)
    {
        var comp = CompositionFor(session, gen, r.Belt);
        if (comp == null) return;
        var spawner = Spawner(gen);
        if (spawner == null) { Warn("spawner", "the generator's entity spawner was not found"); return; }
        if (!DefinitionManager.Instance.TryGetDefinition(VolumePrefab, out PrefabDefinition prefab) || prefab == null) { Warn("prefab", "the procedural volume prefab was not found"); return; }

        var field = new ProceduralField(new EllipsoidVolume(new WorldTransform(r.Berth, Quaternion.Identity), r.Radii), comp, null);
        // Never over another manual volume (the generator's own rule).
        var box = field.GetOrientedBoundingBox().GetAABB();
        using (var buf = new Buffer<IProceduralVolume>(Allocator.Pool, "OrbitalRoids"))
        {
            gen.QueryManualVolumes(in box, buf);
            var en = ((BufferReference<IProceduralVolume>)buf).GetEnumerator();
            bool hit = false;
            while (en.MoveNext()) { var ob = en.Current.GetOrientedBoundingBox().GetAABB(); if (Overlap(ob, box)) hit = true; }
            en.Dispose();
            if (hit) { Warn("overlap " + r.Label, $"{r.Label}: another volume is at its berth; no rocks"); return; }
        }
        var eob = prefab.Get();
        var vob = eob.OB<ProceduralVolumeComponent, ProceduralVolumeComponentObjectBuilder>();
        vob.Name = r.Name;
        vob.Volume = field;
        var e = spawner.SpawnEntity(eob);
        if (e == null) { Warn("spawn " + r.Label, $"{r.Label}: the volume did not spawn"); return; }
        r.Volume = e; r.Comp = comp; r.VolumeSince = now; r.Missing = 0; r.Rocks = 0;
        var vol = e.TryGet<ProceduralVolumeComponent>()?.ProceduralVolume;
        lock (_gate) _owned.Add(new Owned { E = e, Name = r.Name, Vol = vol, C = r.Berth, R = MaxRadius(r.Radii) + ProtectMargin });
        Event($"ROCKS out: {r.Label} (frame #{r.FrameId}) at {ServerPlanetBeacon.Fmt(r.Berth)}, {(r.Cluster ? "cluster" : "rock")} volume {r.Radii.X / 1000:F1}x{r.Radii.Y / 1000:F1} km, {Name(comp)}");
    }

    private static void Dematerialize(ProceduralGeneratorSessionComponent gen, Roid r, string why)
    {
        var e = r.Volume;
        r.Volume = null; r.Rocks = 0;
        lock (_gate) _owned.RemoveAll(o => ReferenceEquals(o.E, e));
        // The volume goes; its untouched rocks go on AsteroidBridge's next pass (edited ones stay: marked manual).
        try { Spawner(gen)?.DeleteEntity(e); } catch (Exception ex) { Warn("delete " + r.Label, $"{r.Label}: volume delete: {ex.Message}"); }
        Event($"ROCKS away: {r.Label} ({why})");
    }

    /// <summary>
    /// Volumes of ours the world holds that we did not put out this session (a save): one at its asteroid's
    /// berth with a reserved composition is taken back; any other is deleted. A live volume gone from the
    /// world (deleted by someone else) is forgotten.
    /// </summary>
    private static void Reconcile(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen, double now)
    {
        List<Owned> owned;
        lock (_gate) owned = new List<Owned>(_owned);
        foreach (var o in owned)
        {
            Roid r;
            lock (_gate) _byName.TryGetValue(o.Name ?? "", out r);
            if (r != null && ReferenceEquals(r.Volume, o.E)) continue;
            bool mine = r != null && r.Volume == null && (o.C - r.Berth).Length() < 2000
                        && o.Vol?.Composition != null && ReferenceEquals(o.Vol.Composition, CompositionFor(session, gen, r.Belt));
            if (mine)
            {
                r.Volume = o.E; r.Comp = o.Vol.Composition; r.VolumeSince = now; r.LastWanted = now; r.Missing = 0;
                Event($"ROCKS kept from the save: {r.Label}");
                continue;
            }
            lock (_gate) _owned.RemoveAll(x => ReferenceEquals(x.E, o.E));
            try { Spawner(gen)?.DeleteEntity(o.E); Event($"stale volume '{o.Name}' at {ServerPlanetBeacon.Fmt(o.C)} deleted"); }
            catch (Exception ex) { Warn("stale " + o.Name, $"stale volume '{o.Name}': {ex.Message}"); }
        }
        lock (_gate)
            foreach (var r in _roids)
            {
                if (r.Volume == null || now - r.VolumeSince < 6) continue;
                if (_owned.Exists(o => ReferenceEquals(o.E, r.Volume))) { r.Missing = 0; continue; }
                if (++r.Missing >= 2) { Event($"volume of {r.Label} is gone from the world"); r.Volume = null; }
            }
    }

    // ───────────────────────────── reserved compositions ─────────────────────────────

    private static FieldInfo _density, _spawnerField;

    /// <summary>A belt's composition: the first of its preferences the world does not use, given a density.</summary>
    private static VolumeDefinition CompositionFor(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen, string belt)
    {
        if (_beltComp.TryGetValue(belt, out var have) && !_givenBack.Contains(have)) return have;
        var used = UsedByGame(session, gen);
        foreach (var (name, id) in Preference(belt))
        {
            if (!DefinitionManager.Instance.TryGetDefinition(id, out VolumeDefinition v) || v == null || v.Entities.Length == 0) continue;
            if (used.Contains(v) || _givenBack.Contains(v)) continue;
            if (!Reserve(v)) return null;
            _beltComp[belt] = v;
            Event($"{belt}: composition {name} (reserved, density {RockDensity}/km3)");
            return v;
        }
        Warn("comp " + belt, $"{belt}: no composition the world leaves unused; no rocks");
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
                foreach (var r in _roids) r.Rocks = 0;
            }
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
        lock (_gate) return _reserved.ContainsKey(v) && !_givenBack.Contains(v);
    }

    /// <summary>The game uses a composition we reserved (one of its own volumes has it): it is the game's again.</summary>
    public static void GameUses(VolumeDefinition v)
    {
        if (!IsReserved(v)) return;
        lock (_gate)
        {
            _givenBack.Add(v);
            if (_reserved.TryGetValue(v, out float d0)) SetDensity(v, d0);
        }
        Warn("givenback " + Name(v), $"the game uses composition {Name(v)}: given back (asteroid volumes with it are put out again with another)");
    }

    /// <summary>A generator body inside one of our volumes (a rock of an asteroid frame): kept, and counted.</summary>
    public static bool Protects(Vector3D p, bool count = true)
    {
        if (!Enabled) return false;
        lock (_gate)
            foreach (var o in _owned)
                if ((p - o.C).Length() <= o.R)
                {
                    if (!count) return true;
                    o.Rocks++;
                    foreach (var r in _roids) if (ReferenceEquals(r.Volume, o.E)) r.Rocks++;
                    return true;
                }
        return false;
    }

    /// <summary>The session ends: reserved compositions get their densities back (definitions are shared by the process).</summary>
    public static void Restore()
    {
        lock (_gate)
        {
            foreach (var kv in _reserved) SetDensity(kv.Key, kv.Value);
            _reserved.Clear(); _beltComp.Clear(); _givenBack.Clear(); _owned.Clear();
        }
    }

    /// <summary>A new system: everything from scratch.</summary>
    public static void Reset()
    {
        Restore();
        lock (_gate) { _roids.Clear(); _byName.Clear(); }
        _built = false; Status = "not built";
    }

    // ───────────────────────────── map ─────────────────────────────

    /// <summary>A belt's asteroid frames for the map: label, home, cluster, rocks out.</summary>
    public static List<(string label, SectorHomes.Home home, bool cluster, bool live)> Of(string belt)
    {
        var l = new List<(string, SectorHomes.Home, bool, bool)>();
        if (!Enabled) return l;
        lock (_gate) foreach (var r in _roids) if (r.Belt == belt) l.Add((r.Label, r.Home, r.Cluster, r.Volume != null));
        return l;
    }

    // ───────────────────────────── harness ─────────────────────────────

    /// <summary>roids status | list | on | off | goto &lt;i&gt; [behindKm] | spawn &lt;i&gt; | despawn &lt;i&gt; (client thread).</summary>
    public static string Command(string[] a)
    {
        string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
        int idx = a.Length > 2 && int.TryParse(a[2], out int ii) ? ii : -1;
        switch (sub)
        {
            case "on": Enabled = true; return "asteroid frames on (built on the next server tick)";
            case "off": Enabled = false; return "asteroid frames off (frames already built stay; no more rocks put out)";
            case "status": case "list":
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"roids: {(Enabled ? "on" : "off")}, {Status}");
                lock (_gate)
                {
                    foreach (var g in _roids.GroupBy(r => r.Belt))
                    {
                        _beltComp.TryGetValue(g.Key, out var comp);
                        sb.Append($"\n  {g.Key}: {g.Count(r => !r.Cluster)} rock(s), {g.Count(r => r.Cluster)} cluster(s), composition {(comp != null ? Name(comp) : "-")}");
                    }
                    sb.Append($"\n  volumes of ours in the world: {_owned.Count}; reserved compositions: {string.Join(", ", _reserved.Keys.Select(Name))}{(_givenBack.Count > 0 ? $"; given back: {string.Join(", ", _givenBack.Select(Name))}" : "")}");
                    foreach (var r in _roids)
                        if (sub == "list" || r.Volume != null || r.Forced)
                            sb.Append($"\n  [{r.Index}] {r.Label} frame #{r.FrameId} slot {r.Slot} r={r.Home.Inner / 1000:F0} km inc={r.Home.Tilt * 180 / Math.PI:F2} deg berth {ServerPlanetBeacon.Fmt(r.Berth)}" +
                                      $"{(r.Volume != null ? $" ROCKS OUT ({r.Rocks} seen)" : "")}{(r.Forced ? " forced" : "")}");
                    if (_errors.Count > 0) sb.Append("\n  errors: " + string.Join(" | ", _errors));
                }
                return sb.ToString();
            }
            case "spawn": case "despawn":
            {
                lock (_gate) if (idx < 0 || idx >= _roids.Count) return "no such asteroid (roids list)";
                _force.Enqueue((idx, sub == "spawn"));
                return $"{sub} queued for [{idx}]";
            }
            case "goto":
            {
                // Onto the asteroid's orbit, this far behind it (as gotosite): the rendezvous merges you in.
                double behind = a.Length > 3 ? double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) * 1000 : 6000;
                Roid r;
                lock (_gate) r = idx >= 0 && idx < _roids.Count ? _roids[idx] : null;
                if (r == null) return "no such asteroid (roids list)";
                lock (ServerFrames.FramesLock)
                {
                    var f = SystemHost.Frames?.Get(r.FrameId);
                    if (f == null) return $"{r.Label}: no frame";
                    double t = SystemHost.Now;
                    var st = OrbitPropagation.StateAt(f.Elements, t);
                    double dtb = behind / Math.Max(1, st.Velocity.Length());
                    var sb = OrbitPropagation.StateAt(f.Elements, t - dtb);
                    var el = CaptureMath.CaptureElements(sb, f.Elements.Mu, t);
                    var pf = SystemHost.Frames.FindByMember(FrameHost.PlayerId);
                    if (pf != null && !pf.IsEncounter) { pf.Elements = el; pf.ParentBodyName = f.ParentBodyName; pf.VirtualVelocity = sb.Velocity; return $"player frame #{pf.Id} -> {behind / 1000:F1} km behind {r.Label} (frame #{f.Id})"; }
                    FrameHost.SetPendingOrbit(f.ParentBodyName, el);
                    return $"stowing {behind / 1000:F1} km behind {r.Label} about {f.ParentBodyName}";
                }
            }
        }
        return "roids status | list | on | off | goto <i> [behindKm] | spawn <i> | despawn <i>";
    }

    // ───────────────────────────── helpers ─────────────────────────────

    static IEntitySpawner Spawner(ProceduralGeneratorSessionComponent gen)
    {
        _spawnerField ??= typeof(ProceduralGeneratorSessionComponent).GetField("_spawner", BindingFlags.Instance | BindingFlags.NonPublic);
        return _spawnerField?.GetValue(gen) as IEntitySpawner;
    }

    static bool Overlap(BoundingBoxD a, BoundingBoxD b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    static double MaxRadius(Vector3D r) => Math.Max(r.X, Math.Max(r.Y, r.Z));

    static string Name(VolumeDefinition v) => v == null ? "-" : v.DebugName ?? v.Guid.ToString();

    // A stable hash of a name (string.GetHashCode differs per process) and a small generator from it.
    static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s ?? "") { h ^= c; h *= 1099511628211UL; }
        return h;
    }
    static ulong Next(ref ulong s)
    {
        s += 0x9E3779B97F4A7C15UL;
        ulong z = s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    static double Unit(ref ulong s) => (Next(ref s) >> 11) * (1.0 / (1UL << 53));

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    static bool IsFinite(Vector3D v) => !double.IsNaN(v.X) && !double.IsInfinity(v.X) && !double.IsNaN(v.Y) && !double.IsInfinity(v.Y) && !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);

    static void Warn(string key, string msg)
    {
        lock (_gate)
        {
            if (!_warned.Add(key)) return;
            _errors.Add(msg);
            if (_errors.Count > 12) _errors.RemoveAt(0);
        }
        Log.Default?.Warning("[ORBIT-ROIDS] " + msg);
    }

    static void Event(string s)
    {
        FrameHost.LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-ROIDS] " + s);
    }
}
