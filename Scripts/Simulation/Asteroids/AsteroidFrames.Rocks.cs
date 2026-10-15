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
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// AsteroidFrames (partial): rocks on demand - which rocks are wanted near you, made real through the game's procedural
// generator (a volume each) and put away again.
public static partial class AsteroidFrames
{
    // ───────────────────────────── rocks on demand ─────────────────────────────

    /// <summary>The locked half (FramesLock held): the ring rocks that become sites, and which rocks are wanted - from the
    /// frames and where you are. The generator's half (Act) runs after the lock, this same tick.</summary>
    private static void Update(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        double now = Wall();
        while (_force.TryDequeue(out var fr))
            lock (_gate) if (fr.index >= 0 && fr.index < _roids.Count) { _roids[fr.index].Forced = fr.on; if (fr.on) _roids[fr.index].LastWanted = now; }

        var gen = AsteroidBridge.Generator(session);
        if (gen == null) { Status = "no generator"; return; }

        var pf = FrameHost.PlayerId != 0 ? SystemHost.Frames.FindByMember(FrameHost.PlayerId) : null;
        StateVector ps = default; bool havePs = false;
        if (pf != null) { ps = OrbitPropagation.StateAt(pf.Elements, t); havePs = IsFinite(ps.Position); }
        // Ring rocks: the nearest few within range of you become sites (their rocks then come out as any).
        if (RingRocks.Enabled && havePs)
        {
            var body = SystemHost.Registry?.Find(pf.ParentBodyName);
            if (body != null)
            {
                int k = 0;
                foreach (var (b, i, d) in RingRocks.Near(body, ps.Position, t, MaterializeRange))
                {
                    if (k++ >= MaxNearVolumes) break;
                    EnsureRingRoid(b, i, t);
                }
            }
        }
        List<Roid> roids;
        lock (_gate) roids = new List<Roid>(_roids);
        // Which ones are wanted: forced, the one you are in, and of those within range only the nearest few
        // (every rock volume out samples the generator's sectors; a whole ring's worth was out at once).
        var inRange = new List<(Roid r, double d)>();
        var inIt = new HashSet<Roid>();
        foreach (var r in roids)
        {
            var f = SystemHost.Frames.Get(r.FrameId);
            if (f == null) continue;
            if (EncounterFrames.HasNonNpc(f)) { inIt.Add(r); continue; }   // you (or your grid) are in it
            if (havePs && pf != f && pf.ParentBodyName == f.ParentBodyName)
            {
                var rs = OrbitPropagation.StateAt(f.Elements, t);
                double d = IsFinite(rs.Position) ? (rs.Position - ps.Position).Length() : double.MaxValue;
                if (d < MaterializeRange) inRange.Add((r, d));
            }
        }
        inRange.Sort((x, y) => x.d.CompareTo(y.d));
        var allowed = new HashSet<Roid>(inIt);
        for (int i = 0; i < inRange.Count && i < MaxNearVolumes; i++) allowed.Add(inRange[i].r);
        var wanted = new HashSet<Roid>();
        foreach (var r in roids)
            if (r.Forced || allowed.Contains(r)) { wanted.Add(r); r.LastWanted = now; }
        _planRoids = roids; _planWanted = wanted; _planNow = now;   // (ServerAfterLock acts on them)
    }

