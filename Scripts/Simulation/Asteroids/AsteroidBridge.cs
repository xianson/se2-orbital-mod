using System.Reflection;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.Volume;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The game never spawns asteroids here: encounters place theirs, and our own system places the
/// rest. The procedural generator's asteroid path (its entity sectors) spawns from volume
/// definitions: the default space (InfiniteArea), its random ellipsoid fields, manual volumes (the
/// rings round planets) and the colonization sectors' volumes. Each of those definitions gets a
/// negative density while the session runs, so no sample ever passes; encounters come from a
/// separate path (encounter sectors) and are untouched. Definitions are saved by name only, so
/// nothing of this reaches the world file; the densities are put back on unload.
/// Asteroids the generator has already spawned are deleted through the generator itself (not the
/// player's delete, which would exclude them in the save) - only those AsteroidRules proves untouched and
/// procedural with nobody at them; player-edited ones (marked manual) stay. Switch: DeleteGenerated.
/// The one exception is our own: an asteroid frame's volume (AsteroidFrames) and the rocks in it, whose
/// composition is a reserved definition the game does not use (never zeroed here).
/// </summary>
public static class AsteroidBridge
{
    public static bool Block = true;
    /// <summary>World.DeleteGeneratedAsteroids: the generator's untouched procedural rocks are deleted (AsteroidRules). Off: none
    /// is (every body kept as an anchor, as before 2026-10-15), the encounter clean-up included.</summary>
    public static bool DeleteGenerated = true;
    public static string Status = "-";

    /// <summary>The game's rings (manual volumes) as found: type, transform, bounds (for the map and the sectors).</summary>
    public static readonly List<string> RingInfo = new List<string>();
    /// <summary>The game's planetary rings (tori): centre (world), inner / outer radius and half-thickness (m).</summary>
    public static readonly List<(Vector3D C, double In, double Out, double Half)> Rings = new List<(Vector3D, double, double, double)>();

    private static System.Reflection.MethodInfo _voxelQuery;

    /// <summary>Generator-spawned bodies (an encounter's rock) to delete near a point, on the next tick.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Vector3D at, double r)> _deleteNear = new System.Collections.Concurrent.ConcurrentQueue<(Vector3D, double)>();
    public static void RequestDeleteNear(Vector3D at, double r) => _deleteNear.Enqueue((at, r));

    /// <summary>The asteroids placed on purpose (encounters, players): world positions, refreshed every couple of seconds.</summary>
    public static readonly List<Vector3D> Asteroids = new List<Vector3D>();
    /// <summary>Their world bounding boxes (same order as found; for 'am I at it').</summary>
    public static readonly List<BoundingBoxD> AsteroidBoxes = new List<BoundingBoxD>();
    /// <summary>Per Asteroids entry: it persists (manual, the mod's own, player-edited) - not an encounter's or a prefab's.</summary>
    public static readonly List<bool> AsteroidPersistent = new List<bool>();

    /// <summary>Distance from a world point to the nearest asteroid's bounding box (0 inside), or +inf.</summary>
    public static double DistanceToAsteroid(Vector3D p)
    {
        double best = double.PositiveInfinity;
        lock (Asteroids) foreach (var b in AsteroidBoxes) best = Math.Min(best, BoxDistance(b, p));
        return best;
    }

