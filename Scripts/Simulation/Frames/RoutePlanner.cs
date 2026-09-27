using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.Frames;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// AUTO-PLAN: maneuver nodes to the selected sector's site, from wherever you are.
///  - Same SOI: the core InterceptPlanner (direct Lambert, phasing, Hohmann-then-phase, whichever is
///    cheapest), its burns turned into nodes.
///  - Another planet (Kemik to Verdure): a porkchop over departure time and time of flight
///    (heliocentric Lambert), cost = ejection from your orbit + capture at the target; the ejection
///    burn is placed where the escape hyperbola leaves along the needed v-infinity; then refined on
///    the patched-conic trajectory (a few evaluations per frame, no stall) until it meets the target
///    at the wanted periapsis; a capture burn at that periapsis; then the local intercept from the
///    captured orbit.
///  - A heliocentric site (belt, ring, L-points): the same search aimed at the site itself, and a
///    velocity-matching burn on arrival.
/// Nodes are the player's to edit afterwards.
/// </summary>
public static class RoutePlanner
{
    public static string Status = "";
    public static bool Busy => _refine != null;
    public static int Progress => _refine == null ? 100 : (int)(100.0 * _refine.Evals / RefineEvals);

    const int RefineEvals = 160;
    const double BudgetMs = 6.0;

    // ───────────────────────────── entry ─────────────────────────────

    public static string Start(string sector)
    {
        double t = SystemHost.Now;
        EncounterFrames.Site site = null;
        foreach (var s in EncounterFrames.Sites) if (s.Sector == sector && (site == null || s.Anchor)) site = s;
        if (site == null) return Say($"No site for {sector}");
        if (!Maneuvers.Base(t, out var body, out var el)) return Say("No orbit to plan from (you are not in flight)");
        if (!EncounterFrames.Ephemeris(site, t, out var sParent, out var sRel)) return Say("Target has no orbit");
        lock (Maneuvers.Nodes) Maneuvers.Nodes.Clear();
        Maneuvers.Selected = null;
        _refine = null;
        if (sParent == body) return Local(site, body, t, t + 60, null);   // a minute to turn and get ready
        return Transfer(site, body, el, sParent, t);
    }

    // ───────────────────────────── same SOI ─────────────────────────────

    /// <summary>Intercept within one SOI from the trajectory state at `from` (after any nodes already set).</summary>
    private static string Local(EncounterFrames.Site site, GravityBody body, double t, double from, string prefix)
    {
        if (!StateOnTrajectory(t, from, out var b, out var me) || b != body) return Say((prefix ?? "") + "no state to plan the approach from");
        if (!EncounterFrames.Ephemeris(site, from, out var sp, out var srel) || sp != body) return Say((prefix ?? "") + "the site is not about " + body.Name + " then");
        var siteEl = CaptureMath.CaptureElements(srel, body.Mu, from);
        var plan = InterceptPlanner.Plan(me, siteEl, body.Mu, from, RendezvousParams.Default,
                                         new InterceptOptions { MatchArrivalVelocity = true });
        if (plan == null || plan.Status != PlanStatus.Ok || plan.Maneuvers == null || plan.Maneuvers.Count == 0)
            return Say((prefix ?? "") + "no intercept found");
        double total = 0;
        foreach (var m in plan.Maneuvers) { AddWorldDv(t, from + m.TimeFromNowSeconds, m.DeltaV); total += m.Magnitude; }
        var arr = plan.Arrival;
        return Say((prefix ?? "") + $"{plan.Maneuvers.Count} burn(s), {total:N0} m/s; arrive in {Maneuvers.Clock(arr.ArrivalTime - t)}, {Km(arr.MissDistance)} off");
    }

    // ───────────────────────────── interplanetary ─────────────────────────────

    private sealed class Refine
    {
        public EncounterFrames.Site Site; public GravityBody Target; public bool Helio;
        public Maneuvers.Node Node; public double TargetPe;
        public double[] X = new double[4], Step = new double[4];
        public double Best; public int Axis, Dir = 1, Evals;
        public double T0; public string Summary;
    }
    private static Refine _refine;

