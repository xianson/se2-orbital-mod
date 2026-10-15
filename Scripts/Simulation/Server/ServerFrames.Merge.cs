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

// ServerFrames (partial): the merge screen - two frames on rendezvous orbits merge (SE1 ScreenMerges / ExecuteMerge).
public static partial class ServerFrames
{
    // ───────────────────────────── merge (SE1 ScreenMerges / ExecuteMerge) ─────────────────────────────

    public const int MergeScreenInterval = 30;       // server ticks between screens (SE1 SeamCheckInterval)
    public const double TcaHorizonSeconds = 1800.0;  // SE1: 30 min look-ahead
    public const int TcaSamples = 48;
    public static readonly RendezvousParams Rendezvous = RendezvousParams.Default;
    private static readonly Dictionary<(long, long), RendezvousTracker> _trackers = new Dictionary<(long, long), RendezvousTracker>();

    /// <summary>
    /// Merge is a rendezvous (SE1): per same-SOI pair, the closest approach over the next 30 min
    /// decides whether to feed the sticky tracker (miss below 10 km and rel speed below 1000 m/s) or
    /// starve it (fly-by). A latched pair merges on the first screen where it is ALSO close now
    /// (inside the enter range) and slow now (below the physics cap), so an incomer never coasts out of the berth.
    /// </summary>
    // THE MERGE SCREEN, its slow part off FramesLock (2026-10-13): each pair's closest approach (ClosestApproach.Find, 48
    // samples over 30 min) ran inside the lock - 50-70 ms every half second with 73 frames, and the client's frame waits on
    // that lock while seated (FrameHost): the whole screen froze for it. Now, under the lock, this tick's share of the pairs
    // is listed (their elements copied) and last tick's answers applied; the approaches are found between the two, unlocked.
    // Each pair is still screened once per MergeScreenInterval ticks, in its own tick of the round (by its ids).
    struct ScreenJob { public long A, B; public KeplerianElements EA, EB; public double T; public ApproachEvent Ev; }
    static readonly List<ProximityFrame> _screenFrames = new List<ProximityFrame>();
    static readonly List<(long, long)> _screenDead = new List<(long, long)>();
    // (handed between the three as whole lists: Collect queues its list, Compute drains the queue and hands its results back
    //  under FramesLock, Apply drains them under it - an overlapping second tick never shares a list, nor loses one)
    static readonly System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>> _screenJobs = new System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>>();
    static readonly List<ScreenJob> _screenDone = new List<ScreenJob>();
    static readonly System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>> _screenLists = new System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>>();   // (a pool; ConcurrentBag is not whitelisted)

