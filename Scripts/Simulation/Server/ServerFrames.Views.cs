using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): what the client may know of the server's grids - the published views, the sensor views,
// the grid positions and the harness snapshot (built on the server thread, read anywhere).
public static partial class ServerFrames
{
    /// <summary>Snapshot for the harness status (built on the server thread). Its text is made only while someone reads it
    /// (asked within SnapshotAskedSeconds), else every SnapshotIdleSeconds: the harness's own text, every half second for
    /// nobody, was 0.14 MB/s (a profile, 2026-10-15). The grid positions it also refreshes are refreshed as ever.</summary>
    public static string GridSnapshot { get { System.Threading.Volatile.Write(ref _snapAsked, Wall()); return _gridSnapshot; } }
    static volatile string _gridSnapshot = "";
    static double _snapAsked = double.NegativeInfinity, _snapBuilt = double.NegativeInfinity;
    public static double SnapshotAskedSeconds = 10, SnapshotIdleSeconds = 5;

    /// <summary>Grid id -> last known world position (server snapshot, for the harness).</summary>
    public static readonly Dictionary<long, Vector3D> GridPositions = new Dictionary<long, Vector3D>(); // lock GridPositions

    /// <summary>What the client may know of a server grid (ServerFrames.View): read from this snapshot, never from the server's
    /// entities (the client read them directly - a static check, a box, a name, a mass - and one path asked Aero to build a
    /// table on the server's entity from the client).</summary>
    public sealed class GridView
    {
        public long Id; public Vector3D Position; public BoundingBoxD Box; public bool HasBox, Dynamic; public string Name; public double Mass;
        /// <summary>Its owner's identity (the server's ownership), and whether it is an encounter's (NPC or procedural).</summary>
        public Keen.Game2.Simulation.GameSystems.Ownership.IdentityId Owner; public bool HasOwner, Encounter;
        /// <summary>Holding station in its frame (dampeners, thrust to cancel the pull): no relative force on it (StationKeeping).</summary>
        public bool Holding;
        /// <summary>Joined to its frame's anchor (a sub-grid of the anchor ship, docked to it): it moves as one with the anchor -
        /// seated in it, you are in the anchor (the frame's orbit), not a rider drifting about it.</summary>
        public bool AnchorJoined;
        /// <summary>In warp, the TRUE velocity relative to its frame the steering keeps (its physical one is N x the average
        /// over the step, a chord: the seated plot built on it drifted off the motion).</summary>
        public Vector3D TrueRel; public bool HasTrueRel;
        /// <summary>The thrust its firing thrusters can give (N; the game's IsThrusting thrusters, their full thrust): its
        /// engines' heat for the telescope's infrared (Contacts / Signature.PlumeReach). 0: nothing firing.</summary>
        public double Firing;
        /// <summary>Its physical velocity (world, m/s; to 0.05 m/s - a view is kept while it changes less), and whether it
        /// rides its conic (HighSpeed: no physical velocity - the conic is its motion). ShipSurface reads them: landed or
        /// orbiting (2026-10-18, B1: a landed ship's orbit was guessed from two positions half a second apart).</summary>
        public Vector3D Velocity; public bool HighSpeed;
    }

    /// <summary>A sensor block as the client may know it: on which grid, where, what, working, how strong.</summary>
    public struct SensorView { public long Grid; public Vector3D Position; public bool Radar, Working; public double Power; }
    private static List<SensorView> _sensorViews = new List<SensorView>();
    public static List<SensorView> SensorViews => System.Threading.Volatile.Read(ref _sensorViews);
    /// <summary>The grid the player's seat is on, as the server has it (0: not seated) - BuildViews.</summary>
    public static long PlayerSeatGrid;
    /// <summary>The snapshot's grids without a copy (published whole each snapshot, never changed after).</summary>
    public static GridViews ViewValues => new GridViews(System.Threading.Volatile.Read(ref _views).Views);
    /// <summary>A snapshot's views to enumerate (foreach: no allocation - a struct enumerator, as the dictionary's values had).
    /// (Not the array itself: a var of a mod type array is banned in game, VRS1001 - and it could be written to.)</summary>
    public readonly struct GridViews
    {
        readonly GridView[] _a;
        public GridViews(GridView[] a) { _a = a; }
        public int Count => _a.Length;
        public GridView this[int i] => _a[i];
        public Enumerator GetEnumerator() => new Enumerator(_a);
        public struct Enumerator
        {
            readonly GridView[] _a; int _i;
            public Enumerator(GridView[] a) { _a = a; _i = -1; }
            public bool MoveNext() => ++_i < _a.Length;
            public GridView Current => _a[_i];
        }
    }
    /// <summary>One published snapshot: its views (in GridMembers.All's order, as the dictionary's values were) and id -> index.</summary>
    sealed class ViewSnap { public GridView[] Views; public Dictionary<long, int> Index; }
    static readonly ViewSnap EmptyViews = new ViewSnap { Views = new GridView[0], Index = new Dictionary<long, int>() };
    private static ViewSnap _views = EmptyViews;