    private static string Transfer(EncounterFrames.Site site, GravityBody body, KeplerianElements el, GravityBody sParent, double t)
    {
        var reg = SystemHost.Registry;
        var root = reg.Root;
        GravityBody from = body;
        while (from.Parent != null && from.Parent != root) from = from.Parent;   // a moon's planet
        if (from.Parent == null) return Say("Plan from a planet's orbit (you are orbiting the sun)");
        GravityBody target = sParent.IsRoot ? null : sParent;
        while (target != null && target.Parent != null && target.Parent != root) target = target.Parent;
        bool helio = target == null;
        if (from != body) return Say($"Leave {body.Name} first, then plan from {from.Name} orbit");

        double muS = root.Mu;
        var sB = from.StateInParentAt(t);
        double rB = sB.Position.Length();
        Vector3D TargetPos(double tt) => helio ? SiteRoot(site, tt).Position : target.StateInParentAt(tt).Position;
        Vector3D TargetVel(double tt) => helio ? SiteRoot(site, tt).Velocity : target.StateInParentAt(tt).Velocity;
        double rT = TargetPos(t).Length();
        double TB = 2 * Math.PI * Math.Sqrt(rB * rB * rB / muS), TT = 2 * Math.PI * Math.Sqrt(rT * rT * rT / muS);
        double syn = Math.Abs(1.0 / (1.0 / TB - 1.0 / TT));
        if (!IsFinite(syn) || syn > 10 * TB) syn = TB;
        double hoh = Math.PI * Math.Sqrt(Math.Pow((rB + rT) / 2, 3) / muS);

        double r0 = OrbitPropagation.StateAt(el, t).Position.Length();
        double rc = helio ? 0 : Math.Max((reg.FindDefinition(target.Name)?.RadiusMeters ?? 6e4) + 150e3, 1);
        if (!helio && EncounterFrames.Ephemeris(site, t, out var spp, out var srl) && spp == target) rc = srl.Position.Length();

        // Porkchop.
        double best = double.MaxValue, bTd = 0, bTof = 0; Vector3D bVinf = default;
        for (int i = 0; i < 72; i++)
        {
            double td = t + 1800 + syn * i / 72.0;
            var sd = from.StateInParentAt(td);
            for (int j = 0; j < 36; j++)
            {
                double tof = hoh * (0.55 + 1.0 * j / 36.0);
                if (!Lambert.Solve(sd.Position, TargetPos(td + tof), tof, muS, true, out var v1, out var v2)) continue;
                Vector3D vd = v1 - sd.Velocity, va = v2 - TargetVel(td + tof);
                double ej = Math.Sqrt(vd.LengthSquared() + 2 * from.Mu / r0) - Math.Sqrt(from.Mu / r0);
                double cap = helio ? va.Length() : Math.Sqrt(va.LengthSquared() + 2 * target.Mu / rc) - Math.Sqrt(target.Mu / rc);
                double c = ej + cap;
                if (IsFinite(c) && c < best) { best = c; bTd = td; bTof = tof; bVinf = vd; }
            }
        }
        if (best == double.MaxValue) return Say("No transfer found");

        // The ejection burn: where the escape hyperbola leaves along v-infinity.
        var s0 = OrbitPropagation.StateAt(el, t);
        Vector3D h = Vector3D.Normalize(Vector3D.Cross(s0.Position, s0.Velocity));
        Vector3D vin = bVinf - h * Vector3D.Dot(bVinf, h);
        if (vin.LengthSquared() < 1e-6) vin = bVinf;
        double vinf = bVinf.Length();
        double eH = 1 + r0 * vinf * vinf / from.Mu;
        double nuInf = Math.Acos(-1.0 / eH);
        Vector3D pDir = PlanetBerths.RotateAboutAxis(Vector3D.Normalize(vin), h, -nuInf);
        double P = el.IsElliptic ? el.Period : 3600;
        double tEsc = from.SoiRadius / Math.Max(100, vinf);
        double centre = Math.Max(t + 120, bTd - tEsc);
        double tb = centre, bestAng = double.MaxValue;
        for (int k = -96; k <= 96; k++)
        {
            double tk = centre + P * k / 96.0;
            if (tk < t + 120) continue;
            var st = OrbitPropagation.StateAt(el, tk);
            double ang = Math.Acos(Math.Clamp(Vector3D.Dot(Vector3D.Normalize(st.Position), pDir), -1, 1)) + Math.Abs(tk - centre) / P * 0.05;
            if (ang < bestAng) { bestAng = ang; tb = tk; }
        }
        var sb = OrbitPropagation.StateAt(el, tb);
        double vp = Math.Sqrt(vinf * vinf + 2 * from.Mu / sb.Position.Length());
        var node = new Maneuvers.Node { T = tb, Pro = vp - sb.Velocity.Length() };
        lock (Maneuvers.Nodes) Maneuvers.Nodes.Add(node);
        Maneuvers.Selected = node;

        // Arrive at a fixed time, at an aim point beside the planet (the B-plane offset that gives the
        // wanted periapsis) or at the site itself; shoot the burn so the velocity leaving Kemik's SOI is
        // the Lambert velocity from there to the aim point (Newton on prograde / normal / radial).
        double ta = bTd + bTof;
        Vector3D Aim()
        {
            if (helio) return TargetPos(ta);
            var ts = target.StateInParentAt(ta);
            if (!Lambert.Solve(from.StateInParentAt(bTd).Position, ts.Position, bTof, muS, true, out _, out var v2)) return ts.Position;
            Vector3D vinfA = v2 - ts.Velocity;
            double va = Math.Max(1, vinfA.Length());
            double bOff = rc * Math.Sqrt(1 + 2 * target.Mu / (rc * va * va));
            Vector3D nrm = Vector3D.Normalize(Vector3D.Cross(ts.Position, ts.Velocity));
            Vector3D side = Vector3D.Normalize(Vector3D.Cross(nrm, vinfA));   // in the plane, across the approach
            return ts.Position + side * bOff;
        }
        Vector3D aim = Aim();
        var r = new Refine { Site = site, Target = target, Helio = helio, Node = node, TargetPe = rc, T0 = t,
                             Summary = $"{(helio ? site.Sector : target.Name)}: depart in {Maneuvers.Clock(tb - t)}, {Maneuvers.Clock(bTof)} flight" };
        Vector3D g = Residual(r, t, aim, ta, out bool ok);
        int it = 0;
        for (; it < 10 && ok && g.Length() > 0.05; it++)
        {
            const double fd = 0.5;
            var J = new Vector3D[3];
            double[] x0 = { node.Pro, node.Nor, node.Rad };
            for (int k = 0; k < 3; k++)
            {
                double[] x = (double[])x0.Clone(); x[k] += fd;
                node.Pro = x[0]; node.Nor = x[1]; node.Rad = x[2]; node.Edit();
                J[k] = (Residual(r, t, aim, ta, out _) - g) / fd;
            }
            if (!Solve3(J[0], J[1], J[2], -g, out Vector3D dx)) break;
            if (dx.Length() > 150) dx = dx * (150 / dx.Length());
            node.Pro = x0[0] + dx.X; node.Nor = x0[1] + dx.Y; node.Rad = x0[2] + dx.Z; node.Edit();
            g = Residual(r, t, aim, ta, out ok);
        }
        Log.Default?.Info($"[ORBIT-PLAN] shooting: {it} iteration(s), residual {g.Length():F2} m/s, node P {node.Pro:F1} N {node.Nor:F1} R {node.Rad:F1}");
        double total = Math.Sqrt(node.Pro * node.Pro + node.Nor * node.Nor + node.Rad * node.Rad);
        r.Summary += $", ejection {total:N0} m/s";
        r.Best = 0;
        Finish(r, t);
        return Status;
    }

