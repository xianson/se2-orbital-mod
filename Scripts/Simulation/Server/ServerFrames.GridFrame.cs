using System.Collections.Concurrent;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// ServerFrames (partial): one frame's server step - anchor, members, warp steering, differential gravity, splits.
public static partial class ServerFrames
{
    // ───────────────────────────── per-frame update (grids) ─────────────────────────────

    /// <summary>The frame's members joined to this grid (its ship: sub-grids, docked craft), itself included - from the
    /// joint search kept once a second per grid (_joinedOf).</summary>
    /// <summary>(into r, cleared first: the caller's kept list - UpdateGridFrame's anchor body and steering set, each used within
    /// its own step; a new list per call before, every tick for a jointed anchor)</summary>
    static List<OrbitalGridComponent> JoinedMembers(ProximityFrame f, OrbitalGridComponent g, List<OrbitalGridComponent> grids, List<OrbitalGridComponent> r)
    {
        var set = JoinedSet(g, Wall());
        r.Clear();
        if (set == null) { r.Add(g); return r; }
        foreach (var o in grids) if (o.Entity != null && set.Contains(o.Entity)) r.Add(o);
        if (!r.Contains(g)) r.Add(g);
        return r;
    }

    /// <summary>A grid's joined entities (GridMembers.MoveSet, itself included; null: joined to something static, or none
    /// found), kept per grid for a second (_joinedOf) - every caller's: the frame step's anchor and steering, the rules'
    /// Joined, the invariants. (One kept scratch list for the search: server thread, under FramesLock - a new one per search
    /// before.)</summary>
    static HashSet<Keen.VRage.DCS.Components.Entity> JoinedSet(OrbitalGridComponent g, double wj)
    {
        if (_joinedOf.TryGetValue(g.Id, out var cj) && wj - cj.at < 1.0) return cj.set;
        var l = _moveSetScratch; l.Clear();
        var set = GridMembers.MoveSet(g, l, out _) ? new HashSet<Keen.VRage.DCS.Components.Entity>(l) : null;
        l.Clear();
        _joinedOf[g.Id] = (set, wj);
        return set;
    }
    static readonly List<Keen.VRage.DCS.Components.Entity> _moveSetScratch = new List<Keen.VRage.DCS.Components.Entity>();
    static readonly List<OrbitalGridComponent> _bodyScratch = new List<OrbitalGridComponent>(), _steerScratch = new List<OrbitalGridComponent>(), _frameAll = new List<OrbitalGridComponent>();