    /// <summary>A server grid as of the last snapshot (every 5 server ticks).</summary>
    public static bool View(long id, out GridView v)
    {
        var s = System.Threading.Volatile.Read(ref _views);
        if (s.Index.TryGetValue(id, out int i)) { v = s.Views[i]; return true; }
        v = null; return false;
    }

    // THE VIEWS: published whole and never changed after (readers enumerate them across FramesLock waits - Contacts, Targets:
    // recycled dictionaries could be cleared under them, a review, 2026-10-14). The scratch sets are kept (a profile: four
    // new ones per build).
    // (2026-10-15, a profile: still 0.68 MB/s - a new dictionary and a new view per grid, 12 times a second. Now a snapshot is
    //  an array and an id -> index map; the map is kept while the grids are the same ones in the same order, and a grid whose
    //  view would read exactly as the last one keeps that object - a view is never changed once published, so sharing it is
    //  safe. Nothing changed at all: the last snapshot stays.)
    static readonly Dictionary<Entity, long> _bvByEntity = new Dictionary<Entity, long>();
    static readonly HashSet<Keen.VRage.DCS.Components.Entity> _bvJoined = new HashSet<Keen.VRage.DCS.Components.Entity>();
    static readonly HashSet<long> _bvHolding = new HashSet<long>();
    static readonly Dictionary<long, Vector3D> _bvTrueRel = new Dictionary<long, Vector3D>();
    static readonly List<GridView> _bvViews = new List<GridView>();
    static readonly List<SensorView> _bvSens = new List<SensorView>();
    static readonly List<TelescopeComponent> _bvScopes = new List<TelescopeComponent>();
    static readonly List<RadarComponent> _bvRadars = new List<RadarComponent>();
    /// <summary>The server seat the player sat in at the last snapshot: asked first (every seat of the world was scanned for
    /// it, 12 times a second).</summary>
    static Entity _bvSeat;

    static bool SameView(GridView o, long id, Vector3D pos, bool hasBox, BoundingBoxD box, bool dyn, string name, double mass,
        Keen.Game2.Simulation.GameSystems.Ownership.IdentityId owner, bool hasOwner, bool enc, bool holding, bool joined, bool hasTr, Vector3D tr, double firing)
        => o.Id == id && o.Firing == firing && o.Position.X == pos.X && o.Position.Y == pos.Y && o.Position.Z == pos.Z && o.HasBox == hasBox
           && (!hasBox || (o.Box.Min.X == box.Min.X && o.Box.Min.Y == box.Min.Y && o.Box.Min.Z == box.Min.Z
                           && o.Box.Max.X == box.Max.X && o.Box.Max.Y == box.Max.Y && o.Box.Max.Z == box.Max.Z))
           && o.Dynamic == dyn && o.Name == name && o.Mass == mass && o.HasOwner == hasOwner && o.Owner.Equals(owner) && o.Encounter == enc
           && o.Holding == holding && o.AnchorJoined == joined && o.HasTrueRel == hasTr
           && (!hasTr || (o.TrueRel.X == tr.X && o.TrueRel.Y == tr.Y && o.TrueRel.Z == tr.Z));

    // ENGINES FIRING, by grid (2026-10-19, fx-detect: a burning ship was as cold as a drifting one to the telescope).
    // Summed once a second (FiringSeconds) from the server scene's thrusters (StationKeepCharge.All: every server thruster
    // with a powerable block) that carry the game's IsThrusting tag: their full thrust (ThrustData.MaxThrustPower). Server
    // thread only; the dictionary is kept (no garbage per build).
    public static double FiringSeconds = 1.0;
    static readonly Dictionary<Entity, double> _bvFiring = new Dictionary<Entity, double>();
    static readonly List<StationKeepCharge> _bvThrusters = new List<StationKeepCharge>();
    static double _bvFiringAt = double.NegativeInfinity;
    static void CollectFiring(Keen.VRage.DCS.Scenes.Scene scene)
    {
        double now = Wall();
        if (now - _bvFiringAt < FiringSeconds && now >= _bvFiringAt) return;
        _bvFiringAt = now;
        _bvFiring.Clear();
        if (scene == null) return;
        var all = _bvThrusters; all.Clear();
        lock (StationKeepCharge.All) all.AddRange(StationKeepCharge.All);
        foreach (var c in all)
        {
            var e = c.Entity;
            // (the server scene's only - the client copies are read on their own thread: StationKeepCharge.RebuildByGrid)
            if (e == null || !ReferenceEquals(e.Data.Scene, scene) || !EntityAlive(e)) continue;
            if (!e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.IsThrusting>()) continue;
            if (!e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ThrustData>(out var td) || !(td.MaxThrustPower > 0)) continue;
            var top = e.GetTopLevelParent();
            if (top == null) continue;
            _bvFiring.TryGetValue(top, out double f);
            _bvFiring[top] = f + td.MaxThrustPower;
        }
        all.Clear();
    }

