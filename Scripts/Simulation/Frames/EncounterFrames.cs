using System.Collections.Concurrent;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// ENCOUNTERS ARE CONJUNCTION FRAMES (docs/design-encounter-frames.md).
///
/// Every encounter lives in frame space:
///  - AUTHORED SITES: the encounter / NPC grids the world already holds outside the planet cells are
///    grouped into SITE frames. A site frame is pinned where the world authored it (a latent frame: no
///    lattice slot, its berth is the site), and its orbit is PRESCRIBED: its sector's home (ellipse,
///    L1/L2 loop, belt, ring, Trojan) plus the site's offset within the sector, co-moving (RTN axes).
///    You reach a site by conjunction: when your frame's orbit meets the site's, the merge puts you
///    into the site's world spot at your true relative position and velocity. The site is always the
///    merge host, so the authored encounter never moves and the game's own triggers keep working.
///  - PROCEDURAL SPAWNS: an encounter the game spawns around a player on the rails is put into frame
///    space by distance: CLOSE (within <see cref="CloseRadius"/>) joins the player's frame (a similar
///    orbit: it floats alongside), FAR gets a frame of its own on a slightly different, eccentric
///    orbit (its relative drift brings it back as a conjunction, or not).
///
/// Dynamics: frames follow orbital dynamics (the rails). Inside a frame, player grids and characters feel
/// the relative-motion terms (differential gravity minus the frame acceleration). NPC grids do NOT: they
/// are plain Newtonian inside their frame, which keeps NPC behaviour (autopilots, AI routes) simple at the
/// local scale. NPC grids are never frame anchors, never drained, never stowed on their own.
/// </summary>
public static class EncounterFrames
{
    public static bool Enabled = true;
    /// <summary>Every deep-space sector gets an anchor site at its charted centre: reaching the sector's orbit
    /// drops you into its region, where the game spawns its encounters around you as usual.</summary>
    public static bool SectorAnchors = true;
    public const double SiteRadius = ServerFrames.SlotRadius;   // m: a site's bubble (same as the split radius)
    public const double CloseRadius = 5000.0;                   // m: a procedural spawn this close joins the frame
    public const double FarReach = 3 * SiteRadius;              // m: spawns beyond this from any frame are left alone
    public const double ClusterRadius = 2000.0;
    public const double FarMinKm = 25, FarMaxKm = 60;           // a far spawn's initial separation                 // m: grids spawned together go together
    public const double SpawnSettleSeconds = 2.0;               // s: let a spawn finish before framing it
    public const double BuildDelaySeconds = 8.0;                // s: after the system is built, before sites are read

    /// <summary>A site: an authored encounter's place in the system.</summary>
    public sealed class Site
    {
        public long FrameId;
        public string Sector, Host;
        public SectorHomes.Home Home;
        public Vector3D Rtn;            // offset from the home point in its radial/along/normal axes (m, model)
        public double OwnR, OwnTheta;   // own-planet sectors: a circular orbit of its own
        public Vector3D World;          // the pinned world spot
        public string Label;
        public bool Anchor;             // a sector's anchor: kept even when empty
    }

    private static readonly Dictionary<long, Site> _sites = new Dictionary<long, Site>();   // frame id -> site
    public static IEnumerable<Site> Sites => _sites.Values;
    public static int SiteCount => _sites.Count;
    public static bool IsSite(ProximityFrame f) => f != null && _sites.ContainsKey(f.Id);
    public static Site SiteOf(long frameId) => _sites.TryGetValue(frameId, out var s) ? s : null;

    private static bool _built;
    private static double _builtAt = -1;
    public static string Status = "not built";

    // ── NPC / encounter classification (server grids), re-checked every few seconds ──
    private static readonly Dictionary<long, (bool npc, bool enc, double at)> _kind = new Dictionary<long, (bool, bool, double)>();

    private static (bool npc, bool enc) Kind(OrbitalGridComponent g)
    {
        double now = Wall();
        lock (_kind)
            if (_kind.TryGetValue(g.Id, out var k) && now - k.at < 5.0) return (k.npc, k.enc);
        bool npc = false, enc = false;
        try
        {
            var own = g.Session?.SessionComponents.TryGet<OwnershipSessionComponent>();
            if (own != null) npc = own.IsNpc(g.Entity) || own.IsHostileNpc(g.Entity);
        }
        catch { }
        try
        {
            enc = g.Entity.Data.TryGet<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.ProcedurallyGeneratedTag>(out _)
                  || EncounterHelper.IsEncounterEntity(g.Entity);
            if (!enc)
            {
                var top = g.Entity.GetTopLevelParent();
                if (top != null && !ReferenceEquals(top, g.Entity)) enc = EncounterHelper.IsEncounterEntity(top);
            }
        }
        catch { }
        lock (_kind) _kind[g.Id] = (npc, enc, now);
        return (npc, enc);
    }

