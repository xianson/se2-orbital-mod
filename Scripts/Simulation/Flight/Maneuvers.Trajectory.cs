using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ───────────────────────────── trajectory ─────────────────────────────

    public struct Leg { public GravityBody Body; public KeplerianElements El; public double T0, T1; public bool Planned; public bool Impact; }   // (Impact: T1 is where it meets the surface - CutAtImpact)
    public struct Applied { public Node Node; public GravityBody Body; public StateVector Before; public Vector3D Dv; public KeplerianElements After; }

    /// <summary>The player's orbit now: the rails frame's - your own in it while you ride a frame something else
    /// anchors (its orbit plus your offset and velocity: up to 20 km from its centre) - or the local orbit in a planet cell.</summary>
    public static bool Base(double t, out GravityBody body, out KeplerianElements el)
    {
        body = null; el = default;
        var reg = SystemHost.Registry;
        if (reg == null) return false;
        var f = FrameHost.PlayerFrame;
        if (f != null)
        {
            // (the frame's body and orbit copied under the frames lock: the server writes them every tick - a torn read seeded
            //  the planner's own orbit with half of the old orbit)
            string fb; KeplerianElements fe;
            lock (ServerFrames.FramesLock) { fb = f.ParentBodyName; fe = f.Elements; }
            body = reg.Find(fb); el = fe;
            if (FrameHost.RiderFrame == f.Id && !EncounterFrames.IsLagrangeSite(f))
            {
                var obs = FrameHost.Observer;
                Vector3D off = obs.HasValue ? SEAerospace.PlanetBerths.SpinToCelestial(obs.Value, FrameHost.RiderOffset) : FrameHost.RiderOffset;
                Vector3D vel = obs.HasValue ? SEAerospace.PlanetBerths.SpinToCelestial(obs.Value, FrameHost.RiderVelocity) : FrameHost.RiderVelocity;
                lock (_own) el = _own.Get(f.Id, fe, off, vel, t);
            }
            return body != null;
        }
        string b = FrameHost.ObserverPlanet;
        if (b == null || !FrameHost.TryGetLocalOrbit(b, t, out el)) return false;
        body = reg.Find(b);
        return body != null;
    }

    /// <summary>Your own orbit while riding (kept while it still says where you are: the plan is not re-solved every tick).</summary>
    static readonly SEAerospace.Frames.RendezvousPlot.OwnOrbit _own = new SEAerospace.Frames.RendezvousPlot.OwnOrbit();

    /// <summary>Your own orbit as the HUD draws it while riding (OrbitDisplay): the same smoothed fit as the plan's (the
    /// drawn orbit and its drift plot were a fresh capture of each tick's noisy measurement), kept on its own (the HUD may
    /// draw a frame the plan is not on - DevRider - and two keys in one fit would restart it every call). False: no orbit.</summary>
    public static bool OwnOrbitDrawn(long frameId, KeplerianElements frameEl, Vector3D offCelestial, Vector3D velCelestial, double t, out KeplerianElements el)
    {
        lock (_ownDrawn) return _ownDrawn.TryGet(frameId, frameEl, offCelestial, velCelestial, t, out el);
    }
    static readonly SEAerospace.Frames.RendezvousPlot.OwnOrbit _ownDrawn = new SEAerospace.Frames.RendezvousPlot.OwnOrbit();

    /// <summary>The prograde / normal / radial-out axes at a state.</summary>
    public static void Axes(StateVector s, out Vector3D P, out Vector3D N, out Vector3D R)
    {
        P = s.Velocity.LengthSquared() > 1e-9 ? Vector3D.Normalize(s.Velocity) : Vector3D.UnitY;
        Vector3D h = Vector3D.Cross(s.Position, s.Velocity);
        N = h.LengthSquared() > 1e-9 ? Vector3D.Normalize(h) : Vector3D.UnitZ;
        R = Vector3D.Cross(P, N);
    }

    /// <summary>The whole trajectory from t: legs (patched), and each node as applied.</summary>
    private static double _cT = double.NaN; private static long _cSig; private static List<Leg> _cLegs; private static List<Applied> _cApplied; private static bool _cOk;

    /// <summary>The trajectory, memoised: the map, the HUD and warp ask for it every frame.</summary>
    public static bool Trajectory(double t, out List<Leg> legs, out List<Applied> applied)
    {
        long sig = Signature(t);
        // (reused for 0.25 s of sim time while nothing changed - the orbit and the nodes are in the signature: keyed on the
        //  exact time it never hit, and the whole patched trajectory was re-solved every frame a node existed)
        if (sig == _cSig && _cLegs != null && t >= _cT && t - _cT < 0.25) { legs = _cLegs; applied = _cApplied; return _cOk; }
        _cOk = TrajectoryUncached(t, out legs, out applied);
        if (_cOk) CutAtImpact(legs, applied);
        _cT = t; _cSig = Signature(t); _cLegs = legs; _cApplied = applied;   // after: re-targeting clears Dirty
        return _cOk;
    }

    /// <summary>
    /// A path that hits a body ends there: the leg is cut at the impact and nothing follows (it drew a
    /// 'Palatine Escape' after the impact). The current orbit on the ground is handled elsewhere.
    /// </summary>
    private static void CutAtImpact(List<Leg> legs, List<Applied> applied)
    {
        for (int i = 0; i < legs.Count; i++)
        {
            var l = legs[i];
            double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
            if (!(R > 0) || !(l.El.PeriapsisRadius < R) || !(l.T1 > l.T0)) continue;
            double span = l.T1 - l.T0, prev = l.T0, hit = double.NaN;
            if (OrbitPropagation.StateAt(l.El, l.T0).Position.Length() < R) continue;   // starts inside: leave it
            // At least 64 samples a revolution (a long leg spans many; one dip is a small part of each).
            int n = 240;
            if (l.El.IsElliptic && IsFinite(l.El.Period) && l.El.Period > 0) n = (int)Math.Min(20000, Math.Max(240, span / l.El.Period * 64));
            for (int k = 1; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                if (OrbitPropagation.StateAt(l.El, tk).Position.Length() < R)
                {
                    double a = prev, b = tk;
                    for (int q = 0; q < 30; q++) { double m = 0.5 * (a + b); if (OrbitPropagation.StateAt(l.El, m).Position.Length() < R) b = m; else a = m; }
                    hit = 0.5 * (a + b); break;
                }
                prev = tk;
            }
            if (double.IsNaN(hit)) continue;
            l.T1 = hit; l.Impact = true; legs[i] = l;
            legs.RemoveRange(i + 1, legs.Count - i - 1);
            applied?.RemoveAll(a => a.Node != null && a.Node.T > hit);   // nothing is flown after the impact
            return;
        }
    }

    /// <summary>What the trajectory depends on, as a 64-bit hash (it was a string built every frame: ~150 KB/s
    /// of garbage and the doubles' formatting).</summary>
    private static long Signature(double t)
    {
        ulong h = 0x9E3779B97F4A7C15UL;
        void Mix(ulong x) { h ^= x; h *= 0xbf58476d1ce4e5b9UL; h ^= h >> 31; }
        void D(double x) => Mix((ulong)BitConverter.DoubleToInt64Bits(x));
        if (Base(t, out var b, out var el)) { Mix((ulong)(uint)(b.Name?.GetHashCode() ?? 0)); D(el.SemiMajorAxis); D(el.Eccentricity); D(el.Epoch); D(el.TrueAnomaly); D(el.Inclination); }
        else Mix(1);
        lock (Nodes)
            foreach (var n in Nodes)
            {
                Mix(0x7C);   // ('|')
                D(n.T); D(n.Pro); D(n.Nor); D(n.Rad); Mix(n.Dirty ? 1UL : 2UL); Mix((ulong)(uint)(n.TBody?.GetHashCode() ?? 0));
            }
        return (long)h;
    }

    private static bool TrajectoryUncached(double t, out List<Leg> legs, out List<Applied> applied)
    {
        legs = new List<Leg>(); applied = new List<Applied>();
        if (!Base(t, out var body, out var el)) return false;
        List<Node> nodes;
        lock (Nodes) { nodes = new List<Node>(Nodes); }
        nodes.Sort((a, b) => a.T.CompareTo(b.T));
        double tc = t;
        bool planned = false;
        foreach (var n in nodes)
        {
            double Tn = Math.Max(n.T, tc);   // a node already past applies now (burning late)
            StateVector st;
            // (the coast the burn integrates from: the last arc's, in ITS body's frame - after a sphere-of-influence change
            //  the old body's elements with the new body's mu made the finite burn garbage)
            KeplerianElements coastEl = el; double coastT0 = tc;
            if (Tn - tc < 0.5)
                st = OrbitPropagation.StateAt(el, Tn);   // the node is now: no coast before it
            else
            {
                var arcs = PatchedConic.Propagate(body, OrbitPropagation.StateAt(el, tc), tc, Tn - tc, 8, 256);
                if (arcs == null || arcs.Count == 0) return legs.Count > 0;
                foreach (var a in arcs) legs.Add(new Leg { Body = a.Body, El = a.Elements, T0 = a.StartTime, T1 = Math.Min(a.EndTime, Tn), Planned = planned });
                var last = arcs[arcs.Count - 1];
                body = last.Body;
                st = OrbitPropagation.StateAt(last.Elements, Tn);
                coastEl = last.Elements; coastT0 = last.StartTime;
            }
            KeplerianElements after;
            Vector3D dv;
            // A node in a Lagrange sector (last plan's stay): the sector's own directions (prograde your
            // drift relative to the point), re-targeted every pass until its burn is near (the Kepler
            // guess of where you will be drifts off the sector's orbit); frozen from then, so the burn
            // closes on it.
            var lg = _lag;
            Vector3D lom = Vector3D.Zero;
            bool inLagSector = lg != null && Tn >= lg.TE - 1 && (double.IsNaN(lg.TX) || Tn <= lg.TX)
                               && SectorHomes.LagrangeOrbit(SystemHost.Registry?.Find(lg.Site.Home.Host), Tn, out lom, out _);
            if (inLagSector && t < BurnStart(n) - AutoBurn.AlignLead - 5) n.Dirty = true;
            // (planned with another ship's acceleration - set on foot with the jetpack, then you sat in a ship: its finite burn
            //  re-planned before it starts, not flown on the old shape)
            if (!n.Dirty && n.TAccel > 0.01 && Accel > 0.01 && Math.Abs(Accel - n.TAccel) > 0.2 * n.TAccel && t < BurnStart(n) - 5) n.Dirty = true;
            if (n.Dirty || n.TBody != body.Name)
            {
                Axes(st, out var P, out var N, out var R);
                if (inLagSector)
                {
                    SectorHomes.LagrangeState(lg.Site.Home, SystemHost.Registry, Tn, out var llp, out var llv);
                    var bo = body.OriginInRoot(Tn);
                    Vector3D ld = bo.Position + st.Position - llp, lu = bo.Velocity + st.Velocity - llv - Vector3D.Cross(lom, bo.Position + st.Position - llp);
                    Axes(new StateVector(ld, lu), out P, out N, out R);
                }
                dv = P * n.Pro + N * n.Nor + R * n.Rad;
                after = FiniteBurn(body, coastEl, coastT0, Tn, st, dv);
                if (!IsFinite(after.SemiMajorAxis)) return legs.Count > 0;   // (true with no legs drew nothing and reported a path)
                n.TAfter = after; n.TBody = body.Name; n.Dirty = false; n.TAccel = Accel;
            }
            else if (n.T < tc - 0.5 && n.TBody == body.Name)
            {
                // behind you (late, paused by warp): what is left at the node's own point - not now, where the orbits have
                // drifted apart (that grew with lateness and never converged) - flown from now (RendezvousPlot.RemainingBurn)
                dv = SEAerospace.Frames.RendezvousPlot.RemainingBurn(el, n.TAfter, n.T);
                after = OrbitalMath.ToElements(new StateVector(st.Position, st.Velocity + dv), body.Mu, Tn);
                if (!IsFinite(after.SemiMajorAxis)) after = n.TAfter;
            }
            else
            {
                after = n.TAfter;
                dv = OrbitPropagation.StateAt(after, Tn).Velocity - st.Velocity;   // what is left
            }
            applied.Add(new Applied { Node = n, Body = body, Before = st, Dv = dv, After = after });
            el = after; tc = Tn; planned = true;
        }
        double horizon = el.IsElliptic && IsFinite(el.Period) ? Math.Min(el.Period * 1.02, 10 * 86400.0) : 10 * 86400.0;   // long enough for a transfer
        var tail = PatchedConic.Propagate(body, OrbitPropagation.StateAt(el, tc), tc, horizon, 8, 256);
        if (tail != null) foreach (var a in tail) legs.Add(new Leg { Body = a.Body, El = a.Elements, T0 = a.StartTime, T1 = Math.Min(a.EndTime, tc + horizon), Planned = planned });
        return legs.Count > 0;
    }

    public static Vector3D RootAt(Leg l, double t) => l.Body.OriginInRoot(t).Position + OrbitPropagation.StateAt(l.El, t).Position;
}
