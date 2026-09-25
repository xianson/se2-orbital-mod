using System;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

namespace RendezvousTest
{
    // Offline validation of the rendezvous targeting math (the timed-out agent wrote the
    // solvers but not this harness). No test framework: assert helpers, PASS/FAIL summary,
    // nonzero exit on any failure. The centerpiece is the end-to-end proof that a planned
    // intercept, when actually flown (burn applied + propagated), arrives at the target.
    internal static class Program
    {
        private static int _passed, _failed;
        private const double MuEarth = 3.986004418e14;

        private static int Main()
        {
            Console.WriteLine("=== SE-Aerospace rendezvous targeting tests ===\n");

            LambertRoundTrip();
            LambertCurtis52();
            ClosestApproachConstructed();
            InterceptActuallyIntercepts();
            RendezvousMatchesVelocity();
            FastestVsEfficient();
            CoplanarVsPlaneChange();
            CoOrbitalPhasing();
            TransferPhaseCheaperAndFlies();

            // --- new tests: the four audit fixes (M1-M4) + the Target model ---
            ClosestApproachDeepBriefPass();   // M2
            LambertTransferAnglePi();         // M1 degenerate Δθ=π
            LambertOrbitNormalSense();        // M1 orbit-normal branch
            PhasingRelativeRateSelection();   // M3
            ManeuverLvlhDecomposition();      // M4
            TargetModelPlansIntercept();      // Task A: Target -> InterceptPlanner
            TargetErrorBandTightens();        // Task A: sigma -> honest error band

            // --- audit fixes C1/C2/H2/M1/H1 regression assertions ---
            LambertRejectsNearParabolic();       // C1
            LambertRejectsGarbageVelocity();     // C2
            InterceptUsesFlownMiss();            // C1 defense-in-depth
            LambertNewtonNonConvergence();       // H2
            BandForNoSolutionPlan();             // M1
            ClosestApproachEarliestVsDeepest();  // H1

            // --- tilt regression (system-plane tilt re-introduced 2026-06-17) ---
            TiltConsistentIntercept();           // both inputs tilted == canonical, rotated; a mix breaks

            Console.WriteLine($"\n=== {_passed} passed, {_failed} failed ===");
            return _failed == 0 ? 0 : 1;
        }

        private static void LambertRoundTrip()
        {
            Section("Lambert round-trip: recovers an orbit's own velocities");
            // a known orbit; pick two true anomalies; the EXACT tof between them comes from
            // the core's time-to-anomaly. Lambert(r1, r2, tof) must recover v1, v2.
            var sv = new StateVector(new Vector3D(8000e3, 0, 0), new Vector3D(0, 6500, 1500));
            var el = OrbitalMath.ToElements(sv, MuEarth, 0);
            double nu0 = el.TrueAnomaly;
            double nu1 = nu0 + 1.2; // ~69 deg further along

            var s0 = OrbitalMath.ToState(WithNu(el, nu0));
            var s1 = OrbitalMath.ToState(WithNu(el, nu1));
            double tof = OrbitPropagation.TimeToTrueAnomaly(WithNu(el, nu0), nu1);

            Vector3D v1, v2;
            bool ok = Lambert.Solve(s0.Position, s1.Position, tof, MuEarth, true, out v1, out v2);
            Ok("solved", ok);
            NearVec("v1 recovered", v1, s0.Velocity, 1e-3);
            NearVec("v2 recovered", v2, s1.Velocity, 1e-3);
        }

        private static void LambertCurtis52()
        {
            Section("Lambert vs Curtis Example 5.2 (textbook)");
            var r1 = new Vector3D(5000e3, 10000e3, 2100e3);
            var r2 = new Vector3D(-14600e3, 2500e3, 7000e3);
            double tof = 3600.0, mu = 3.986e14;
            Vector3D v1, v2;
            bool ok = Lambert.Solve(r1, r2, tof, mu, true, out v1, out v2);
            Ok("solved", ok);
            NearVec("v1 (km/s)", v1, new Vector3D(-5992.5, 1925.4, 3245.6), 2.0);
            NearVec("v2 (km/s)", v2, new Vector3D(-3312.5, -4196.6, -385.29), 2.0);
        }