    /// <summary>NPC-owned: plain Newtonian inside its frame (no relative-motion terms), never an anchor.</summary>
    public static bool IsNpc(OrbitalGridComponent g) => g != null && Kind(g).npc;

    /// <summary>Part of an encounter (NPC-owned, or spawned from an encounter prefab).</summary>
    public static bool IsEncounterGrid(OrbitalGridComponent g) { if (g == null) return false; var k = Kind(g); return k.npc || k.enc; }

    public static bool IsNpcId(long id) => GridMembers.IsGridId(id) && IsNpc(GridMembers.Get(id));

    // ── new grids (spawns), queued from GridMembers.Register ──
    private static readonly ConcurrentQueue<(long id, double at)> _added = new ConcurrentQueue<(long, double)>();
    private static readonly List<(long id, double at)> _waiting = new List<(long, double)>();
    internal static void OnGridRegistered(long id) => _added.Enqueue((id, Wall()));

    // ───────────────────────────── server tick (holds FramesLock) ─────────────────────────────

    public static void ServerTick(Keen.VRage.Core.Game.Systems.Session session, int tick)
    {
        if (!Enabled || !SystemHost.Built || SystemHost.Frames == null) return;
        double t = SystemHost.Now;
        if (_builtAt < 0) _builtAt = Wall();
        if (!_built && Wall() - _builtAt > BuildDelaySeconds && SavedState.Idle) BuildSites(session, t);
        if (!_built) return;
        while (_devSites.TryDequeue(out var ds)) DevMakeSite(session, ds.grid, ds.sector, t);
        while (_devFar.TryDequeue(out long fg)) DevFar(fg, t);
        RefreshSites(t);
        if (tick % 20 == 0) AdoptIntoSites();
        ProcessNewGrids(t);
        if (tick % 60 == 0) Prune();
    }

    // ───────────────────────────── authored sites ─────────────────────────────