    static bool EntityAlive(Entity e)
    {
        if (e == null) return false;
        var d = e.Data; var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }

    private static void BuildViews()
    {
        var prev = System.Threading.Volatile.Read(ref _views);
        var views = _bvViews; views.Clear();
        var byEntity = _bvByEntity; byEntity.Clear();
        // what is joined to each frame's anchor (the joint search the member loop keeps, once a second per anchor)
        var joinedAll = _bvJoined; joinedAll.Clear();
        var holding = _bvHolding; holding.Clear();
        var trueRel = _bvTrueRel; trueRel.Clear();
        lock (FramesLock)   // (the registry and these sets change on the client thread too)
        {
            foreach (var kv in _joinedOf)
            {
                if (kv.Value.set == null || Wall() - kv.Value.at > 3.0) continue;   // (a stale set: the anchor lost its joints since)
                var af = SystemHost.Frames?.FindByMember(kv.Key);
                if (af == null || af.AnchorEntityId != kv.Key) continue;
                foreach (var je in kv.Value.set) joinedAll.Add(je);
            }
            foreach (long hid in Holding.Keys) holding.Add(hid);
            foreach (var kv in _trueRel) trueRel[kv.Key] = kv.Value;
        }
        bool changed = false;
        Keen.VRage.DCS.Scenes.Scene serverScene = null;
        foreach (var g in GridMembers.All()) if (g.IsServer && EntityAlive(g.Entity)) { serverScene = g.Entity.Data.Scene; break; }
        try { CollectFiring(serverScene); } catch { _bvFiring.Clear(); }
        foreach (var g in GridMembers.All())
        {
            // (a grid deleted before its Unregister ran is left out: its Position threw out of the whole build, ~200 ms - 2026-10-15)
            if (!g.IsServer || !EntityAlive(g.Entity)) continue;
            Vector3D pos = GridMembers.Position(g); bool dyn = GridMembers.IsDynamic(g); string name = g.DisplayName; double mass = GridMembers.Mass(g);
            bool hold = holding.Contains(g.Id), joined = joinedAll.Contains(g.Entity);
            bool hasTr = trueRel.TryGetValue(g.Id, out var tr);
            // (TryGet: no exception - ~200 ms each in SE2 - for a grid whose box is not set yet, every 5 ticks)
            var tb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data);
            Keen.Game2.Simulation.GameSystems.Ownership.IdentityId owner = default; bool hasOwner = false, enc = false;
            try
            {
                var own = g.Session?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent>();
                if (own != null) { owner = own.TryGetOwnerOf(g.Entity); hasOwner = true; }
                enc = EncounterFrames.IsEncounterGrid(g);
            }
            catch { }
            BoundingBoxD box = tb.HasValue ? tb.Value : default;
            _bvFiring.TryGetValue(g.Entity, out double firing);
            // (the last snapshot's view of this grid: at the same place in the array when nothing came or went)
            int vi = views.Count;
            GridView v = vi < prev.Views.Length && prev.Views[vi].Id == g.Id ? prev.Views[vi]
                       : prev.Index.TryGetValue(g.Id, out int pi) ? prev.Views[pi] : null;
            Vector3D vel = GridMembers.Velocity(g); bool hs = _gridHighSpeed.ContainsKey(g.Id);
            if (v == null || !SameView(v, g.Id, pos, tb.HasValue, box, dyn, name, mass, owner, hasOwner, enc, hold, joined, hasTr, tr, firing)
                || v.HighSpeed != hs || (v.Velocity - vel).LengthSquared() > 0.05 * 0.05)
            {
                v = new GridView { Id = g.Id, Position = pos, Dynamic = dyn, Name = name, Mass = mass, Holding = hold, AnchorJoined = joined, Firing = firing };
                if (hasTr) { v.TrueRel = tr; v.HasTrueRel = true; }
                if (tb.HasValue) { v.Box = tb.Value; v.HasBox = true; }
                v.Owner = owner; v.HasOwner = hasOwner; v.Encounter = enc;
                v.Velocity = vel; v.HighSpeed = hs;
            }
            if (vi >= prev.Views.Length || !ReferenceEquals(prev.Views[vi], v)) changed = true;
            views.Add(v);
            byEntity[g.Entity] = g.Id;
        }
        if (views.Count != prev.Views.Length) changed = true;
        if (changed)
        {
            // (the map kept while the ids are the same, in the same order: the arrays index alike)
            bool sameIds = views.Count == prev.Views.Length;
            for (int i = 0; sameIds && i < views.Count; i++) sameIds = views[i].Id == prev.Views[i].Id;
            Dictionary<long, int> index = prev.Index;
            if (!sameIds) { index = new Dictionary<long, int>(views.Count); for (int i = 0; i < views.Count; i++) index[views[i].Id] = i; }
            System.Threading.Volatile.Write(ref _views, new ViewSnap { Views = views.ToArray(), Index = index });
        }
        views.Clear();
        // the player's seat grid, by the server's own seat (its pilot the server character): the client pairs by this id
        long seatGrid = 0;
        try
        {
            var ss = ServerPlanetBeacon.ServerSession; var sch = ss != null ? PlayerCache.Of(ss) : null;
            if (sch != null && sch.Data.Has<Keen.VRage.Core.Game.Components.EntityParentData>())
            {
                // (the last seat first - still alive, its pilot still the player: the scan over every seat found that one)
                Entity found = null;
                var last = _bvSeat;
                if (EntityAlive(last) && (last.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == sch) found = last;
                else
                    foreach (var se in ss.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>())
                    {
                        if (!((se.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == sch)) continue;
                        found = se;
                        break;
                    }
                _bvSeat = found;
                if (found != null)
                {
                    var ge = found.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;
                    if (ge != null && byEntity.TryGetValue(ge, out long gid)) seatGrid = gid;
                }
            }
            else _bvSeat = null;
        }
        catch { _bvSeat = null; }
        System.Threading.Interlocked.Exchange(ref PlayerSeatGrid, seatGrid);
        // the sensor blocks (their grid by its top entity) - published anew only when one changed (a new list per build before)
        var sens = _bvSens; sens.Clear();
        bool sensOk = true;
        try
        {
            var scopes = _bvScopes; scopes.Clear(); lock (SensorBlocks.Telescopes) scopes.AddRange(SensorBlocks.Telescopes);
            var radars = _bvRadars; radars.Clear(); lock (SensorBlocks.Radars) radars.AddRange(SensorBlocks.Radars);
            // (a sensor block whose entity is gone is skipped before it is read: it threw and ended the pass, ~200 ms, every
            //  build until its list let it go - 2026-10-15)
            foreach (var c in scopes)
            {
                if (!EntityAlive(c.Entity)) continue;
                var top = c.Entity.GetTopLevelParent();
                if (top != null && byEntity.TryGetValue(top, out long gid))
                    sens.Add(new SensorView { Grid = gid, Position = c.Entity.Data.GetWorldTransform().Position, Radar = false, Working = c.Working });
            }
            foreach (var c in radars)
            {
                if (!EntityAlive(c.Entity)) continue;
                var top = c.Entity.GetTopLevelParent();
                if (top != null && byEntity.TryGetValue(top, out long gid))
                    sens.Add(new SensorView { Grid = gid, Position = c.Entity.Data.GetWorldTransform().Position, Radar = true, Working = c.Working, Power = c.Power });
            }
        }
        catch { sensOk = false; }
        _bvScopes.Clear(); _bvRadars.Clear();
        var was = System.Threading.Volatile.Read(ref _sensorViews);
        bool sameSens = sensOk && was.Count == sens.Count;
        for (int i = 0; sameSens && i < sens.Count; i++)
        {
            SensorView x = was[i], y = sens[i];
            sameSens = x.Grid == y.Grid && x.Position.X == y.Position.X && x.Position.Y == y.Position.Y && x.Position.Z == y.Position.Z
                       && x.Radar == y.Radar && x.Working == y.Working && x.Power == y.Power;
        }
        // (a read that failed part way publishes what it had, as before)
        if (!sameSens) System.Threading.Volatile.Write(ref _sensorViews, new List<SensorView>(sens));
        sens.Clear();
    }

    /// <summary>The client's StaticAnchorOf: from the snapshot (StaticAnchorOf itself reads the server's entities).</summary>
    public static bool StaticAnchorView(ProximityFrame f, out Vector3D pos)
    {
        pos = f.BerthCenter;
        if (f.AnchorEntityId == AsteroidAnchorId) return true;
        if (!GridMembers.IsGridId(f.AnchorEntityId) || !View(f.AnchorEntityId, out var v) || v.Dynamic) return false;
        pos = v.Position;
        return true;
    }

    /// <summary>The server grids' positions, every tick (the client reads them: never the server entities).</summary>
    private static void RefreshGridPositions()
    {
        try
        {
            var grids = GridMembers.All();
            lock (GridPositions) foreach (var gg in grids) if (gg.IsServer && EntityAlive(gg.Entity)) GridPositions[gg.Id] = GridMembers.Position(gg);   // (a dead one threw: see BuildViews)
        }
        catch { }
    }

    static readonly List<OrbitalGridComponent> _snapGrids = new List<OrbitalGridComponent>();
    [ThreadStatic] static Vector3D _snapP;
    static System.Text.StringBuilder _snapSb;   // (taken whole by the builder - Interlocked - never shared while in use)
    static readonly Comparison<OrbitalGridComponent> _snapByDistance =
        (x, y) => (GridMembers.Position(x) - _snapP).LengthSquared().CompareTo((GridMembers.Position(y) - _snapP).LengthSquared());
    private static void BuildSnapshot()
    {
        // (the positions every time, the text only when wanted - GridSnapshot)
        try { var every = GridMembers.All(); lock (GridPositions) foreach (var gg in every) if (gg.IsServer && EntityAlive(gg.Entity)) GridPositions[gg.Id] = GridMembers.Position(gg); }
        catch { }
        double wallNow = Wall();
        if (!(wallNow - System.Threading.Volatile.Read(ref _snapAsked) < SnapshotAskedSeconds) && wallNow - _snapBuilt < SnapshotIdleSeconds) return;
        _snapBuilt = wallNow;
        try
        {
            var grids = _snapGrids; grids.Clear(); grids.AddRange(GridMembers.All());
            grids.RemoveAll(g => !g.IsServer);
            Vector3D p = PlayerPosition;
            // (the comparison made once, its point per thread; the builder kept per thread and cleared: the text is the same,
            //  without a closure, a delegate and the builder's chunks per build - 124 KB/s in a profile, 2026-10-15)
            _snapP = p;
            grids.Sort(_snapByDistance);
            var sb = System.Threading.Interlocked.Exchange(ref _snapSb, null) ?? new System.Text.StringBuilder();
            sb.Clear();
            sb.AppendLine($"grids {grids.Count} server-side (nearest to player first)");
            for (int i = 0; i < Math.Min(8, grids.Count); i++)
            {
                var g = grids[i];
                ProximityFrame fr;
                lock (FramesLock) fr = SystemHost.Frames?.FindByMember(g.Id);
                sb.AppendLine($"  grid {g.Id} '{g.DisplayName}' d={(GridMembers.Position(g) - p).Length() / 1000:F2}km " +
                              $"r={CellR(GridMembers.Position(g))} hs={_gridHighSpeed.ContainsKey(g.Id)} " +
                              $"dyn={GridMembers.IsDynamic(g)} m={GridMembers.Mass(g):F0}kg v={GridMembers.Velocity(g).Length():F1} " +
                              $"frame={(fr != null ? fr.Id + (fr.AnchorEntityId == g.Id ? "(anchor)" : "") : "-")}");
            }
            lock (FramesLock)
            {
                if (SystemHost.Frames != null)
                    foreach (var f in SystemHost.Frames.Frames)
                    {
                        sb.Append($"  frame #{f.Id} anchor={f.AnchorEntityId} members:");
                        foreach (long id in f.Members)
                        {
                            var g = GridMembers.IsGridId(id) ? GridMembers.Get(id) : null;
                            if (g == null) { sb.Append($" {id}(" + (GridMembers.IsGridId(id) ? "gone" : "char") + ")"); continue; }
                            sb.Append($" {id}(srv={g.IsServer} dyn={GridMembers.IsDynamic(g)} dB={(GridMembers.Position(g) - f.BerthCenter).Length() / 1000:F2}km)");
                        }
                        sb.AppendLine();
                    }
            }
            sb.AppendLine($"  serverPlayer={ServerPlanetBeacon.Fmt(PlayerPosition)} gridHS={_gridHighSpeed.Count}");
            _gridSnapshot = sb.ToString();
            sb.Clear(); System.Threading.Volatile.Write(ref _snapSb, sb);   // (handed back: an overlapping tick took none, or made its own)
            grids.Clear();
        }
        catch (Exception e) { _gridSnapshot = "grids ? " + e.Message; _snapGrids.Clear(); }
    }
}