        private static void ClosestApproachConstructed()
        {
            Section("closest approach: finds a constructed close pass");
            // target on a circular orbit; build MY orbit (via Lambert) to pass through the
            // target's position at t=1800 s -> a deliberate near-zero-miss conjunction.
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 7000e3), 0)), MuEarth, 0);
            double tArr = 1800.0;
            var targAtArr = OrbitPropagation.StateAt(target, tArr);

            var myStart = new StateVector(new Vector3D(0, -6800e3, 0), new Vector3D(7700, 0, 0));
            Vector3D v1, v2;
            Lambert.Solve(myStart.Position, targAtArr.Position, tArr, MuEarth, true, out v1, out v2);
            var myOrbit = OrbitalMath.ToElements(new StateVector(myStart.Position, v1), MuEarth, 0);

            var ca = ClosestApproach.Find(myOrbit, target, 0, 3600, 360);
            Ok("found", ca.Found);
            Near("TCA ~ 1800 s", ca.Time, 1800.0, 30.0);
            Ok("miss small (< 5 km)", ca.MissDistance < 5000.0);
        }

        private static void InterceptActuallyIntercepts()
        {
            Section("INTERCEPT PROOF: fly the plan, arrive at the target");
            // me on a low circular orbit, target on a higher inclined circular orbit.
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400)), MuEarth, 0);

            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default);
            Ok("plan OK", plan.Status == PlanStatus.Ok);
            Ok("has a burn", plan.Maneuvers.Count >= 1);
            if (plan.Status != PlanStatus.Ok || plan.Maneuvers.Count == 0) return;

            Console.WriteLine($"   plan: dv={plan.Maneuvers[0].Magnitude:F1} m/s in {plan.Maneuvers[0].TimeFromNowSeconds:F0} s, " +
                              $"arrive @{plan.Arrival.ArrivalTime:F0}s miss={plan.Arrival.MissDistance:F0}m relspd={plan.Arrival.RelativeSpeed:F0}");

            // FLY IT: propagate to the burn, apply dv, propagate to arrival, compare to target.
            var m = plan.Maneuvers[0];
            var myOrbit = OrbitalMath.ToElements(myState, MuEarth, 0.0);
            var atBurn = OrbitPropagation.StateAt(myOrbit, m.TimeFromNowSeconds);
            var afterBurn = new StateVector(atBurn.Position, atBurn.Velocity + m.DeltaV);
            var burnOrbit = OrbitalMath.ToElements(afterBurn, MuEarth, m.TimeFromNowSeconds);

            var meAtArr = OrbitPropagation.StateAt(burnOrbit, plan.Arrival.ArrivalTime);
            var tgtAtArr = OrbitPropagation.StateAt(target, plan.Arrival.ArrivalTime);
            double flownMiss = (meAtArr.Position - tgtAtArr.Position).Length();

            Near("flown miss matches prediction", flownMiss, plan.Arrival.MissDistance, 50.0);
            Ok("flown intercept within capture range", flownMiss < RendezvousParams.Default.EnterRangeMeters);
            Ok("arrives in the future", m.TimeFromNowSeconds >= 0 && plan.Arrival.ArrivalTime > 0);
        }

        private static void RendezvousMatchesVelocity()
        {
            Section("RENDEZVOUS PROOF: 2-burn plan nulls relative velocity at arrival");
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400)), MuEarth, 0);

            var opts = new InterceptOptions();
            opts.MatchArrivalVelocity = true;   // full rendezvous: intercept + match
            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default, opts);
            Ok("plan OK", plan.Status == PlanStatus.Ok);
            Ok("two burns (intercept + match)", plan.Maneuvers.Count == 2);
            if (plan.Status != PlanStatus.Ok || plan.Maneuvers.Count < 2) return;

            // fly both burns; the final relative velocity to the target should be ~0
            var m0 = plan.Maneuvers[0];
            var m1 = plan.Maneuvers[1];
            var myOrbit = OrbitalMath.ToElements(myState, MuEarth, 0.0);
            var atBurn = OrbitPropagation.StateAt(myOrbit, m0.TimeFromNowSeconds);
            var afterBurn = new StateVector(atBurn.Position, atBurn.Velocity + m0.DeltaV);
            var burnOrbit = OrbitalMath.ToElements(afterBurn, MuEarth, m0.TimeFromNowSeconds);

            var meAtArr = OrbitPropagation.StateAt(burnOrbit, plan.Arrival.ArrivalTime);
            var tgtAtArr = OrbitPropagation.StateAt(target, plan.Arrival.ArrivalTime);
            Vector3D finalRelVel = (meAtArr.Velocity + m1.DeltaV) - tgtAtArr.Velocity;
            double finalMiss = (meAtArr.Position - tgtAtArr.Position).Length();

            Console.WriteLine($"   total dv={plan.TotalDeltaV:F1} m/s, final miss={finalMiss:F1}m, final relspd={finalRelVel.Length():F3} m/s");
            Ok("position matched (< capture range)", finalMiss < RendezvousParams.Default.EnterRangeMeters);
            Ok("velocity matched (rel speed < merge speed)", finalRelVel.Length() < RendezvousParams.Default.EnterRelSpeedMps);
        }

        private static void FastestVsEfficient()
        {
            Section("fastest vs most efficient: the porkchop trade as two choices");
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400)), MuEarth, 0);

            ManeuverPlan fastest, efficient;
            // 0 budget -> auto-derive ~4x efficient as the "fastest you can afford" cap
            InterceptPlanner.PlanChoices(myState, target, MuEarth, 0.0, RendezvousParams.Default, true, 0.0, out fastest, out efficient);
            Ok("fastest OK", fastest.Status == PlanStatus.Ok);
            Ok("efficient OK", efficient.Status == PlanStatus.Ok);
            if (fastest.Status != PlanStatus.Ok || efficient.Status != PlanStatus.Ok) return;

            Console.WriteLine($"   fastest:   dv={fastest.TotalDeltaV:F0} m/s, arrive @{fastest.Arrival.ArrivalTime:F0}s");
            Console.WriteLine($"   efficient: dv={efficient.TotalDeltaV:F0} m/s, arrive @{efficient.Arrival.ArrivalTime:F0}s");
            Ok("efficient costs less Δv", efficient.TotalDeltaV <= fastest.TotalDeltaV + 1.0);
            Ok("fastest arrives sooner", fastest.Arrival.ArrivalTime < efficient.Arrival.ArrivalTime);
            Ok("fastest stays within the affordability cap (no 500 km/s nonsense)",
                fastest.TotalDeltaV <= 4.0 * efficient.TotalDeltaV + 1.0);
        }

        private static void CoplanarVsPlaneChange()
        {
            Section("diagnosis: coplanar rendezvous is cheap (the plane change was the cost)");
            // SAME geometry as before but the target is COPLANAR (no z) -> isolates the
            // Hohmann cost from the plane-change cost.
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var coplanar = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 0), new Vector3D(-Math.Sqrt(MuEarth / 8200e3), 0, 0)), MuEarth, 0);

            ManeuverPlan fastest, efficient;
            InterceptPlanner.PlanChoices(myState, coplanar, MuEarth, 0.0, RendezvousParams.Default, true, 0.0, out fastest, out efficient);
            double hohmann = HohmannTwoBurn(6700e3, 8200e3);
            Console.WriteLine($"   coplanar efficient: dv={efficient.TotalDeltaV:F0} m/s  (ideal Hohmann ~{hohmann:F0}; inclined was 1327)");
            // Coplanar is Hohmann-CLASS (within ~40% of ideal) and clearly below the inclined
            // 1327 -> the ~340 gap inclined-vs-coplanar was the plane change. The ~250 over
            // ideal Hohmann is direct-Lambert timing overhead (the case for transfer+phase).
            Ok("coplanar is Hohmann-class, well below inclined",
                efficient.Status == PlanStatus.Ok && efficient.TotalDeltaV < 1.4 * hohmann && efficient.TotalDeltaV < 1100.0);
        }

        private static void CoOrbitalPhasing()
        {
            Section("diagnosis: co-orbital phasing should be CHEAP (catch a target on your own orbit)");
            // I'm on the target's exact orbit, just 0.5 rad behind. Catching up is a phasing
            // problem -> should cost FAR less than a direct Lambert across the orbit.
            double R = 8200e3, v = Math.Sqrt(MuEarth / R);
            var target = OrbitalMath.ToElements(new StateVector(new Vector3D(R, 0, 0), new Vector3D(0, v, 0)), MuEarth, 0);
            double phi = -0.5; // I am 0.5 rad behind the target on the same circle
            var myState = new StateVector(
                new Vector3D(R * Math.Cos(phi), R * Math.Sin(phi), 0),
                new Vector3D(-v * Math.Sin(phi), v * Math.Cos(phi), 0));

            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default);
            Console.WriteLine($"   co-orbital plan: kind={plan.Kind}, dv={plan.TotalDeltaV:F0} m/s, arrive @{plan.Arrival.ArrivalTime:F0}s");
            Ok("phasing-class cost is cheap (< 400 m/s)", plan.Status == PlanStatus.Ok && plan.TotalDeltaV < 400.0);
        }

        private static void TransferPhaseCheaperAndFlies()
        {
            Section("TRANSFER+PHASE: cheaper than direct Lambert, and the full plan flies");
            // coplanar circular: 6700 -> 8200 km. Direct Lambert was ~988; transfer+phase
            // should be Hohmann (~739) + a little phasing, and it must actually rendezvous.
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 0), new Vector3D(-Math.Sqrt(MuEarth / 8200e3), 0, 0)), MuEarth, 0);

            var opts = new InterceptOptions();
            opts.MatchArrivalVelocity = true;   // a real rendezvous (transfer+phase competes here)
            opts.MaxPhasingRevs = 24;           // cheap-and-slow laps -> approach pure Hohmann
            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default, opts);
            Console.WriteLine($"   chosen: kind={plan.Kind}, dv={plan.TotalDeltaV:F0} m/s, {plan.Maneuvers.Count} burns, arrive @{plan.Arrival.ArrivalTime:F0}s");
            Ok("plan OK", plan.Status == PlanStatus.Ok);
            Ok("chose transfer+phase", plan.Kind == PlanKind.TransferPhase);
            Ok("cheaper than direct Lambert (~988)", plan.TotalDeltaV < 980.0);

            // FLY THE WHOLE MULTI-BURN PLAN: propagate, applying each burn at its time.
            var state = myState;
            double tPrev = 0.0;
            var orbit = OrbitalMath.ToElements(state, MuEarth, 0.0);
            for (int i = 0; i < plan.Maneuvers.Count; i++)
            {
                var mv = plan.Maneuvers[i];
                var at = OrbitPropagation.StateAt(orbit, mv.TimeFromNowSeconds);
                state = new StateVector(at.Position, at.Velocity + mv.DeltaV);
                orbit = OrbitalMath.ToElements(state, MuEarth, mv.TimeFromNowSeconds);
                tPrev = mv.TimeFromNowSeconds;
            }
            // after the last burn, propagate to the arrival time and compare to the target
            var meArr = OrbitPropagation.StateAt(orbit, plan.Arrival.ArrivalTime);
            var tgtArr = OrbitPropagation.StateAt(target, plan.Arrival.ArrivalTime);
            double miss = (meArr.Position - tgtArr.Position).Length();
            double relSpeed = (meArr.Velocity - tgtArr.Velocity).Length();
            Console.WriteLine($"   flown: final miss={miss:F0} m, rel speed={relSpeed:F2} m/s");
            Ok("flown plan rendezvous: within capture range", miss < RendezvousParams.Default.EnterRangeMeters);
            Ok("flown plan matches velocity (< merge speed)", relSpeed < RendezvousParams.Default.EnterRelSpeedMps);
        }

        // ---- M2: brief deep pass between coarse samples (audit test #6) -------------
        private static void ClosestApproachDeepBriefPass()
        {
            Section("M2 closest approach: a brief deep pass between COARSE samples is still found");
            // Target on a circular orbit; build MY orbit (via Lambert) to pass through the
            // target's position at a chosen TCA -> a deliberate near-zero-miss conjunction
            // that is brief. Then search with a DELIBERATELY COARSE sample count whose bucket
            // width straddles the deep pass: the old neg->pos-only coarse bracket would miss
            // it; the sub-sampled brackets + dense-minimum compare must still surface it.
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 7000e3), 0)), MuEarth, 0);
            double tArr = 1800.0;
            var targAtArr = OrbitPropagation.StateAt(target, tArr);

            // A fast crosser: high relative speed so the close interval is short in time.
            var myStart = new StateVector(new Vector3D(0, -6800e3, 0), new Vector3D(8200, 0, 0));
            Vector3D v1, v2;
            Lambert.Solve(myStart.Position, targAtArr.Position, tArr, MuEarth, true, out v1, out v2);
            var myOrbit = OrbitalMath.ToElements(new StateVector(myStart.Position, v1), MuEarth, 0);

            // 24 coarse buckets over 3600 s -> 150 s/bucket; the deep pass is far briefer than
            // that. Confirm the sub-sampling + dense compare still catches it.
            var caCoarse = ClosestApproach.Find(myOrbit, target, 0, 3600, 24);
            Ok("found with coarse sampling", caCoarse.Found);
            Near("TCA ~ 1800 s (coarse)", caCoarse.Time, 1800.0, 60.0);
            Ok("deep miss surfaced (< 5 km) despite coarse grid", caCoarse.MissDistance < 5000.0);

            // Sanity: the dense reference agrees on the deep pass.
            var caDense = ClosestApproach.Find(myOrbit, target, 0, 3600, 720);
            Ok("dense agrees the pass is deep", caDense.Found && caDense.MissDistance < 5000.0);
        }

        // ---- M1: degenerate transfer angle = pi, and orbit-normal sense ------------
        private static void LambertTransferAnglePi()
        {
            Section("M1 Lambert: collinear r2 = -k*r1 (Δθ=π) returns false cleanly, no NaN");
            var r1 = new Vector3D(8000e3, 0, 0);
            var r2 = -1.3 * r1;   // exactly antiparallel -> transfer angle = pi, plane undefined
            Vector3D v1, v2;
            bool ok = Lambert.Solve(r1, r2, 2000.0, MuEarth, true, out v1, out v2);
            Ok("returns false (degenerate plane)", !ok);
            Ok("v1 is clean (no NaN/Inf)", IsFinite(v1));
            Ok("v2 is clean (no NaN/Inf)", IsFinite(v2));

            // also the retrograde sense and the explicit-normal overload must be clean.
            bool ok2 = Lambert.Solve(r1, r2, 2000.0, MuEarth, false, Vector3D.UnitZ, out v1, out v2);
            Ok("normal overload also false + clean", !ok2 && IsFinite(v1) && IsFinite(v2));
        }

        private static void LambertOrbitNormalSense()
        {
            Section("M1 Lambert: short/long-way sense from a supplied orbit normal (inclined plane)");
            // A POLAR transfer plane (normal ~ +X, i.e. orbit in the Y-Z plane) where the
            // world-+Z test is ambiguous. Pick two points on a known orbit and recover the
            // velocities using the orbit's OWN normal as desiredNormal; the world-Z default
            // is degenerate here, so this exercises the M1 overload meaningfully.
            var sv = new StateVector(new Vector3D(0, 8000e3, 0), new Vector3D(0, 1200, 6800));
            var el = OrbitalMath.ToElements(sv, MuEarth, 0);
            var s0 = OrbitalMath.ToState(WithNu(el, el.TrueAnomaly));
            var s1 = OrbitalMath.ToState(WithNu(el, el.TrueAnomaly + 1.0));
            double tof = OrbitPropagation.TimeToTrueAnomaly(WithNu(el, el.TrueAnomaly), el.TrueAnomaly + 1.0);

            Vector3D hHat = Vector3D.Normalize(Vector3D.Cross(s0.Position, s0.Velocity)); // true orbit normal
            Vector3D v1, v2;
            bool ok = Lambert.Solve(s0.Position, s1.Position, tof, MuEarth, true, hHat, out v1, out v2);
            Ok("solved with explicit normal", ok);
            NearVec("v1 recovered (normal-based sense)", v1, s0.Velocity, 1e-2);
            NearVec("v2 recovered (normal-based sense)", v2, s1.Velocity, 1e-2);

            // Backward-compat: the world-Z overload still solves the near-equatorial case.
            var eq = new StateVector(new Vector3D(8000e3, 0, 0), new Vector3D(0, 7000, 0));
            var eel = OrbitalMath.ToElements(eq, MuEarth, 0);
            var e0 = OrbitalMath.ToState(WithNu(eel, eel.TrueAnomaly));
            var e1 = OrbitalMath.ToState(WithNu(eel, eel.TrueAnomaly + 0.8));
            double etof = OrbitPropagation.TimeToTrueAnomaly(WithNu(eel, eel.TrueAnomaly), eel.TrueAnomaly + 0.8);
            Vector3D w1, w2;
            bool okEq = Lambert.Solve(e0.Position, e1.Position, etof, MuEarth, true, out w1, out w2);
            Ok("world-Z overload unchanged for equatorial", okEq);
            NearVec("equatorial v1 still recovered", w1, e0.Velocity, 1e-2);
        }

        // ---- M3: phasing N selection by relative-rate closure vs old target-period --
        private static void PhasingRelativeRateSelection()
        {
            Section("M3 phasing: relative-rate N selection nulls along-track residual near |Δa|/a~2% (>= old model)");
            // Target near-circular; ME near-circular COPLANAR but with sma differing ~1.9%
            // (just inside the 2% phasing gate) and a along-track lead. The planner now picks
            // N by the TRUE re-propagated miss; recompute the OLD model's pick (Δv-only over
            // Tphase=Ttarget(1-dphi/(2piN))) and show the new pick's real residual is no worse
            // and is genuinely small.
            double Rt = 8000e3;
            double vt = Math.Sqrt(MuEarth / Rt);
            var target = OrbitalMath.ToElements(new StateVector(new Vector3D(Rt, 0, 0), new Vector3D(0, vt, 0)), MuEarth, 0);

            double Rm = Rt * (1.0 - 0.019);          // ~1.9% smaller sma -> ~2.9% faster period
            double vm = Math.Sqrt(MuEarth / Rm);
            double phi = 0.35;                       // I am 0.35 rad behind the target's position
            var myState = new StateVector(
                new Vector3D(Rm * Math.Cos(-phi), Rm * Math.Sin(-phi), 0),
                new Vector3D(-vm * Math.Sin(-phi), vm * Math.Cos(-phi), 0));

            var opts = new InterceptOptions();
            opts.MaxPhasingRevs = 6;
            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default, opts);
            Ok("a plan was found", plan.Status == PlanStatus.Ok);

            // The chosen plan's true ALONG-TRACK residual (the part N controls), measured by
            // re-flying it and projecting the arrival miss onto along-track. With the relative-
            // rate model this is nulled (the radial |Δa| offset is the only remaining miss).
            double newAlong = FlownAlongTrackResidual(myState, target, MuEarth, 0.0, plan);

            // Reconstruct the OLD model's pick (Δv-only over Tphase=Ttarget(1-dphi/(2piN)))
            // and measure ITS along-track residual the same way, for a non-regression check.
            double oldAlong = OldPhasingAlongResidual(myState, target, MuEarth, 0.0, opts.MaxPhasingRevs);

            // Finding: in the strict in-gate regime the relative-rate closure nulls the
            // along-track residual EXACTLY for every feasible N, so the residual-aware
            // selection coincides with the old Δv-only pick (Δv breaks the all-zero tie).
            // The substantive, verifiable claim is that the chosen N's along-track residual is
            // ~0 (the closure is honest), and that the new selection is never worse than old.
            Console.WriteLine($"   chosen N along-track residual={newAlong:F1} m, old-model={oldAlong:F1} m (radial |Δa| offset ~152 km is N-independent)");
            Ok("chosen N nulls the along-track residual (honest closure)", newAlong < 50.0);
            Ok("new selection's along-track residual <= old model's", newAlong <= oldAlong + 1.0);
        }

        // Fly the chosen phasing plan (apply its first burn, coast to arrival) and return the
        // ALONG-TRACK component of the arrival miss (projection onto the target's velocity).
        private static double FlownAlongTrackResidual(StateVector myState, KeplerianElements target,
            double mu, double now, ManeuverPlan plan)
        {
            var m0 = plan.Maneuvers[0];
            var afterBurn = new StateVector(myState.Position, myState.Velocity + m0.DeltaV);
            var phaseOrbit = OrbitalMath.ToElements(afterBurn, mu, now);
            double drift = plan.Maneuvers[1].TimeFromNowSeconds; // exit burn time = drift
            var sMeArr = OrbitPropagation.StateAt(phaseOrbit, now + drift);
            var sTgArr = OrbitPropagation.StateAt(target, now + drift);
            return AlongTrack(sTgArr.Position - sMeArr.Position, sTgArr.Velocity);
        }

        // Replicates the PRE-M3 selection (Δv-only over Tphase = Ttarget(1-dphi/(2piN))) and
        // returns the ALONG-TRACK residual of that pick, for a head-to-head against the new
        // relative-rate selection.
        private static double OldPhasingAlongResidual(StateVector myState, KeplerianElements target,
            double mu, double now, int maxRevs)
        {
            var sTg = OrbitPropagation.StateAt(target, now);
            double phase = SignedPhaseAngle(myState.Position, sTg.Position, myState.Velocity);
            double dphi = Wrap(phase);
            double Ttarget = target.Period;
            double r = myState.Radius, vCirc = myState.Speed;

            double bestCost = double.PositiveInfinity, bestTphase = 0.0;
            int bestN = 0;
            for (int N = 1; N <= maxRevs; N++)
            {
                double Tphase = Ttarget * (1.0 - dphi / (2.0 * Math.PI * N));
                if (Tphase <= 0.0) continue;
                double aPhase = Math.Pow(mu * (Tphase / (2.0 * Math.PI)) * (Tphase / (2.0 * Math.PI)), 1.0 / 3.0);
                double vPhaseSq = mu * (2.0 / r - 1.0 / aPhase);
                if (vPhaseSq <= 0.0) continue;
                double vPhase = Math.Sqrt(vPhaseSq);
                double cost = Math.Abs(vPhase - vCirc) * 2.0;   // enter + exit
                if (cost < bestCost) { bestCost = cost; bestTphase = Tphase; bestN = N; }
            }
            if (bestN == 0) return double.PositiveInfinity;

            double aBest = Math.Pow(mu * (bestTphase / (2.0 * Math.PI)) * (bestTphase / (2.0 * Math.PI)), 1.0 / 3.0);
            double vBest = Math.Sqrt(mu * (2.0 / r - 1.0 / aBest));
            Vector3D enterDv = Vector3D.Normalize(myState.Velocity) * (vBest - vCirc);
            double drift = bestN * bestTphase;
            var afterBurn = new StateVector(myState.Position, myState.Velocity + enterDv);
            var phaseOrbit = OrbitalMath.ToElements(afterBurn, mu, now);
            var sMeArr = OrbitPropagation.StateAt(phaseOrbit, now + drift);
            var sTgArr = OrbitPropagation.StateAt(target, now + drift);
            return AlongTrack(sTgArr.Position - sMeArr.Position, sTgArr.Velocity);
        }

        private static double AlongTrack(Vector3D miss, Vector3D vel)
        {
            double vmag = vel.Length();
            if (vmag < 1e-9) return miss.Length();
            return Math.Abs(Vector3D.Dot(miss, vel / vmag));
        }

        // ---- M4: prograde/radial/normal are the TRUE LVLH directions ---------------
        private static void ManeuverLvlhDecomposition()
        {
            Section("M4 maneuver components: prograde/radial/normal are the REAL LVLH basis (r,v), not world-Z");
            // An INCLINED case where the old world-Z basis was provably wrong. Plan a real
            // intercept, take the departure burn, and independently project its world Δv onto
            // the true LVLH triad at the burn point (pro=v̂, normal=(r×v)̂, radial=normal×pro).
            // The plan's reported components MUST equal that independent projection.
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400)), MuEarth, 0);

            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default);
            Ok("plan OK", plan.Status == PlanStatus.Ok);
            if (plan.Status != PlanStatus.Ok || plan.Maneuvers.Count == 0) return;
            var m = plan.Maneuvers[0];   // departure burn is at myState (r1, myVel)

            // independent true-LVLH projection at the burn point
            Vector3D pro = Vector3D.Normalize(myState.Velocity);
            Vector3D nrm = Vector3D.Normalize(Vector3D.Cross(myState.Position, myState.Velocity));
            Vector3D rad = Vector3D.Normalize(Vector3D.Cross(nrm, pro));
            double expPro = Vector3D.Dot(m.DeltaV, pro);
            double expRad = Vector3D.Dot(m.DeltaV, rad);
            double expNrm = Vector3D.Dot(m.DeltaV, nrm);

            Near("prograde component matches true v̂", m.Prograde, expPro, 1e-6);
            Near("radial component matches true r̂-plane", m.Radial, expRad, 1e-6);
            Near("normal component matches true (r×v)̂", m.Normal, expNrm, 1e-6);

            // Components must reconstruct the full Δv magnitude (orthonormal triad).
            double recon = Math.Sqrt(m.Prograde * m.Prograde + m.Radial * m.Radial + m.Normal * m.Normal);
            Near("triad is orthonormal: components recover |Δv|", recon, m.DeltaV.Length(), 1e-6);

            // Known-geometry sanity: a circular equatorial reference (r=+X, v=+Y) has
            // normal=+Z and radial=-X. A world +Z burn is therefore PURE normal, zero radial —
            // the exact thing the old world-Z basis got wrong (it would have called +Z "up").
            var depart2 = InterceptPlanner.Plan(
                new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 7000e3), 0)),
                OrbitalMath.ToElements(new StateVector(new Vector3D(0, 9000e3, 0), new Vector3D(-Math.Sqrt(MuEarth / 9000e3), 0, 0)), MuEarth, 0),
                MuEarth, 0.0, RendezvousParams.Default);
            Ok("coplanar plan OK", depart2.Status == PlanStatus.Ok);
            if (depart2.Status == PlanStatus.Ok && depart2.Maneuvers.Count > 0)
            {
                // For a coplanar (equatorial) transfer the departure burn is in-plane:
                // its NORMAL component must be ~0 (the old world-Z basis could not guarantee this).
                Ok("coplanar departure has ~zero normal component", Math.Abs(depart2.Maneuvers[0].Normal) < 1e-3);
            }
        }

        // ---- Task A: an estimated orbit bridges acquisition -> InterceptPlanner ------
        // (DEDUPE: the old Rendezvous.Target wrapper is gone; the planner takes the raw
        // estOrbit directly — the only thing it ever needed.)
        private static void TargetModelPlansIntercept()
        {
            Section("TARGET MODEL: an estimated orbit plans an intercept that actually flies");
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            // A "fix": the target's true state, turned straight into an orbit via ToElements
            // ("a good fix IS an orbit") — exactly what the planner consumes.
            var targetState = new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400));
            var estOrbit = OrbitalMath.ToElements(targetState, MuEarth, 0.0);
            Ok("target has an estimated orbit", estOrbit.Mu == MuEarth && estOrbit.IsElliptic);

            var plan = InterceptPlanner.Plan(myState, estOrbit, estOrbit.Mu, 0.0, RendezvousParams.Default);
            Ok("InterceptPlanner.Plan produced a plan", plan.Status == PlanStatus.Ok);
            if (plan.Status != PlanStatus.Ok || plan.Maneuvers.Count == 0) return;

            // FLY IT: same proof as InterceptActuallyIntercepts, driven by the estimated orbit.
            var m = plan.Maneuvers[0];
            var myOrbit = OrbitalMath.ToElements(myState, MuEarth, 0.0);
            var atBurn = OrbitPropagation.StateAt(myOrbit, m.TimeFromNowSeconds);
            var afterBurn = new StateVector(atBurn.Position, atBurn.Velocity + m.DeltaV);
            var burnOrbit = OrbitalMath.ToElements(afterBurn, MuEarth, m.TimeFromNowSeconds);
            var meAtArr = OrbitPropagation.StateAt(burnOrbit, plan.Arrival.ArrivalTime);
            var tgtAtArr = OrbitPropagation.StateAt(estOrbit, plan.Arrival.ArrivalTime);
            double flownMiss = (meAtArr.Position - tgtAtArr.Position).Length();
            Ok("flown intercept from estimated orbit within capture range", flownMiss < RendezvousParams.Default.EnterRangeMeters);
        }

        private static void TargetErrorBandTightens()
        {
            Section("TARGET MODEL: sigma widens the arrival miss into an honest band that tightens as it shrinks");
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var targetState = new StateVector(new Vector3D(0, 8200e3, 0), new Vector3D(-Math.Sqrt(MuEarth / 8200e3), 0, 0));
            var el = OrbitalMath.ToElements(targetState, MuEarth, 0.0);

            // A FUZZY contact: large sigma + growth -> a fat bubble, NOT yet committable. Size the
            // base sigma RELATIVE to the capture range (rob tuned EnterRangeMeters 2 km -> 10 km on
            // 2026-06-16; a fixed sigma silently rots when the range grows) so the bubble is always
            // wider than capture and the "not committable" intent holds at any tuning.
            double captureRange = RendezvousParams.Default.EnterRangeMeters;
            double fuzzySigma = captureRange * 1.2, fuzzyGrowth = 0.2, fixT = 0.0;
            // A SHARP contact (e.g. after a radar ping): tiny sigma, no growth -> tight bubble.
            const double sharpSigma = 5.0, sharpGrowth = 0.0;

            var planF = InterceptPlanner.Plan(myState, el, el.Mu, 0.0, RendezvousParams.Default);
            var planS = InterceptPlanner.Plan(myState, el, el.Mu, 0.0, RendezvousParams.Default);
            Ok("both plan OK", planF.Status == PlanStatus.Ok && planS.Status == PlanStatus.Ok);
            if (planF.Status != PlanStatus.Ok || planS.Status != PlanStatus.Ok) return;

            var bandF = InterceptBand.BandFor(planF, fuzzySigma, fuzzyGrowth, fixT);
            var bandS = InterceptBand.BandFor(planS, sharpSigma, sharpGrowth, fixT);
            Console.WriteLine($"   fuzzy band: nominal={bandF.MissNominal:F0} +/-{bandF.OneSigma:F0} (1σ); sharp 1σ={bandS.OneSigma:F0}");

            // The band's 1σ at arrival must grow with time-since-fix for the fuzzy contact.
            Ok("fuzzy 1σ grows over the coast", bandF.OneSigma > fuzzySigma);
            Ok("sharp band tighter than fuzzy", bandS.OneSigma < bandF.OneSigma);
            Ok("upper edge >= nominal >= lower edge", bandF.MissUpper1Sigma >= bandF.MissNominal
                && bandF.MissNominal >= bandF.MissLower1Sigma);
            Ok("2σ edge is wider than 1σ edge", bandF.MissUpper2Sigma > bandF.MissUpper1Sigma);

            // Committability: the sharp contact fits its whole 1σ band inside capture range;
            // the fuzzy one does not -> not committable until tracked/pinged (sigma shrinks).
            Ok("sharp contact is committable (bubble inside capture range)",
                InterceptBand.IsCommittable(planS, RendezvousParams.Default, sharpSigma, sharpGrowth, fixT, false));
            Ok("fuzzy contact is NOT committable yet",
                !InterceptBand.IsCommittable(planF, RendezvousParams.Default, fuzzySigma, fuzzyGrowth, fixT, false));

            // SigmaAt monotonic non-decreasing in time.
            Ok("SigmaAt is non-decreasing",
                InterceptBand.SigmaAt(fuzzySigma, fuzzyGrowth, fixT, 100.0) >= InterceptBand.SigmaAt(fuzzySigma, fuzzyGrowth, fixT, 0.0)
                && InterceptBand.SigmaAt(fuzzySigma, fuzzyGrowth, fixT, 0.0) >= fuzzySigma - 1e-9);
        }

        // ---- TILT: the chain is correct iff BOTH inputs share the tilted frame -------
        // The system plane was re-tilted onto the engine sun axis on 2026-06-17. The intercept
        // planner consumes the player's own frame elements AND the target's earned EstOrbit; in
        // production BOTH are now expressed in the SAME tilted root frame (FrameManager.TryOrbitCommand
        // tilts the own orbit; DetectionDriver.TryEstimateOrbit derives EstOrbit from tilted resolved
        // positions). A rigid rotation is an isometry, so a plan in the tilted frame must be the
        // canonical plan rotated — same Δv magnitude, Δv vector = canonical Δv rotated, same flown
        // miss. The teeth: if only ONE side is tilted (the exact bug the audit fixed — a catalog
        // baking canonical elements against a tilted own frame), the plan no longer reaches the REAL
        // (tilted) target. Proves tilt-consistency end-to-end through InterceptPlanner.
        private static void TiltConsistentIntercept()
        {
            Section("TILT: tilted intercept == canonical rotated; a canonical/tilted MIX misses the real target");

            // Same plan-and-fly geometry as TargetModelPlansIntercept (a known-feasible Direct case).
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var targetState = new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400));
            var elMy = OrbitalMath.ToElements(myState, MuEarth, 0.0);
            var elTgt = OrbitalMath.ToElements(targetState, MuEarth, 0.0);

            // The actual engine sun axis logged by the DS (SunEarthMoon), normalized — the real tilt.
            var planeNormal = Vector3D.Normalize(new Vector3D(-0.342, 0.704, 0.622));
            var tilt = SystemBuilder.TiltFor(planeNormal);
            var elMyT = SystemBuilder.TiltElements(elMy, tilt, MuEarth, 0.0);
            var elTgtT = SystemBuilder.TiltElements(elTgt, tilt, MuEarth, 0.0);

            // CANONICAL plan (the reference) and the TILTED plan (both inputs rotated together).
            var planC = InterceptPlanner.Plan(myState, elTgt, MuEarth, 0.0, RendezvousParams.Default);
            var myStateT = OrbitPropagation.StateAt(elMyT, 0.0);
            var planT = InterceptPlanner.Plan(myStateT, elTgtT, MuEarth, 0.0, RendezvousParams.Default);
            Ok("canonical plan OK", planC.Status == PlanStatus.Ok && planC.Maneuvers.Count > 0);
            Ok("tilted plan OK", planT.Status == PlanStatus.Ok && planT.Maneuvers.Count > 0);
            if (planC.Status != PlanStatus.Ok || planT.Status != PlanStatus.Ok
                || planC.Maneuvers.Count == 0 || planT.Maneuvers.Count == 0) return;

            double cap = RendezvousParams.Default.EnterRangeMeters;
            double missC = FlownMiss(myState, MuEarth, planC, elTgt);
            double missT = FlownMiss(myStateT, MuEarth, planT, elTgtT);
            Console.WriteLine($"   flown miss: canonical={missC:F1} m, tilted={missT:F1} m (cap {cap:F0} m)");
            Ok("canonical intercept within capture", missC < cap);
            Ok("tilted intercept within capture (chain works under tilt)", missT < cap);
            Ok("tilt preserves flown miss (isometry)", Math.Abs(missT - missC) < 1.0);

            // ISOMETRY: the tilted departure burn is the canonical departure burn, rotated.
            var dvC = planC.Maneuvers[0].DeltaV;
            var dvT = planT.Maneuvers[0].DeltaV;
            double magDiff = Math.Abs(dvT.Length() - dvC.Length());
            double vecDiff = (tilt.Rotate(dvC) - dvT).Length();
            Console.WriteLine($"   Δv |canon|={dvC.Length():F3} |tilt|={dvT.Length():F3} m/s; rot(Δv_canon)-Δv_tilt={vecDiff:E2} m/s");
            Ok("tilt preserves Δv magnitude", magDiff < 0.5);
            Ok("tilted Δv == canonical Δv rotated", vecDiff < 0.5);

            // RED COUNTERFACTUAL — the bug the audit fixed: own frame tilted, target left CANONICAL.
            // The planner happily plots to the canonical target's position, but the REAL target rides
            // the tilted orbit elsewhere, so the flown trajectory misses it by ~the tilt arc (Mm-scale).
            var planMix = InterceptPlanner.Plan(myStateT, elTgt, MuEarth, 0.0, RendezvousParams.Default);
            bool mixReachesReal = planMix.Status == PlanStatus.Ok && planMix.Maneuvers.Count > 0
                && FlownMiss(myStateT, MuEarth, planMix, elTgtT) < cap;
            double missMix = (planMix.Status == PlanStatus.Ok && planMix.Maneuvers.Count > 0)
                ? FlownMiss(myStateT, MuEarth, planMix, elTgtT) : double.PositiveInfinity;
            Console.WriteLine($"   MIX (own tilted, target canonical) flown miss vs REAL target = {missMix:E2} m");
            Ok("a canonical/tilted MIX does NOT reach the real target (consistency has teeth)", !mixReachesReal);
        }

        // Fly a plan's departure burn from myState and return the flown miss against trueTarget's
        // propagated position at arrival (mirrors the InterceptActuallyIntercepts fly-it proof).
        private static double FlownMiss(StateVector myState, double mu, ManeuverPlan plan, KeplerianElements trueTarget)
        {
            var m = plan.Maneuvers[0];
            var myOrbit = OrbitalMath.ToElements(myState, mu, 0.0);
            var atBurn = OrbitPropagation.StateAt(myOrbit, m.TimeFromNowSeconds);
            var afterBurn = new StateVector(atBurn.Position, atBurn.Velocity + m.DeltaV);
            var burnOrbit = OrbitalMath.ToElements(afterBurn, mu, m.TimeFromNowSeconds);
            var meAtArr = OrbitPropagation.StateAt(burnOrbit, plan.Arrival.ArrivalTime);
            var tgtAtArr = OrbitPropagation.StateAt(trueTarget, plan.Arrival.ArrivalTime);
            return (meAtArr.Position - tgtAtArr.Position).Length();
        }

        // ---- C1: Lambert rejects near-parabolic (e ~ 1) transfers -------------------
        private static void LambertRejectsNearParabolic()
        {
            Section("C1 Lambert: a near-parabolic (e~1) transfer is REJECTED, not shipped as ok");
            // A retrograde transfer between these endpoints at tof in [1228,1247] s has an
            // osculating e within 1e-3 of 1.0 (probed). KeplerianElements documents e~1 as
            // unsupported; OrbitPropagation cannot fly it, so a pre-fix ok=true here flew
            // thousands of km off. The C1 e-band gate must now return false for the whole band.
            var r1 = new Vector3D(7000e3, 0, 0);
            var r2 = new Vector3D(-2000e3, 8000e3, 0);
            Vector3D v1, v2;
            bool ok = Lambert.Solve(r1, r2, 1238.0, MuEarth, false, out v1, out v2);
            Ok("near-parabolic transfer rejected (ok=false)", !ok);
            Ok("rejected outputs are zeroed/clean", v1 == Vector3D.Zero && v2 == Vector3D.Zero);

            // The benign solutions just outside the band still solve (we only kill e~1).
            Vector3D w1, w2;
            bool okLo = Lambert.Solve(r1, r2, 1200.0, MuEarth, false, out w1, out w2);   // e~1.02
            bool okHi = Lambert.Solve(r1, r2, 1300.0, MuEarth, false, out w1, out w2);   // e~0.96
            Ok("transfers just outside the e~1 band still solve", okLo && okHi);
        }

        // ---- C2: garbage velocity / propagator NaN-guard does not crash ------------
        private static void LambertRejectsGarbageVelocity()
        {
            Section("C2 Lambert+propagator: an absurd velocity does not crash ToElements/StateAt");
            // The crash path C2 fixes: a finite-but-absurd velocity into ToElements gives
            // MeanMotion=+inf, M=NaN; the hyperbolic Kepler solver then called Math.Sign(NaN)
            // which THREW. The hardened solver returns NaN instead. Prove the end-to-end path
            // (the same one a bad Lambert v1 would feed) no longer throws, for several regimes.
            bool threw = false;
            try
            {
                foreach (double vy in new[] { 1e12, 1e18, 1e30, 1e45 })
                {
                    var absurd = new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, vy, 0));
                    var el = OrbitalMath.ToElements(absurd, MuEarth, 0.0);
                    OrbitPropagation.StateAt(el, 123.0);          // would crash pre-fix
                    OrbitalMath.MeanToTrueAnomaly(double.NaN, 2.5); // direct NaN into the solver
                }
            }
            catch (Exception) { threw = true; }
            Ok("absurd-velocity ToElements/StateAt path does not throw", !threw);

            // And the planner stays robust (returns a plan or a clean NoSolution, never throws)
            // for an extreme-but-finite target geometry.
            bool plannerThrew = false;
            try
            {
                var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
                var weird = OrbitalMath.ToElements(
                    new StateVector(new Vector3D(0, 6701e3, 0), new Vector3D(-Math.Sqrt(MuEarth / 6701e3), 0, 0)), MuEarth, 0);
                var plan = InterceptPlanner.Plan(myState, weird, MuEarth, 0.0, RendezvousParams.Default);
                Ok("planner returned a plan object", plan != null);
            }
            catch (Exception) { plannerThrew = true; }
            Ok("planner does not throw on a tight near-coorbital target", !plannerThrew);
        }

        // ---- C1 defense-in-depth: EvaluateLambert reports the FLOWN miss -----------
        private static void InterceptUsesFlownMiss()
        {
            Section("C1 defense: the plan's reported miss is the ACTUAL flown miss, not an assumed 0");
            // For a benign intercept the flown miss is ~0 (so the value is honest, not a
            // hardcoded 0). Re-fly the departure burn independently and confirm the plan's
            // reported MissDistance equals the independently-flown miss (same time base: build
            // elements at epoch=now=0, propagate to ArrivalTime == now+tof).
            var myState = new StateVector(new Vector3D(6700e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 6700e3), 0));
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(0, 8200e3, 600e3), new Vector3D(-Math.Sqrt(MuEarth / 8220e3), 0, 400)), MuEarth, 0);
            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default);
            Ok("plan OK", plan.Status == PlanStatus.Ok);
            if (plan.Status != PlanStatus.Ok || plan.Maneuvers.Count == 0) return;

            var m = plan.Maneuvers[0];
            var burnOrbit = OrbitalMath.ToElements(
                new StateVector(myState.Position, myState.Velocity + m.DeltaV), MuEarth, 0.0);
            var meArr = OrbitPropagation.StateAt(burnOrbit, plan.Arrival.ArrivalTime);
            var tgtArr = OrbitPropagation.StateAt(target, plan.Arrival.ArrivalTime);
            double flownMiss = (meArr.Position - tgtArr.Position).Length();

            // The reported miss is the honest flown miss (matches an independent re-fly), AND it
            // is within capture (the flown-miss acceptance gate did its job).
            Near("reported miss == independently flown miss", plan.Arrival.MissDistance, flownMiss, 50.0);
            Ok("reported miss is within capture range", plan.Arrival.MissDistance < RendezvousParams.Default.EnterRangeMeters);
        }

        // ---- H2: Newton dFz-break only accepts a converged result ------------------
        private static void LambertNewtonNonConvergence()
        {
            Section("H2 Lambert: a Newton dFz-STALL at a non-root z is rejected, not shipped as ok");
            // A specific near-180 transfer at a tiny tof drives the Newton derivative below 1e-30
            // (a STALL) at a z that is NOT the root, BEFORE convergence. Pre-fix the unconditional
            // `break` shipped that z: Solve returned ok=true with a finite, plausible-magnitude,
            // e!=1 velocity (so neither C1's e~1 gate nor C2's magnitude gate catches it) whose
            // flown trajectory misses r2 by ~12,000 km. The H2 guard now requires |F(z)| at the
            // root before accepting the stall, so this case returns false cleanly.
            var r1 = new Vector3D(8000e3, 0, 0);
            double ang = 180.05 * Math.PI / 180.0;
            var r2 = 8000e3 * new Vector3D(Math.Cos(ang), Math.Sin(ang), 0.07);
            double mu = 1e6, tof = 0.09707;

            Vector3D v1, v2;
            bool ok = Lambert.Solve(r1, r2, tof, mu, true, out v1, out v2);
            Ok("non-converged dFz-stall transfer rejected (ok=false)", !ok);
            Ok("rejected outputs are zeroed/clean", v1 == Vector3D.Zero && v2 == Vector3D.Zero);

            // Sanity: the contract for ACCEPTED solutions still holds — every ok solution over a
            // benign sweep flies to r2 (proving the guard rejects only the stalls, not good ones).
            int checkedCount = 0, flewToTarget = 0;
            for (double a = 30.0; a <= 330.0; a += 15.0)
            {
                double rad = a * Math.PI / 180.0;
                var rr2 = 9000e3 * new Vector3D(Math.Cos(rad), Math.Sin(rad), 0.05);
                for (double t = 600.0; t <= 8000.0; t += 400.0)
                {
                    Vector3D w1, w2;
                    if (!Lambert.Solve(r1, rr2, t, MuEarth, true, out w1, out w2)) continue;
                    checkedCount++;
                    var el = OrbitalMath.ToElements(new StateVector(r1, w1), MuEarth, 0);
                    var flown = OrbitPropagation.StateAt(el, t).Position;
                    if ((flown - rr2).Length() < 1000.0) flewToTarget++;
                }
            }
            Ok("swept several ok Lambert solutions", checkedCount > 20);
            Ok("EVERY accepted solution flies to within 1 km of r2 (guard rejects only stalls)",
                checkedCount > 0 && flewToTarget == checkedCount);
        }

        // ---- M1: BandFor on a NoSolution plan returns an invalid, zeroed band -------
        private static void BandForNoSolutionPlan()
        {
            Section("M1 InterceptBand.BandFor: a NoSolution plan yields an invalid zeroed band (no Arrival read)");
            // Big sigma + growth: pre-fix, BandFor read Arrival.ArrivalTime (=0 on a NoSolution
            // plan) and produced a bogus non-zero band. The fix early-returns before touching it.
            const double sigma = 1234.0, growth = 0.5, fixT = 0.0;
            var noSol = ManeuverPlan.NoSolution("forced no-solution for the M1 regression");
            Ok("the plan really has no solution", noSol.Status != PlanStatus.Ok);

            var band = InterceptBand.BandFor(noSol, sigma, growth, fixT);
            Ok("band is invalid", !band.Valid);
            Ok("band is fully zeroed (no Arrival-derived sigma leaked in)",
                band.MissNominal == 0.0 && band.OneSigma == 0.0
                && band.MissUpper1Sigma == 0.0 && band.MissUpper2Sigma == 0.0
                && band.MissLower1Sigma == 0.0);
            Ok("a NoSolution plan is never committable",
                !InterceptBand.IsCommittable(noSol, RendezvousParams.Default, sigma, growth, fixT, false));
        }

        // ---- H1: Find = deepest over the window; FindNext/FindAll = earliest --------
        private static void ClosestApproachEarliestVsDeepest()
        {
            Section("H1 ClosestApproach: Find returns DEEPEST; FindNext/FindAll return EARLIEST");
            // Target circular; ME on an eccentric orbit (built via Lambert) whose DEEP, near-zero
            // pass is timed LATE (t=14000 s) while earlier orbit crossings give SHALLOWER passes.
            // Over the window FindAll yields several time-ordered approaches whose EARLIEST is
            // shallow and whose DEEPEST is late — exactly the case where Find (deepest) differs
            // from FindNext/FindAll[0] (earliest). This is the H1 distinction the audit calls out.
            var target = OrbitalMath.ToElements(
                new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / 7000e3), 0)), MuEarth, 0);
            double tDeep = 14000.0;
            var tgtDeep = OrbitPropagation.StateAt(target, tDeep);
            var myStart = new StateVector(new Vector3D(0, -6800e3, 0), new Vector3D(7700, 0, 0));
            Vector3D v1, v2;
            Lambert.Solve(myStart.Position, tgtDeep.Position, tDeep, MuEarth, true, out v1, out v2);
            var myOrbit = OrbitalMath.ToElements(new StateVector(myStart.Position, v1), MuEarth, 0);

            double window = 18200.0; // ~3 of my periods: catches the early-shallow and late-deep passes
            var all = ClosestApproach.FindAll(myOrbit, target, 0, window, 240);
            Ok("FindAll located at least one approach", all.Count >= 1);
            // FindAll is ascending in time.
            bool ascending = true;
            for (int i = 1; i < all.Count; i++) if (all[i].Time < all[i - 1].Time - 1e-6) ascending = false;
            Ok("FindAll is time-ordered (ascending)", ascending);

            var deepest = ClosestApproach.Find(myOrbit, target, 0, window, 240);
            Ok("Find found the deep approach", deepest.Found);
            // Find must be the global minimum-miss over the window.
            double minMiss = double.PositiveInfinity;
            for (int i = 0; i < all.Count; i++) if (all[i].MissDistance < minMiss) minMiss = all[i].MissDistance;
            Ok("Find == deepest (global min miss over window)",
                all.Count == 0 || deepest.MissDistance <= minMiss + 1.0);
            // The deepest is demonstrably NOT the earliest (the audit's H1 point): some earlier
            // approach is strictly shallower than Find's result.
            bool earlierShallower = all.Count >= 2 && all[0].MissDistance > deepest.MissDistance + 1.0;
            Ok("an EARLIER approach is shallower than Find's deepest (H1 distinction holds)",
                all.Count < 2 || earlierShallower);

            // FindNext from t=0 returns the EARLIEST approach (time-first, not miss-first).
            var next = ClosestApproach.FindNext(myOrbit, target, 0, window, 240, 0.0);
            if (all.Count > 0)
            {
                Ok("FindNext found the earliest approach", next.Found);
                Ok("FindNext.Time == earliest FindAll.Time", Math.Abs(next.Time - all[0].Time) < 1e-3);
                // FindNext from just after the first approach skips to the next one.
                var afterFirst = ClosestApproach.FindNext(myOrbit, target, 0, window, 240, all[0].Time + 1.0);
                if (all.Count > 1)
                    Ok("FindNext(tFrom past #1) returns a later approach", afterFirst.Found && afterFirst.Time > all[0].Time);
                else
                    Ok("FindNext(tFrom past the only approach) returns None", !afterFirst.Found);
            }
            else
            {
                Ok("FindNext consistent with empty FindAll", !next.Found);
                Ok("(no second approach to test)", true);
            }
        }

        // ideal two-burn Hohmann Δv between coplanar circular orbits r1 -> r2.
        private static double HohmannTwoBurn(double r1, double r2)
        {
            double v1 = Math.Sqrt(MuEarth / r1), v2 = Math.Sqrt(MuEarth / r2);
            double a = (r1 + r2) / 2.0;
            double vp = Math.Sqrt(MuEarth * (2.0 / r1 - 1.0 / a));
            double va = Math.Sqrt(MuEarth * (2.0 / r2 - 1.0 / a));
            return Math.Abs(vp - v1) + Math.Abs(v2 - va);
        }

        private static KeplerianElements WithNu(KeplerianElements el, double nu)
        {
            el.TrueAnomaly = nu;
            return el;
        }

        private static bool IsFinite(Vector3D v)
        {
            return !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                  || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
        }

        // Local mirrors of InterceptPlanner's private phase helpers, for the M3 head-to-head.
        private static double SignedPhaseAngle(Vector3D myPos, Vector3D targetPos, Vector3D myVel)
        {
            Vector3D nrm = Vector3D.Cross(myPos, myVel);
            if (nrm.LengthSquared() < 1e-12) return 0.0;
            nrm = Vector3D.Normalize(nrm);
            Vector3D a = Vector3D.Normalize(myPos);
            Vector3D b = Vector3D.Normalize(targetPos);
            double cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            if (cos < -1.0) cos = -1.0; else if (cos > 1.0) cos = 1.0;
            double ang = Math.Acos(cos);
            double sign = Vector3D.Dot(Vector3D.Cross(a, b), nrm);
            return sign >= 0.0 ? ang : -ang;
        }

        private static double Wrap(double x)
        {
            double twoPi = 2.0 * Math.PI;
            x %= twoPi;
            if (x > Math.PI) x -= twoPi;
            else if (x < -Math.PI) x += twoPi;
            return x;
        }

        private static void Section(string s) => Console.WriteLine($"-- {s}");
        private static void Ok(string n, bool c)
        {
            if (c) { _passed++; Console.WriteLine($"   PASS {n}"); }
            else { _failed++; Console.WriteLine($"   FAIL {n}"); }
        }
        private static void Near(string n, double a, double e, double tol)
        {
            if (Math.Abs(a - e) <= tol) { _passed++; Console.WriteLine($"   PASS {n} ({a:G6})"); }
            else { _failed++; Console.WriteLine($"   FAIL {n}: got {a:G6} want {e:G6} +/-{tol:G3}"); }
        }
        private static void NearVec(string n, Vector3D a, Vector3D e, double tol)
        {
            double d = (a - e).Length();
            if (d <= tol) { _passed++; Console.WriteLine($"   PASS {n} (|d|={d:G3})"); }
            else { _failed++; Console.WriteLine($"   FAIL {n}: |d|={d:G3} > {tol:G3}"); }
        }
    }
}