    /// <summary>Your heliocentric velocity leaving the departure SOI, minus the Lambert velocity from there to the aim point.</summary>
    private static Vector3D Residual(Refine r, double t, Vector3D aim, double ta, out bool ok)
    {
        ok = false;
        if (!Maneuvers.Trajectory(t, out var legs, out _)) return new Vector3D(1e3, 0, 0);
        foreach (var l in legs)
        {
            if (!l.Body.IsRoot || l.T0 < r.Node.T) continue;
            var st = OrbitPropagation.StateAt(l.El, l.T0);
            double tof = ta - l.T0;
            if (tof <= 60 || !Lambert.Solve(st.Position, aim, tof, l.Body.Mu, true, out var v1, out _)) return new Vector3D(1e3, 0, 0);
            ok = true;
            return st.Velocity - v1;
        }
        return new Vector3D(1e3, 0, 0);   // not escaping (yet)
    }

    /// <summary>Solve [a b c] x = y (columns), Cramer's rule.</summary>
    private static bool Solve3(Vector3D a, Vector3D b, Vector3D c, Vector3D y, out Vector3D x)
    {
        double det = Vector3D.Dot(a, Vector3D.Cross(b, c));
        x = default;
        if (Math.Abs(det) < 1e-12) return false;
        x = new Vector3D(Vector3D.Dot(y, Vector3D.Cross(b, c)), Vector3D.Dot(a, Vector3D.Cross(y, c)), Vector3D.Dot(a, Vector3D.Cross(b, y))) / det;
        return true;
    }

