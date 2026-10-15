using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── the Lagrangian preview: your orbit in a Lagrange sector, now or from where you will enter one ──
    /// <summary>Your path in a Lagrange sector: from now (you are in one) or from where your path enters one.</summary>
    public sealed class LagPlan
    {
        public EncounterFrames.Site Site;
        public double TE, TX = double.NaN;    // entry (now, if Now) and exit (NaN: you stay)
        public bool Now;
        public double Period, Amp;
        public List<Vector3D> Path = new List<Vector3D>();    // offsets from the point, in the frame turning with the planet (axes as at TE)
        public List<Vector3D> After = new List<Vector3D>();   // past the exit, in the same frame
        public Dictionary<Node, Vector3D> NodeAt = new Dictionary<Node, Vector3D>();   // burns in the sector, where (same frame)
        public Vector3D Point;                                 // the point at TE (root)
        public double BestT = double.NaN, BestU, BestD;        // closest to the point before any burn: when, drift speed, distance
        public Vector3D BestX, BestV, Axis;                    // there: offset and drift (turning frame); the frame's axis
        public double W, Core;                                 // the sector's rate (rad/s), its calm core (m)
        public Dictionary<Node, (Vector3D P, Vector3D N, Vector3D R)> NodeAxes = new Dictionary<Node, (Vector3D, Vector3D, Vector3D)>();
        public Vector3D Omega;                                  // at BestT: the planet's turn (W above: the sector's rate there)
        public Vector3D P0; public GravityBody Host;           // the planet's place at TE (for its turn since)
        /// <summary>How far the planet has turned since TE (about Axis): offsets are kept as at TE.</summary>
        public double Theta(double t)
        {
            Vector3D p = Host.StateInParentAt(t).Position;
            return Math.Atan2(Vector3D.Dot(Axis, Vector3D.Cross(P0, p)), Vector3D.Dot(P0, p));
        }
    }
    private static LagPlan _lag;
    // The ghost (a sector drawn where it will be when you enter it) on screen last frame: the map fades
    // today's sectors under it, so the two times do not read as one.
    private static Vector2 _ghostAt; private static float _ghostR; private static string _ghostName; private static double _ghostWall;
    public static bool UnderGhost(Vector2 s, string sector)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        return _ghostName != null && sector != _ghostName && now - _ghostWall < 0.3 && (s - _ghostAt).Length() < _ghostR;
    }
    /// <summary>A Lagrange sector ahead or around you: the planner draws your path (cut at its entry).</summary>
    public static bool LagActive => _lag != null;
    public static LagPlan Lag => _lag;
    private static double _lagWall;
    public static string LibDebug = "-", LibStart = "-";

    public static Vector3D Turn(Vector3D v, Vector3D axis, double ang)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang);
        return v * c + Vector3D.Cross(axis, v) * s + axis * (Vector3D.Dot(axis, v) * (1 - c));
    }

    /// <summary>The plan: every frame while you are in a sector, a few times a second otherwise.</summary>
    static LagPlan LagrangeCached(double t, List<Leg> legs, List<Applied> applied)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var pf = FrameHost.PlayerFrame;
        bool inSector = pf != null && (EncounterFrames.RideOf(pf.Id) != null || EncounterFrames.SiteOf(pf.Id)?.Home?.Kind == SectorHomes.Kind.Lagrange);
        double every = inSector || (_lag != null && _lag.Now) ? 0.1 : 0.25;
        if (now - _lagWall > every)
        {
            try { _lag = PlanLagrange(t, legs, applied); } catch (Exception e) { _lag = null; LibDebug = "plan failed: " + e.Message; }
            _lagWall = now;
        }
        return _lag;
    }

    /// <summary>
    /// Lagrange sectors are spheres of influence (SectorHomes.LagrangeRegion): where your path first
    /// crosses into one (or now, if you are in one) you switch onto its Lagrange orbit, stepped with the
    /// sector's own model (a pull toward the point in the frame turning with the planet; none in the calm
    /// core), planned burns included, until it leaves the region; then your plain orbit again.
    /// </summary>
    static LagPlan PlanLagrange(double t, List<Leg> legs, List<Applied> applied)
    {
        var reg = SystemHost.Registry;
        if (reg == null) return null;
        var pf = FrameHost.PlayerFrame;
        EncounterFrames.Site site = null;
        double tE = double.NaN;
        Vector3D d = default, v = default;
        bool nowIn = false;
        if (pf != null)
        {
            var ride = EncounterFrames.RideOf(pf.Id);
            var own = EncounterFrames.SiteOf(pf.Id);
            if (ride != null) { site = ride.Site; tE = t; d = ride.D; v = ride.V; nowIn = true; }
            else if (own?.Home?.Kind == SectorHomes.Kind.Lagrange && FrameHost.RiderFrame == pf.Id)
            {
                // (the rider state is in window axes: celestial ones here, as everywhere it meets an orbit)
                var obs = FrameHost.Observer;
                site = own; tE = t; nowIn = true;
                d = obs.HasValue ? SEAerospace.PlanetBerths.SpinToCelestial(obs.Value, FrameHost.RiderOffset) : FrameHost.RiderOffset;
                v = obs.HasValue ? SEAerospace.PlanetBerths.SpinToCelestial(obs.Value, FrameHost.RiderVelocity) : FrameHost.RiderVelocity;
            }
        }
        if (site == null)
        {
            // The first crossing into any sector's region along the path.
            foreach (var s in EncounterFrames.LagrangeSites())
            {
                var host = reg.Find(s.Home.Host);
                if (host?.Parent == null) continue;
                double P = SectorHomes.Period(s.Home, reg);
                foreach (var l in legs)
                {
                    double t1 = double.IsNaN(tE) ? l.T1 : Math.Min(l.T1, tE);
                    if (!(t1 > l.T0)) continue;
                    if (s.Home.Point >= 3)
                    {
                        if (l.Body != host.Parent) continue;
                        double rb = (SectorHomes.Where(s.Home, reg, l.T0, out var c0) - c0).Length();
                        double apo = l.El.IsElliptic ? l.El.ApoapsisRadius : double.PositiveInfinity;
                        if (l.El.PeriapsisRadius > rb * 1.04 || apo < rb * 0.96) continue;
                    }
                    else if (l.Body != host && l.Body != host.Parent) continue;
                    var leg = l;
                    bool In(double tk) { var test = SectorHomes.LagrangeRegion(s.Home, reg, tk); return test != null && test(RootAt(leg, tk)); }
                    if (In(l.T0)) { tE = l.T0; site = s; break; }
                    int n = (int)Math.Min(4000, Math.Max(48, (t1 - l.T0) / (P / 360)));
                    double prev = l.T0, hit = double.NaN;
                    for (int k = 1; k <= n; k++)
                    {
                        double tk = l.T0 + (t1 - l.T0) * k / n;
                        if (In(tk))
                        {
                            double lo = prev, hi = tk;
                            for (int q = 0; q < 30; q++) { double m = 0.5 * (lo + hi); if (In(m)) hi = m; else lo = m; }
                            hit = hi; break;
                        }
                        prev = tk;
                    }
                    if (!double.IsNaN(hit)) { tE = hit; site = s; break; }
                }
            }
            if (site == null) return null;
            foreach (var l in legs)
            {
                if (tE < l.T0 - 1e-6 || tE > l.T1 + 1e-6) continue;
                var o = l.Body.OriginInRoot(tE);
                var st = OrbitPropagation.StateAt(l.El, tE);
                SectorHomes.LagrangeState(site.Home, reg, tE, out var lp, out var lv);
                d = o.Position + st.Position - lp; v = o.Velocity + st.Velocity - lv;
                break;
            }
        }
        var body = reg.Find(site.Home.Host);
        if (!SectorHomes.LagrangeOrbit(body, tE, out var om, out var w)) return null;
        var plan = new LagPlan { Site = site, TE = tE, Now = nowIn, Period = 2 * Math.PI / w };
        LibStart = $"{site.Sector} from {(nowIn ? "now" : "entry")}: offset {d.Length() / 1000:F1} km, speed {v.Length():F1} m/s";
        // Stepped exactly as the sector flies you (EncounterFrames.RefreshRides: the same pull, with the
        // planet's turn and rate as they are at each moment: an eccentric planet's vary), then turned into
        // the frame turning with the planet (axes as at tE) to be drawn.
        double core = SectorHomes.LagrangeCore(site.Home, reg);
        Vector3D ax = Vector3D.Normalize(om);
        Vector3D p0 = body.StateInParentAt(tE).Position;
        plan.Core = core; plan.Axis = ax; plan.P0 = p0; plan.Host = body;
        Vector3D D = d, V = v;
        var burns = new List<Applied>();
        foreach (var ab in applied) if (ab.Node.T >= tE) burns.Add(ab);
        double horizon = 2.5 * plan.Period;   // a slow orbit round the calm core takes a while: shown whole
        if (burns.Count > 0) horizon = Math.Min(5 * plan.Period, burns[burns.Count - 1].Node.T - tE + 2.5 * plan.Period);   // a slow orbit round the calm core: shown whole
        int steps = (int)Math.Max(64, Math.Min(6000, horizon / 60.0));
        double h = horizon / steps;
        Vector3D lp0 = SectorHomes.Where(site.Home, reg, tE, out var cen);
        int every = Math.Max(1, steps / 400), bi = 0;
        double tau = 0, amp = D.Length();
        plan.Path.Add(D);
        for (int k = 1; k <= steps; k++)
        {
            double tk = tE + tau;
            if (!SectorHomes.LagrangeOrbit(body, tk, out var omk, out var wk)) break;
            while (bi < burns.Count && burns[bi].Node.T <= tk + h)
            {
                // In a sector a node's directions are the sector's own: prograde along your drift
                // relative to the point (as it turns), radial away from it.
                var bn = burns[bi].Node;
                Vector3D u0 = V - Vector3D.Cross(omk, D);
                Axes(new StateVector(D, u0), out var bP, out var bN, out var bR);
                // (a frozen node - its burn near or under way: what is LEFT of it (the plan's own, inertial like V); the full
                //  delta-v again mid-burn previewed half a burn too many, 150% at half flown)
                Vector3D left = burns[bi].Dv;
                V += !bn.Dirty && IsFinite(left.X) && IsFinite(left.Y) && IsFinite(left.Z) ? left : bP * bn.Pro + bN * bn.Nor + bR * bn.Rad;
                double thb = plan.Theta(tk);
                plan.NodeAxes[bn] = (Turn(bP, ax, -thb), Turn(bN, ax, -thb), Turn(bR, ax, -thb));
                plan.NodeAt[bn] = Turn(D, ax, -thb);
                amp = D.Length();   // the size you settle into, after the burn
                bi++;
            }
            Vector3D omDot = SectorHomes.LagrangeTurnRate(body, tk);
            Vector3D a1 = SectorHomes.LagrangeAccel(D, V, omk, wk, core, omDot);
            Vector3D dm = D + V * (h / 2), vm = V + a1 * (h / 2);
            Vector3D a2 = SectorHomes.LagrangeAccel(dm, vm, omk, wk, core, omDot);
            D += vm * h; V += a2 * h; tau += h;
            tk = tE + tau;
            amp = Math.Max(amp, D.Length());
            if (bi == 0 && (double.IsNaN(plan.BestT) || D.Length() < plan.BestD))
            {
                plan.BestT = tk; plan.BestD = D.Length(); plan.BestX = D; plan.Omega = omk; plan.W = wk;
                plan.BestV = V - Vector3D.Cross(omk, D); plan.BestU = plan.BestV.Length();
            }
            var inside = SectorHomes.LagrangeRegion(site.Home, reg, tk);
            bool gone = inside != null && !inside(SectorHomes.Where(site.Home, reg, tk) + D);
            if (gone || k % every == 0 || k == steps) plan.Path.Add(Turn(D, ax, -plan.Theta(tk)));
            if (gone) { plan.TX = tk; break; }
        }
        plan.Amp = amp; plan.Point = lp0;
        if (!double.IsNaN(plan.TX))
        {
            // Back on a plain orbit: drawn in the same turning frame, drifting off the sector.
            double tX = plan.TX;
            SectorHomes.LagrangeState(site.Home, reg, tX, out var lpX, out var lvX);
            Vector3D pos = lpX + D, vel = lvX + V;
            var b2 = reg.Root.DeepestSoiContaining(pos, tX) ?? reg.Root;
            var o2 = b2.OriginInRoot(tX);
            var el = CaptureMath.CaptureElements(new StateVector(pos - o2.Position, vel - o2.Velocity), b2.Mu, tX);
            double far = 0.35 * (lp0 - cen).Length();
            if (IsFinite(el.SemiMajorAxis))
                for (int k = 0; k <= 160; k++)
                {
                    double tk = tX + plan.Period * k / 160;
                    Vector3D q = b2.OriginInRoot(tk).Position + OrbitPropagation.StateAt(el, tk).Position - SectorHomes.Where(site.Home, reg, tk);
                    Vector3D rq = Turn(q, ax, -plan.Theta(tk));
                    plan.After.Add(rq);
                    if (rq.Length() > far) break;
                }
        }
        return plan;
    }

    /// <summary>The legs up to tE (your plain orbit ends where the Lagrange sector takes over).</summary>
    static List<Leg> CutAtLagrange(List<Leg> legs, double tE)
    {
        var cut = new List<Leg>();
        foreach (var l in legs)
        {
            if (l.T0 >= tE && cut.Count > 0) break;
            var c = l; if (c.T1 > tE) c.T1 = Math.Max(c.T0, tE);
            cut.Add(c);
            if (l.T1 >= tE) break;
        }
        return cut;
    }

    /// <summary>A plan offset (turning frame, axes as at TE) on the map, as DrawLibration places it.</summary>
    static Vector3D LagLoc(LagPlan p, Vector3D off, double t, Func<Vector3D, double, Vector3D> toMap)
    {
        double tD = p.Now ? t : p.TE;
        return toMap(SectorHomes.Where(p.Site.Home, SystemHost.Registry, tD) + Turn(off, p.Axis, p.Theta(tD)), tD);
    }

    static void Marker(Vector2 s, ColorSRGB col, float u)
    {
        if (!MapPipeline.ScreenIcon("ring", s, 6f * u, col)) MapPipeline.ScreenCircle(s, 5f * u, col, 2f * u);
    }

    /// <summary>
    /// The Lagrange sector as a sphere of influence (KSP style): its teardrop, dotted, round the point as
    /// it is when you enter (or now); your orbit inside it, from the Entry to the Exit; past the Exit the
    /// plain orbit you leave on, dashed; the calm core ringed. Size and period on the point.
    /// </summary>
    static void DrawLibration(LagPlan plan, double t, Func<Vector3D, double, Vector3D> toMap, Func<Vector3D, Vector3D> W, float u)
    {
        if (plan == null || plan.Path.Count < 2) return;
        var reg = SystemHost.Registry;
        var home = plan.Site.Home;
        double tE = plan.Now ? t : plan.TE, turn = plan.Theta(tE);
        Vector3D pt = SectorHomes.Where(home, reg, tE, out Vector3D centre);
        var col = new ColorSRGB(0.95f, 0.75f, 0.35f, 0.9f);
        bool On(Vector3D off, out Vector2 sp) => MapPipeline.ToScreen(W(toMap(pt + Turn(off, plan.Axis, turn), tE)), out sp) && InMapArea(sp);
        // The region: a boundary (dim, dotted; gold is for you).
        var bcol = MapStyle.Zone;
        if (home.Point >= 3)
        {
            double P = SectorHomes.Period(home, reg);
            const double span = 0.035, k = 0.035;
            var outer = new List<Vector2>(); var inner = new List<Vector2>();
            for (int i = 0; i <= 32; i++)
            {
                double q = -span + 2 * span * i / 32, ki = k * Math.Max(0.08, Math.Sin(Math.PI * i / 32));
                // Relative to the parent where it is then: a moon's points only (its planet's own orbit left out).
                Vector3D r = SectorHomes.Where(home, reg, tE + P * q, out var cq) - cq;
                if (MapPipeline.ToScreen(W(toMap(centre + r * (1 + ki), tE)), out var so)) outer.Add(so);
                if (MapPipeline.ToScreen(W(toMap(centre + r * (1 - ki), tE)), out var si)) inner.Add(si);
            }
            var lens = new List<Vector2>(outer); for (int i = inner.Count - 1; i >= 0; i--) lens.Add(inner[i]);
            MapStyle.Boundary(lens, true, bcol, MapStyle.Thin(u), u);
        }
        else
        {
            var host = reg.Find(home.Host);
            double R = 0.15 * SectorHomes.HillRadius(host);
            Vector3D e1 = Vector3D.Normalize(pt - centre), e2 = Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(e1, host.StateInParentAt(tE).Velocity), e1));
            var zone = new List<Vector2>(48);
            for (int i = 0; i < 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                if (!On((e1 * Math.Cos(a) + e2 * Math.Sin(a)) * R, out var sp)) { zone.Clear(); break; }
                zone.Add(sp);
            }
            MapStyle.Boundary(zone, true, bcol, MapStyle.Thin(u), u);
        }
        // The calm core, ringed (when big enough to see).
        bool hasC = On(Vector3D.Zero, out var cs);
        if (!plan.Now && hasC && On(Vector3D.Normalize(plan.Point - centre) * (0.035 * (pt - centre).Length()), out var gw))
        {
            // The ghost's reach on screen: about its half-length (six times its half-width).
            _ghostAt = cs; _ghostR = Math.Max(20f * u, 6f * (gw - cs).Length()); _ghostName = plan.Site.Sector;
            _ghostWall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }
        if (hasC)
        {
            Vector3D side = Vector3D.Cross(plan.Axis, plan.Point - centre);
            side = side.LengthSquared() > 0 ? Vector3D.Normalize(side) : Vector3D.UnitX;
            if (plan.Core > 0 && On(side * plan.Core, out var es))
            {
                float rpx = (es - cs).Length();
                var ccol = HudPanel.Alpha(new ColorSRGB(0.55f, 0.85f, 1f, 1f), 0.5f);
                if (rpx > 4f * u) MapStyle.BoundaryCircle(cs, rpx, ccol, MapStyle.Thin(u), u);
                if (rpx > 40f * u) HudPanel.TagAt(cs + new Vector2(0, rpx + 8f * u), "Calm core", ccol, u, diamond: false);
            }
        }
        // The point itself (as it will be, for an entry ahead).
        if (!plan.Now && hasC && !MapPipeline.ScreenIcon("diamond", cs, 5f * u, col)) MapPipeline.ScreenCircle(cs, 3f * u, col, 2f * u);
        // Your orbit in the sector.
        Vector2 prev = default; bool hp = false;
        foreach (var off in plan.Path)
        {
            if (!On(off, out var sp)) { hp = false; continue; }
            if (hp) MapPipeline.ScreenLine(prev, sp, col, MapStyle.Thick(u));
            prev = sp; hp = true;
        }
        if (!plan.Now && On(plan.Path[0], out var en))
        {
            Marker(en, col, u);
            HudPanel.TagAt(en + new Vector2(10f * u, 0), $"{plan.Site.Sector} Entry  ·  in {Clock(plan.TE - SystemHost.Now)}", col, u, diamond: false);
        }
        if (!double.IsNaN(plan.TX))
        {
            var fcol = HudPanel.Alpha(YouColor, 0.55f);
            var run = new List<Vector2>();
            foreach (var off in plan.After)
            {
                if (!On(off, out var sp)) { MapStyle.Plan(run, false, fcol, MapStyle.Thick(u), u); run.Clear(); continue; }
                run.Add(sp);
            }
            MapStyle.Plan(run, false, fcol, MapStyle.Thick(u), u);
            if (On(plan.Path[plan.Path.Count - 1], out var ex))
            {
                Marker(ex, col, u);
                HudPanel.TagAt(ex + new Vector2(10f * u, 0), $"{plan.Site.Sector} Exit", col, u, diamond: false);
            }
        }
        LibDebug = $"{(plan.Now ? "in" : "entry in " + Clock(plan.TE - SystemHost.Now))} {plan.Site.Sector}: amp {plan.Amp / 1000:F0} km, {(double.IsNaN(plan.TX) ? "stays" : "exits after " + Clock(plan.TX - plan.TE))}, points {plan.Path.Count}+{plan.After.Count}";
        // The orbit's size and fate on the point: now, or (for an entry ahead) once the ghost is big
        // enough to read, named, so it is not taken for whatever sector sits there today.
        bool readable = plan.Now;
        if (!readable && hasC && On(Vector3D.Normalize(plan.Point - centre) * (0.035 * (pt - centre).Length()), out var ws)) readable = (ws - cs).Length() > 40f * u;
        if (hasC && readable)
        {
            string stay = double.IsNaN(plan.TX) ? "stays" : $"exit {(plan.Now ? "in" : "after")} {Clock(plan.TX - plan.TE)}";
            string lp = $"{SystemHost.DisplayName(home.Host)} L{home.Point}";
            string who = plan.Now ? lp : $"{plan.Site.Sector} ({lp}, in {Clock(plan.TE - SystemHost.Now)})";
            HudPanel.TagAt(cs, $"{who}  ·  orbit ±{HudPanel.Km(plan.Amp)}  ·  {stay}", col, u);
        }
    }
}