    /// <summary>The generator's half (FramesLock NOT held): stale volumes reconciled, its sectors rebuilt when due, the wanted
    /// rocks' volumes out and the others' in, and their bodies counted. A ring rock left behind gives its site back under a
    /// brief FramesLock (the same pass as before: its volume went in just above).</summary>
    private static void Act(Keen.VRage.Core.Game.Systems.Session session, List<Roid> roids, HashSet<Roid> wantedSet, double now)
    {
        var gen = AsteroidBridge.Generator(session);
        if (gen == null) { Status = "no generator"; return; }
        Reconcile(session, gen, now);
        // A volume put out where the generator already built its sectors (you jumped straight in) is only
        // sampled when they are built again: rebuild them once it is registered (as the game does when it
        // places a manual volume: Deactivate, then its triggers again).
        if (_refreshAt > 0 && now >= _refreshAt)
        {
            _refreshAt = 0;
            try
            {
                var gt = gen.GetType();
                var de = gt.GetMethod("Deactivate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                var ct = gt.GetMethod("CreateTriggers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (de != null && ct != null && gen.IsActive) { de.Invoke(gen, new object[] { false }); ct.Invoke(gen, null); Event("generator sectors rebuilt (a volume of ours is out)"); }
                else Warn("refresh", "generator sector rebuild not found");
            }
            catch (Exception ex) { Warn("refresh", "generator sector rebuild failed: " + (ex.InnerException ?? ex).Message); }
        }

        // DEV: what the generator makes of the first live volume of ours.
        try
        {
            Roid lv = null; lock (_gate) lv = _roids.Find(x => x.Volume != null);
            if (lv != null)
            {
                int manual = 0, near = 0;
                var bb = new BoundingBoxD(lv.Berth - new Vector3D(3000, 3000, 3000), lv.Berth + new Vector3D(3000, 3000, 3000));
                using (var qb = new Buffer<IProceduralVolume>(Allocator.Pool, "OrbitalRoidsDiag"))
                {
                    gen.QueryManualVolumes(in bb, qb);
                    var qe = ((BufferReference<IProceduralVolume>)qb).GetEnumerator();
                    while (qe.MoveNext()) manual++;
                    qe.Dispose();
                }
                foreach (var kv in gen.Entities) { try { if ((kv.Value.Data.GetWorldTransform().Position - lv.Berth).Length() < 10000) near++; } catch { } }
                object samples = gen.GetType().GetProperty("MaxSectorSpawnSamples", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(gen);
                double pd = FrameHost.PlayerId != 0 && ServerPlanetBeacon.PlayerState(out Vector3D pp, out _) ? (pp - lv.Berth).Length() : double.NaN;
                Diag = $"diag {lv.Label}: generator lists {manual} manual volume(s) at its berth; {near} generator bod(ies) within 10 km; spawn samples {samples}; active {gen.IsActive}; comp density {lv.Comp?.Density}; player {pd / 1000:F1} km from it";
            }
        }
        catch (Exception ex) { Diag = "diag failed: " + ex.Message; }

        int live = 0;
        foreach (var r in roids)
        {
            bool wanted = wantedSet.Contains(r);
            try
            {
                if (r.Volume != null && r.Comp != null && _givenBack.Contains(r.Comp)) Dematerialize(gen, r, "its composition is the game's again");
                if (wanted && r.Volume == null && now >= r.NextTry) { r.NextTry = now + 10; Materialize(session, gen, r, now); }
                else if (!wanted && r.Volume != null && now - r.LastWanted > LeaveSeconds) Dematerialize(gen, r, "left");
            }
            catch (Exception e) { Warn("vol " + r.Label, $"{r.Label}: {(e.InnerException ?? e).Message}"); }
            if (r.Volume != null) live++;
            // A ring rock you have left (its rocks gone, nobody in it): back to a number. (Its site and slot are the frames':
            // under the lock, briefly - and only for one that has gone: the condition is checked before taking it.)
            if (r.Dynamic && !wanted && r.Volume == null && now - r.LastWanted > LeaveSeconds)
            {
                bool gone;
                lock (ServerFrames.FramesLock)
                {
                    gone = EncounterFrames.RemoveSite(r.FrameId);
                    if (gone) SystemHost.Frames?.Allocator?.Free(r.Slot);
                }
                if (gone)
                {
                    lock (_gate) { _roids.Remove(r); _byName.Remove(r.Name); for (int k = 0; k < _roids.Count; k++) _roids[k].Index = k; }   // (indices stay list positions)
                    if (r.Site != null) lock (_left) _left[r.Label] = r.Site;   // (still a target: its orbit is known - EncounterFrames.TargetSite)
                    Event($"ring rock {r.Label} left behind: its site is gone (slot {r.Slot} free)");
                }
            }
        }
        CountRocks(gen, roids);
        Status = $"{roids.Count} asteroid frame(s), {live} with rocks out{(gen.IsActive ? "" : " (the game's asteroid generation is OFF: no rocks)")}";
    }

    /// <summary>
    /// Rocks per live volume: the generator's bodies whose world box reaches within the volume's protect radius.
    /// Counted here from the generator's own list, not in the bridge's pass: that pass only asked a body it had
    /// not already kept as player-edited (and a pass that failed half-way counted nothing), and a voxel body's
    /// position is its storage corner, not its middle.
    /// </summary>
    private static void CountRocks(ProceduralGeneratorSessionComponent gen, List<Roid> roids)
    {
        var live = roids.FindAll(r => r.Volume != null);
        foreach (var r in live) r.Rocks = 0;
        if (live.Count == 0) return;
        foreach (var kv in gen.Entities)
        {
            BoundingBoxD b;
            try { var tb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(kv.Value.Data); if (!tb.HasValue) continue; b = tb.Value; }
            catch { try { var p = kv.Value.Data.GetWorldTransform().Position; b = new BoundingBoxD(p, p); } catch { continue; } }
            foreach (var r in live)
                if (AsteroidBridge.BoxDistance(b, r.Berth) <= MaxRadius(r.Radii) + ProtectMargin) { r.Rocks++; break; }
        }
    }

    private static void Materialize(Keen.VRage.Core.Game.Systems.Session session, ProceduralGeneratorSessionComponent gen, Roid r, double now)
    {
        var comp = CompositionFor(session, gen, r.Belt);
        if (comp != null && _shared.Contains(comp)) SetDensity(comp, RockDensity);   // (a shared one is on only while ours are out)
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
        // You are already there (you jumped in): the generator built the sectors round you without it, so they
        // are rebuilt once it is registered. Approaching normally, it is out long before you arrive.
        if (ServerPlanetBeacon.PlayerState(out Vector3D pp, out _) && (pp - r.Berth).Length() < 5000) _refreshAt = now + 1.5;
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
}