    public static double BoxDistance(BoundingBoxD b, Vector3D p)
    {
        double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
        double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
        double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The nearest placed asteroid within reach of a world point.</summary>
    public static bool NearestAsteroid(Vector3D at, double reach, out Vector3D pos)
    {
        pos = default; double best = reach;
        lock (Asteroids) foreach (var a in Asteroids) { double d = (a - at).Length(); if (d <= best) { best = d; pos = a; } }
        return best < reach;
    }

    /// <summary>The nearest rock within reach, and whether it persists - placed on purpose, the mod's own, player-edited -
    /// rather than an encounter's or a prefab's, which come and go with whoever is near.</summary>
    public static bool NearestAsteroid(Vector3D at, double reach, out Vector3D pos, out bool persistent)
    {
        pos = default; persistent = false; double best = reach;
        lock (Asteroids)
            for (int i = 0; i < Asteroids.Count; i++)
            {
                double d = (Asteroids[i] - at).Length();
                if (d <= best) { best = d; pos = Asteroids[i]; persistent = i < AsteroidPersistent.Count && AsteroidPersistent[i]; }
            }
        return best < reach;
    }

    /// <summary>The ring round a world point (a planet's centre), if the game has one there.</summary>
    public static bool RingAround(Vector3D centre, out double inner, out double outer)
    {
        inner = outer = 0;
        lock (Rings)
            foreach (var r in Rings)
                if ((r.C - centre).Length() < Math.Max(5000.0, 0.05 * r.Out)) { inner = r.In; outer = r.Out; return inner > 0 && outer > inner; }
        return false;
    }

    private static readonly List<(VolumeDefinition v, float density)> _orig = new List<(VolumeDefinition, float)>();
    private static readonly HashSet<VolumeDefinition> _seen = new HashSet<VolumeDefinition>();
    private static FieldInfo _density;
    private static double _next;
    // (Tick's own lists, kept: six new lists and a closure per rock every pass before - 2026-10-15. Server thread only: Tick
    //  runs in the server's frame step)
    static readonly List<string> _tInfo = new List<string>();
    static readonly List<(Vector3D, double, double, double)> _tRings = new List<(Vector3D, double, double, double)>();
    static readonly List<(SpaceEntityId Id, RockFacts F, BoundingBoxD Box, Vector3D Pos)> _tBodies = new List<(SpaceEntityId, RockFacts, BoundingBoxD, Vector3D)>();
    static readonly List<(SpaceEntityId Id, BoundingBoxD Box, bool Encounter)> _tDelete = new List<(SpaceEntityId, BoundingBoxD, bool)>();
    static readonly HashSet<SpaceEntityId> _tNear = new HashSet<SpaceEntityId>();
    static readonly List<BoundingBoxD> _tGrids = new List<BoundingBoxD>();
    static readonly List<Vector3D> _tChars = new List<Vector3D>();
    static readonly List<Entity> _tCharBuf = new List<Entity>();
    static readonly int[] _tCount = new int[(int)RockVerdict.PlayerNear + 1];
    /// <summary>Bodies whose 'kept' reason was logged (once each); the log's rate (deletions and keeps).</summary>
    static readonly HashSet<SpaceEntityId> _heldLogged = new HashSet<SpaceEntityId>();
    static readonly LogLimiter _logRate = new LogLimiter(8, 30);
    private static string _lastPass = "";
    static readonly List<Vector3D> _tKept = new List<Vector3D>(), _tDoomed = new List<Vector3D>();
    static readonly List<BoundingBoxD> _tBoxes = new List<BoundingBoxD>();
    /// <summary>A point within 1 m of one in the list (the closures' Exists, without a closure per rock).</summary>
    static bool Within1m(List<Vector3D> l, Vector3D p) { foreach (var k in l) if ((k - p).Length() < 1.0) return true; return false; }
    private static int _deleted;
    private static bool _loggedRings;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!Block || session == null) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _next) return;
        _next = now + 2.0;
        try
        {
            var gen = Generator(session);
            if (gen == null) { Status = "no generator"; return; }
            AsteroidFrames.Scan(session);   // our asteroid frames' own volumes (and their rocks) are left alone

            var def = gen.GenerationData?.Definition;
            if (def != null)
            {
                Kill(def.InfiniteArea);
                foreach (var f in def.EllipsoidFields) Kill(f.Definition);
            }
            // Manual volumes (the rings round planets): their compositions, and what they are.
            using (var buf = new Buffer<IProceduralVolume>(Allocator.Pool, "OrbitalRings"))
            {
                var all = new BoundingBoxD(new Vector3D(-1e13, -1e13, -1e13), new Vector3D(1e13, 1e13, 1e13));
                gen.QueryManualVolumes(in all, buf);
                var en = ((BufferReference<IProceduralVolume>)buf).GetEnumerator();
                var info = _tInfo; info.Clear();
                var rings = _tRings; rings.Clear();
                while (en.MoveNext())
                {
                    var vol = en.Current;
                    if (AsteroidFrames.IsOurVolume(vol)) continue;   // an asteroid frame's rocks (AsteroidFrames)
                    AsteroidFrames.GameUses(vol.Composition);        // (a reserved composition the game uses: the game's again)
                    Kill(vol.Composition);
                    var box = vol.GetOrientedBoundingBox().GetAABB();
                    var wt = vol.Transform;
                    string radii = "";
                    if (vol is ProceduralRing pr)
                    {
                        var tv = pr.Torus;
                        rings.Add((tv.WorldTransform.Position, tv.InnerRadius, tv.OuterRadius, tv.MinorRadii.Y));
                        radii = $" inner {tv.InnerRadius / 1000:F1} km outer {tv.OuterRadius / 1000:F1} km half-height {tv.MinorRadii.Y / 1000:F2} km";
                    }
                    info.Add($"{vol.GetType().Name} at {Km(wt.Position)} up {wt.Orientation.GetUp()} box {Km(box.Min)}..{Km(box.Max)}{radii}");
                }
                en.Dispose();
                lock (RingInfo) { RingInfo.Clear(); RingInfo.AddRange(info); }
                lock (Rings) { Rings.Clear(); Rings.AddRange(rings); }
                if (!_loggedRings && info.Count > 0 && OrbitalLog.Asteroids)
                {
                    _loggedRings = true;
                    foreach (var s in info) Log.Default?.Info("[ORBIT-ROIDS] manual volume: " + s);
                }
                info.Clear(); rings.Clear();
            }
            var col = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.ProceduralGeneration.ColonizationSectorProceduralGenerationDataSessionComponent>();
            if (col?.Definition != null) foreach (var kv in col.Definition.SectorVolumes) Kill(kv.Value);

            // Encounters moved off a planet's border leave their rock behind (voxels cannot move): gone too, by the same rule as
            // every other generator body below (2026-10-15: this deleted every body in reach, manual or not, a grid on it or not;
            // the engine only forgot a manual one, but nothing checked who was there). Here only marked, for the log.
            var near = _tNear; near.Clear();
            while (_deleteNear.TryDequeue(out var dn))
                foreach (var kv in gen.Entities)
                {
                    if (!Num.Alive(kv.Value)) continue;   // (a dead body's transform read threw, ~200 ms an exception - 2026-10-15)
                    Vector3D ep = kv.Value.Data.GetWorldTransform().Position;
                    if ((ep - dn.at).Length() <= dn.r) near.Add(kv.Key);
                }
            // What the generator already spawned: each body's facts, then AsteroidRules' verdict. Kept ones are anchors as before;
            // deleted ones never are (they were, for a pass).
            bool server = gen.GetType().Name.Contains("Server");
            bool framesKnown = AsteroidFrames.ScanOk;   // (Scan ran at the top of this pass)
            var gd = gen.GenerationData;
            var bodies = _tBodies; bodies.Clear();
            var kept = _tKept; kept.Clear();
            var boxes = _tBoxes; boxes.Clear();
            var doomed = _tDoomed; doomed.Clear();
            var del = _tDelete; del.Clear();
            Array.Clear(_tCount, 0, _tCount.Length);
            bool any = false;
            foreach (var kv in gen.Entities)
            {
                var f = Facts(gd, kv.Key, kv.Value, out var box, out var pos);
                if (AsteroidRules.Classify(DeleteGenerated, server, framesKnown, in f) == RockVerdict.Delete) any = true;
                bodies.Add((kv.Key, f, box, pos));
            }
            bool nearKnown = any && FillNear(session);   // (grids and characters asked only when a body may go)
            foreach (var b in bodies)
            {
                var f = b.F;
                if (AsteroidRules.Classify(DeleteGenerated, server, framesKnown, in f) == RockVerdict.Delete)
                {
                    f.GridNear = !nearKnown || GridNear(b.Box);   // (not known: someone may be there - kept)
                    f.PlayerNear = !nearKnown || PlayerNear(b.Box);
                }
                var v = AsteroidRules.Classify(DeleteGenerated, server, framesKnown, in f);
                _tCount[(int)v]++;
                if (v == RockVerdict.Delete) { del.Add((b.Id, b.Box, near.Contains(b.Id))); doomed.Add(b.Pos); }
                else if (AsteroidRules.Persists(v))
                {
                    kept.Add(b.Pos); boxes.Add(f.HasBox ? b.Box : new BoundingBoxD(b.Pos, b.Pos));   // an asteroid placed on purpose: a frame anchor
                    if (AsteroidRules.LogKeep(v) && _heldLogged.Add(b.Id) && OrbitalLog.Asteroids)
                        Say($"[ORBIT-ROIDS] kept generator body {b.Id} at {Km(f.HasBox ? b.Box.Center : b.Pos)}: {AsteroidRules.Why(v)}", now);
                }
            }
            bodies.Clear(); near.Clear();
            int persistentCount = kept.Count;   // (those so far: placed on purpose, ours, player-edited)
            // Every other voxel body that is not a planet (an encounter's rock, one spawned with a prefab,
            // not through the generator): planets are the voxel bodies at a planet's centre.
            try
            {
                // (Its base class is in VRage.Voxels, which scripts cannot reference: the query by reflection.)
                if (_voxelQuery == null)
                {
                    Type vt = PlanetRenderBridge.FindType("Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Voxels.VoxelOperationsComponent");
                    var gm = session.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetEntitiesOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    if (vt != null && gm != null) _voxelQuery = gm.MakeGenericMethod(vt);
                }
                var found = _voxelQuery?.Invoke(session, null) as System.Collections.IEnumerable;
                if (found != null)
                foreach (var o in found)
                {
                    if (!(o is Entity ve) || !Num.Alive(ve)) continue;   // (a dead one's read threw: the rest of the list was lost)
                    Vector3D vp = ve.Data.GetWorldTransform().Position;
                    bool planet = false;
                    foreach (var b in SystemHost.BeaconOf.Values) if ((b.Center - vp).Length() < 5000) { planet = true; break; }
                    if (Within1m(doomed, vp)) continue;
                    if (!planet && !Within1m(kept, vp))
                    {
                        kept.Add(vp);
                        var vb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(ve.Data);   // (a body may have no box: GetWorldAABB threw)
                        boxes.Add(vb.HasValue ? vb.Value : new BoundingBoxD(vp, vp));
                    }
                }
            }
            catch { }
            lock (Asteroids)
            {
                // (each rock by its box's centre: a voxel body's position is its storage corner - distances to rocks were off by
                //  half a rock, and a frame was centred on a rock's corner)
                Asteroids.Clear();
                if (boxes.Count == kept.Count) foreach (var b in boxes) Asteroids.Add(b.Min == b.Max ? b.Min : b.Center); else Asteroids.AddRange(kept);
                AsteroidBoxes.Clear(); AsteroidBoxes.AddRange(boxes);
                AsteroidPersistent.Clear(); for (int i = 0; i < kept.Count; i++) AsteroidPersistent.Add(i < persistentCount);
            }
            foreach (var d in del)
            {
                bool ok = false;
                try { gen.DeleteEntity(d.Id); _deleted++; ok = true; } catch { }
                Say(ok ? $"[ORBIT-ROIDS] deleted generator body {d.Id} at {Km(d.Box.Center)}, {(d.Box.Max - d.Box.Min).Length():F0} m across: {AsteroidRules.Why(RockVerdict.Delete)}{(d.Encounter ? "; left by an encounter moved off a planet's border" : "")}"
                       : $"[ORBIT-ROIDS] delete of generator body {d.Id} failed", now);
            }
            del.Clear(); kept.Clear(); boxes.Clear(); doomed.Clear();
            _lastPass = AsteroidRules.PassLine(DeleteGenerated, server, framesKnown, _tCount);
            Status = $"asteroids blocked: {_orig.Count} volume definition(s) at no density, {_deleted} deleted, {RingInfo.Count} ring(s), {Asteroids.Count} asteroid(s) as anchors{_lastPass}";
        }
        catch (Exception e) { Status = "asteroid block failed: " + (e.InnerException ?? e).Message; }
    }

    // The server generator lives in an assembly mods cannot reference: by name.
    // (the server's generator kept per session, once found: the server's frame step asks every tick - a walk over the session's
    //  components with a new closure and delegate each time, 2026-10-15. Not kept until the server's own one is there: a
    //  plain one found first is asked again, as before)
    static Keen.VRage.Core.Game.Systems.Session _genSession;
    static ProceduralGeneratorSessionComponent _genServer;
    internal static ProceduralGeneratorSessionComponent Generator(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (session != null && ReferenceEquals(session, System.Threading.Volatile.Read(ref _genSession)))
        {
            var kept = System.Threading.Volatile.Read(ref _genServer);
            if (kept != null && ReferenceEquals(session, System.Threading.Volatile.Read(ref _genSession))) return kept;
        }
        var found = FindGenerator(session, out bool isServer);
        if (isServer) { System.Threading.Volatile.Write(ref _genServer, null); System.Threading.Volatile.Write(ref _genSession, session); System.Threading.Volatile.Write(ref _genServer, found); }
        return found;
    }

    static ProceduralGeneratorSessionComponent FindGenerator(Keen.VRage.Core.Game.Systems.Session session, out bool isServer)
    {
        // The session's components: the server's generator (the one that spawns).
        ProceduralGeneratorSessionComponent server = null, any = null;
        session.SessionComponents?.ForEach(delegate (Keen.VRage.Core.Game.Components.SessionComponent sc)
        {
            if (sc is ProceduralGeneratorSessionComponent g) { any ??= g; if (g.GetType().Name.Contains("Server")) server = g; }
        });
        isServer = server != null;
        return server ?? any;
    }

    // What a generator body is (AsteroidRules' facts, all but who is near). 2026-10-15: this was IsManual, a reflected
    // Entity.TryGet<T>() with no parameters for ProceduralVoxelEntityComponent - the real one takes a tag
    // (REFLECTION_AND_WHITELIST.md §6), so it never matched and every body was "manual": none deleted. Now typed, no reflection:
    // the component's public base (VRage.Core.Game; the one the engine's own DeleteEntity asks) and its
    // IEditableProceduralEntity; the voxel one known by its type's name (VRage.Voxels cannot be referenced). A read failing:
    // not alive - neither deleted nor an anchor (as a body whose read threw before).
    static RockFacts Facts(ProceduralGenerationDataSessionComponent gd, SpaceEntityId key, Entity e, out BoundingBoxD box, out Vector3D pos)
    {
        var f = new RockFacts(); box = default; pos = default;
        if (!Num.Alive(e)) return f;
        try
        {
            pos = e.Data.GetWorldTransform().Position;
            var tb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(e.Data);
            // (ours by the box's middle: a voxel body's position is its storage corner)
            if (tb.HasValue) { box = tb.Value; f.HasBox = true; f.Ours = AsteroidFrames.Protects(box.Center); }
            var p = e.TryGet<ProceduralEntityComponentBase>();
            if (p != null)
            {
                f.Procedural = true;
                var ed = p as IEditableProceduralEntity;
                f.Voxel = ed != null && p.GetType().Name == "ProceduralVoxelEntityComponent";
                f.Manual = p.MarkedAsManual || (ed != null && ed.IsManual);
                f.IdMatches = p.ID.HasValue && p.ID.Value.Equals(key);
            }
            f.Skipped = gd == null || gd.SkippedEntityIds.Contains(key);   // (no generation data: cannot tell - as excluded)
            f.Alive = true;
        }
        catch { f = new RockFacts(); }
        return f;
    }

    /// <summary>The grids' boxes and the characters' positions, for GridNear / PlayerNear. False: not known (then all kept).</summary>
    static bool FillNear(Keen.VRage.Core.Game.Systems.Session session)
    {
        _tGrids.Clear(); _tChars.Clear(); _tCharBuf.Clear();
        try
        {
            foreach (var g in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
            {
                if (!Num.Alive(g)) continue;
                var gb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Data);
                if (gb.HasValue) _tGrids.Add(gb.Value);
                else { var gp = g.Data.GetWorldTransform().Position; _tGrids.Add(new BoundingBoxD(gp - new Vector3D(500, 500, 500), gp + new Vector3D(500, 500, 500))); }   // (no box: its size unknown - a wide one)
            }
            if (session.TryFillAliveCharacters(_tCharBuf))
                foreach (var c in _tCharBuf) if (Num.Alive(c)) _tChars.Add(c.Data.GetWorldTransform().Position);
            _tCharBuf.Clear();
            return true;
        }
        catch { _tGrids.Clear(); _tChars.Clear(); _tCharBuf.Clear(); return false; }
    }

    static bool GridNear(BoundingBoxD rock) { foreach (var g in _tGrids) if (AsteroidRules.Gap(rock, g) <= AsteroidRules.GridMargin) return true; return false; }
    static bool PlayerNear(BoundingBoxD rock) { foreach (var c in _tChars) if (BoxDistance(rock, c) <= AsteroidRules.PlayerMargin) return true; return false; }

    /// <summary>A rate-limited log line (deletions and keeps: at most 8 per 30 s; what was held back is counted). Deletions are
    /// logged in every game (World.DeleteGeneratedAsteroids promises it); the keeps only with OrbitalLog.Asteroids (dev).</summary>
    static void Say(string line, double now)
    {
        bool ok = _logRate.Allow(now, out int held);
        if (held > 0) Log.Default?.Info($"[ORBIT-ROIDS] ({held} line(s) not logged in the last {_logRate.Window:F0} s)");
        if (ok) Log.Default?.Info(line);
    }

    static void Kill(VolumeDefinition v)
    {
        if (v == null || AsteroidFrames.IsReserved(v) || !_seen.Add(v)) return;   // (a reserved one is our asteroid frames')
        _density ??= typeof(VolumeDefinition).GetField("<Density>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_density == null) return;
        _orig.Add((v, v.Density));
        _density.SetValue(v, -1f);   // (0 still passes when the draw is exactly 0)
    }

    /// <summary>The session ends: the definitions (shared by the whole process) get their densities back.</summary>
    public static void Restore()
    {
        AsteroidFrames.Restore();   // first: a definition both zeroed here and reserved there gets its first-known density
        if (_density != null) foreach (var (v, d) in _orig) { try { _density.SetValue(v, d); } catch { } }
        _orig.Clear(); _seen.Clear(); _loggedRings = false;
        _heldLogged.Clear(); _logRate.Reset(); _lastPass = ""; System.Threading.Volatile.Write(ref _genServer, null); System.Threading.Volatile.Write(ref _genSession, null);
    }

    static string Km(Vector3D v) => $"({v.X / 1000:F1}, {v.Y / 1000:F1}, {v.Z / 1000:F1}) km";
}