    /// <summary>Under FramesLock: this tick's pairs to screen, their elements copied (cheap filters only).</summary>
    // (each frame's own facts - finite elements, radial band, epoch passed, a site, anyone non-NPC in it - found once per
    //  listing, not once per pair: HasNonNpc walked a frame's members for every pair it was in, 2026-10-15. Under FramesLock:
    //  no frame changes during the listing. The filters are the same ones, cheapest first - all are pure, their order is free)
    static readonly List<byte> _screenFacts = new List<byte>();
    static readonly List<double> _screenPe = new List<double>(), _screenAp = new List<double>();
    const byte FactFinite = 1, FactBand = 2, FactMutable = 4, FactSite = 8, FactNonNpcKnown = 16, FactNonNpc = 32;
    static bool ScreenNonNpc(int i, ProximityFrame f)
    {
        byte x = _screenFacts[i];
        if ((x & FactNonNpcKnown) == 0) { x |= FactNonNpcKnown; if (EncounterFrames.HasNonNpc(f)) x |= FactNonNpc; _screenFacts[i] = x; }
        return (x & FactNonNpc) != 0;
    }
    private static void ScreenMergesCollect(int phase)
    {
        List<ScreenJob> jobs = null;
        double t = SystemHost.Now;
        var frames = _screenFrames; frames.Clear(); frames.AddRange(SystemHost.Frames.Frames);   // (reused: this runs every tick)
        double er = Rendezvous.EnterRangeMeters;
        var facts = _screenFacts; facts.Clear(); _screenPe.Clear(); _screenAp.Clear();
        bool screenAll = !EncounterFrames.Enabled;   // (EncounterFrames.ShouldScreen: encounters off, every pair is screened)
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            byte x = 0;
            if (Finite(f.Elements)) x |= FactFinite;
            if (RadialBand(f.Elements, out double pe, out double ap)) x |= FactBand;
            if (FrameRails.CanMutateAt(f.Elements, t)) x |= FactMutable;
            if (!screenAll && EncounterFrames.IsSite(f)) x |= FactSite;
            facts.Add(x); _screenPe.Add(pe); _screenAp.Add(ap);
        }
        for (int a = 0; a < frames.Count; a++)
        {
            var fa = frames[a];
            byte xa = facts[a];
            if ((xa & FactFinite) == 0) continue;
            bool bandA = (xa & FactBand) != 0;
            double peA = _screenPe[a], apA = _screenAp[a];
            for (int b = a + 1; b < frames.Count; b++)
            {
                var fb = frames[b];
                byte xb = facts[b];
                if ((xb & FactFinite) == 0) continue;
                if (fa.ParentBodyName != fb.ParentBodyName) continue;
                if ((int)(((ulong)(fa.Id < fb.Id ? fa.Id : fb.Id) * 7919UL + (ulong)(fa.Id < fb.Id ? fb.Id : fa.Id)) % (ulong)MergeScreenInterval) != phase) continue;
                if (bandA && (xb & FactBand) != 0 && (apA + er < _screenPe[b] || _screenAp[b] + er < peA)) continue;
                if ((xa & FactMutable) == 0 || (xb & FactMutable) == 0) continue;
                // (EncounterFrames.ShouldScreen, on the facts: two sites never; else anyone non-NPC in either)
                if (!screenAll && (((xa & xb & FactSite) != 0) || !(ScreenNonNpc(a, fa) || ScreenNonNpc(b, fb)))) continue;
                (jobs ??= (_screenLists.TryDequeue(out var pooled) ? pooled : new List<ScreenJob>())).Add(new ScreenJob { A = Math.Min(fa.Id, fb.Id), B = Math.Max(fa.Id, fb.Id),
                    EA = fa.Id < fb.Id ? fa.Elements : fb.Elements, EB = fa.Id < fb.Id ? fb.Elements : fa.Elements, T = t });
            }
        }
        if (jobs != null) _screenJobs.Enqueue(jobs);
    }

    /// <summary>OFF FramesLock: the listed pairs' closest approaches (the slow part), into the done list.</summary>
    private static void ScreenMergesCompute()
    {
        while (_screenJobs.TryDequeue(out var jobs))
        {
            (_screenNowT ??= new Dictionary<long, StateVector>()).Clear();   // (one listing: one time, one element set per frame)
            (_screenKeptT ??= new Dictionary<long, (KeplerianElements, StateVector)>()).Clear();   // (one time: the kept copies' states at it)
            for (int i = 0; i < jobs.Count; i++)
            {
                var d = jobs[i];
                d.Ev = Approach(d);
                jobs[i] = d;
            }
            lock (FramesLock) _screenDone.AddRange(jobs);
            jobs.Clear(); _screenLists.Enqueue(jobs);   // (back to the pool: a new list per screen tick before)
        }
    }

    // A PAIR'S APPROACH, KEPT (a profile, 2026-10-14: the screen's approaches were a tenth of a core - every pair's 30 min
    // look-ahead found anew every half second, two coasting orbits giving the same answer each time). Reused while both
    // orbits are the same ones (SameOrbit: the shape and where along it - a re-epoched copy is the same orbit), the answer
    // a clear fly-by (no approach, or a miss over ReuseMissRanges enter ranges), its approach still ahead, and no older
    // than ReuseSeconds. A pair that may merge, or whose orbit changed (someone thrusting), is found anew every screen.
    // (SameOrbit: two Kepler solves an orbit, against the screen's ~100)
    public static double ScreenReuseSeconds = 30.0, ScreenReuseMissRanges = 4.0;
    static readonly Dictionary<(long, long), ScreenJob> _screenKept = new Dictionary<(long, long), ScreenJob>();
    static double _screenKeptSwept;
    public static int ScreenFound, ScreenReused, ScreenNew, ScreenOld, ScreenClose, ScreenPassed, ScreenMoved, ScreenApart;
    public static string ScreenStats => $"found {ScreenFound} reused {ScreenReused} | new {ScreenNew} old {ScreenOld} close {ScreenClose} passed {ScreenPassed} moved {ScreenMoved} | proven apart {ScreenApart}";
    /// <summary>The most steps (two state solves each, against Find's ~1500) the fly-by proof may take before Find runs.</summary>
    public static int ScreenApartSteps = 48;

    static ApproachEvent Approach(in ScreenJob d)
    {
        lock (_screenKept)
        {
            if (!_screenKept.TryGetValue((d.A, d.B), out var k)) ScreenNew++;
            else if (!(d.T - k.T < ScreenReuseSeconds && d.T >= k.T)) ScreenOld++;
            else if (k.Ev.Found && !(k.Ev.MissDistance > ScreenReuseMissRanges * Rendezvous.EnterRangeMeters)) ScreenClose++;
            // (an approach at the window's very start is a pair moving apart - its closest is now: kept as long as its orbits
            //  are, its minimum can only change by the horizon's end, ScreenReuseSeconds later, half an hour ahead)
            else if (k.Ev.Found && !(k.Ev.Time > d.T) && !(k.Ev.Time <= k.T + 1.0)) ScreenPassed++;
            else if (!SameOrbit(k.EA, d.EA, d.T, d.A) || !SameOrbit(k.EB, d.EB, d.T, d.B)) ScreenMoved++;
            else { ScreenReused++; return k.Ev; }
        }
        // A PROVEN FLY-BY (2026-10-15): when the two cannot come within the enter range anywhere in the window - the separation
        // changes no faster than their two top speeds (ClosestApproach.CertainlyApart, tested offline against a dense search and
        // Find) - Find could only answer a miss of at least that range, which ScreenMergesApply treats exactly as no approach:
        // its tracker starved, no merge-ahead noted. So it answers "none" without the search. Not kept: the next screen asks
        // anew (the proof, or Find - never an answer older than this screen).
        if (ClosestApproach.CertainlyApart(d.EA, d.EB, d.T, TcaHorizonSeconds, Rendezvous.EnterRangeMeters, ScreenApartSteps))
        {
            System.Threading.Interlocked.Increment(ref ScreenApart);
            return ApproachEvent.None;
        }
        var ev = ClosestApproach.Find(d.EA, d.EB, d.T, TcaHorizonSeconds, TcaSamples);
        ScreenFound++;
        lock (_screenKept)
        {
            var kept = d; kept.Ev = ev; _screenKept[(d.A, d.B)] = kept;
            if (d.T - _screenKeptSwept > 120.0)
            {
                _screenKeptSwept = d.T;
                var old = new List<(long, long)>();
                foreach (var kv in _screenKept) if (d.T - kv.Value.T > 120.0) old.Add(kv.Key);
                foreach (var key in old) _screenKept.Remove(key);
            }
        }
        return ev;
    }

    /// <summary>The same orbit as far as the screen can tell: where the two put the frame now, and how fast, agree to
    /// SameOrbitMeters and SameOrbitMps. (Elements themselves differ from tick to tick for a frame re-osculated every tick
    /// - a Lagrange ride, a site - so they were compared by the state they give, at the time of the screen.)</summary>
    static bool SameOrbit(in KeplerianElements x, in KeplerianElements y, double t, long yId)
    {
        if (x.Mu != y.Mu) return false;
        var sx = KeptNow(yId, x, t); var sy = ListedNow(yId, y, t);
        if (!IsFinite(sx.Position) || !IsFinite(sy.Position) || !IsFinite(sx.Velocity) || !IsFinite(sy.Velocity)) return false;
        return (sx.Position - sy.Position).LengthSquared() < SameOrbitMeters * SameOrbitMeters
            && (sx.Velocity - sy.Velocity).LengthSquared() < SameOrbitMps * SameOrbitMps;
    }
    public static double SameOrbitMeters = 20.0, SameOrbitMps = 0.02;

    // A FRAME'S LISTED STATE, ONCE PER SCREEN (2026-10-15: SameOrbit solved each pair's current orbits anew - a frame in 70 pairs
    // was solved 70 times at the same time on the same elements). One listing (ScreenMergesCollect) copies each frame's elements
    // once, at one time: every job of a list has the same T and the same elements for a frame id, so its state at T is kept by
    // id for that list. Per thread: two overlapping ticks may compute at once.
    [ThreadStatic] static Dictionary<long, StateVector> _screenNowT;
    // (and the KEPT elements' state at the listing's time, by frame id: pairs kept at the same screen hold the same copy of a
    //  frame's elements - SameOrbit solved it once per pair. Reused only for bit-identical elements: StateAt is a pure function
    //  of the elements and the time, and every job of a list has the same time)
    [ThreadStatic] static Dictionary<long, (KeplerianElements el, StateVector s)> _screenKeptT;
    static bool SameBits(in KeplerianElements a, in KeplerianElements b) =>
        BitConverter.DoubleToInt64Bits(a.SemiMajorAxis) == BitConverter.DoubleToInt64Bits(b.SemiMajorAxis)
        && BitConverter.DoubleToInt64Bits(a.Eccentricity) == BitConverter.DoubleToInt64Bits(b.Eccentricity)
        && BitConverter.DoubleToInt64Bits(a.Inclination) == BitConverter.DoubleToInt64Bits(b.Inclination)
        && BitConverter.DoubleToInt64Bits(a.Raan) == BitConverter.DoubleToInt64Bits(b.Raan)
        && BitConverter.DoubleToInt64Bits(a.ArgPeriapsis) == BitConverter.DoubleToInt64Bits(b.ArgPeriapsis)
        && BitConverter.DoubleToInt64Bits(a.TrueAnomaly) == BitConverter.DoubleToInt64Bits(b.TrueAnomaly)
        && BitConverter.DoubleToInt64Bits(a.Mu) == BitConverter.DoubleToInt64Bits(b.Mu)
        && BitConverter.DoubleToInt64Bits(a.Epoch) == BitConverter.DoubleToInt64Bits(b.Epoch);
    static StateVector KeptNow(long id, in KeplerianElements el, double t)
    {
        var c = _screenKeptT;
        if (c == null || _screenNowT == null) return OrbitPropagation.StateAt(el, t);
        if (c.TryGetValue(id, out var k) && SameBits(k.el, el)) return k.s;
        var s = OrbitPropagation.StateAt(el, t);
        c[id] = (el, s);
        return s;
    }
    static StateVector ListedNow(long id, in KeplerianElements el, double t)
    {
        var c = _screenNowT;
        if (c == null) return OrbitPropagation.StateAt(el, t);
        if (c.TryGetValue(id, out var s)) return s;
        s = OrbitPropagation.StateAt(el, t);
        c[id] = s;
        return s;
    }

    /// <summary>Under FramesLock: last tick's approaches judged - the rendezvous trackers, the merges (on the frames as they
    /// are NOW: a pair gone or changed since is judged on its current state, ExecuteMerge's own checks unchanged).</summary>
    private static void ScreenMergesApply(double dt, int phase)
    {
        double t = SystemHost.Now;
        double er = Rendezvous.EnterRangeMeters;
        foreach (var j in _screenDone)
        {
            var fa = SystemHost.Frames.Get(j.A); var fb = SystemHost.Frames.Get(j.B);
            if (fa == null || fb == null) continue;   // (merged or dissolved since)
            // (the screen's own filters again, on the frames as they are now: a reparent or a new epoch since the listing)
            if (fa.ParentBodyName != fb.ParentBodyName || !EncounterFrames.ShouldScreen(fa, fb)
                || !FrameRails.CanMutateAt(fa.Elements, t) || !FrameRails.CanMutateAt(fb.Elements, t)) continue;
            var ev = j.Ev;
            bool ok = ev.Found && IsFinite(ev.MissDistance) && IsFinite(ev.RelativeSpeed);
            double miss = ok ? ev.MissDistance : double.PositiveInfinity;
            double rel = ok ? ev.RelativeSpeed : double.PositiveInfinity;
            var key = (j.A, j.B);
            if (!_trackers.TryGetValue(key, out var tr)) { tr = new RendezvousTracker(); _trackers[key] = tr; }
            if (miss < er && rel < Rendezvous.EnterRelSpeedMps && ev.Time > t && FrameHost.PlayerFrame != null
                && (FrameHost.PlayerFrame.Id == fa.Id || FrameHost.PlayerFrame.Id == fb.Id))
                NoteMergeAhead(ev.Time);
            bool merged = miss < er && rel < Rendezvous.EnterRelSpeedMps
                ? tr.Update(miss, rel, dt, Rendezvous)
                : tr.Update(double.PositiveInfinity, double.PositiveInfinity, dt, Rendezvous);
            if (!merged) continue;
            if (!Finite(fa.Elements) || !Finite(fb.Elements)) continue;
            var sa = OrbitPropagation.StateAt(fa.Elements, t);
            var sb = OrbitPropagation.StateAt(fb.Elements, t);
            if (!IsFinite(sa.Position) || !IsFinite(sb.Position) || !IsFinite(sa.Velocity) || !IsFinite(sb.Velocity)) continue;
            if ((sb.Position - sa.Position).Length() >= er) continue;
            if ((sb.Velocity - sa.Velocity).Length() >= FrameHost.SpeedCap) continue;
            ExecuteMerge(fa, fb, sa, sb);
            _trackers.Remove(key);
        }
        _screenDone.Clear();
        if (phase != 0) return;   // (the dead pairs swept once a round)
        var dead = _screenDead; dead.Clear();
        foreach (var k in _trackers.Keys) if (SystemHost.Frames.Get(k.Item1) == null || SystemHost.Frames.Get(k.Item2) == null) dead.Add(k);
        foreach (var k in dead) _trackers.Remove(k);
    }

    static readonly Dictionary<long, double> _mergeRefused = new Dictionary<long, double>();
    static readonly HashSet<long> _rebasePending = new HashSet<long>();
    static readonly Dictionary<long, double> _lastSteerNow = new Dictionary<long, double>();
    static double _steerStep;

    /// <summary>A frame's orbit stays clear of its planet's border (periapsis over the frame border with 5 km to spare, and
    /// bound - an escape trajectory is no place for a station either). The star: always stable.</summary>
    internal static bool OrbitStable(ProximityFrame f)
    {
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        var node = SystemHost.Registry?.Find(f.ParentBodyName);
        if (def == null || node == null || node.IsRoot || string.IsNullOrEmpty(def.ParkSubtype)) return true;
        // (the rule itself is game-free: MembershipRules.OrbitStable, tested offline)
        return SEAerospace.Frames.MembershipRules.OrbitStable(f.Elements, PlanetBerths.ShellRadius(def));
    }
    private static void ExecuteMerge(ProximityFrame fa, ProximityFrame fb, StateVector sa, StateVector sb)
    {
        ProximityFrame host, inc; StateVector sHost, sInc;
        int pa = EncounterFrames.HostPriority(fa), pb = EncounterFrames.HostPriority(fb);
        if (pa > pb || (pa == pb && FrameMass(fa) >= FrameMass(fb))) { host = fa; inc = fb; sHost = sa; sInc = sb; }
        else { host = fb; inc = fa; sHost = sb; sInc = sa; }

        // Anchors sit at their berths (pinned), so the incomer moves by berth-to-berth plus the
        // celestial separation, and gains the celestial relative velocity. Members keep their own
        // offsets and velocities relative to their old anchor.
        Vector3D dPos = sInc.Position - sHost.Position;
        Vector3D dVel = sInc.Velocity - sHost.Velocity;
        Vector3D translation = host.BerthCenter + dPos - inc.BerthCenter;
        if (!IsFinite(translation) || !IsFinite(dVel)) return;
        // (in warp a member's physical velocity is N x its true one - the gridN rescale: the true relative velocity added
        //  unscaled was cut by N at the merge, a free braking)
        Vector3D dVelPhys = dVel * Math.Max(1.0, SystemHost.Timescale);
        // Called off, before anything moves, when it could not be done whole: a member (or you) would land past the host's
        // edge (split straight back out), the incomer holds a static grid (it cannot move), or a ship of it cannot be moved
        // (joined to something static). A merge half done stranded ships at a freed berth.
        string why = null;
        Vector3D hostAt = StaticAnchorOf(host, out Vector3D hsa) ? hsa : host.BerthCenter;
        foreach (long id in inc.Members)
        {
            if (!GridMembers.IsGridId(id))
            {
                if (id == FrameHost.PlayerId && ServerPlanetBeacon.PlayerState(out Vector3D pp, out _) && (pp + translation - hostAt).Length() > SlotRadius - 500) { why = "you would land past the host's edge"; break; }
                continue;
            }
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer) continue;
            if (!GridMembers.IsDynamic(g)) { why = $"it holds a static grid ({g.DisplayName})"; break; }
            if ((GridMembers.Position(g) + translation - hostAt).Length() > SlotRadius - 500) { why = $"'{g.DisplayName}' would land past the host's edge"; break; }
            if (GridMembers.IsConstrained(g) && !GridMembers.MoveSet(g, new List<Keen.VRage.DCS.Components.Entity>(), out string mw)) { why = $"'{g.DisplayName}' cannot be moved ({mw})"; break; }
        }
        if (why != null)
        {
            long key = Math.Min(host.Id, inc.Id) * 100000 + Math.Max(host.Id, inc.Id);
            if (!_mergeRefused.TryGetValue(key, out double at) || Wall() - at > 60) { _mergeRefused[key] = Wall(); Event($"MERGE frame #{inc.Id} -> #{host.Id} called off: {why}"); }
            return;
        }
        int n = 0;
        var went = new HashSet<long>();   // (moved with an earlier member's joined set: not moved twice)
        foreach (long id in new List<long>(inc.Members))
        {
            if (!GridMembers.IsGridId(id) || went.Contains(id)) continue;
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer) continue;
            // Its true relative velocity in the host, kept for the warp steering (as every member's in warp): in physics it is
            // N x that, and the game caps physical speed near 1000 m/s - a merge at x1000 with 8.5 m/s between the frames
            // asked 8500 m/s and got ~980, a ship cut to a ninth of its speed, 2.7 km off its orbit after.
            double nm = Math.Max(1.0, SystemHost.Timescale);
            Vector3D trueOld = _trueRel.TryGetValue(g.Id, out var kt) ? kt : GridMembers.Velocity(g) / nm;
            MoveGrid(g, GridMembers.Position(g) + translation, null, out var withM, dVelPhys);
            foreach (var w in withM) went.Add(w.Id);
            // (its velocity is in this warp's scale now: recorded so, for the rescale when the warp changes)
            _gridN[g.Id] = nm; _settle.Remove(g.Id); _vSet.Remove(g.Id);
            if (nm > 1.0) _trueRel[g.Id] = trueOld + dVel; else _trueRel.Remove(g.Id);
            foreach (var w in withM) { _gridN[w.Id] = nm; _trueRel.Remove(w.Id); _settle.Remove(w.Id); }
            n++;
        }
        FrameHost.RequestMerge(inc.Id, translation, dVelPhys);   // the player, if a member of the incomer
        long hostId = host.Id, incId = inc.Id;
        SystemHost.Frames.MergeInto(host, inc);
        AnchorAccel.Remove(incId);
        Event($"MERGE frame #{incId} -> #{hostId}: {n} grid(s) moved, sep {dPos.Length() / 1000:F2} km, |dv| {dVel.Length():F1} m/s (host now {host.Members.Count} members)");
    }

    /// <summary>When the player's frame next meets another closely enough to merge (NaN: none seen): warp stops a minute
    /// before it (SystemHost.AdvanceClock), so the pass is screened at x1 - in warp the screens are hundreds of game seconds
    /// apart and a merge went by between two of them.</summary>
    public static double MergeAhead => System.Threading.Volatile.Read(ref _mergeAhead);
    private static double _mergeAhead = double.NaN;
    private static void NoteMergeAhead(double tca)
    {
        double cur = System.Threading.Volatile.Read(ref _mergeAhead);
        if (double.IsNaN(cur) || cur < SystemHost.Now || tca < cur) System.Threading.Volatile.Write(ref _mergeAhead, tca);
    }
    public const double MergeWarpLead = 60.0;

    private static double FrameMass(ProximityFrame f)
    {
        double m = 0;
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) { m += 100; continue; }   // a character
            var g = GridMembers.Get(id);
            if (g != null) m += GridMembers.Mass(g);
        }
        return m;
    }

    private static bool RadialBand(KeplerianElements el, out double rPe, out double rAp)
    {
        rPe = rAp = 0;
        double a = el.SemiMajorAxis, e = el.Eccentricity;
        if (!IsFinite(a) || a <= 0 || !IsFinite(e) || e < 0 || e >= 1) return false;
        rPe = a * (1 - e); rAp = a * (1 + e);
        return true;
    }

    private static bool Finite(KeplerianElements el) => IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion);
}