    /// <summary>Per frame: a few refinement evaluations; finishing adds the arrival burns.</summary>
    public static void Tick(double t)
    {
        var r = _refine;
        if (r == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < BudgetMs && r.Evals < RefineEvals)
        {
            // Coordinate descent: try a step on one axis, keep it if better, else the other way, else shrink.
            double[] x = (double[])r.X.Clone();
            x[r.Axis] += r.Dir * r.Step[r.Axis];
            if (x[0] < t + 60) x[0] = t + 60;
            Apply(r, x);
            double c = Cost(r, t);
            r.Evals++;
            if (c < r.Best) { r.Best = c; r.X = x; continue; }
            Apply(r, r.X);
            if (r.Dir > 0) { r.Dir = -1; continue; }
            r.Dir = 1;
            r.Step[r.Axis] *= 0.6;
            r.Axis = (r.Axis + 1) % 4;
        }
        Status = $"Refining the transfer {Progress}% (miss {Km(r.Best)})";
        if (r.Evals < RefineEvals && r.Best > 2000) return;
        _refine = null;
        Finish(r, t);
    }

    private static void Apply(Refine r, double[] x)
    {
        r.Node.T = x[0]; r.Node.Pro = x[1]; r.Node.Nor = x[2]; r.Node.Rad = x[3]; r.Node.Edit();
    }

    /// <summary>How far the trajectory is from what we want: the target's periapsis (planet), or the site (heliocentric).</summary>
    private static double Cost(Refine r, double t)
    {
        if (!Maneuvers.Trajectory(t, out var legs, out _)) return 1e12;
        if (!r.Helio)
        {
            foreach (var l in legs)
                if (l.Body == r.Target)
                    return Math.Abs(l.El.PeriapsisRadius - r.TargetPe);
        }
        double best = 1e12;
        foreach (var l in legs)
        {
            if (l.T1 <= r.Node.T) continue;
            double span = l.T1 - l.T0;
            for (int k = 0; k <= 60; k++)
            {
                double tk = l.T0 + span * k / 60;
                Vector3D me = Maneuvers.RootAt(l, tk);
                Vector3D tg = r.Helio ? SiteRoot(r.Site, tk).Position : r.Target.OriginInRoot(tk).Position;
                double d = (me - tg).Length();
                if (d < best) best = d;
            }
        }
        return best + (r.Helio ? 0 : 1e7);   // not yet in the SOI: worse than any periapsis error
    }

