using System.Reflection;
using Keen.Game2.Simulation.GameSystems.Colonization;
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
public static partial class AsteroidFrames
{
    /// <summary>On by default ("roids on|off" in the harness; a world setting).</summary>
    public static bool Enabled = true;   // verified in game: rocks come out at an Oblivara cluster
    public static string Status = "not built";

    public const int MinPerBelt = 12, MaxPerBelt = 20;
    public const double ClusterShare = 0.35;
    /// <summary>First lattice slot id tried for an asteroid's berth (frames take the lowest free ones).</summary>
    public const int SlotBase = 150;
    /// <summary>Ring rocks' own slots: from here, ring by ring (RingRocks.Belts order), one per seeded rock.</summary>
    public const int RingSlotBase = 1000;
    /// <summary>A ring rock whose own slot is refused takes RingOverflowBase + the same offset (rings 0-18 fit below it).</summary>
    public const int RingOverflowBase = 20000;
    static int RingSlot(RingRocks.Belt b, int i)
    {
        var belts = RingRocks.Belts();
        int k = belts.FindIndex(x => x.Host == b.Host);   // (by its planet: the list is rebuilt when a ring is renamed)
        return RingSlotBase + System.Math.Max(0, k) * 1000 + (b.Number[i] - 1);   // (by its number: stable when a ring's count changes)
    }
    /// <summary>Rocks are put out when your frame is this close to the asteroid's orbit position (m), or merged with it.</summary>
    public const double MaterializeRange = 50000.0;
    /// <summary>...and taken away this long after neither holds (s).</summary>
    public const double LeaveSeconds = 20.0;
    /// <summary>At most this many asteroids within range have their rocks out (besides the one you are in).</summary>
    public const int MaxNearVolumes = 2;
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
        public bool Dynamic;                  // a ring rock (RingRocks) made a site because you came near: taken away when you leave
    }

    private static readonly object _gate = new object();
    private static readonly List<Roid> _roids = new List<Roid>();
    private static readonly Dictionary<string, Roid> _byName = new Dictionary<string, Roid>();
    /// <summary>Ring rocks left behind (site gone): label -> their site's description, its orbit still known.</summary>
    private static readonly Dictionary<string, EncounterFrames.Site> _left = new Dictionary<string, EncounterFrames.Site>();
    public static EncounterFrames.Site LeftSite(string label) { lock (_left) return label != null && _left.TryGetValue(label, out var s) ? s : null; }
    private static bool _built;

    /// <summary>Every volume of ours in the world (live or saved), refreshed each AsteroidBridge pass.</summary>
    private sealed class Owned { public Entity E; public string Name; public IProceduralVolume Vol; public Vector3D C; public double R; }
    private static List<Owned> _owned = new List<Owned>();

    /// <summary>Reserved compositions: the definition and the density it came with.</summary>
    private static readonly Dictionary<VolumeDefinition, float> _reserved = new Dictionary<VolumeDefinition, float>();
    private static readonly Dictionary<string, VolumeDefinition> _beltComp = new Dictionary<string, VolumeDefinition>();
    private static readonly HashSet<VolumeDefinition> _givenBack = new HashSet<VolumeDefinition>();
    /// <summary>Compositions shared with the game's own volumes: a density only while ours are out.</summary>
    private static readonly HashSet<VolumeDefinition> _shared = new HashSet<VolumeDefinition>();
    private static double _refreshAt;
    public static string Diag = "-";

    private static readonly ConcurrentQueue<(int index, bool on)> _force = new ConcurrentQueue<(int, bool)>();
    private static readonly HashSet<string> _warned = new HashSet<string>();
    private static readonly List<string> _errors = new List<string>();

    // ───────────────────────────── server tick (holds FramesLock; from EncounterFrames) ─────────────────────────────

    // The pass is in two halves. Under FramesLock (ServerTick): what reads or changes frames - the sites, which rocks are
    // wanted (from where you are). After it (ServerAfterLock, the same server tick): the generator's work - volumes spawned
    // and deleted, its sectors rebuilt, its bodies counted. That work is the engine's and can be slow; under the lock it
    // held the client's HUD, map and player tick (all of which take FramesLock) for as long.
    private static List<Roid> _planRoids;
    private static HashSet<Roid> _planWanted;
    private static double _planNow;

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

    /// <summary>Server tick, after FramesLock is released (ServerFrames.Tick): the generator's half of the pass the locked
    /// half planned this tick (nothing when it planned nothing). Takes FramesLock only briefly, to free a left ring rock's site.</summary>
    public static void ServerAfterLock(Keen.VRage.Core.Game.Systems.Session session)
    {
        var roids = _planRoids; var wanted = _planWanted; double now = _planNow;
        _planRoids = null; _planWanted = null;
        if (roids == null || wanted == null || !Enabled || session == null) return;
        try { Act(session, roids, wanted, now); }
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
        // A planet's ring is RingRocks' (seeded rocks, a site only for the one you meet): no fixed ones there.
        if (RingRocks.Enabled) belts.RemoveAll(h => reg.Find(h.Host) is GravityBody rb && !rb.IsRoot);
        belts.Sort((a, b) => string.CompareOrdinal(a.Sector, b.Sector));

        int slot = SlotBase, made = 0;
        var list = new List<Roid>();
        foreach (var belt in belts)
        {
            ulong seed = Hash(belt.Sector);
            // Never on a planet's border: a planet's belt (its ring) keeps its asteroids outside the planet's
            // keep radius (where frames hand over to its space and back), with a margin.
            double inner = belt.Inner, outer = belt.Outer;
            var hostDef = reg.FindDefinition(belt.Host);
            if (hostDef != null && reg.Find(belt.Host) is GravityBody hb && !hb.IsRoot)
                inner = Math.Max(inner, PlanetBerths.KeepRadius(hostDef) * 1.1);
            if (!(outer > inner)) { Event($"{belt.Sector}: the whole band is on {belt.Host}'s border: no asteroids"); continue; }
            int n = MinPerBelt + (int)(Next(ref seed) % (ulong)(MaxPerBelt - MinPerBelt + 1));
            double half = BeltHalfThickness(belt);
            int rocks = 0, clusters = 0;
            for (int i = 0; i < n; i++)
            {
                bool cluster = Unit(ref seed) < ClusterShare;
                double r = inner + (outer - inner) * (0.08 + 0.84 * Unit(ref seed));
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

    /// <summary>A ring rock (RingRocks) you have come near, as a site of its own (once: by its name).</summary>
    static void EnsureRingRoid(RingRocks.Belt b, int i, double t)
    {
        string label = $"{b.Name} #{b.Number[i]}";
        string name = VolumePrefix + label.Replace(' ', '_').Replace("#", "n");
        lock (_gate) if (_byName.ContainsKey(name)) return;
        var reg = SystemHost.Registry;
        var alloc = SystemHost.Frames?.Allocator;
        if (reg == null || alloc == null) return;
        // Each ring rock's OWN slot, always the same (its ring and seeded number), so it comes back to the same
        // berth: a rock you mined (the generator keeps what you edited) is there again when you return, and
        // the game's save keeps it there.
        int slot = RingSlot(b, i);
        var home = RingRocks.Home(b, i);
        // A frame already on its berth (saved as a plain frame because you were in the site): its site again.
        // (its own slot, or its overflow one: where it went when its own was refused)
        int overSlot = RingOverflowBase + (slot - RingSlotBase);
        Vector3D own = alloc.SlotCenter(slot), overC = alloc.SlotCenter(overSlot);
        ProximityFrame there = null; int thereSlot = slot;
        foreach (var fr in SystemHost.Frames.Frames)
        {
            if (EncounterFrames.IsSite(fr)) continue;
            if ((fr.BerthCenter - own).Length() < 1.0) { there = fr; thereSlot = slot; break; }
            if ((fr.BerthCenter - overC).Length() < 1.0) { there = fr; thereSlot = overSlot; }
        }
        int sid; Vector3D berth; ProximityFrame f;
        EncounterFrames.Site site;
        if (there != null)
        {
            sid = thereSlot; berth = thereSlot == slot ? own : overC;
            if (!alloc.IsOccupied(sid)) alloc.Reserve(sid);   // (a frame already sits there: kept even if refused - its grids are there)
            site = new EncounterFrames.Site { Sector = label, Host = b.Host, Home = home, World = berth, Label = label, Anchor = true };
            f = EncounterFrames.AdoptAsSite(site, there);
            Event($"ring rock {label}: frame #{there.Id} is on its berth (you loaded there): it is the rock's site again");
        }
        else
        {
            // Its own slot only; refused (or taken), its own overflow slot and on from there - never the walk to slot+1,
            // the next rock's own slot (2026-10-15: berths then depended on which rock you met first).
            int own1 = slot;
            if (!ReserveBerth(alloc, reg, ref own1, out sid, out berth, 1))
            {
                int over = overSlot;
                if (!ReserveBerth(alloc, reg, ref over, out sid, out berth)) { Warn("ringslot", "no free lattice slot for a ring rock"); return; }
            }
            site = new EncounterFrames.Site { Sector = label, Host = b.Host, Home = home, World = berth, Label = label, Anchor = true };
            f = EncounterFrames.AddSite(site, t);
        }
        if (f == null) { alloc.Free(sid); Warn("ring " + label, $"{label}: no orbit"); return; }
        var r = new Roid
        {
            Belt = b.Name, Label = label, Name = name, Cluster = b.Cluster[i], Home = home, Site = site,
            FrameId = f.Id, Slot = sid, Berth = berth, Radii = b.Cluster[i] ? ClusterRadii : RockRadii,
            Dynamic = true, LastWanted = Wall(),
        };
        lock (_gate) { r.Index = _roids.Count; _roids.Add(r); _byName[name] = r; }
        Event($"ring rock {label} near: a site of its own (frame #{f.Id}, slot {sid})");
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
    internal static bool ReserveBerth(BerthAllocator alloc, SystemRegistry reg, ref int slot, out int id, out Vector3D centre, int maxTries = 4096)
    {
        id = -1; centre = default;
        for (int tries = 0; tries < maxTries; tries++, slot++)
        {
            if (alloc.IsOccupied(slot)) continue;
            Vector3D c = alloc.SlotCenter(slot);
            if (VoxelBerthRegistry.TryCellContaining(c, reg, out _, out _)) continue;
            bool clear = true;
            foreach (var kv in VoxelBerthRegistry.PinnedCells) if ((kv.Value - c).Length() < 2 * alloc.SlotRadius) { clear = false; break; }
            if (clear) foreach (var f in SystemHost.Frames.Frames) if ((f.BerthCenter - c).Length() < alloc.SlotRadius) { clear = false; break; }
            if (!clear) continue;
            if (!alloc.Reserve(slot)) continue;   // (refused - inside a planet's frame: never a berth, never left unmarked)
            id = slot++; centre = c;
            return true;
        }
        return false;
    }

    // ───────────────────────────── discoveries ─────────────────────────────

    private static FieldInfo _providers;
    private static MethodInfo _providerRemoved;
    private static readonly HashSet<Entity> _hidden = new HashSet<Entity>();

    /// <summary>
    /// A procedural volume is itself a discoverable (ProceduralVolumeComponent is an IDiscoverableProvider: the
    /// game lists it under Discovered Objects, by its name, once you enter it). It is the volume, so it cannot be
    /// taken off the prefab: instead the discovery system forgets it as it does when one leaves the scene
    /// (DiscoveriesServerSessionComponent.ProviderRemoved: its entry for every player, the client's copy, the
    /// proximity trigger). The same call runs again when the volume is deleted: every step of it tolerates that.
    /// </summary>
    private static void Undiscoverable(Keen.VRage.Core.Game.Systems.Session session, Entity e)
    {
        if (e == null) return;
        lock (_gate) if (_hidden.Contains(e)) return;
        try
        {
            var disc = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Discoveries.DiscoveriesServerSessionComponent>();
            if (disc == null) return;
            var t = typeof(Keen.Game2.Simulation.GameSystems.Discoveries.DiscoveriesServerSessionComponent);
            _providers ??= t.GetField("_providerEntities", BindingFlags.Instance | BindingFlags.NonPublic);
            _providerRemoved ??= t.GetMethod("ProviderRemoved", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Entity) }, null);
            if (_providers == null || _providerRemoved == null) { Warn("undiscover", "the discovery system's provider list was not found: our volumes stay listed as discoveries"); return; }
            var pv = e.TryGet<ProceduralVolumeComponent>();
            if (pv == null) return;
            // Not registered yet (added to the scene later): the next scan catches it.
            if (!(_providers.GetValue(disc) is System.Collections.IDictionary d) || !d.Contains(pv)) return;
            _providerRemoved.Invoke(disc, new object[] { e });
            lock (_gate) { _hidden.RemoveWhere(x => !_owned.Exists(o => ReferenceEquals(o.E, x))); _hidden.Add(e); }
            Event($"volume '{pv.Name}' taken off the discoveries");
        }
        catch (Exception ex) { Warn("undiscover", "discoveries: " + (ex.InnerException ?? ex).Message); }
    }

    // ───────────────────────────── map ─────────────────────────────

    /// <summary>A belt's asteroid frames for the map: label, home, cluster, rocks out.</summary>
    /// <summary>Every asteroid frame (not the ring rocks' own: RingRocks has those): belt, label, home, cluster.</summary>
    public static List<(string belt, string label, SectorHomes.Home home, bool cluster)> All()
    {
        var l = new List<(string, string, SectorHomes.Home, bool)>();
        if (!Enabled) return l;
        lock (_gate) foreach (var r in _roids) if (!r.Dynamic) l.Add((r.Belt, r.Label, r.Home, r.Cluster));
        return l;
    }

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
                    sb.Append("\n  " + Diag);
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

    internal static IEntitySpawner Spawner(ProceduralGeneratorSessionComponent gen)
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