    private static void BuildSites(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        _built = true;
        var reg = SystemHost.Registry;
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        var homes = sectors != null ? GameMap.HomesBySector(sectors, reg) : new Dictionary<string, SectorHomes.Home>();

        // Candidates: unframed encounter / NPC grids outside every planet cell.
        var cand = new List<OrbitalGridComponent>();
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D p = GridMembers.Position(g);
            if (VoxelBerthRegistry.TryCellContaining(p, reg, out _, out _)) continue;
            if (!IsEncounterGrid(g)) continue;
            cand.Add(g);
        }
        // Heaviest first: a station seeds its site.
        cand.Sort((x, y) => GridMembers.Mass(y).CompareTo(GridMembers.Mass(x)));
        var used = new HashSet<long>();
        int n = 0;
        foreach (var seed in cand)
        {
            if (used.Contains(seed.Id)) continue;
            Vector3D c = GridMembers.Position(seed);
            var cluster = new List<OrbitalGridComponent>();
            foreach (var g in cand)
                if (!used.Contains(g.Id) && (GridMembers.Position(g) - c).Length() <= SiteRadius) { cluster.Add(g); used.Add(g.Id); }
            var f = MakeSite(sectors, homes, c, seed.DisplayName, t);
            if (f == null) continue;
            foreach (var g in cluster) SystemHost.Frames.AddMember(f, g.Id);
            n++;
        }
        int anchors = 0;
        var hazards = Hazards(session);
        if (SectorAnchors && sectors != null)
            foreach (var sc in sectors.Sectors)
            {
                // (A planet's own sector is its cell: no anchor. The star's and a body still to come's get one.)
                if (!homes.TryGetValue(sc.Name, out var h) || (h.Kind == SectorHomes.Kind.Body && !h.Future && !(reg.Find(h.Host)?.IsRoot ?? false))) continue;
                Vector3D c = ClearOf(hazards, sc.Area.Center, h.Host);
                if (VoxelBerthRegistry.TryCellContaining(c, reg, out _, out _) || SiteContaining(c) != null) continue;
                var f = MakeSite(sectors, homes, c, sc.Name, t, sc.Area.Center);
                if (f == null) continue;
                _sites[f.Id].Anchor = true;
                anchors++;
            }
        Status = $"{n} site(s) from {cand.Count} encounter grid(s), {anchors} sector anchor(s), {hazards.Count} hazard(s); {homes.Count} sector homes";
        Event($"SITES built: {Status}");
    }

    public const double HazardMargin = 1.08;   // keep anchors this far outside a killing field's outer radius

    /// <summary>
    /// Killing fields (a brown dwarf's heat): centre and outer radius. Delfos Sector's charted centre IS the
    /// brown dwarf, whose field kills a character within about 430 km in one 1.5 s damage tick.
    /// </summary>
    private static List<(Vector3D c, double r)> Hazards(Keen.VRage.Core.Game.Systems.Session session)
    {
        var l = new List<(Vector3D, double)>();
        try
        {
            foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.BrownDwarf.KillingFieldComponent>())
            {
                var comp = e.TryGet<Keen.Game2.Simulation.WorldObjects.BrownDwarf.KillingFieldComponent>();
                object def = comp != null ? PlanetRenderBridge.GetMember(comp, "_componentDefinition") : null;
                object rv = def != null ? PlanetRenderBridge.GetMember(def, "Radius") : null;
                double r = rv is double d ? d : rv is float fl ? fl : 7.0e5;   // Delfos: 700 km
                l.Add((e.Data.GetWorldTransform().Position, r));
                Event($"hazard: killing field r={r / 1000:F0} km at {ServerPlanetBeacon.Fmt(e.Data.GetWorldTransform().Position)}");
            }
        }
        catch (Exception ex) { Event("hazards: " + ex.Message); }
        return l;
    }

    /// <summary>A point pushed radially out of every killing field (towards the sector's planet when at the centre).</summary>
    private static Vector3D ClearOf(List<(Vector3D c, double r)> hazards, Vector3D p, string host)
    {
        foreach (var (c, r) in hazards)
        {
            double keep = r * HazardMargin;
            Vector3D d = p - c;
            if (d.Length() >= keep) continue;
            // Out along the chart plane (XZ); from the very centre, towards the sector's planet.
            Vector3D flat = new Vector3D(d.X, 0, d.Z);
            if (flat.Length() < 1000.0)
            {
                Vector3D to = SystemHost.BeaconOf.TryGetValue(host ?? "", out var b) ? b.Center - c : Vector3D.UnitX;
                flat = new Vector3D(to.X, 0, to.Z);
                if (flat.LengthSquared() < 1) flat = Vector3D.UnitX;
            }
            d = flat;
            p = c + Vector3D.Normalize(d) * keep;
        }
        return p;
    }

    /// <summary>A site frame at a world spot (its sector gives it an orbit). Caller holds FramesLock.</summary>
    internal static ProximityFrame MakeSite(SectorsSessionComponent sectors, Dictionary<string, SectorHomes.Home> homes,
                                            Vector3D world, string label, double t, Vector3D? chartPoint = null)
    {
        var reg = SystemHost.Registry;
        SectorComponent sc = null;
        Vector3D cp = chartPoint ?? world;   // where it is on the chart (an anchor moved clear of a hazard keeps its sector's point)
        try { sc = sectors?.TryGetSectorAt(cp); } catch { }
        if (sc == null && sectors != null)
        {
            double best = double.MaxValue;
            foreach (var s in sectors.Sectors) { double d = (s.Area.Center - cp).Length(); if (d < best) { best = d; sc = s; } }
        }
        if (sc == null || !homes.TryGetValue(sc.Name, out var home) || reg.Find(home.Host) == null) return null;

        var site = new Site { Sector = sc.Name, Host = home.Host, Home = home, World = world, Label = label };
        if (home.Kind == SectorHomes.Kind.Body && !home.Future && reg.Find(home.Host) is GravityBody sb && sb.IsRoot)
        {
            // The star's own space: a circular orbit inside its zone.
            site.OwnR = home.Outer * 0.6;
            site.OwnTheta = 0;
        }
        else if (home.Kind == SectorHomes.Kind.Body && !home.Future)
        {
            // Its own circular orbit about the planet: the charted distance scaled like every orbit,
            // kept clear of the planet's cell.
            var planet = reg.Find(home.Host);
            var def = reg.FindDefinition(home.Host);
            Vector3D pc = SystemHost.BeaconOf.TryGetValue(home.Host, out var b) ? b.Center : Vector3D.Zero;
            Vector3D d = cp - pc;
            double keep = def != null ? PlanetBerths.KeepRadius(def) : 1e5;
            site.OwnR = Math.Max(keep * 1.6, new Vector3D(d.X, d.Z, 0).Length() * SystemHost.SectorOrbitScale);
            site.OwnTheta = Math.Atan2(d.Z, d.X);
        }
        else
        {
            // The site's place within its sector, scaled like the orbits, held in the home's co-moving axes.
            Vector3D dw = (cp - sc.Area.Center) * SystemHost.SectorOrbitScale;
            Vector3D dm = new Vector3D(dw.X, dw.Z, dw.Y);   // map/world XZ is the ecliptic (model XY)
            Basis(site, t, out Vector3D R, out Vector3D T, out Vector3D N);
            site.Rtn = new Vector3D(Vector3D.Dot(dm, R), Vector3D.Dot(dm, T), Vector3D.Dot(dm, N));
        }
        if (!Ephemeris(site, t, out GravityBody parent, out StateVector rel)) return null;
        var el = CaptureMath.CaptureElements(rel, parent.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return null;
        var f = SystemHost.Frames.CreateLatentFrame(parent.Name, el, 0, world);
        f.IsEncounter = true;
        site.FrameId = f.Id;
        _sites[f.Id] = site;
        Event($"SITE #{f.Id} '{label}' in {sc.Name} ({home.Kind} of {home.Host}) at {ServerPlanetBeacon.Fmt(world)}: " +
              $"orbits {parent.Name} r={rel.Position.Length() / 1000:F0} km");
        return f;
    }

    /// <summary>The home point's root position (sun-centred model), before the site offset.</summary>
    private static Vector3D HomeRoot(Site s, double t, out Vector3D centre)
    {
        var reg = SystemHost.Registry;
        var root = reg.Root;
        var planet = reg.Find(s.Host);
        Vector3D po = planet != null ? planet.OriginInRoot(t).Position : Vector3D.Zero;
        centre = Vector3D.Zero;
        if (s.Home.Kind == SectorHomes.Kind.Body && !s.Home.Future && planet != null)
        {
            // A body's own space: the site on its own circular orbit about the body.
            centre = po;
            double n = Math.Sqrt(planet.Mu / (s.OwnR * s.OwnR * s.OwnR));
            double a = s.OwnTheta + n * t;
            return po + new Vector3D(Math.Cos(a), Math.Sin(a), 0) * s.OwnR;
        }
        return SectorHomes.Where(s.Home, reg, t, out centre);   // a ring's point, a Lagrange point, a body still to come
    }

    /// <summary>
    /// A Lagrange site's dynamics: its Lagrange orbit (SectorHomes.LagrangeOrbit: a closed ellipse round
    /// the point in the frame turning with the planet, at the pair's real libration rate), at every point;
    /// L1 / L2 / L3 hold you: no relative pull at all (the real ones are unstable; drifting off is no fun).
    /// False for any frame that is not a Lagrange site.
    /// </summary>
    /// <summary>A site's point at t (root frame), or null.</summary>
    public static Vector3D? SitePoint(long frameId, double t) => _sites.TryGetValue(frameId, out var s) ? SiteRoot(s, t) : (Vector3D?)null;

    public static bool LagrangeDynamics(long frameId, double t, out Func<Vector3D, Vector3D, Vector3D> relAccel)
    {
        relAccel = null;
        if (!_sites.TryGetValue(frameId, out var s) || s.Home == null || s.Home.Kind != SectorHomes.Kind.Lagrange) return false;
        var body = SystemHost.Registry?.Find(s.Home.Host);
        if (!SectorHomes.LagrangeOrbit(body, t, out var omega, out var w)) return false;
        relAccel = (d, v) => SectorHomes.LagrangeAccel(d, v, omega, w);
        return true;
    }

    /// <summary>The home's co-moving axes at t: radial (from its centre), along-track, normal.</summary>
    private static void Basis(Site s, double t, out Vector3D R, out Vector3D T, out Vector3D N)
    {
        const double h = 0.5;
        Vector3D p0 = HomeRoot(s, t - h, out Vector3D c0), p1 = HomeRoot(s, t + h, out Vector3D c1);
        Vector3D r = (p0 + p1) * 0.5 - (c0 + c1) * 0.5;
        Vector3D v = (p1 - c1 - (p0 - c0)) / (2 * h);
        R = r.LengthSquared() > 1e-6 ? Vector3D.Normalize(r) : Vector3D.UnitX;
        Vector3D nn = Vector3D.Cross(r, v);
        N = nn.LengthSquared() > 1e-12 ? Vector3D.Normalize(nn) : Vector3D.UnitZ;
        T = Vector3D.Cross(N, R);
    }

    private static Vector3D SiteRoot(Site s, double t)
    {
        Vector3D p = HomeRoot(s, t, out _);
        if (s.Rtn.LengthSquared() < 1e-6) return p;
        Basis(s, t, out Vector3D R, out Vector3D T, out Vector3D N);
        return p + R * s.Rtn.X + T * s.Rtn.Y + N * s.Rtn.Z;
    }

    /// <summary>The site's state at t, relative to the deepest SOI containing it.</summary>
    public static bool Ephemeris(Site s, double t, out GravityBody parent, out StateVector rel)
    {
        var reg = SystemHost.Registry;
        const double h = 0.5;
        Vector3D p = SiteRoot(s, t);
        Vector3D v = (SiteRoot(s, t + h) - SiteRoot(s, t - h)) / (2 * h);
        parent = reg.Root.DeepestSoiContaining(p, t) ?? reg.Root;
        StateVector o = parent.OriginInRoot(t);
        rel = new StateVector(p - o.Position, v - o.Velocity);
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>Sites ride their prescribed ephemeris: re-osculate the frame's elements every tick.</summary>
    private static void RefreshSites(double t)
    {
        foreach (var kv in _sites)
        {
            var f = SystemHost.Frames.Get(kv.Key);
            if (f == null) continue;
            if (!Ephemeris(kv.Value, t, out var parent, out var rel)) continue;
            var el = CaptureMath.CaptureElements(rel, parent.Mu, t);
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) continue;
            f.ParentBodyName = parent.Name;
            f.Elements = el;
            f.VirtualVelocity = rel.Velocity;
        }
    }

    /// <summary>Anything unframed in a site's bubble (outside the planet cells) belongs to the site.</summary>
    private static void AdoptIntoSites()
    {
        if (_sites.Count == 0) return;
        var reg = SystemHost.Registry;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D p = GridMembers.Position(g);
            var f = SiteContaining(p);
            if (f == null || VoxelBerthRegistry.TryCellContaining(p, reg, out _, out _)) continue;
            if (SystemHost.Frames.AddMember(f, g.Id))
                Event($"site #{f.Id}: grid {g.Id} '{g.DisplayName}' joins ({(p - f.BerthCenter).Length() / 1000:F1} km from the site)");
        }
    }

    /// <summary>The site frame whose bubble holds this world point, if any. Caller holds FramesLock.</summary>
    public static ProximityFrame SiteContaining(Vector3D world)
    {
        ProximityFrame best = null; double bd = SiteRadius;
        foreach (var kv in _sites)
        {
            var f = SystemHost.Frames.Get(kv.Key);
            if (f == null) continue;
            double d = (world - f.BerthCenter).Length();
            if (d < bd) { bd = d; best = f; }
        }
        return best;
    }

    /// <summary>Client: an unframed player in a site's bubble joins it (as a rider). Caller holds FramesLock.</summary>
    public static bool TryAdoptPlayer(long playerId, Vector3D pos)
    {
        if (!Enabled || _sites.Count == 0 || playerId == 0) return false;
        if (VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out _, out _)) return false;
        var f = SiteContaining(pos);
        if (f == null || !SystemHost.Frames.AddMember(f, playerId)) return false;
        Event($"player joins site #{f.Id} '{SiteOf(f.Id)?.Label}' ({(pos - f.BerthCenter).Length() / 1000:F1} km from the site)");
        return true;
    }

    // ───────────────────────────── procedural spawns ─────────────────────────────

    private static void ProcessNewGrids(double t)
    {
        while (_added.TryDequeue(out var a)) _waiting.Add(a);
        if (_waiting.Count == 0) return;
        double now = Wall();
        var due = _waiting.FindAll(w => now - w.at >= SpawnSettleSeconds);
        if (due.Count == 0) return;
        _waiting.RemoveAll(w => now - w.at >= SpawnSettleSeconds);
        var reg = SystemHost.Registry;
        var handled = new HashSet<long>();
        foreach (var (id, _) in due)
        {
            if (handled.Contains(id)) continue;
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer || SystemHost.Frames.FindByMember(id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            if (VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out _)) continue;   // planet cells are world space

            // The frame whose berth it spawned near.
            ProximityFrame F = null; double d = FarReach;
            foreach (var f in SystemHost.Frames.Frames)
            {
                double dd = (pos - f.BerthCenter).Length();
                if (dd < d) { d = dd; F = f; }
            }
            if (F == null) continue;

            if (!IsEncounterGrid(g))
            {
                // A new player grid in a frame's space (a split-off piece, a projection): it is that frame's.
                if (d <= SiteRadius && SystemHost.Frames.AddMember(F, id)) Event($"new grid {id} '{g.DisplayName}' joins frame #{F.Id}");
                continue;
            }
            // A static grid is built on something (an asteroid base on its rock, which is voxel, not a
            // grid): it cannot move without it, so it stays where it spawned, in this frame.
            bool anchored = false;
            foreach (var o in GridMembers.All())
                if (o.IsServer && SystemHost.Frames.FindByMember(o.Id) == null && (GridMembers.Position(o) - pos).Length() <= ClusterRadius
                    && IsEncounterGrid(o) && !GridMembers.IsDynamic(o)) { anchored = true; break; }
            if (d <= CloseRadius || anchored || (IsSite(F) && d <= SiteRadius))
            {
                // CLOSE: a similar orbit, i.e. the same frame.
                if (SystemHost.Frames.AddMember(F, id))
                    Event($"SPAWN close: encounter grid {id} '{g.DisplayName}' {d / 1000:F1} km from frame #{F.Id} -> joins it");
                continue;
            }

            // FAR: every unframed encounter grid spawned with it goes into one new frame on its own orbit.
            var cluster = new List<OrbitalGridComponent>();
            foreach (var o in GridMembers.All())
            {
                if (!o.IsServer || SystemHost.Frames.FindByMember(o.Id) != null) continue;
                if ((GridMembers.Position(o) - pos).Length() > ClusterRadius || !IsEncounterGrid(o)) continue;
                cluster.Add(o); handled.Add(o.Id);
            }
            FarFrame(F, cluster, t);
        }
    }

    private static void FarFrame(ProximityFrame F, List<OrbitalGridComponent> cluster, double t)
    {
        if (cluster.Count == 0) return;
        Vector3D c = Vector3D.Zero, vAvg = Vector3D.Zero;
        foreach (var g in cluster) { c += GridMembers.Position(g); vAvg += GridMembers.Velocity(g); }
        c /= cluster.Count; vAvg /= cluster.Count;
        StateVector cur = OrbitPropagation.StateAt(F.Elements, t);
        // Its place: the spawn's bearing from the berth, but genuinely far (25-60 km, beyond the merge
        // range): it comes back only if its relative orbit brings it back (a conjunction).
        Vector3D delta = c - F.BerthCenter;                        // berth offsets are inertial
        uint h0 = (uint)(cluster[0].Id * 40503L);
        double far = FarMinKm * 1000 + (FarMaxKm - FarMinKm) * 1000 * ((h0 & 0xFFFF) / 65535.0);
        delta = (delta.LengthSquared() > 1 ? Vector3D.Normalize(delta) : Vector3D.UnitX) * Math.Max(far, delta.Length());
        // A deterministic kick (2-15 m/s, mostly in the orbit plane): a relative orbit that is eccentric
        // against the spawning frame's, so the encounter drifts off and may come round again.
        uint hsh = (uint)(cluster[0].Id * 2654435761L);
        double mag = 2 + 13 * ((hsh & 0xFFFF) / 65535.0);
        double ang = ((hsh >> 16) & 0xFFFF) / 65535.0 * 2 * Math.PI;
        Vector3D R = Vector3D.Normalize(cur.Position), N = Vector3D.Cross(cur.Position, cur.Velocity);
        N = N.LengthSquared() > 1e-12 ? Vector3D.Normalize(N) : Vector3D.UnitZ;
        Vector3D T = Vector3D.Cross(N, R);
        Vector3D kick = (R * Math.Cos(ang) + T * Math.Sin(ang) + N * 0.15) * mag;
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + delta, cur.Velocity + vAvg + kick), F.Elements.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.CreateFrame(F.ParentBodyName, el, 0);
        if (nf == null) return;
        nf.IsEncounter = true;
        foreach (var g in cluster)
        {
            GridMembers.SetPosition(g, nf.BerthCenter + (GridMembers.Position(g) - c));
            GridMembers.SetVelocity(g, GridMembers.Velocity(g) - vAvg);
            SystemHost.Frames.AddMember(nf, g.Id);
        }
        Event($"SPAWN far: {cluster.Count} encounter grid(s) '{cluster[0].DisplayName}' {delta.Length() / 1000:F1} km from frame #{F.Id} " +
              $"-> own frame #{nf.Id} (kick {mag:F1} m/s, a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F4})");
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static readonly ConcurrentQueue<(long grid, string sector)> _devSites = new ConcurrentQueue<(long, string)>();
    /// <summary>DEV: move a grid to a sector's charted centre and make it an authored site there.</summary>
    public static void RequestDevSite(long gridId, string sector) => _devSites.Enqueue((gridId, sector));

    private static readonly ConcurrentQueue<long> _devFar = new ConcurrentQueue<long>();
    /// <summary>DEV: treat a grid as a far spawn from its frame (0 = the first dynamic encounter grid in a frame).</summary>
    public static void RequestDevFar(long gridId) => _devFar.Enqueue(gridId);

    private static void DevFar(long gridId, double t)
    {
        OrbitalGridComponent g = gridId != 0 ? GridMembers.Get(gridId) : null;
        if (g == null)
            foreach (var c in GridMembers.All())
                if (c.IsServer && GridMembers.IsDynamic(c) && IsEncounterGrid(c) && SystemHost.Frames.FindByMember(c.Id) != null && !GridMembers.IsConstrained(c)) { g = c; break; }
        var F = g != null ? SystemHost.Frames.FindByMember(g.Id) : null;
        if (g == null || F == null) { Event("devfar: no framed dynamic encounter grid"); return; }
        SystemHost.Frames.RemoveMember(g.Id);
        FarFrame(F, new List<OrbitalGridComponent> { g }, t);
    }

    private static void DevMakeSite(Keen.VRage.Core.Game.Systems.Session session, long gridId, string sectorName, double t)
    {
        var g = gridId != 0 ? GridMembers.Get(gridId) : null;
        if (gridId == 0)
            foreach (var c in GridMembers.All())
                if (c.IsServer && GridMembers.IsDynamic(c) && !IsEncounterGrid(c) && SystemHost.Frames.FindByMember(c.Id) == null && !GridMembers.IsConstrained(c)) { g = c; break; }
        if (g == null || !g.IsServer) { Event($"devsite: grid {gridId} not found"); return; }
        if (GridMembers.IsConstrained(g)) { Event($"devsite: grid {g.Id} is held by a constraint (moving it would break Havok)"); return; }
        gridId = g.Id;
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        if (sectors == null) { Event("devsite: no sectors"); return; }
        SectorComponent sc = null;
        foreach (var s in sectors.Sectors) if (s.Name.IndexOf(sectorName, StringComparison.OrdinalIgnoreCase) >= 0) { sc = s; break; }
        if (sc == null) { Event($"devsite: no sector '{sectorName}'"); return; }
        var old = SystemHost.Frames.FindByMember(gridId);
        if (old != null) SystemHost.Frames.RemoveMember(gridId);
        Vector3D world = sc.Area.Center;
        GridMembers.SetPosition(g, world);
        GridMembers.SetVelocity(g, Vector3D.Zero);
        var f = MakeSite(sectors, GameMap.HomesBySector(sectors, SystemHost.Registry), world, g.DisplayName, t);
        if (f == null) { Event("devsite: no home for that sector"); return; }
        SystemHost.Frames.AddMember(f, gridId);
        _built = true;
    }

    // ───────────────────────────── bookkeeping ─────────────────────────────

    /// <summary>Drop members that no longer exist; an encounter frame left empty dissolves.</summary>
    private static void Prune()
    {
        long player = FrameHost.PlayerId;
        foreach (var f in new List<ProximityFrame>(SystemHost.Frames.Frames))
        {
            // A character id that is not the live player's is a dead character (a respawn gets a new id).
            if (player != 0)
                foreach (long id in new List<long>(f.Members))
                    if (!GridMembers.IsGridId(id) && id != player) SystemHost.Frames.RemoveMember(id);
            if (!f.IsEncounter)
            {
                if (f.Members.Count == 0) { long pid = f.Id; SystemHost.Frames.Dissolve(pid); ServerFrames.AnchorAccel.Remove(pid); Event($"frame #{pid} empty -> dissolved"); }
                continue;
            }
            foreach (long id in new List<long>(f.Members))
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) == null) SystemHost.Frames.RemoveMember(id);
            if (f.Members.Count == 0 && !(SiteOf(f.Id)?.Anchor ?? false))
            {
                long fid = f.Id;
                SystemHost.Frames.Dissolve(fid);
                _sites.Remove(fid);
                ServerFrames.AnchorAccel.Remove(fid);
                Event($"encounter frame #{fid} empty -> dissolved");
            }
        }
    }

    /// <summary>Only frames that carry a player (or a player's grid) need to merge; NPC-only frames never meet.</summary>
    public static bool ShouldScreen(ProximityFrame a, ProximityFrame b)
    {
        if (!Enabled) return true;
        if (IsSite(a) && IsSite(b)) return false;
        return HasNonNpc(a) || HasNonNpc(b);
    }

    public static bool HasNonNpc(ProximityFrame f)
    {
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) return true;   // a character
            var g = GridMembers.Get(id);
            if (g != null && !IsNpc(g)) return true;
        }
        return false;
    }

    /// <summary>Merge host priority: a site never moves; then the frame with players; then an NPC-only frame.</summary>
    public static int HostPriority(ProximityFrame f) => IsSite(f) ? 3 : HasNonNpc(f) ? 2 : 1;

    /// <summary>Saving: site frames are rebuilt from the world on load (their members are re-adopted).</summary>
    public static bool IsTransient(long frameId) => _sites.ContainsKey(frameId);

    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"encounters: {Status}");
        try
        {
            int all = 0, npc = 0, enc = 0, cellN = 0;
            var reg = SystemHost.Registry;
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer) continue;
                all++;
                var k = Kind(g);
                bool inCell = reg != null && VoxelBerthRegistry.TryCellContaining(GridMembers.Position(g), reg, out _, out _);
                if (k.npc) npc++;
                if (k.enc) enc++;
                if ((k.npc || k.enc) && inCell) cellN++;
                if (k.npc || k.enc || !inCell)
                    Log.Default?.Info($"[ORBIT-DEV] grid {g.Id} '{g.DisplayName}' npc={k.npc} enc={k.enc} inCell={inCell} at {ServerPlanetBeacon.Fmt(GridMembers.Position(g))}");
            }
            sb.AppendLine($"  grids {all}: npc {npc}, encounter {enc}, of which in planet cells {cellN}");
        }
        catch (Exception e) { sb.AppendLine("  census failed: " + e.Message); }
        lock (ServerFrames.FramesLock)
        {
            if (SystemHost.Frames == null) return sb.ToString();
            double t = SystemHost.Now;
            int i = 0;
            foreach (var f in SystemHost.Frames.Frames)
            {
                if (!f.IsEncounter) continue;
                var s = SiteOf(f.Id);
                var st = OrbitPropagation.StateAt(f.Elements, t);
                int npc = 0, other = 0;
                foreach (long id in f.Members) { if (IsNpcId(id)) npc++; else other++; }
                sb.AppendLine($"  [{i++}] frame #{f.Id} {(s != null ? $"SITE '{s.Label}' {s.Sector} ({s.Home.Kind} of {s.Host})" : "procedural")} " +
                              $"about {f.ParentBodyName} r={st.Position.Length() / 1000:F0} km a={f.Elements.SemiMajorAxis / 1000:F0} km e={f.Elements.Eccentricity:F3} " +
                              $"members npc={npc} other={other} berth={ServerPlanetBeacon.Fmt(f.BerthCenter)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The i-th encounter frame (harness).</summary>
    public static ProximityFrame Nth(int i)
    {
        int k = 0;
        foreach (var f in SystemHost.Frames.Frames) if (f.IsEncounter && k++ == i) return f;
        return null;
    }

    private static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static void Event(string s)
    {
        FrameHost.LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-ENC] " + s);
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