    private static void Finish(Refine r, double t)
    {
        if (!Maneuvers.Trajectory(t, out var legs, out _)) { Say("Transfer lost"); return; }
        if (!r.Helio)
        {
            int li = legs.FindIndex(l => l.Body == r.Target);
            if (li < 0) { Say($"{r.Summary}: no encounter with {r.Target.Name} yet; adjust the node"); return; }
            var l = legs[li];
            double tp = l.T0 + OrbitPropagation.TimeToPeriapsis(OrbitPropagation.AtTime(l.El, l.T0));
            if (!IsFinite(tp) || tp < l.T0) tp = l.T0 + 60;
            var sp = OrbitPropagation.StateAt(l.El, tp);
            double vc = Math.Sqrt(r.Target.Mu / sp.Position.Length());
            var cap = new Maneuvers.Node { T = tp, Pro = vc - sp.Velocity.Length() };
            lock (Maneuvers.Nodes) Maneuvers.Nodes.Add(cap);
            Local(r.Site, r.Target, t, tp + 30, r.Summary + "; capture at " + r.Target.Name + "; ");
            return;
        }
        // Heliocentric site: match its velocity at the closest approach.
        double bestD = double.MaxValue, bt = double.NaN; Maneuvers.Leg bl = default;
        foreach (var l in legs)
        {
            if (l.T1 <= r.Node.T) continue;
            double span = l.T1 - l.T0;
            for (int k = 0; k <= 200; k++)
            {
                double tk = l.T0 + span * k / 200;
                double d = (Maneuvers.RootAt(l, tk) - SiteRoot(r.Site, tk).Position).Length();
                if (d < bestD) { bestD = d; bt = tk; bl = l; }
            }
        }
        if (double.IsNaN(bt)) { Say("Transfer lost"); return; }
        var mine = OrbitPropagation.StateAt(bl.El, bt);
        Vector3D myV = bl.Body.OriginInRoot(bt).Velocity + mine.Velocity;
        AddWorldDv(t, bt, SiteRoot(r.Site, bt).Velocity - myV);
        Say(r.Summary + $"; arrive {Km(bestD)} off, match speed there");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>A burn given as a world delta-v at time T, stored as P/N/R on the trajectory as it then is.</summary>
    private static void AddWorldDv(double t, double T, Vector3D dv)
    {
        if (!StateOnTrajectory(t, T, out _, out var st)) return;
        Maneuvers.Axes(st, out var P, out var N, out var R);
        var n = new Maneuvers.Node { T = T, Pro = Vector3D.Dot(dv, P), Nor = Vector3D.Dot(dv, N), Rad = Vector3D.Dot(dv, R) };
        lock (Maneuvers.Nodes) Maneuvers.Nodes.Add(n);
        if (Maneuvers.Selected == null) Maneuvers.Selected = n;
    }

    /// <summary>The state on the (noded) trajectory at time T, relative to its body then.</summary>
    private static bool StateOnTrajectory(double t, double T, out GravityBody body, out StateVector st)
    {
        body = null; st = default;
        if (!Maneuvers.Trajectory(t, out var legs, out _)) return false;
        foreach (var l in legs)
            if (T >= l.T0 - 1e-6 && T <= l.T1 + 1e-6) { body = l.Body; st = OrbitPropagation.StateAt(l.El, T); return true; }
        if (legs.Count == 0) return false;
        var last = legs[legs.Count - 1];
        body = last.Body; st = OrbitPropagation.StateAt(last.El, T);
        return true;
    }

    private static StateVector SiteRoot(EncounterFrames.Site s, double tt)
    {
        if (!EncounterFrames.Ephemeris(s, tt, out var p, out var rel)) return default;
        var o = p.OriginInRoot(tt);
        return new StateVector(o.Position + rel.Position, o.Velocity + rel.Velocity);
    }

    private static string Say(string s) { Status = s; Log.Default?.Info("[ORBIT-PLAN] " + s); return s; }
    private static string Km(double m) => HudPanel.Km(m);
    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