    /// <summary>The game behind the membership rules (Core/Frames/MembershipRules): server grids by their mod id.</summary>
    internal sealed class GridWorld : SEAerospace.Frames.IFrameWorld
    {
        public static readonly GridWorld I = new GridWorld();
        static OrbitalGridComponent G(long id) => GridMembers.Get(id) is OrbitalGridComponent g && g.IsServer && g.Entity != null ? g : null;
        public bool IsLive(long id) => G(id) != null;
        public bool MayReturn(long id) => GridMembers.Tombstoned(id);
        public bool IsDynamic(long id) => G(id) is OrbitalGridComponent g && GridMembers.IsDynamic(g);
        public bool IsNpc(long id) => G(id) is OrbitalGridComponent g && EncounterFrames.IsNpc(g);
        public double Mass(long id) => G(id) is OrbitalGridComponent g ? GridMembers.Mass(g) : 0;
        public long FrameOf(long id) => SystemHost.Frames?.FindByMember(id)?.Id ?? -1;
        // (the joint search once a second per grid, its entities mapped to ids through the live registry once a tick)
        static readonly Dictionary<Keen.VRage.DCS.Components.Entity, long> _ids = new Dictionary<Keen.VRage.DCS.Components.Entity, long>();
        // (the answer in a kept list of this thread's: the rules enumerate it at once and keep nothing - ElectAnchor, per frame
        //  per tick; a new array or list per call was ~0.1 MB/s, 2026-10-15. The entity -> id map built again only when grids
        //  come or go - GridMembers.All's snapshot changes - not every tick: an entity's id is fixed while it is registered)
        [ThreadStatic] static List<long> _joinedOut;
        static List<OrbitalGridComponent> _idsOf;
        public IReadOnlyCollection<long> Joined(long id)
        {
            var r = _joinedOut ??= new List<long>();
            r.Clear();
            var g = G(id);
            if (g == null || !GridMembers.IsConstrained(g)) { r.Add(id); return r; }
            var set = JoinedSet(g, Wall());
            if (set == null) { r.Add(id); return r; }
            var allNow = GridMembers.All();
            if (!ReferenceEquals(_idsOf, allNow)) { _ids.Clear(); foreach (var o in allNow) if (o.IsServer && o.Entity != null) _ids[o.Entity] = o.Id; _idsOf = allNow; }
            foreach (var e in set) if (_ids.TryGetValue(e, out long oid)) r.Add(oid);
            if (!r.Contains(id)) r.Add(id);
            return r;
        }
    }
    private static readonly List<OrbitalGridComponent> _frameGrids = new List<OrbitalGridComponent>();   // (UpdateGridFrame's: one frame at a time, from Tick)
    /// <summary>(diagnostics: UpdateGridFrame's phase marks - start, members/anchor rules, anchor step, others - for a slow frame)</summary>
    [ThreadStatic] static long[] _ugfT;
    static long[] _ugf => _ugfT ??= new long[4];
    private static void UpdateGridFrame(ProximityFrame f, double dt)
    {
        _ugf[0] = Sw(); _ugf[1] = _ugf[2] = _ugf[3] = 0;
        // (a restored frame whose berth was moved, its grids still loading: not stepped until they follow - its members are
        //  at the old berth, and every one would be "moved out" of it)
        if (f.PendingBerthShift.LengthSquared() >= 1) return;
        var reg = SystemHost.Registry;
        var parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;

        // Grid members (server copies, still alive and dynamic).
        var grids = _frameGrids; grids.Clear();
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) continue;
            var g = GridMembers.Get(id);
            if (g != null && g.IsServer && GridMembers.IsDynamic(g)) grids.Add(g);
        }
        // A static anchor (a static grid, or an asteroid) comes first: nothing is pinned or drained, the
        // rest (the player too: a rider, not the anchor) move about it normally.
        bool isStatic = false; Vector3D staticPos = default;
        if (!_born.ContainsKey(f.Id))
        {
            _born[f.Id] = Wall();
            // (one entry per frame id, for good: dissolved frames' entries dropped now and then)
            if (_born.Count > 256) { var gone = new List<long>(); foreach (long k in _born.Keys) if (SystemHost.Frames.Get(k) == null) gone.Add(k); foreach (long k in gone) _born.Remove(k); }
        }
        // (no move off a "dirty" slot any more, and no young frame kept off a rock: a frame is on whatever static is in it)
        // A station exists only on a stable orbit: a static-anchored frame whose orbit dips into its planet's border (a station
        // pushed off its orbit, a frame captured on a low one) has its static grids made ships (the game's own static/dynamic
        // switch) - then it is a ship's frame, and arrives as one. (The orbits the mod spawns things on are stable to begin with.)
        // (StaticAnchor once per frame step, not twice - it scans the world's static grids and its rocks, 7.5 ms/s in a profile,
        //  2026-10-15: the second call answered the same, its first having made any join and noted any rock - asked again
        //  only after the block below, which may have made its static grids ships)
        long sid = 0;
        bool staticHit = !f.IsEncounter && StaticAnchor(f, out sid, out staticPos);
        if (staticHit && sid != AsteroidAnchorId && !OrbitStable(f))
        {
            int made = 0;
            foreach (long m in new List<long>(f.Members))
                if (GridMembers.IsGridId(m) && GridMembers.Get(m) is OrbitalGridComponent sg && sg.IsServer && !GridMembers.IsDynamic(sg))
                    try { var gp = sg.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss.GridPhysicsComponent>(); if (gp != null) { gp.SetGridMotion(false); GridMembers.ForgetMotion(sg); made++; } } catch { }
            if (made > 0) Event($"frame #{f.Id}: its orbit meets {f.ParentBodyName}'s border (periapsis {(f.Elements.PeriapsisRadius / 1000):F0} km): {made} static grid(s) made ship(s) - a station needs a stable orbit");
            staticHit = StaticAnchor(f, out sid, out staticPos);
        }
        if (staticHit)
        {
            isStatic = true;
            if (f.AnchorEntityId != sid)
            {
                Event($"frame #{f.Id}: anchor -> {(sid == AsteroidAnchorId ? "an asteroid" : $"static grid {sid}")} (the player rides)");
                f.AnchorEntityId = sid;
                AnchorAccel.Remove(f.Id);   // no one folds thrust into a static anchor's frame
            }
            // The frame is the anchor's: its centre moves onto the anchor and its orbit becomes the anchor's.
            // Nothing in the world moves (moving a static grid off its rock made the game turn it dynamic,
            // and an asteroid cannot move at all): only the frame's reference point does.
            Vector3D off = staticPos - f.BerthCenter;
            if (off.Length() > PinTolerance)
            {
                double tn = SystemHost.Now;
                StateVector c0 = OrbitPropagation.StateAt(f.Elements, tn);
                var el = CaptureMath.CaptureElements(new StateVector(c0.Position + off, c0.Velocity), f.Elements.Mu, tn);
                if (IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion))
                {
                    f.Elements = el;
                    f.BerthCenter = staticPos;
                    Event($"frame #{f.Id}: centred on its static anchor ({off.Length() / 1000:F2} km; its orbit is the anchor's)");
                }
            }
        }
        else if (!f.IsEncounter && (f.AnchorEntityId == AsteroidAnchorId || (GridMembers.IsGridId(f.AnchorEntityId) && !HasGrid(grids, f.AnchorEntityId))))
        {
            // The static anchor (or a grid anchor) is gone: the player anchors again if nothing else can. (Not while the grid
            // may be coming back - an area respawn re-adds it within moments, its old id kept: GridMembers.Tombstoned.)
            if (grids.Count == 0 && f.HasMember(FrameHost.PlayerId) && !GridMembers.Tombstoned(f.AnchorEntityId))
            {
                // The centre moves back onto the player (with the player's orbit), as it moved onto the anchor.
                if (ServerPlanetBeacon.PlayerState(out Vector3D pp, out Vector3D pv) && (pp - f.BerthCenter).Length() > PinTolerance)
                {
                    double tn = SystemHost.Now;
                    StateVector c0 = OrbitPropagation.StateAt(f.Elements, tn);
                    // (position only: the player's own velocity is drained into the rails by the client next tick - added here
                    //  too, it went in twice)
                    var el = CaptureMath.CaptureElements(new StateVector(c0.Position + (pp - f.BerthCenter), c0.Velocity), f.Elements.Mu, tn);
                    if (IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion)) { f.Elements = el; f.BerthCenter = pp; }
                }
                f.AnchorEntityId = FrameHost.PlayerId;
                AnchorAccel.Remove(f.Id);   // (the old grid anchor's last acceleration would push every later joiner for good)
                Event($"frame #{f.Id}: anchor -> the player");
            }
        }
        _ugf[1] = Sw(); if (grids.Count == 0 && !isStatic) return;   // (a static anchor's frame still steps: its riders, its arrival)

        // Anchor election: keep a live grid anchor, else the heaviest PLAYER grid (SE1 ElectAnchor).
        // NPC grids never anchor, and an encounter frame has no anchor at all: its origin is the berth
        // (the site, pinned) and its orbit is its own (a site's ephemeris, a procedural spawn's conic).
        OrbitalGridComponent anchor = null;
        bool elected = false;
        // (the anchor's joint check, made once in the anchor step and read again for the joined set below: a physics query each -
        //  the two were asked the same thing within the same step, nothing between them changes a joint)
        bool anchorJointed = false;
        if (!f.IsEncounter && !isStatic)
        {
            // The rules (Core/Frames/MembershipRules.ElectAnchor, tested offline on a fake world): a live grid anchor kept -
            // unless a heavier grid of its own ship is in the frame (a ship is anchored by its heaviest grid: a light sub-grid
            // kept as anchor had its velocity drained alone while the hull pushed it back through the joint); one able to
            // come back (an area respawn): wait; else the heaviest live player grid (a player-anchored frame hands over to it).
            // (The player fallback, no grid left at all, is above.)
            long prevAnchor = f.AnchorEntityId;
            var choice = SEAerospace.Frames.MembershipRules.ElectAnchor(GridWorld.I, f.Id, f.AnchorEntityId, f.Members, FrameHost.PlayerId, false, out long aid);
            // (waiting for a respawning anchor: no acceleration of the old one pushing the riders meanwhile)
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Wait) AnchorAccel.Remove(f.Id);
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Keep || choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Grid)
                foreach (var g in grids) if (g.Id == aid) { anchor = g; break; }
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Grid && anchor != null)
            {
                bool sameShip = GridMembers.IsGridId(prevAnchor) && GridWorld.I.IsLive(prevAnchor);
                Event($"frame #{f.Id}: anchor -> grid {anchor.Id} '{anchor.DisplayName}' ({GridMembers.Mass(anchor):F0} kg){(sameShip ? $" - the heaviest of the ship grid {prevAnchor} is part of" : "")}");
                f.AnchorEntityId = anchor.Id;
                elected = true;
            }
        }

        _ugf[2] = Sw(); double t = SystemHost.Now;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D A = Vector3D.Zero;
        Vector3D anchorPos = isStatic ? staticPos : f.BerthCenter;

        // An anchor moved out of its berth by something else (the game's fast travel, an admin move): not this frame's any
        // more - out of it quietly (pinned back, it was dragged home; split, it got an orbit made of a berth-to-berth offset)
        if (anchor != null && SEAerospace.Frames.PlayerOrbit.MovedOut(f, GridMembers.Position(anchor)))
        {
            SystemHost.Frames.RemoveMember(anchor.Id);
            Event($"frame #{f.Id}: anchor grid {anchor.Id} '{anchor.DisplayName}' left - {(GridMembers.Position(anchor) - f.BerthCenter).Length() / 1000:F0} km from the berth (moved by something else)");
            anchor = null;
        }
        if (anchor != null)
        {
            // Anchor owner = server: pin + drain + fold.
            anchorPos = GridMembers.Position(anchor);
            if ((anchorPos - f.BerthCenter).Length() > PinTolerance)
            {
                // A NEW anchor (the old one destroyed, undocked, a handover from the player): the frame's orbit moves onto it
                // first - the re-pin below shifts every member by its offset, and without this the whole frame jumped that far
                // in space (up to 20 km: the map, a plan, an arrival point all off by it). A drifting anchor's re-pin keeps the
                // rails: its motion is already folded in.
                // (a rebase refused - the orbit committed to a coming change of body: the re-pin waits for it, tried each tick;
                //  re-pinned anyway, the frame jumped by the new anchor's offset, up to 10 km)
                bool repin = true;
                if (elected || _rebasePending.Contains(f.Id))
                {
                    if (FrameRails.TryRebaseShift(f, t, anchorPos - f.BerthCenter)) _rebasePending.Remove(f.Id);
                    else
                    {
                        if (_rebasePending.Add(f.Id)) Event($"frame #{f.Id}: the new anchor's offset waits to be folded into the orbit (rebase refused for now)");
                        repin = false;
                    }
                }
                if (repin)
                {
                    // Re-pin by shifting the WHOLE frame back (members keep their offsets; no rails change).
                    Vector3D shift = f.BerthCenter - anchorPos;
                    // (MoveGrid: a jointed grid's wheels, rotor parts and docked ships must come too, before the physics step)
                    var went = new HashSet<long>();   // (a member moved with another's joined set is not moved twice)
                    foreach (var g in grids)
                        if (!went.Contains(g.Id)) { MoveGrid(g, GridMembers.Position(g) + shift, null, out var withP); foreach (var w in withP) went.Add(w.Id); }
                    FrameHost.RequestShift(f.Id, shift);
                    anchorPos = f.BerthCenter;
                }
            }
            // The anchor's motion drained into the rails - the whole ship's, if it is joined to others in the frame: their
            // mass-weighted velocity, and every one of them zeroed (one body; the anchor alone zeroed was refilled through
            // the joints, and drained again).
            Vector3D vA = GridMembers.Velocity(anchor);
            anchorJointed = GridMembers.IsConstrained(anchor);
            List<OrbitalGridComponent> body = anchorJointed ? JoinedMembers(f, anchor, grids, _bodyScratch) : null;
            if (body != null && body.Count > 1)
            {
                double mt = 0; Vector3D p = Vector3D.Zero;
                foreach (var bg in body) { double m = Math.Max(1.0, GridMembers.Mass(bg)); Vector3D v = GridMembers.Velocity(bg); if (!IsFinite(v)) continue; p += v * m; mt += m; }
                if (mt > 0) vA = p / mt;
            }
            if (IsFinite(vA))
            {
                if (body != null && body.Count > 1) foreach (var bg in body) GridMembers.SetVelocity(bg, Vector3D.Zero);
                else GridMembers.SetVelocity(anchor, Vector3D.Zero);
                f.PendingDrainDv += vA;
            }
            var fold = FrameRails.FoldDrain(f, t, FrameHost.FoldThreshold, FrameHost.MaxApparentAccel * dt, ref cur, out Vector3D slice);
            // (the anchor's burn per GAME second: the drained impulse is true dv, and in warp a real step is N game steps - as
            //  slice / dt, riders were pushed N times too hard)
            if (fold == FrameRails.FoldResult.Folded)
            {
                A = slice / (dt * Math.Max(1.0, SystemHost.Timescale));
                // members steered in warp keep their TRUE relative velocity: the frame's own change takes its share off it
                // (their absolute motion is theirs - an anchor's burn moved them with it)
                foreach (var g in grids) if (g != anchor && _trueRel.TryGetValue(g.Id, out var tr)) _trueRel[g.Id] = tr - slice;
            }
            if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;
            AnchorAccel[f.Id] = A;
        }
        else if (!f.IsEncounter && !isStatic && AnchorAccel.TryGetValue(f.Id, out var pa)) A = pa;   // the player anchors (client folds)

        // Other members: CW differential gravity minus the frame acceleration; split beyond the slot.
        // NPC grids are Newtonian inside the frame: no relative-motion terms, and they never split off.
        _ugf[3] = Sw(); double mu = f.Elements.Mu;
        Vector3D rA = cur.Position;
        Vector3D gA = Grav(rA, mu);
        // A Lagrange site: its own simple dynamics (EncounterFrames.LagrangeDynamics).
        Func<Vector3D, Vector3D, Vector3D> lag = null;
        if (f.IsEncounter) EncounterFrames.LagrangeDynamics(f.Id, t, out lag);
        // Warp N: relative motion N times faster, as the rails. In warp (and for a moment after) each member is
        // steered along its own Kepler orbit (see below); at x1 the differential gravity is integrated as ever.
        double N = Math.Max(1.0, SystemHost.Timescale);
        double fastest = 0; OrbitalGridComponent fastestG = null;
        // the game clock's step since this frame's last step (the steering's horizon)
        { double tnow = SystemHost.Now; _steerStep = _lastSteerNow.TryGetValue(f.Id, out double ln) && tnow > ln && tnow - ln < 10 * dt * N ? tnow - ln : 0; _lastSteerNow[f.Id] = tnow; }   // the largest physical speed a member needs this tick (N x its true relative speed)
        // what is joined to the anchor (docked, on its rotors and hinges): one body with it - never station-keeping on its
        // own (a docked ship with dampeners on skipped the relative pull while the joint dragged it: the anchor's thrust,
        // drained over the anchor's mass alone, counted double for a tug pushing a ship of its own mass)
        HashSet<Keen.VRage.DCS.Components.Entity> joined = null;
        if (anchor != null && anchorJointed)
        {
            // (the joint search once a second per anchor, not every tick)
            joined = JoinedSet(anchor, Wall());
        }
        else if (anchor != null) _joinedOf.Remove(anchor.Id);   // (no joints left - undocked, a hinge broken: no joined set kept)
        // The frame's stations (static grids): their boxes, grown by StationHoldMargin. A ship parked at one, or locked to
        // one, is not steered along its own orbit in warp - it keeps its place at the station. (Steered at N x its drift, or
        // teleported along its orbit in a frame nobody is in, ships went into Concordia at x1000 and broke a piece off it:
        // 2026-10-08, ~20 wreck pieces in one tick.)
        List<BoundingBoxD> stations = null;
        if (N > 1.0)
            foreach (var sg in grids)
            {
                if (sg.Entity == null || GridMembers.IsDynamic(sg)) continue;
                var sb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(sg.Entity.Data);
                if (!sb.HasValue) continue;
                var bb = sb.Value; bb.Inflate(StationHoldMargin);
                (stations ??= new List<BoundingBoxD>()).Add(bb);
            }
        foreach (var g in grids)
        {
            // Not this frame's any more (a split earlier this tick took it with its joined set into the new frame): not
            // judged here. Its queued position is already its new berth's - read as "6,731 km from its berth, moved by
            // something else", it was taken out of the frame it had just joined: a jointed ship left on no rails.
            if (!SEAerospace.Frames.MembershipRules.StillJudged(GridWorld.I, f.Id, g.Id)) continue;
            // (an anchor keeps no member's warp state: one left from before it anchored - its scale at x1 - multiplied the
            //  velocity a merge gave it at x1000 by 1000 again, once it was a member of the host)
            if (g == anchor) { _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id); continue; }
            if (EncounterFrames.IsNpc(g) && !DevNpcRelative) continue;
            if (stations != null && GridMembers.IsDynamic(g) && AtStation(g, stations))
            {
                // held at the station: no warp steering, no rescale - its velocity zeroed relative to the frame
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id); _vSet.Remove(g.Id);
                GridMembers.SetVelocity(g, Vector3D.Zero);
                continue;
            }
            Vector3D rRel = GridMembers.Position(g) - anchorPos;
            if (_gridN.TryGetValue(g.Id, out double n0) && Math.Abs(n0 - N) > 1e-9)
            {
                GridMembers.SetVelocity(g, GridMembers.Velocity(g) * (N / n0));
                _vSet.Remove(g.Id);   // (a rescale is no thrust)
                if (N <= 1.0 && n0 > 1.0) _settle[g.Id] = SettleTicks;   // (warp over: steer a moment longer)
            }
            _gridN[g.Id] = N;
            if (SEAerospace.Frames.PlayerOrbit.MovedOut(f, GridMembers.Position(g)))
            {
                // moved out by something else (fast travel): out of the frame quietly, framed again where it now is
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id);
                SystemHost.Frames.RemoveMember(g.Id);
                Event($"grid {g.Id} '{g.DisplayName}' left frame #{f.Id}: {(GridMembers.Position(g) - f.BerthCenter).Length() / 1000:F0} km from its berth (moved by something else)");
                continue;
            }
            if (rRel.Length() > LeaveRadius(isStatic))
            {
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id);
                SplitGrid(f, g, cur, rRel, GridMembers.Velocity(g) / N, t);
                continue;
            }
            Vector3D accel = lag != null ? lag(rRel, GridMembers.Velocity(g) / N) : (Grav(rA + rRel, mu) - gA) - A;
            // Station-keeping (dampeners on, and thrust on the side that cancels the pull): no relative force, as
            // SE1 and as a rider with dampeners on. Its dampeners only null its own motion: it holds exactly.
            // (judged against the TRUE pull at any warp, as the on-foot rider: warp compresses time, not the ship's authority -
            //  tested against N^2 x the pull, every ship let go of its station as warp rose, and its dampeners then fought the
            //  steering along its own orbit: it lagged the plot)
            if ((joined == null || !joined.Contains(g.Entity)) && StationKeeping(g, accel, N)) { _trueRel.Remove(g.Id); continue; }
            // In warp: along its own orbit, exactly. Integrating the N^2 forces in N*dt steps blew up near a low
            // periapsis (x1000: 16.7 s steps against ~30 s relative dynamics flung members at hundreds of m/s);
            // instead its true state (the frame's + its offset) goes N*dt along its own Kepler orbit, and it gets
            // the velocity that carries it there in this physics step. Its true relative velocity is kept here, not
            // read back from the physics (a velocity set as warp changes did not always hold), and the steering
            // goes on for SettleTicks after warp ends, so it leaves warp at its true speed.
            _settle.TryGetValue(g.Id, out int settle);
            // (a grid held by joints, to the anchor or to anything, cannot be steered: it holds with them)
            // A ship of several grids (rotors, hinges, wheels, docked craft): steered as ONE body - its heaviest grid works the
            // step along its own orbit, and every grid of it gets that velocity. (Skipped outright, it froze in warp while the
            // plot showed it drifting.) One joined to the anchor moves with the anchor: nothing to steer.
            List<OrbitalGridComponent> steerSet = null;
            if ((N > 1.0 || settle > 0) && GridMembers.IsConstrained(g))
            {
                steerSet = JoinedMembers(f, g, grids, _steerScratch);
                bool withAnchor = anchor != null && steerSet.Contains(anchor);
                OrbitalGridComponent root = g; double rm = GridMembers.Mass(g);
                foreach (var o in steerSet) { double m = GridMembers.Mass(o); if (m > rm) { rm = m; root = o; } }
                if (withAnchor || root != g) { _trueRel.Remove(g.Id); _settle.Remove(g.Id); _vSet.Remove(g.Id); continue; }
            }
            if ((N > 1.0 || settle > 0) && lag == null && IsFinite(rRel))
            {
                // Thrust or a bump since the last step (what physics made of the velocity set then): into the true relative
                // velocity, physical / N - kept as it was, a burn or a collision in warp never reached the orbit.
                if (_trueRel.TryGetValue(g.Id, out var kept0) && _vSet.TryGetValue(g.Id, out var vSet0))
                {
                    Vector3D dvPhys = GridMembers.Velocity(g) - vSet0;
                    if (IsFinite(dvPhys) && dvPhys.Length() < 50) _trueRel[g.Id] = kept0 + dvPhys / N;
                }
                Vector3D vRel = _trueRel.TryGetValue(g.Id, out var kept) ? kept : GridMembers.Velocity(g) / N;
                var mine = CaptureMath.CaptureElements(new StateVector(rA + rRel, cur.Velocity + vRel), mu, t);
                // (the clock's own step since the last: the client advances it, not always by exactly dt x N)
                double tn = t + (_steerStep > 0 ? _steerStep : dt * N);
                StateVector mineNext = OrbitPropagation.StateAt(mine, tn), frameNext = OrbitPropagation.StateAt(f.Elements, tn);
                Vector3D relNext = mineNext.Position - frameNext.Position;
                if (IsFinite(relNext))
                {
                    Vector3D vPhys = (relNext - rRel) / dt;
                    _trueRel[g.Id] = mineNext.Velocity - frameNext.Velocity;
                    // A frame you are not in (nobody to see it): a member too fast to carry in this warp is PLACED on its own
                    // orbit each tick, not pushed there - so a cluster drifting far away (the world's own ships, merged into
                    // one frame) no longer holds everyone's warp at x10. Yours keeps the physics' limit (you see its ships).
                    bool unseen = FrameHost.PlayerFrame == null || FrameHost.PlayerFrame.Id != f.Id;
                    // (judged on what the limiter judges - N x its true speed - not this tick's catch-up: the clock's step and the
                    //  physics tick differ, the catch-up came in under the line while N x speed was over it, and a frame you are
                    //  not in held your warp at x10 - the user, 2026-10-08: "my warp is mine alone")
                    if (unseen && (vPhys.Length() > WarpMemberSpeed || N * _trueRel[g.Id].Length() > 0.5 * WarpMemberSpeed)
                        && GridMembers.SetPosition(g, f.BerthCenter + relNext))
                    {
                        GridMembers.SetVelocity(g, Vector3D.Zero);
                        if (settle > 0) { if (--settle == 0) { _settle.Remove(g.Id); _trueRel.Remove(g.Id); } else _settle[g.Id] = settle; }
                        continue;
                    }
                    GridMembers.SetVelocity(g, vPhys);
                    _vSet[g.Id] = vPhys;
                    if (steerSet != null) foreach (var o in steerSet) if (o != g) GridMembers.SetVelocity(o, vPhys);   // (the whole ship)
                    if (!unseen && N * _trueRel[g.Id].Length() > fastest) { fastest = N * _trueRel[g.Id].Length(); fastestG = g; }   // (its own orbit's speed, not a catch-up)
                    if (settle > 0) { if (--settle == 0) { _settle.Remove(g.Id); _trueRel.Remove(g.Id); } else _settle[g.Id] = settle; }
                    continue;
                }
            }
            _trueRel.Remove(g.Id); _vSet.Remove(g.Id);
            // At x1 nothing is steered, and its velocity IS its true relative one: judged as the steering would judge it in
            // warp (the same members - not an encounter's, not one off the chart), so a raise from x1 knows it too.
            if (N <= 1.0 && lag == null && IsFinite(rRel) && FrameHost.PlayerFrame is ProximityFrame pf1 && pf1.Id == f.Id)
            {
                double v1 = GridMembers.Velocity(g).Length();
                if (IsFinite(v1) && v1 > fastest) { fastest = v1; fastestG = g; }
            }
            if (IsFinite(accel)) GridMembers.AddVelocity(g, accel * (dt * N * N));
        }
        // Warp is limited by what the physics can carry: a member moving relative to you needs N times its speed,
        // and the world caps speed (as KSP limits warp by circumstance).
        if (N > 1.0 && fastest > WarpMemberSpeed) { _fastestName = $"grid {fastestG?.Id} '{fastestG?.DisplayName}' in frame #{f.Id}"; LimitWarp(fastest / N); }
        // (and left for a raise to read: your frame only - the unseen ones never limit)
        if (FrameHost.PlayerFrame is ProximityFrame pf2 && pf2.Id == f.Id) PublishFastest(f.Id, fastest / N, fastestG);

        // encounter frames never arrive; a player-anchored frame with no grids arrives client-side - with grids in it, here
        // (the client moved only the player and left the ships at the berth with no frame)
        if (f.IsEncounter || (anchor == null && !isStatic && grids.Count == 0)) return;
        FrameHost.TryReparent(f, t);
        // A static anchor's frame arrives too (its orbit meets the planet): everything in it drops into
        // the planet's space, static grids placed at their offsets (an asteroid itself stays).
        // (only grids still this frame's: one split off or let go earlier this tick has its queued position at another berth -
        //  carried along, it landed 100+ km off in the planet's space)
        var all = _frameAll; all.Clear();   // (kept: one frame at a time - TryMaterializeGrids keeps nothing of it)
        foreach (var g in grids) if (SEAerospace.Frames.MembershipRules.StillJudged(GridWorld.I, f.Id, g.Id)) all.Add(g);
        if (isStatic)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent sg && sg.IsServer && !GridMembers.IsDynamic(sg)) all.Add(sg);
        TryMaterializeGrids(f, all, anchor != null ? GridMembers.Position(anchor) : isStatic ? staticPos : f.BerthCenter, t);   // (player-anchored: the berth is the player)
    }

    private static Vector3D Grav(Vector3D r, double mu)
    {
        double d = r.Length();
        return d > 1 ? r * (-mu / (d * d * d)) : Vector3D.Zero;
    }

    /// <summary>SE1 ExecuteSplits: the member becomes its own frame from its celestial state.</summary>
    private static void SplitGrid(ProximityFrame f, OrbitalGridComponent g, StateVector cur, Vector3D rRel, Vector3D vRel, double t)
    {
        long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vRel), f.Elements.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.SplitOff(f, g.Id, f.ParentBodyName, el);
        if (nf == null) return;
        long s1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Vector3D p = GridMembers.Position(g);
        Vector3D vShip = GridMembers.Velocity(g);
        // You on foot inside it (or on it: its box, 30 m to spare): you go with it - the same move, the same change of
        // velocity, into its new frame. (Left behind, the ship vanished around you and you split off alone.)
        bool carryPlayer = false;
        if (f.HasMember(FrameHost.PlayerId) && !FrameHost.Seated && ServerPlanetBeacon.PlayerState(out Vector3D ppos, out _))
        {
            var bx = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data);
            if (bx.HasValue) { var bb = bx.Value; bb.Inflate(30); carryPlayer = bb.Contains(ppos); }
        }
        if (!MoveGrid(g, nf.BerthCenter, Vector3D.Zero, out var withG)) { SystemHost.Frames.Dissolve(nf.Id); SystemHost.Frames.AddMember(f, g.Id); return; }
        long s2 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Joined to others (docked, wheels on a sub-grid): they go with it, into its new frame.
        // (MembershipRules.SplitTakes: the old frame's grids of the ship, and its grids in no frame - left out, on no rails)
        var withIds = new List<long>(); foreach (var o in withG) withIds.Add(o.Id);
        foreach (long oid in SEAerospace.Frames.MembershipRules.SplitTakes(GridWorld.I, f.Id, g.Id, withIds))
        {
            if (SystemHost.Frames.FindByMember(oid) == f) SystemHost.Frames.RemoveMember(oid);
            SystemHost.Frames.AddMember(nf, oid);
        }
        if (carryPlayer)
        {
            FrameHost.RequestMerge(f.Id, nf.BerthCenter - p, -vShip);   // (the player's move: requested while still in the old frame)
            SystemHost.Frames.RemoveMember(FrameHost.PlayerId); SystemHost.Frames.AddMember(nf, FrameHost.PlayerId);
            Event($"SPLIT: the player, on foot in grid {g.Id}, goes with it to frame #{nf.Id}");
        }
        Event($"SPLIT grid {g.Id} '{g.DisplayName}' from frame #{f.Id} at {rRel.Length() / 1000:F1} km -> frame #{nf.Id} (slot {nf.BerthSlotId})");
        SpawnGuard.Refresh();   // (its new berth closed to encounters now if nobody is in it: one spawned there in the next tick)
        long s3 = System.Diagnostics.Stopwatch.GetTimestamp(); double ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms(s0, s3) > 30) Event($"split of grid {g.Id} took {ms(s0, s3):F0} ms (frame {ms(s0, s1):F0}, move {ms(s1, s2):F0}, members and guard {ms(s2, s3):F0})");
    }
}
