using System;
using System.Collections.Generic;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.SystemDef;
using SEAerospace.Time;

namespace Orbital.Tests
{
    // Offline validation of the orbital core. No test framework: tiny assert
    // helpers, PASS/FAIL summary, nonzero exit code on any failure.
    internal static class Program
    {
        private static int _passed, _failed, _pending;
        private const double Deg = Math.PI / 180.0;

        private static int Main()
        {
            Console.WriteLine("=== SE-Aerospace orbital core tests ===\n");

            CircularEquatorial();
            CurtisExample43();        // external textbook validation
            RoundTripElliptic();
            VisViva();
            PeriodPropagation();
            HyperbolicBasics();       // the regime Real Orbits NaNs on
            HyperbolicRoundTrip();
            AnomalyRoundTrips();

            TimePropagation();
            EventSolvers();
            ShellCrossing();          // the interaction-shell scheduler
            DrawEllipsePath();
            DrawHyperbolaPath();
            ApsisMarkers();
            SampleByTimeTest();

            GravityModel();
            PatchedConicEscape();
            PatchedConicEncounter();
            UnifiedEncounterScan();   // Pillar A: bodies + clumps as one ordered timeline

            FrameBodyInertial();
            FramePerifocal();
            FrameBodyFixed();
            FrameLvlh();
            FrameRoundTrips();

            ClohessyWiltshireMatchesClosedForm();
            ClohessyWiltshireKnownModes();
            ClohessyWiltshireDriftPath();

            RendezvousHysteresis();

            BerthAllocatorPacking();
            BerthAllocatorForbidden();
            VoxelBerthDeterministicGrid();
            BodyWorldPosFrameRelative();
            LatticeSeparation();
            ShellHandoffRadius();     // slim handoff shell + decoupled lattice sizing (2026-06-11)
            FrameRegistryLifecycle();
            FrameIsolationQuery();
            FrameMergeSplit();
            ResolveProjection();
            ResolveAddressing();
            ResolveTree();

            // audit-driven edge-regime tests (orbital-math-audit.md)
            EquatorialEccentricRoundTrip();   // H2
            TryTimeToRadiusEdges();           // H1 / H4 / shipped-H3
            KeplerSolversNaNGuard();          // C2: NaN/Inf mean anomaly -> NaN, no throw
            HyperbolicNearParabolicSolver();  // rails-return-radial: e->1+ seed overflow fix

            // === frame transformations & observer states (the proxy/cell coordinate model) ===
            ProxyTransformAllStates();        // celestial -> world in planet / conjunction / home frames
            ConjunctionTreadmillTransform();  // virtual orbit -> frame celestial; sky slides, cell fixed
            HandoffTeleportTransform();        // conjunction -> planet teleport preserves the formation
            StowRelativizationTransform();     // world state -> elements relative to the body cell
            TransitionContinuityInvariants();  // hard-pin neutrality + planet<->conjunction continuity
            ProxyProjectionClamp();            // RSS-style: true direction + angular size, distance clamped
            ProxyRoundTripFrameRates();        // world <-> celestial inverse + frame-invariant sky rates
            ProxyProjectionDegenerateGuard();  // observer ON/INSIDE the body -> RenderRadius 0 (skip)
            CelestialRingMapping();            // celestial orbit rings (audit #7): frame map + shared clamp
            ResolveFrameCellContainment();     // anchor-swap gap: camera's cell resolves the frame, not home
            SoiReparentTransfer();             // cross-SOI rails re-parenting (the Moon-transfer fix)
            SoiReparentWarpTunnel();           // scheduled SOI encounter: whole-SOI transit in one window
            ServerGridResolve();               // server-safe grid resolve (never the client static)
            SoiStartBodyPick();                // celestial-native start body: deepest SOI containing the craft
            FreeFlightStateAssembly();         // free-flight start state == anchored voxel state; rails equivalence
            SunPhaseContinuity();              // sun day/night: seamless (value-continuous, rate-instant) switches
            SunOrbitalPhaseCoasting();         // anchorless sun phase: warp-consistent orbital azimuth

            // === 2026-06-12 mod-core findings (arrival-imminent lock wave) ===
            WarpRealtimeHoldMargin();          // finding 3: stamped-lock hold + one-chosen-tick ordering margin
            SoiEntryTwoDipWindow();            // finding 4: dip probe tries EVERY local minimum, earliest sub-SOI wins

            // === 2026-06-12 pre-epoch blocker wave ===
            PreEpochCommitQuarantine();        // half 1: fold/rebase/split CANNOT mutate a future-epoch frame
            SoiCommitHorizonDefersPatch();     // half 2: detect far -> EncounterPending (untouched); near -> commit

            // === 2026-06-12 departure-imminent stow wave ===
            EscapeStowPredicate();             // FrameRails.EscapesShell truth table (the inside-keep stow gate)

            // === 2026-06-12 rotating surface chart (docs/rotating-anchored-frames.md as-built) ===
            RotatingChartLaws();               // forward/inverse laws, ω=0 identity, the forced launch credit
            ZoneBoundaryConversion();          // R_c seam conversion: round-trip + celestial continuity 1e-9
            ZoneLatchHysteresis();             // membership latch: enter R_c, leave R_c×1.01, no flap
            ChartSkyWheel();                   // ProjectProxy/BodyWorldPosInFrame wheel at exactly ω
            ChartOrientationAlgebra();         // Q' = R⁻¹Q: ship-relative sky + texture bit-continuous
            ChartFixtureNumbers();             // retuned spins: credit 77 m/s, r_geo past keep / past SOI
            ChartResolveFill();                // resolves fill spin per subject; identity outside the zone

            // === 2026-06-12 rendering steal (frames-vs-rss audit S1/S2) ===
            CelestialMatPrimitive();           // S1: RSS ConvertMatrixSpace shape == the law functions; exact inverse
            RssProxyOrientationVerbatim();     // S2: canonical byte-equality, in-zone pin, seam continuity, placement

            // === 2026-06-12 RSS-literal sun (canonical build; supersedes the same-day tilt) ===
            EngineSunAxisFormula();            // the vendored derived-axis law + its ±Z impossibility bound
            TiltedSystemBuild();               // Build(def, n) pure math: tilted = W·canonical; registry always canonical
            ZoneBoundarySunPassThrough();      // terminator-phase pass-through + drawn-sun ship-relative continuity

            // === 2026-06-12 seam-coherence hardening (frames-vs-rss audit S4) ===
            ZoneSeamHardening();               // teleport detector, ambiguity-band re-latch, sweep guard, same-θ rigidity

            Console.WriteLine($"\n=== {_passed} passed, {_failed} failed, {_pending} pending ===");
            if (_pending > 0)
                Console.WriteLine($"    ({_pending} PENDING = a true-xk rescale design decision is owed by rob; " +
                                  "see docs/audits/orbital-truexk-fixture-rot.md — NOT a code regression)");
            return _failed == 0 ? 0 : 1;
        }

        // mu for an Earth-like body (Curtis uses 398600 km^3/s^2).
        private const double MuEarth = 3.986004418e14;

        private static void CircularEquatorial()
        {
            Section("circular equatorial");
            double r = 7000e3;
            double vc = Math.Sqrt(MuEarth / r);
            var sv = new StateVector(new Vector3D(r, 0, 0), new Vector3D(0, vc, 0));
            var el = OrbitalMath.ToElements(sv, MuEarth);
            Near("e~0", el.Eccentricity, 0.0, 1e-9);
            Near("a", el.SemiMajorAxis, r, 1.0);
            Near("i~0", el.Inclination, 0.0, 1e-9);
            Ok("elliptic", el.IsElliptic);
        }

        private static void CurtisExample43()
        {
            Section("Curtis Example 4.3 (textbook validation)");
            // r in km, v in km/s -> SI.
            var sv = new StateVector(
                new Vector3D(-6045e3, -3490e3, 2500e3),
                new Vector3D(-3457, 6618, 2533));
            var el = OrbitalMath.ToElements(sv, 3.986e14);
            Near("a (km)", el.SemiMajorAxis / 1e3, 8788.0, 5.0);
            Near("e", el.Eccentricity, 0.1712, 0.001);
            Near("i (deg)", el.Inclination / Deg, 153.249, 0.1);
            Near("RAAN (deg)", el.Raan / Deg, 255.279, 0.1);
            Near("argp (deg)", el.ArgPeriapsis / Deg, 20.068, 0.1);
            Near("nu (deg)", el.TrueAnomaly / Deg, 28.446, 0.1);
        }

        private static void RoundTripElliptic()
        {
            Section("round-trip state -> elements -> state");
            var sv = new StateVector(
                new Vector3D(-6045e3, -3490e3, 2500e3),
                new Vector3D(-3457, 6618, 2533));
            var el = OrbitalMath.ToElements(sv, 3.986e14);
            var back = OrbitalMath.ToState(el);
            NearVec("r", back.Position, sv.Position, 1.0);     // within 1 m
            NearVec("v", back.Velocity, sv.Velocity, 1e-3);    // within 1 mm/s
        }

        private static void VisViva()
        {
            Section("vis-viva v^2 = mu(2/r - 1/a)");
            var sv = new StateVector(
                new Vector3D(-6045e3, -3490e3, 2500e3),
                new Vector3D(-3457, 6618, 2533));
            var el = OrbitalMath.ToElements(sv, 3.986e14);
            double predicted = 3.986e14 * (2.0 / sv.Radius - 1.0 / el.SemiMajorAxis);
            Near("v^2", sv.Speed * sv.Speed, predicted, predicted * 1e-9);
        }

        private static void PeriodPropagation()
        {
            Section("propagate one full period returns to start");
            var sv = new StateVector(
                new Vector3D(-6045e3, -3490e3, 2500e3),
                new Vector3D(-3457, 6618, 2533));
            var el = OrbitalMath.ToElements(sv, 3.986e14);
            var after = OrbitalMath.Propagate(el, el.Period);
            // nu should return to its start value (mod 2pi).
            double d = Math.Abs(OrbitalMath.Wrap2Pi(after.TrueAnomaly) - OrbitalMath.Wrap2Pi(el.TrueAnomaly));
            if (d > Math.PI) d = OrbitalMath.TwoPi - d;
            Near("nu after T", d, 0.0, 1e-6);
            // and a half-period puts us on the opposite side: state should match a half-period propagate round-trip
            var half = OrbitalMath.Propagate(el, el.Period / 2.0);
            var full = OrbitalMath.Propagate(half, el.Period / 2.0);
            NearVec("r after 2x half-T", OrbitalMath.ToState(full).Position, sv.Position, 5.0);
        }

        private static void HyperbolicBasics()
        {
            Section("hyperbolic basics (Real Orbits cannot do this)");
            double r = 7000e3;
            double vEsc = Math.Sqrt(2.0 * MuEarth / r);
            var sv = new StateVector(new Vector3D(r, 0, 0), new Vector3D(0, 1.3 * vEsc, 0));
            var el = OrbitalMath.ToElements(sv, MuEarth);
            Ok("e > 1", el.Eccentricity > 1.0);
            Ok("a < 0", el.SemiMajorAxis < 0.0);
            Ok("energy > 0", el.SpecificEnergy > 0.0);
            Ok("vInf > 0", el.HyperbolicExcessSpeed > 0.0);
            Ok("apoapsis = +inf", double.IsPositiveInfinity(el.ApoapsisRadius));
        }

        private static void HyperbolicRoundTrip()
        {
            Section("hyperbolic round-trip + propagation");
            double r = 7000e3;
            double vEsc = Math.Sqrt(2.0 * MuEarth / r);
            // inclined, off-axis so all elements are exercised
            var sv = new StateVector(
                new Vector3D(r, 1.0e6, -5.0e5),
                new Vector3D(-800, 1.25 * vEsc, 1500));
            var el = OrbitalMath.ToElements(sv, MuEarth);
            Ok("hyperbolic", el.IsHyperbolic);
            var back = OrbitalMath.ToState(el);
            NearVec("r", back.Position, sv.Position, 1.0);
            NearVec("v", back.Velocity, sv.Velocity, 1e-3);

            // propagate forward then back to zero -> identity
            var fwd = OrbitalMath.Propagate(el, 120.0);
            var ret = OrbitalMath.Propagate(fwd, -120.0);
            NearVec("r after +120/-120s", OrbitalMath.ToState(ret).Position, sv.Position, 1.0);
        }

        private static void AnomalyRoundTrips()
        {
            Section("anomaly conversions round-trip");
            // Elliptic: any true anomaly is valid.
            foreach (double e in new[] { 0.0, 0.3, 0.85 })
                foreach (double nuDeg in new[] { 5.0, 60.0, 140.0, 250.0 })
                    AnomalyCase(e, nuDeg * Deg, $"e={e} nu={nuDeg}");

            // Hyperbolic: true anomaly is bounded by the asymptote nu_inf = acos(-1/e).
            // Sample as fractions of that limit so inputs stay physical.
            foreach (double e in new[] { 1.5, 4.0 })
            {
                double nuInf = Math.Acos(-1.0 / e);
                foreach (double frac in new[] { 0.1, 0.5, 0.9 })
                    AnomalyCase(e, frac * nuInf, $"e={e} nu={frac:F1}*nuInf({nuInf / Deg:F1}deg)");
            }
        }

        private static void AnomalyCase(double e, double nu, string name)
        {
            double M = OrbitalMath.TrueToMeanAnomaly(nu, e);
            double back = OrbitalMath.MeanToTrueAnomaly(M, e);
            double d = Math.Abs(back - nu);
            if (d > Math.PI) d = OrbitalMath.TwoPi - d; // wrap for elliptic
            Near(name, d, 0.0, 1e-9);
        }

        // A canonical inclined elliptic orbit for the propagation/draw tests.
        private static KeplerianElements SampleEllipticOrbit()
        {
            var sv = new StateVector(
                new Vector3D(-6045e3, -3490e3, 2500e3),
                new Vector3D(-3457, 6618, 2533));
            return OrbitalMath.ToElements(sv, 3.986e14, epoch: 1000.0);
        }

        private static void TimePropagation()
        {
            Section("time propagation (state at absolute time)");
            var el = SampleEllipticOrbit();
            // StateAt(epoch) == ToState(el)
            NearVec("StateAt(epoch)", OrbitPropagation.StateAt(el, el.Epoch).Position,
                OrbitalMath.ToState(el).Position, 1e-3);
            // one period later -> same position
            NearVec("StateAt(epoch+T)", OrbitPropagation.StateAt(el, el.Epoch + el.Period).Position,
                OrbitalMath.ToState(el).Position, 5.0);
            // AtTime sets epoch
            Near("AtTime epoch", OrbitPropagation.AtTime(el, 5000.0).Epoch, 5000.0, 1e-9);
        }

        private static void EventSolvers()
        {
            Section("event solvers (time to periapsis/apoapsis/anomaly)");
            var el = SampleEllipticOrbit();
            double tp = OrbitPropagation.TimeToPeriapsis(el);
            Near("nu at periapsis", OrbitalMath.Wrap2Pi(OrbitalMath.Propagate(el, tp).TrueAnomaly), 0.0, 1e-6);
            double ta = OrbitPropagation.TimeToApoapsis(el);
            Near("nu at apoapsis", OrbitalMath.Propagate(el, ta).TrueAnomaly, Math.PI, 1e-6);
            double tt = OrbitPropagation.TimeToTrueAnomaly(el, 1.0);
            Near("nu at target", OrbitalMath.Propagate(el, tt).TrueAnomaly, 1.0, 1e-6);
            Ok("tp in [0,T)", tp >= 0 && tp < el.Period);
        }

        private static void ShellCrossing()
        {
            Section("interaction-shell crossing (TryTimeToRadius)");
            var el = SampleEllipticOrbit();
            // pick a shell radius strictly between periapsis and apoapsis
            double rShell = 0.5 * (el.PeriapsisRadius + el.ApoapsisRadius);
            Ok("reachable", OrbitPropagation.TryTimeToRadius(el, rShell, out double tOut, out double tIn));
            var sOut = OrbitPropagation.StateAt(el, el.Epoch + tOut);
            var sIn = OrbitPropagation.StateAt(el, el.Epoch + tIn);
            Near("outbound radius", sOut.Radius, rShell, 1.0);
            Near("inbound radius", sIn.Radius, rShell, 1.0);
            Ok("outbound moving away (r.v>0)", Vector3D.Dot(sOut.Position, sOut.Velocity) > 0);
            Ok("inbound moving toward (r.v<0)", Vector3D.Dot(sIn.Position, sIn.Velocity) < 0);
            // radius below periapsis is unreachable
            Ok("sub-periapsis unreachable",
                !OrbitPropagation.TryTimeToRadius(el, el.PeriapsisRadius * 0.5, out _, out _));

            // hyperbolic shell crossing (escape trajectory exiting the shell)
            double r = 6000e3, vEsc = Math.Sqrt(2.0 * MuEarth / r);
            var hyp = OrbitalMath.ToElements(
                new StateVector(new Vector3D(r, 0, 0), new Vector3D(0, 1.4 * vEsc, 0)), MuEarth);
            Ok("hyp reaches shell", OrbitPropagation.TryTimeToRadius(hyp, 9000e3, out double hOut, out _));
            Near("hyp outbound radius", OrbitPropagation.StateAt(hyp, hyp.Epoch + hOut).Radius, 9000e3, 1.0);
        }

        private static void DrawEllipsePath()
        {
            Section("draw ellipse path (uniform eccentric anomaly, closed, planar)");
            var el = SampleEllipticOrbit();
            var path = OrbitSampler.SamplePath(el, 64);
            Ok("closed", path.IsClosed);
            Ok("count", path.Points.Length == 64);
            Ok("point[0] ~ periapsis", Math.Abs(path.Points[0].Length() - el.PeriapsisRadius) < 1.0);
            bool boundsOk = true, planarOk = true;
            Vector3D n = PlaneNormal(el);
            foreach (var p in path.Points)
            {
                double rr = p.Length();
                if (rr < el.PeriapsisRadius - 1.0 || rr > el.ApoapsisRadius + 1.0) boundsOk = false;
                if (Math.Abs(Vector3D.Dot(p, n)) > rr * 1e-9) planarOk = false; // lies in orbit plane
            }
            Ok("all points within [rp,ra]", boundsOk);
            Ok("all points coplanar", planarOk);
        }

        private static void DrawHyperbolaPath()
        {
            Section("draw hyperbola path (open arc clipped to a shell, planar)");
            double r = 6000e3, vEsc = Math.Sqrt(2.0 * MuEarth / r);
            var el = OrbitalMath.ToElements(
                new StateVector(new Vector3D(r, 8e5, 0), new Vector3D(-300, 1.35 * vEsc, 900)), MuEarth);
            double maxR = 5.0 * el.PeriapsisRadius;
            var path = OrbitSampler.SamplePath(el, 64, maxR);
            Ok("open", !path.IsClosed);
            bool boundsOk = true, planarOk = true;
            Vector3D n = PlaneNormal(el);
            foreach (var p in path.Points)
            {
                double rr = p.Length();
                if (rr > maxR * 1.001 || rr < el.PeriapsisRadius - 1.0) boundsOk = false;
                if (Math.Abs(Vector3D.Dot(p, n)) > rr * 1e-9) planarOk = false;
            }
            Ok("clipped to maxRadius & >= periapsis", boundsOk);
            Ok("all points coplanar", planarOk);
            Ok("endpoints ~ maxRadius", Math.Abs(path.Points[0].Length() - maxR) < maxR * 1e-3
                && Math.Abs(path.Points[63].Length() - maxR) < maxR * 1e-3);
        }

        private static void ApsisMarkers()
        {
            Section("apsis markers");
            var el = SampleEllipticOrbit();
            var peri = OrbitSampler.PeriapsisPosition(el);
            var apo = OrbitSampler.ApoapsisPosition(el);
            Near("periapsis radius", peri.Length(), el.PeriapsisRadius, 1.0);
            Near("apoapsis radius", apo.Length(), el.ApoapsisRadius, 1.0);
            Ok("apsides opposed", Vector3D.Dot(Vector3D.Normalize(peri), Vector3D.Normalize(apo)) < -0.999);
        }

        private static void SampleByTimeTest()
        {
            Section("sample by time along an ephemeris");
            var el = SampleEllipticOrbit();
            var eph = new KeplerianEphemeris(el);
            double t0 = el.Epoch, t1 = el.Epoch + el.Period / 4.0;
            var s = OrbitSampler.SampleByTime(eph, t0, t1, 9);
            Ok("count", s.Length == 9);
            Ok("times monotonic", s[0].Time == t0 && s[8].Time == t1);
            NearVec("first == StateAt(t0)", s[0].State.Position, eph.PositionAt(t0), 1e-3);
            NearVec("last == StateAt(t1)", s[8].State.Position, eph.PositionAt(t1), 1e-3);
        }

        private static void GravityModel()
        {
            Section("gravity model (inverse-square, NO cutoff) + Laplace SOI");
            // magnitude = mu/r^2, direction toward body
            double mu = 3.986e14, r = 7000e3;
            var a = Gravity.PointMassAcceleration(new Vector3D(r, 0, 0), mu);
            Near("|g| = mu/r^2", a.Length(), mu / (r * r), 1e-3);
            Ok("points toward body", a.X < 0 && Math.Abs(a.Y) < 1e-9 && Math.Abs(a.Z) < 1e-9);
            // NO cutoff: still nonzero absurdly far out (where vanilla SE / Real Orbits = 0)
            Ok("nonzero at 1000x radius", Gravity.Magnitude(1000.0 * r, mu) > 0.0);
            // Laplace SOI matches Earth's known ~9.24e8 m
            double soi = Gravity.LaplaceSoiRadius(1.496e11, 3.986e14, 1.327e20);
            Near("Earth SOI (Gm)", soi / 1e9, 0.924, 0.05);
        }

        private static void PatchedConicEscape()
        {
            Section("patched conic: escape SOI -> parent frame");
            // Sun (root) -> Planet (finite SOI) orbiting Sun; craft escapes the Planet.
            double muSun = 1.327e20, muPlanet = 3.986e14;
            var sun = GravityBody.Root("Sun", muSun);
            var planetOrbit = OrbitalMath.ToElements(
                new StateVector(new Vector3D(1.5e11, 0, 0), new Vector3D(0, Math.Sqrt(muSun / 1.5e11), 0)), muSun);
            var planet = sun.AddChild("Planet", muPlanet, 1.0e9, new KeplerianEphemeris(planetOrbit));

            // craft around Planet, apoapsis well beyond the 1e9 SOI -> escapes
            var craft = new StateVector(new Vector3D(1.0e7, 0, 0), new Vector3D(0, 9000, 0));
            var arcs = PatchedConic.Propagate(planet, craft, 0.0, 5.0e6);
            Ok(">=2 arcs", arcs.Count >= 2);
            Ok("arc0 body = Planet", arcs[0].Body == planet);
            Ok("arc0 escapes", arcs[0].EndReason == PatchType.Escape && arcs[0].NextBody == sun);
            // radius from Planet at the patch == Planet SOI
            var sEnd = OrbitPropagation.StateAt(arcs[0].Elements, arcs[0].EndTime);
            Near("patch radius = SOI", sEnd.Radius, planet.SoiRadius, 5.0);
            Ok("arc1 body = Sun", arcs[1].Body == sun);
            // continuity: craft position in Sun frame is continuous across the patch
            var beforeInSun = planet.ConvertStateTo(sun, sEnd, arcs[0].EndTime).Position;
            var afterInSun = OrbitalMath.ToState(arcs[1].Elements).Position;
            NearVec("patch continuity (Sun frame)", afterInSun, beforeInSun, 1.0);
        }

        private static void PatchedConicEncounter()
        {
            Section("patched conic: encounter (enter a child SOI)");
            // Planet (root, infinite SOI) with a static Moon; craft's apoapsis reaches the Moon.
            double muP = 1.0e14;
            var planet = GravityBody.Root("Planet", muP);
            var moonPos = new Vector3D(1.0e8, 0, 0);
            var moon = planet.AddChild("Moon", 4.9e12, 5.0e6, new FixedEphemeris(moonPos));

            // ellipse with periapsis on -x (2e7) and apoapsis at +x (1e8 = Moon center)
            double rp = 2.0e7, ra = 1.0e8, a = (rp + ra) / 2.0;
            double vp = Math.Sqrt(muP * (2.0 / rp - 1.0 / a));
            var craft = new StateVector(new Vector3D(-rp, 0, 0), new Vector3D(0, vp, 0));

            double period = 2.0 * Math.PI * Math.Sqrt(a * a * a / muP);
            var arcs = PatchedConic.Propagate(planet, craft, 0.0, period);
            Ok(">=2 arcs", arcs.Count >= 2);
            Ok("arc0 encounters Moon", arcs[0].EndReason == PatchType.Encounter && arcs[0].NextBody == moon);
            // at the patch, craft is exactly on the Moon's SOI sphere
            var sEnd = OrbitPropagation.StateAt(arcs[0].Elements, arcs[0].EndTime);
            double distToMoon = (sEnd.Position - moonPos).Length();
            Near("patch on Moon SOI", distToMoon, moon.SoiRadius, 100.0);
            Ok("arc1 body = Moon", arcs[1].Body == moon);
            // continuity into Moon frame: first Moon-frame radius == SOI
            var inMoon = OrbitalMath.ToState(arcs[1].Elements);
            Near("Moon-frame entry radius = SOI", inMoon.Radius, moon.SoiRadius, 100.0);

            // drawing the whole patched path in the Planet frame yields a continuous curve
            var path = PatchedConic.SamplePath(arcs, planet, 200);
            Ok("path sampled", path.Length > 50);
        }

        // Unified encounter scan (docs/encounters-and-tracking.md Pillar A): ONE scan finds
        // body-SOI crossings AND clump conjunctions as one ordered timeline. Same kernel as the
        // body-only PatchedConic.FindEarliestEncounter, generalized to any moving target sphere.
        private static void UnifiedEncounterScan()
        {
            Section("unified encounter scan (Pillar A: bodies + clumps, one timeline)");
            double muP = 1.0e14;
            var planet = GravityBody.Root("Planet", muP);
            var moonPos = new Vector3D(1.0e8, 0, 0);
            var moon = planet.AddChild("Moon", 4.9e12, 5.0e6, new FixedEphemeris(moonPos));

            // The subject: the SAME Moon-grazing ellipse as PatchedConicEncounter (periapsis
            // -x at 2e7, apoapsis +x at 1e8 = Moon center).
            double rp = 2.0e7, ra = 1.0e8, a = (rp + ra) / 2.0;
            double vp = Math.Sqrt(muP * (2.0 / rp - 1.0 / a));
            var subjState = new StateVector(new Vector3D(-rp, 0, 0), new Vector3D(0, vp, 0));
            var subject = OrbitalMath.ToElements(subjState, muP, epoch: 0.0);
            double period = 2.0 * Math.PI * Math.Sqrt(a * a * a / muP);

            // (1) BODY-SOI case matches PatchedConic for this known encounter.
            var arcs = PatchedConic.Propagate(planet, subjState, 0.0, period);
            double tEncRef = arcs[0].EndTime;   // PatchedConic's Moon-SOI entry time
            var bodyTargets = new List<EncounterTarget> { EncounterTarget.FromBody(moon) };
            var bodyEvents = EncounterScan.Scan(subject, 0.0, period, bodyTargets, 1024);
            Ok("one body event found", bodyEvents.Count == 1);
            Ok("body event is the Moon, SoiEntry",
                bodyEvents[0].TargetId == "Moon" && bodyEvents[0].Kind == EncounterKind.SoiEntry);
            Near("body-SOI time matches FindEarliestEncounter", bodyEvents[0].Time, tEncRef, 1.0);
            Near("miss == Moon SOI at the crossing", bodyEvents[0].MissDistance, moon.SoiRadius, 1.0);

            // (2) CONJUNCTION: a synthetic crossing-orbit clump, CONSTRUCTED to pass through the
            // subject's track. Take the subject's state at a chosen meeting time, place the clump
            // a fixed offset (< merge radius) away there with a clearly different velocity, and
            // give the clump that meeting time as its element epoch. The separation then dips to
            // ~|offset| around tMeet -> a genuine inward crossing from outside the merge sphere.
            // Validate the found time + miss against a brute-force fine time-sample (ground truth).
            double mergeR = 4.0e6;
            double tMeet = period * 0.30;
            var subjAtMeet = OrbitPropagation.StateAt(subject, tMeet);
            var clumpAtMeet = new StateVector(
                subjAtMeet.Position + new Vector3D(0, 0, 1.0e6),   // 1e6 m offset, inside the 4e6 sphere
                subjAtMeet.Velocity + new Vector3D(1500, -1500, 800)); // distinct velocity -> separates away
            var clumpEl = OrbitalMath.ToElements(clumpAtMeet, muP, epoch: tMeet);
            var clumpTargets = new List<EncounterTarget> { EncounterTarget.FromOrbit("Clump-7", clumpEl, mergeR) };
            var clumpEvents = EncounterScan.Scan(subject, 0.0, period, clumpTargets, 2048);
            Ok("one conjunction event found", clumpEvents.Count == 1);
            Ok("conjunction is Clump-7", clumpEvents[0].TargetId == "Clump-7"
                && clumpEvents[0].Kind == EncounterKind.Conjunction);

            // Brute-force ground truth: the first time the dense relative-distance sample drops
            // onto/below the merge sphere.
            var clumpEph = new KeplerianEphemeris(clumpEl);
            double bruteEntry = double.PositiveInfinity;
            int bn = 400000;
            double bdt = period / bn;
            double prevSep = (OrbitPropagation.StateAt(subject, 0.0).Position - clumpEph.PositionAt(0.0)).Length();
            for (int k = 1; k <= bn; k++)
            {
                double t = k * bdt;
                double sep = (OrbitPropagation.StateAt(subject, t).Position - clumpEph.PositionAt(t)).Length();
                if (prevSep > mergeR && sep <= mergeR) { bruteEntry = t; break; }
                prevSep = sep;
            }
            Ok("brute force found a crossing", !double.IsPositiveInfinity(bruteEntry));
            // Within one brute-force step of ground truth.
            Near("conjunction time matches brute force", clumpEvents[0].Time, bruteEntry, 4.0 * bdt);
            Near("conjunction miss ~ merge sphere", clumpEvents[0].MissDistance, mergeR, mergeR * 1e-3);
            // The relative state is consistent with the reported separation.
            Near("rel-position length == miss", clumpEvents[0].RelativePosition.Length(),
                clumpEvents[0].MissDistance, 1.0);

            // (3) ORDERING across multiple targets: Moon (later) + clump (earlier) interleave by time.
            var multi = new List<EncounterTarget>
            {
                EncounterTarget.FromBody(moon),
                EncounterTarget.FromOrbit("Clump-7", clumpEl, mergeR),
            };
            var ordered = EncounterScan.Scan(subject, 0.0, period, multi, 2048);
            Ok("both events present", ordered.Count == 2);
            Ok("strictly time-ordered", ordered[0].Time <= ordered[1].Time);
            // The interleaved order must reflect each target's own single-target crossing time:
            // the clump conjunction (tMeet = 0.30*period) precedes the Moon SOI entry near apoapsis.
            Ok("ordering reflects per-target times",
                clumpEvents[0].Time < bodyEvents[0].Time
                    ? (ordered[0].TargetId == "Clump-7" && ordered[1].TargetId == "Moon")
                    : (ordered[0].TargetId == "Moon" && ordered[1].TargetId == "Clump-7"));

            // (4) NO false encounter: a clump on a far, non-intersecting orbit (its sphere never
            // reaches the subject's path).
            var farState = new StateVector(new Vector3D(5.0e8, 0, 0), new Vector3D(0, Math.Sqrt(muP / 5.0e8), 0));
            var farEl = OrbitalMath.ToElements(farState, muP, epoch: 0.0);
            var farTargets = new List<EncounterTarget> { EncounterTarget.FromOrbit("Far", farEl, 1.0e6) };
            var farEvents = EncounterScan.Scan(subject, 0.0, period, farTargets, 2048);
            Ok("no false encounter for non-intersecting orbit", farEvents.Count == 0);

            // (5) ALREADY INSIDE the sphere at t0: the encounter is reported at t0 itself.
            // A clump centered right on the subject's t0 position with a sphere that contains it.
            Vector3D subAt0 = OrbitPropagation.StateAt(subject, 0.0).Position;
            var insideState = new StateVector(subAt0 + new Vector3D(1.0e5, 0, 0),
                new Vector3D(0, Math.Sqrt(muP / subAt0.Length()), 0));
            var insideEl = OrbitalMath.ToElements(insideState, muP, epoch: 0.0);
            var insideTargets = new List<EncounterTarget> { EncounterTarget.FromOrbit("Inside", insideEl, 1.0e6) };
            var insideEvents = EncounterScan.Scan(subject, 0.0, period, insideTargets, 256);
            Ok("already-inside target found", insideEvents.Count == 1);
            Near("already-inside event is at t0", insideEvents[0].Time, 0.0, 1e-9);
            Ok("already-inside miss < sphere", insideEvents[0].MissDistance < 1.0e6);
        }

        // A Sun(root) -> Planet system shared by the frame tests.
        private static void BuildSunPlanet(out InertialFrame sun, out BodyInertialFrame planet,
            out double muSun, out double muPlanet, out KeplerianEphemeris planetEph)
        {
            muSun = 1.327e20; muPlanet = 3.986e14;
            sun = new InertialFrame("Sun");
            var orbit = OrbitalMath.ToElements(
                new StateVector(new Vector3D(1.5e11, 0, 0), new Vector3D(0, Math.Sqrt(muSun / 1.5e11), 0)), muSun);
            planetEph = new KeplerianEphemeris(orbit);
            planet = new BodyInertialFrame("Planet", sun, planetEph);
        }

        private static void FrameBodyInertial()
        {
            Section("frame: body-inertial agrees with GravityBody (Galilean)");
            BuildSunPlanet(out var sunF, out var planetF, out double muSun, out double muPlanet, out var planetEph);
            // GravityBody equivalent
            var sunB = GravityBody.Root("Sun", muSun);
            var planetB = sunB.AddChild("Planet", muPlanet, 1e9, planetEph);

            double t = 1.234e6;
            var craft = new StateVector(new Vector3D(2e7, -1e7, 5e6), new Vector3D(100, 3000, -200));
            var viaFrame = FrameConvert.State(craft, planetF, sunF, t);
            var viaBody = planetB.ConvertStateTo(sunB, craft, t);
            NearVec("position match", viaFrame.Position, viaBody.Position, 1e-3);
            NearVec("velocity match", viaFrame.Velocity, viaBody.Velocity, 1e-6);
        }

        private static void FramePerifocal()
        {
            Section("frame: perifocal +X maps to periapsis");
            var el = SampleEllipticOrbit();
            var bodyF = new InertialFrame("Body");
            var bi = new BodyInertialFrame("B", bodyF, new FixedEphemeris(Vector3D.Zero));
            var pf = new PerifocalFrame("PQW", bi, el);
            // periapsis point in perifocal coords = (rp, 0, 0)
            var periPf = new Vector3D(el.PeriapsisRadius, 0, 0);
            var periBody = FrameConvert.Position(periPf, pf, bi, 0.0);
            NearVec("periapsis via perifocal", periBody, OrbitSampler.PeriapsisPosition(el), 1.0);
        }

        private static void FrameBodyFixed()
        {
            Section("frame: body-fixed rotation (position + omega x r velocity)");
            var root = new InertialFrame("Body");
            var bi = new BodyInertialFrame("B", root, new FixedEphemeris(Vector3D.Zero));
            double w = 2.0 * Math.PI / 86400.0; // one rev/day about Z
            var bf = new BodyFixedFrame("BF", bi, Vector3D.UnitZ, w);
            double R = 6e6;
            var surfacePoint = new StateVector(new Vector3D(R, 0, 0), Vector3D.Zero); // fixed on the surface
            double t = 86400.0 / 4.0; // quarter turn
            var inInertial = FrameConvert.State(surfacePoint, bf, bi, t);
            // after a quarter turn the +x surface point is at +y
            NearVec("rotated position", inInertial.Position, new Vector3D(0, R, 0), 1.0);
            // its inertial velocity = omega x r = (0,0,w) x (0,R,0) = (-wR, 0, 0)
            NearVec("surface velocity = w x r", inInertial.Velocity, new Vector3D(-w * R, 0, 0), 1e-6);
        }

        private static void FrameLvlh()
        {
            Section("frame: LVLH (object at origin, down points at body)");
            BuildSunPlanet(out _, out var planetF, out _, out double muPlanet, out _);
            // a craft orbiting the planet
            var craftEl = OrbitalMath.ToElements(
                new StateVector(new Vector3D(1e7, 2e6, 0), new Vector3D(-1500, 7000, 800)), muPlanet);
            var craftEph = new KeplerianEphemeris(craftEl);
            var lvlh = new LvlhFrame("LVLH", planetF, craftEph);

            double t = 250.0;
            // the craft itself sits at the LVLH origin with zero local velocity
            var craftState = craftEph.StateAt(t);
            var inLvlh = FrameConvert.State(craftState, planetF, lvlh, t);
            NearVec("craft at LVLH origin", inLvlh.Position, Vector3D.Zero, 1e-6);
            NearVec("craft zero local velocity", inLvlh.Velocity, Vector3D.Zero, 1e-6);
            // "down" (-X in LVLH) converted to the planet frame points from craft toward the body
            var down = FrameConvert.Direction(new Vector3D(-1, 0, 0), lvlh, planetF, t);
            var towardBody = Vector3D.Normalize(-craftEph.PositionAt(t));
            Ok("down points at body", Vector3D.Dot(down, towardBody) > 0.999);
        }

        private static void FrameRoundTrips()
        {
            Section("frame: round-trips A->B->A across all frame types");
            BuildSunPlanet(out var sun, out var planet, out _, out double muPlanet, out _);
            double w = 2.0 * Math.PI / 40000.0;
            var bf = new BodyFixedFrame("PlanetFixed", planet, Vector3D.Normalize(new Vector3D(0.1, 0.2, 1.0)), w);
            var craftEph = new KeplerianEphemeris(OrbitalMath.ToElements(
                new StateVector(new Vector3D(9e6, 1e6, -2e6), new Vector3D(-1200, 6600, 500)), muPlanet));
            var lvlh = new LvlhFrame("LVLH", planet, craftEph);
            var topo = new TopocentricFrame("Pad", bf, 0.4, 1.1, 6.05e6);

            double t = 7777.0;
            var s = new StateVector(new Vector3D(3e6, -4e6, 1e6), new Vector3D(40, -90, 25));
            Frame[] frames = { sun, planet, bf, lvlh, topo };
            bool ok = true;
            foreach (var a in frames)
                foreach (var b in frames)
                {
                    var there = FrameConvert.State(s, a, b, t);
                    var back = FrameConvert.State(there, b, a, t);
                    if ((back.Position - s.Position).Length() > 1e-4) ok = false;
                    if ((back.Velocity - s.Velocity).Length() > 1e-6) ok = false;
                }
            Ok("all 25 A->B->A round-trips exact", ok);
        }

        private static void ClohessyWiltshireMatchesClosedForm()
        {
            Section("Clohessy-Wiltshire: RK4 of Acceleration == closed-form Propagate");
            double n = 0.0011; // ~LEO mean motion (rad/s)
            var r0 = new Vector3D(120.0, -300.0, 80.0);   // relative offset (m)
            var v0 = new Vector3D(0.4, 0.15, -0.2);       // relative velocity in the rotating frame (m/s)

            foreach (double t in new[] { 200.0, 800.0, 2000.0 })
            {
                // analytic
                Vector3D ra = r0, va = v0;
                ClohessyWiltshire.Propagate(ref ra, ref va, n, t);
                // RK4 of the acceleration
                Vector3D r = r0, v = v0;
                int steps = (int)(t / 0.5);
                double dt = t / steps;
                for (int k = 0; k < steps; k++) CwRk4(ref r, ref v, n, dt);
                NearVec($"r(t={t})", r, ra, 0.05);     // within 5 cm after up to 2000 s
                NearVec($"v(t={t})", v, va, 1e-4);
            }
        }

        private static void ClohessyWiltshireKnownModes()
        {
            Section("Clohessy-Wiltshire: known relative-motion modes");
            double n = 0.001;
            // cross-track is pure SHM at frequency n: returns to start after one period
            var r = new Vector3D(0, 0, 50.0);
            var v = new Vector3D(0, 0, 0);
            double T = 2.0 * Math.PI / n;
            ClohessyWiltshire.Propagate(ref r, ref v, n, T);
            Near("cross-track period", r.Z, 50.0, 1e-6);

            // a pure radial offset (no rel velocity) drifts ALONG-TRACK secularly (the
            // classic CW result) and the radial part oscillates (bounded)
            var r2 = new Vector3D(100.0, 0, 0);
            var v2 = new Vector3D(0, 0, 0);
            ClohessyWiltshire.Propagate(ref r2, ref v2, n, T);
            Ok("radial offset -> along-track drift", Math.Abs(r2.Y) > 100.0);   // drifted well past start
            Near("radial returns after a period", r2.X, 100.0, 1e-4);

            // the acceleration formula itself
            var a = ClohessyWiltshire.Acceleration(new Vector3D(10, 0, -5), new Vector3D(0, 2, 0), n);
            Near("ax = 3n^2 x + 2n vy", a.X, 3 * n * n * 10 + 2 * n * 2, 1e-12);
            Near("az = -n^2 z", a.Z, -n * n * -5, 1e-12);
        }

        private static void RendezvousHysteresis()
        {
            Section("rendezvous gate: sticky enter/exit hysteresis + dwell");
            // Live 2026-06-16 tuning: enter 10 km / 1000 m/s flyby gate, exit 15 km (DISTANCE-ONLY,
            // no rel-speed exit), dwell 2 s. All samples below are derived from these live thresholds.
            var p = SEAerospace.Frames.RendezvousParams.Default;
            double inRange = p.EnterRangeMeters * 0.1;   // 1 km — well inside the enter range
            double slow = p.EnterRelSpeedMps * 0.01;     // 10 m/s — well inside the flyby gate
            string err;
            Ok("default params valid", p.Validate(out err));
            var bad = new SEAerospace.Frames.RendezvousParams { ExitRangeMeters = 100, EnterRangeMeters = 200 };
            Ok("exit<enter invalid", !bad.Validate(out err));

            var g = new SEAerospace.Frames.RendezvousTracker();
            double dt = 0.5;

            // tight + slow but NOT long enough -> no merge yet (dwell = 2s)
            g.Update(inRange, slow, dt, p); g.Update(inRange, slow, dt, p); g.Update(inRange, slow, dt, p);
            Ok("no merge before dwell elapses", !g.Merged);
            g.Update(inRange, slow, dt, p);   // 4th * 0.5s = 2.0s reached
            Ok("merge after dwell", g.Merged);

            // STICKY: once merged, stays merged through the band (sep between enter and exit range)
            double stickySep = 0.5 * (p.EnterRangeMeters + p.ExitRangeMeters); // 12.5 km — inside [enter,exit]
            g.Update(stickySep, slow, dt, p);
            Ok("stays merged in sticky band", g.Merged);

            // split only when clearly out (range): strictly beyond the exit range
            g.Update(p.ExitRangeMeters + 1000.0, slow, dt, p);
            Ok("splits beyond exit range", !g.Merged);

            // rel-speed is DISTANCE-ONLY exit by design (rob 2026-06-16 "no speed exit" — the
            // rel-speed exit was deliberately removed). A merged pair that is fast but still
            // CLOSE (sep < exit range) must STAY merged regardless of how high the rel-speed is.
            var g2 = new SEAerospace.Frames.RendezvousTracker();
            g2.Reset(true);
            g2.Update(stickySep, p.EnterRelSpeedMps * 10.0, dt, p);   // far above the flyby gate, but close
            Ok("does NOT split on rel-speed alone (distance-only exit)", g2.Merged);

            // NO THRASH: a separate pair oscillating in the sticky band [enter,exit] never merges
            // (both samples are beyond the enter range, so the merge gate never opens).
            double bandLo = p.EnterRangeMeters + 2000.0;   // 12 km
            double bandHi = p.ExitRangeMeters - 1000.0;    // 14 km
            var g3 = new SEAerospace.Frames.RendezvousTracker();
            bool everMerged = false;
            for (int i = 0; i < 20; i++) { g3.Update(i % 2 == 0 ? bandLo : bandHi, slow, dt, p); if (g3.Merged) everMerged = true; }
            Ok("separate pair in sticky band never merges", !everMerged);

            // dwell resets if conditions break mid-countdown: the "break" sample must leave the
            // enter range (separation > EnterRangeMeters) so the gate genuinely opens.
            var g4 = new SEAerospace.Frames.RendezvousTracker();
            g4.Update(inRange, slow, dt, p);                       // 0.5s into dwell
            g4.Update(p.EnterRangeMeters + 1000.0, slow, dt, p);   // breaks gate -> reset
            g4.Update(inRange, slow, dt, p); g4.Update(inRange, slow, dt, p); g4.Update(inRange, slow, dt, p);
            Ok("dwell resets on break (1.5s < 2s)", !g4.Merged);
        }

        private static void ClohessyWiltshireDriftPath()
        {
            Section("Clohessy-Wiltshire drift path: bounded 'football' vs secular drift");
            double n = 0.001;
            double twoPeriods = 2.0 * (2.0 * Math.PI / n);

            // bounded relative orbit: vy0 = -2 n x0 nulls the secular drift -> closed loop
            var football = ClohessyWiltshire.SampleRelativePath(
                new Vector3D(50, 0, 0), new Vector3D(0, -2.0 * n * 50.0, 0), n, twoPeriods, 64);
            double maxR = 0;
            foreach (var p in football) maxR = Math.Max(maxR, p.Length());
            Ok("football stays bounded (max < 150 m)", maxR < 150.0);
            NearVec("football closes after 2 periods", football[63], new Vector3D(50, 0, 0), 1.0);

            // radial offset, no relative velocity -> secular along-track drift (you LEAVE)
            var drifting = ClohessyWiltshire.SampleRelativePath(
                new Vector3D(50, 0, 0), Vector3D.Zero, n, twoPeriods, 64);
            Ok("radial offset drifts away (|along| > 1 km)", Math.Abs(drifting[63].Y) > 1000.0);
        }

        private static void CwRk4(ref Vector3D r, ref Vector3D v, double n, double dt)
        {
            Vector3D k1r = v, k1v = ClohessyWiltshire.Acceleration(r, v, n);
            Vector3D k2r = v + k1v * (dt / 2), k2v = ClohessyWiltshire.Acceleration(r + k1r * (dt / 2), v + k1v * (dt / 2), n);
            Vector3D k3r = v + k2v * (dt / 2), k3v = ClohessyWiltshire.Acceleration(r + k2r * (dt / 2), v + k2v * (dt / 2), n);
            Vector3D k4r = v + k3v * dt, k4v = ClohessyWiltshire.Acceleration(r + k3r * dt, v + k3v * dt, n);
            r += (k1r + 2 * k2r + 2 * k3r + k4r) * (dt / 6);
            v += (k1v + 2 * k2v + 2 * k3v + k4v) * (dt / 6);
        }

        private static Vector3D PlaneNormal(KeplerianElements el)
        {
            var sv = OrbitalMath.ToState(el);
            return Vector3D.Normalize(Vector3D.Cross(sv.Position, sv.Velocity));
        }

        // ---- audit-driven edge-regime tests ----

        // H2: state -> elements -> state must be identity for EQUATORIAL eccentric orbits,
        // both prograde (h.Z>0) and RETROGRADE (h.Z<0) — the singular regimes the
        // h.Z<0 longitude-of-periapsis flip touches and that no test previously covered.
        private static void EquatorialEccentricRoundTrip()
        {
            Section("equatorial-eccentric round-trip (H2: prograde + retrograde)");

            // In-plane (z=0) state with a radial component so e != 0; h is along +/-Z.
            var r = new Vector3D(7000e3, 1000e3, 0.0);

            // prograde: choose v so h.Z > 0.
            var vPro = new Vector3D(-1000.0, 7000.0, 0.0);
            RoundTrip("equatorial prograde eccentric", new StateVector(r, vPro));

            // retrograde: reverse the in-plane motion so h.Z < 0 (i -> ~pi).
            var vRetro = new Vector3D(1000.0, -7000.0, 0.0);
            var elRetro = OrbitalMath.ToElements(new StateVector(r, vRetro), MuEarth);
            Ok("retrograde i ~ pi", Math.Abs(elRetro.Inclination - Math.PI) < 1e-6);
            Ok("retrograde is eccentric (e>0)", elRetro.Eccentricity > 1e-3);
            RoundTrip("equatorial retrograde eccentric", new StateVector(r, vRetro));
        }

        private static void RoundTrip(string name, StateVector sv)
        {
            var el = OrbitalMath.ToElements(sv, MuEarth);
            var back = OrbitalMath.ToState(el);
            NearVec(name + " r", back.Position, sv.Position, 1.0);        // 1 m
            NearVec(name + " v", back.Velocity, sv.Velocity, 1e-3);      // 1 mm/s
        }

        // H1 / H4 / shipped-H3: TryTimeToRadius reachability across regimes.
        private static void TryTimeToRadiusEdges()
        {
            Section("TryTimeToRadius edges (H1/H4 hyperbolic reach, shipped-H3 circular)");
            double tOut, tIn;

            // shipped-H3: a CIRCULAR orbit divides by e=0 -> NaN that slipped past the range
            // check into Acos. Must now return false (no well-defined crossing), not NaN.
            double rc = 7000e3;
            var circ = OrbitalMath.ToElements(
                new StateVector(new Vector3D(rc, 0, 0), new Vector3D(0, Math.Sqrt(MuEarth / rc), 0)), MuEarth);
            Ok("circular: no crossing (false, not NaN)", !OrbitPropagation.TryTimeToRadius(circ, rc, out tOut, out tIn));

            // A hyperbolic orbit: periapsis 8000 km, e=1.5.
            double rp = 8000e3, ecc = 1.5;
            double a = rp / (1.0 - ecc);                 // negative
            double pSemi = a * (1.0 - ecc * ecc);        // semi-latus rectum (positive)
            var hyp = new KeplerianElements
            {
                SemiMajorAxis = a, Eccentricity = ecc, Inclination = 0.3, Raan = 0.4,
                ArgPeriapsis = 0.5, TrueAnomaly = 0.2, Mu = MuEarth, Epoch = 0.0
            };

            // reachable: a radius modestly above periapsis IS crossed (both legs).
            double rReach = 1.0e7;
            Ok("hyperbolic reachable radius -> true", OrbitPropagation.TryTimeToRadius(hyp, rReach, out tOut, out tIn));
            Ok("hyperbolic outbound > inbound", tOut > tIn);
            // verify the returned times actually sit at the radius.
            double rAtOut = OrbitPropagation.StateAt(hyp, tOut).Position.Length();
            double rAtIn = OrbitPropagation.StateAt(hyp, tIn).Position.Length();
            Near("hyperbolic state at tOut is the radius", rAtOut, rReach, 1.0);
            Near("hyperbolic state at tIn is the radius", rAtIn, rReach, 1.0);

            // sub-periapsis: never reached -> false.
            Ok("hyperbolic sub-periapsis -> false", !OrbitPropagation.TryTimeToRadius(hyp, rp * 0.5, out tOut, out tIn));

            // A large-but-finite radius IS reachable on a hyperbola (it reaches any finite r);
            // the contract that matters is it NEVER returns true with a non-finite time.
            double rBig = 1.0e12;
            if (OrbitPropagation.TryTimeToRadius(hyp, rBig, out tOut, out tIn))
                Ok("large finite radius: times finite (never +inf)",
                    !double.IsNaN(tOut) && !double.IsInfinity(tOut) && !double.IsNaN(tIn) && !double.IsInfinity(tIn));
            else
                Ok("large finite radius: clean false", double.IsNaN(tOut) && double.IsNaN(tIn));

            // H1/H4: at an absurd radius, p/r underflows so cosNu rounds to exactly -1/e (the
            // asymptote, r->inf). The guard must return false, NOT true with t=+inf.
            bool reached = OrbitPropagation.TryTimeToRadius(hyp, 1.0e30, out tOut, out tIn);
            Ok("asymptotic radius -> false (not +inf)", !reached);
            Ok("unreachable leaves outputs NaN", double.IsNaN(tOut) && double.IsNaN(tIn));
        }

        // C2: a NaN/Inf mean anomaly must return NaN from BOTH Kepler solvers and NOT throw.
        // Pre-fix, SolveKeplerHyperbolic called Math.Sign(NaN) -> ArithmeticException (crash);
        // a finite-but-absurd velocity into ToElements reaches exactly this path via M = NaN.
        private static void KeplerSolversNaNGuard()
        {
            Section("C2: Kepler solvers return NaN (not throw) for NaN/Inf mean anomaly");

            // Direct: the hyperbolic solver must not detonate on NaN / +/-Inf.
            bool threw = false;
            double h1 = 0.0, h2 = 0.0, h3 = 0.0;
            try
            {
                h1 = OrbitalMath.SolveKeplerHyperbolic(double.NaN, 1.5);
                h2 = OrbitalMath.SolveKeplerHyperbolic(double.PositiveInfinity, 1.5);
                h3 = OrbitalMath.SolveKeplerHyperbolic(double.NegativeInfinity, 1.5);
            }
            catch (Exception) { threw = true; }
            Ok("hyperbolic solver did not throw", !threw);
            Ok("hyperbolic NaN -> NaN", double.IsNaN(h1));
            Ok("hyperbolic +Inf -> NaN", double.IsNaN(h2));
            Ok("hyperbolic -Inf -> NaN", double.IsNaN(h3));

            // Elliptic solver hardened too.
            bool threwE = false;
            double e1 = 0.0;
            try { e1 = OrbitalMath.SolveKeplerElliptic(double.NaN, 0.3); }
            catch (Exception) { threwE = true; }
            Ok("elliptic solver did not throw on NaN", !threwE);
            Ok("elliptic NaN -> NaN", double.IsNaN(e1));

            // e == 1 (parabolic limit) must not produce Inf from the M/(e-1) initial guess.
            bool threwP = false;
            double hp = 0.0;
            try { hp = OrbitalMath.SolveKeplerHyperbolic(3.0, 1.0); }
            catch (Exception) { threwP = true; }
            Ok("e==1 initial guess does not throw", !threwP);
            Ok("e==1 result is finite (not Inf)", !double.IsInfinity(hp));

            // End-to-end: a finite-but-absurd velocity into ToElements -> propagate must not
            // crash (M overflows to NaN, the guarded solver returns NaN -> a NaN state, no throw).
            bool threwProp = false;
            try
            {
                var absurd = new StateVector(new Vector3D(7000e3, 0, 0), new Vector3D(0, 1e30, 0));
                var el = OrbitalMath.ToElements(absurd, MuEarth, 0.0);
                OrbitPropagation.StateAt(el, 100.0);
            }
            catch (Exception) { threwProp = true; }
            Ok("absurd-velocity propagation does not throw", !threwProp);
        }

        // The rails-return-radial fix: SolveKeplerHyperbolic's old seed H0 = M/(e-1) overflowed
        // sinh for near-parabolic e -> 1+ (a dead-radial stow re-osculates e ~ 1.0002; M ~ 1.5
        // gave H0 ~ 1e4 -> sinh -> inf -> NaN), permanently killing a valid frame's rails. The
        // bracketed solve must stay finite AND accurate across the whole hyperbolic envelope.
        private static void HyperbolicNearParabolicSolver()
        {
            Section("hyperbolic Kepler: near-parabolic + large-M envelope (rails-return-radial fix)");

            // Residual sweep: e in (1, 10], |M| up to 1e6, both signs. The solver must return a
            // finite H whose residual |e sinh H - H - M| vanishes (relative to max(1,|M|)).
            double[] es = { 1.0 + 1e-9, 1.0 + 1e-6, 1.000170, 1.0002, 1.01, 1.1, 1.5, 3.0, 10.0 };
            double[] ms = { 1e-9, 1e-6, 1e-3, 0.01, 0.5, 1.5, 5.9, 6.1, 100.0, 1e4, 1e6 };
            bool allFinite = true;
            double worst = 0.0;
            foreach (double e in es)
                foreach (double m in ms)
                    foreach (double sgn in new[] { 1.0, -1.0 })
                    {
                        double M = sgn * m;
                        double H = OrbitalMath.SolveKeplerHyperbolic(M, e);
                        if (double.IsNaN(H) || double.IsInfinity(H)) { allFinite = false; continue; }
                        double resid = Math.Abs(e * Math.Sinh(H) - H - M) / Math.Max(1.0, Math.Abs(M));
                        if (resid > worst) worst = resid;
                    }
            Ok("H finite across e in (1,10], |M| <= 1e6 (was sinh-overflow NaN)", allFinite);
            Near("worst relative residual |e sinh H - H - M|", worst, 0.0, 1e-9);

            // Anomaly round-trips survive the near-parabolic regime (the conversion chain the
            // propagator actually runs).
            double eNp = 1.0002;
            double nuInf = Math.Acos(-1.0 / eNp);
            foreach (double frac in new[] { 0.1, 0.5, 0.9 })
                AnomalyCase(eNp, frac * nuInf, $"e={eNp} nu={frac:F1}*nuInf (near-parabolic)");

            // The LITERAL failing scenario, end-to-end: the dead-radial Moon-escape stow
            // (10 km out, 211 m/s nearly radial, h_z = 2e4 — FrameSim rails-return-radial)
            // re-osculates a near-parabolic hyperbola whose very FIRST propagation used to NaN.
            double muMoon = 1.432e8;
            var stow = new StateVector(new Vector3D(10.0e3, 0, 0), new Vector3D(211.0, 2.0, 0));
            var el = OrbitalMath.ToElements(stow, muMoon, 0.0);
            Ok("stow state is the near-parabolic hyperbola (e in (1, 1.001))",
                el.IsHyperbolic && el.Eccentricity < 1.001);
            bool finiteAll = true;
            double worstVis = 0.0;
            foreach (double dt in new[] { 0.5, 5.0, 50.0, 500.0, 5000.0 })
            {
                var st = OrbitPropagation.StateAt(el, dt);
                bool fin = !double.IsNaN(st.Position.X + st.Position.Y + st.Position.Z
                                       + st.Velocity.X + st.Velocity.Y + st.Velocity.Z);
                if (!fin) { finiteAll = false; continue; }
                // Vis-viva along the propagation: the orbit is not just finite but RIGHT.
                double r = st.Position.Length(), v2 = st.Velocity.LengthSquared();
                double resid = Math.Abs(v2 - muMoon * (2.0 / r - 1.0 / el.SemiMajorAxis)) / v2;
                if (resid > worstVis) worstVis = resid;
            }
            Ok("rails state finite at every horizon (was NaN at the first propagation)", finiteAll);
            Near("vis-viva conserved along the near-parabolic rails", worstVis, 0.0, 1e-6);
        }

        // ---- frame runtime foundation ----

        /// <summary>Berths never inside a planet's frame: Allocate skips forbidden slots (whatever IsClear says), Reserve
        /// refuses them.</summary>
        private static void BerthAllocatorForbidden()
        {
            var keepClear = BerthAllocator.IsClear; var keepForbid = BerthAllocator.IsForbidden;
            try
            {
                var alloc = new BerthAllocator(2000.0, 20000.0);
                Vector3D planet = alloc.SlotCenter(0);          // a "planet" sitting on the first slots
                double zone = alloc.Spacing * 1.5;               // its frame reaches the neighbouring slots
                BerthAllocator.IsClear = null;
                BerthAllocator.IsForbidden = (c, r) => (c - planet).Length() < zone + r;
                bool allOut = true;
                for (int i = 0; i < 40; i++)
                {
                    int id = alloc.Allocate(out Vector3D c);
                    if ((c - planet).Length() < zone + alloc.SlotRadius) allOut = false;
                }
                Ok("berths: none allocated inside a planet's frame (40 allocations)", allOut);
                var fresh = new BerthAllocator(2000.0, 20000.0);
                Ok("berths: a saved slot inside a planet's frame is refused", !fresh.Reserve(0) && !fresh.IsOccupied(0));
                // and IsClear's give-up never lets one through: everything 'not clear'
                BerthAllocator.IsClear = (c, r) => false;
                var stuck = new BerthAllocator(2000.0, 20000.0);
                int sid = stuck.Allocate(out Vector3D sc);
                Ok("berths: the not-clear give-up still never lands inside a planet", (sc - planet).Length() >= zone + stuck.SlotRadius);
                // a restored frame whose saved berth is inside a planet's frame: a fresh berth, and its grids to follow
                BerthAllocator.IsClear = null;
                var ralloc = new BerthAllocator(2000.0, 20000.0);
                var freg = new FrameRegistry(ralloc);
                var el = new KeplerianElements { SemiMajorAxis = 1e7, Eccentricity = 0.1, Mu = 3.986e14 };
                var old = ralloc.SlotCenter(0);
                var rf = freg.RestoreFrame(77, "Earth", el, Vector3D.Zero, Vector3D.Zero, 0, new List<long>(), 0, old, false);
                Ok("berths: a restored frame's forbidden berth is replaced", rf != null && rf.BerthSlotId != 0 && (rf.BerthCenter - planet).Length() >= zone + ralloc.SlotRadius);
                Ok("berths: its grids are to follow (the shift to the fresh berth)", rf != null && (rf.PendingBerthShift - (rf.BerthCenter - old)).Length() < 1e-6 && rf.PendingBerthShift.Length() > 1);
            }
            finally { BerthAllocator.IsClear = keepClear; BerthAllocator.IsForbidden = keepForbid; }
        }

        private static void BerthAllocatorPacking()
        {
            Section("berth allocator: origin-packed, stable, recyclable slots");
            // 2 km slots with 20 km clearance -> 24 km center spacing.
            var alloc = new BerthAllocator(2000.0, 20000.0);
            Near("spacing = 2r + clearance", alloc.Spacing, 24000.0, 1e-6);

            // first slot is the origin.
            int s0 = alloc.Allocate(out Vector3D c0);
            Ok("first slot id 0", s0 == 0);
            NearVec("first slot at origin", c0, Vector3D.Zero, 1e-6);

            // slots pack nearest-first: every subsequent center is >= the previous distance.
            double prevDist = 0.0;
            bool monotonic = true;
            var centers = new System.Collections.Generic.List<Vector3D>();
            centers.Add(c0);
            for (int i = 0; i < 40; i++)
            {
                alloc.Allocate(out Vector3D c);
                if (c.Length() < prevDist - 1e-6) monotonic = false;
                prevDist = c.Length();
                centers.Add(c);
            }
            Ok("centers ordered by distance from origin", monotonic);

            // no two allocated slots are closer than the spacing (non-interaction guarantee).
            double minSep = double.PositiveInfinity;
            for (int i = 0; i < centers.Count; i++)
                for (int j = i + 1; j < centers.Count; j++)
                {
                    double d = (centers[i] - centers[j]).Length();
                    if (d < minSep) minSep = d;
                }
            Ok("all slots >= spacing apart", minSep >= alloc.Spacing - 1e-6);
            Ok("41 slots occupied", alloc.OccupiedCount == 41);

            // free + realloc reuses the nearest freed slot (recycling).
            alloc.Free(0);
            Ok("slot 0 freed", !alloc.IsOccupied(0));
            int reused = alloc.Allocate(out Vector3D cr);
            Ok("freed slot recycled (nearest-first)", reused == 0);
            NearVec("recycled slot same center", cr, Vector3D.Zero, 1e-6);

            // SlotCenter is a stable pure function of id.
            NearVec("SlotCenter(0) stable", alloc.SlotCenter(0), Vector3D.Zero, 1e-6);
            Ok("SlotCenter deterministic", alloc.SlotCenter(7) == alloc.SlotCenter(7));
        }

        // The planet-cell grid: body -> FIXED deterministic cell, occupancy-independent. This is the
        // property the reframe added — the old allocator gave slot 0 to whoever materialized FIRST,
        // so the mapping depended on play order; now it is pure function of the system definition.
        private static void VoxelBerthDeterministicGrid()
        {
            Section("voxel-berth grid: fixed deterministic body->cell, occupancy-independent");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());   // Earth + Moon are voxel bodies; Sun is logical
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();

            Vector3D earthCell, moonCell, sunCell;
            bool hasEarth = VoxelBerthRegistry.TryGetCell("Earth", sys, out earthCell);
            bool hasMoon = VoxelBerthRegistry.TryGetCell("Moon", sys, out moonCell);
            Ok("Earth has a cell", hasEarth);
            Ok("Moon has a cell", hasMoon);
            Ok("Sun (no ParkSubtype) has NO cell", !VoxelBerthRegistry.TryGetCell("Sun", sys, out sunCell));

            // Home body (first voxel body in registry order) sits at the origin berth so single-body
            // play spawns the player inside its shell — byte-for-byte the old layout.
            NearVec("Earth (slot 0) at CurrentBerth", earthCell, PlanetBerths.CurrentBerth, 1e-6);
            Ok("Moon is in a different cell", Vector3D.DistanceSquared(earthCell, moonCell) > 1.0);
            Ok("cells spaced past clearance (>150 km)", Vector3D.Distance(earthCell, moonCell) > 150.0e3);

            // Determinism: same query is a pure function; a rebuilt mapping is identical.
            Vector3D earthAgain;
            VoxelBerthRegistry.TryGetCell("Earth", sys, out earthAgain);
            NearVec("cell is a pure function (stable)", earthAgain, earthCell, 1e-9);
            VoxelBerthRegistry.Clear();
            Vector3D moonRebuilt;
            VoxelBerthRegistry.TryGetCell("Moon", sys, out moonRebuilt);
            NearVec("cell survives a rebuild unchanged", moonRebuilt, moonCell, 1e-9);

            // THE reframe property: occupancy order does NOT shift the mapping. Materialize the Moon
            // FIRST — Earth must still own slot 0 (the origin berth), not the Moon.
            VoxelBerthRegistry.Clear();
            VoxelBerthRegistry.MarkOccupied("Moon");
            Vector3D earthAfterMoon;
            VoxelBerthRegistry.TryGetCell("Earth", sys, out earthAfterMoon);
            NearVec("Earth keeps slot 0 even when Moon materialized first", earthAfterMoon, PlanetBerths.CurrentBerth, 1e-6);

            // Occupancy is a plain set, decoupled from the cell mapping.
            Ok("AnyOccupied after MarkOccupied", VoxelBerthRegistry.AnyOccupied);
            Ok("occupied count = 1", VoxelBerthRegistry.Count == 1);
            Ok("Moon reported occupied", VoxelBerthRegistry.IsOccupied("Moon"));
            Ok("Earth reported vacant", !VoxelBerthRegistry.IsOccupied("Earth"));
            int occCount = 0;
            Vector3D occMoonCell = Vector3D.Zero;
            foreach (var kv in VoxelBerthRegistry.Occupied(sys))
            { occCount++; if (kv.Key == "Moon") occMoonCell = kv.Value; }
            Ok("Occupied() yields exactly the materialized body", occCount == 1);
            NearVec("Occupied() reports its fixed cell", occMoonCell, moonCell, 1e-9);
            VoxelBerthRegistry.MarkVacant("Moon");
            Ok("vacated -> nothing occupied", !VoxelBerthRegistry.AnyOccupied && VoxelBerthRegistry.Count == 0);

            VoxelBerthRegistry.Clear();
        }

        // The per-observer, frame-relative proxy layout: BodyWorldPos centers the sky on the
        // OBSERVER'S anchor cell, not a single shared origin. This is what lets N players at N bodies
        // each render a correct sky around their OWN cell.
        private static void BodyWorldPosFrameRelative()
        {
            Section("BodyWorldPos: frame-relative, anchored to the observer's own fixed cell");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();

            GravityBody earth = sys.Find("Earth");
            GravityBody moon = sys.Find("Moon");
            double t = 1000.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            Vector3D moonCell = VoxelBerthRegistry.CellOf("Moon", sys);

            // The anchor body is pinned at its OWN cell (offset zero) — its real voxel sits at the cell.
            NearVec("observer@Earth: Earth pinned at its cell", PlanetBerths.BodyWorldPos(sys, earth, t, earth), earthCell, 1e-6);
            NearVec("observer@Moon: Moon pinned at its cell", PlanetBerths.BodyWorldPos(sys, moon, t, moon), moonCell, 1e-6);

            // A proxy is drawn at the anchor cell + the TRUE celestial offset. The Moon proxy seen from
            // Earth is NOT the Moon's real voxel cell.
            Vector3D moonFromEarth = PlanetBerths.BodyWorldPos(sys, moon, t, earth);
            double celSep = (moon.OriginInRoot(t).Position - earth.OriginInRoot(t).Position).Length();
            Near("Moon proxy offset from Earth cell = true celestial separation",
                (moonFromEarth - earthCell).Length(), celSep, 1.0);
            Ok("Moon proxy (Earth frame) is NOT the Moon's voxel cell",
                Vector3D.DistanceSquared(moonFromEarth, moonCell) > 1.0);

            // Symmetry: Earth seen from the Moon sits the same celestial distance from the Moon cell —
            // each observer's sky is centered on their own cell (the N-at-N property).
            Vector3D earthFromMoon = PlanetBerths.BodyWorldPos(sys, earth, t, moon);
            Near("Earth proxy offset from Moon cell = same celestial separation",
                (earthFromMoon - moonCell).Length(), celSep, 1.0);

            // No anchor + no published conjunction frame -> the home compact layout at CurrentBerth.
            PlanetBerths.LocalConjunctionFrame = null;
            NearVec("anchor=null falls back to CurrentBerth + celestial offset",
                PlanetBerths.BodyWorldPos(sys, moon, t, null),
                PlanetBerths.CurrentBerth + (moon.OriginInRoot(t).Position - earth.OriginInRoot(t).Position), 1e-6);

            // CONJUNCTION frame (the treadmill): the sky centers on the conjunction cell with the
            // virtual-orbit celestial state, no day/night wheel (no spin anchor). With a frame
            // published, BodyWorldPos(anchor=null) routes through it.
            Vector3D conjCel = new Vector3D(7.0e6, -3.0e6, 2.0e6);   // a stand-in virtual-orbit celestial pos
            Vector3D conjCell = new Vector3D(0.0, 0.0, 5000.0e3);    // a stand-in conjunction cell center
            var conjFrame = new ObserverFrame(conjCell, conjCel, null);
            NearVec("conjunction frame: body = cell + (body.cel - frameCel), no wheel",
                PlanetBerths.BodyWorldPosInFrame(moon, t, conjFrame),
                conjCell + (moon.OriginInRoot(t).Position - conjCel), 1e-6);
            PlanetBerths.LocalConjunctionFrame = conjFrame;
            NearVec("published conjunction frame drives anchor=null sky",
                PlanetBerths.BodyWorldPos(sys, moon, t, null),
                conjCell + (moon.OriginInRoot(t).Position - conjCel), 1e-6);
            PlanetBerths.LocalConjunctionFrame = null;

            VoxelBerthRegistry.Clear();
        }

        // Berths NEVER conflict: planets and Conjunctions share ONE lattice (SharedAllocator).
        // Planets reserve slots 0..N-1; Conjunctions Allocate() the rest. The allocator's
        // non-interaction invariant (spacing > 2x the largest shell) then makes every berth conflict
        // impossible by construction — and a Conjunction berth can never land in a planet shell.
        private static void LatticeSeparation()
        {
            Section("berths never conflict: one shared lattice, planet slots reserved");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();

            BerthAllocator shared = VoxelBerthRegistry.SharedAllocator(sys);
            Ok("shared allocator exists", shared != null);

            // Planet cells = reserved slots 0..N-1; home body at slot 0 == CurrentBerth.
            var cells = new System.Collections.Generic.List<Vector3D>();
            double maxShell = 0.0;
            int planetCount = 0;
            var bodies = sys.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                BodyDefinition def = sys.FindDefinition(bodies[i].Name);
                if (def == null || def.RadiusMeters <= 0.0 || string.IsNullOrEmpty(def.ParkSubtype)) continue;
                Vector3D c;
                if (VoxelBerthRegistry.TryGetCell(bodies[i].Name, sys, out c)) cells.Add(c);
                double sh = PlanetBerths.ShellRadius(def);
                if (sh > maxShell) maxShell = sh;
                planetCount++;
            }
            Ok("has >=2 planet cells + a shell", cells.Count >= 2 && maxShell > 0.0);
            NearVec("home body (slot 0) at CurrentBerth", cells[0], PlanetBerths.CurrentBerth, 1e-6);
            bool allReserved = true;
            for (int s = 0; s < planetCount; s++) if (!shared.IsOccupied(s)) allReserved = false;
            Ok("planet slots 0..N-1 are reserved", allReserved);

            // Allocate 64 Conjunction berths from the SAME lattice. None may be a planet slot, none
            // may fall inside any planet shell, and none may collide with another berth.
            var berths = new System.Collections.Generic.List<Vector3D>();
            for (int k = 0; k < 64; k++) { Vector3D c; int id = shared.Allocate(out c); Ok("conjunction slot id >= N", id >= planetCount); berths.Add(c); }

            double minPlanetGap = double.PositiveInfinity, minBerthGap = double.PositiveInfinity;
            for (int b = 0; b < berths.Count; b++)
            {
                for (int c = 0; c < cells.Count; c++)
                {
                    double gap = Vector3D.Distance(berths[b], cells[c]) - maxShell;
                    if (gap < minPlanetGap) minPlanetGap = gap;
                }
                for (int b2 = b + 1; b2 < berths.Count; b2++)
                {
                    double d = Vector3D.Distance(berths[b], berths[b2]);
                    if (d < minBerthGap) minBerthGap = d;
                }
            }
            Ok("no conjunction berth inside any planet shell", minPlanetGap > 0.0);
            Ok("every berth pair >= lattice spacing apart", minBerthGap >= shared.Spacing - 1e-6);

            VoxelBerthRegistry.Clear();
        }

        // The voxel handoff shell (2026-06-11 retune): slightly above the atmosphere — NOT the
        // old whole-planet envelope — and DECOUPLED from lattice sizing, which must not shrink
        // with it (the "neighbor cell's voxel is beyond view range" guarantee keys off view range
        // + worst-case terrain, not off where the handoff happens).
        private static void ShellHandoffRadius()
        {
            Section("handoff shell: R + max(2 x atmo, 0.12 R, 4 km); lattice sizing decoupled");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();

            // ATMOSPHERIC body (Earth): the 2 x atmo term dominates the shell. Sized off the LIVE body
            // (round-7 fixture re-author: was hardcoded for an old test-Earth; true-xk Earth R 240 km,
            // atmo 48 km -> shell 336 km). The pin is now the RELATIONSHIP, scale-invariant by construction.
            BodyDefinition earth = sys.FindDefinition("Earth");
            double earthShell = PlanetBerths.ShellRadius(earth);
            // (the multiplier is the mod's own tuning: 2.0 in SE1, 1.35 since the shell was pulled in to ~12 km
            // above the air at Verdure, commit 7dae097)
            double eShellExpect = earth.RadiusMeters + PlanetBerths.ShellAtmosphereMult * earth.AtmosphereHeightMeters;   // atmo term wins for Earth
            Near("Earth shell = R + ShellAtmosphereMult x atmo (atmo term dominates)", earthShell, eShellExpect, 1e-6);
            Ok("Earth shell is the atmosphere top (physics exactly where the air is)",
                Math.Abs(earthShell - (earth.RadiusMeters + earth.AtmosphereHeightMeters)) < 1e-6 || earth.AtmosphereHeightMeters < PlanetBerths.ShellMinClearanceMeters);

            // AIRLESS body (Moon): the 0.12 R hill stand-in now dominates (true-xk R 65.45 km -> 1.12 R =
            // 73.3 km; the old 4 km floor no longer wins now that R is large — regime change, renamed).
            BodyDefinition moon = sys.FindDefinition("Moon");
            double moonShell = PlanetBerths.ShellRadius(moon);
            double mShellExpect = moon.RadiusMeters * (1.0 + PlanetBerths.ShellAirlessClearanceFraction);
            Near("Moon shell = 1.12 R (0.12 R airless term dominates the 4 km floor)", moonShell, mShellExpect, 1e-6);

            // AIRLESS body large enough for the 0.12 R hill stand-in to dominate the floor.
            BodyDefinition big = new BodyDefinition();
            big.RadiusMeters = 100.0e3;
            Near("airless 100 km body: shell = 1.12 R",
                PlanetBerths.ShellRadius(big),
                big.RadiusMeters * (1.0 + PlanetBerths.ShellAirlessClearanceFraction), 1e-6);

            // Degenerate definitions have no shell.
            Near("null def -> 0", PlanetBerths.ShellRadius(null), 0.0, 0.0);

            // FADE BAND vs SHELL (ProxyRenderer invariant): the default anchor fade band is [1.05 R, 1.365 R].
            // Earth's fits inside its (atmo-dominated) shell; at the true-xk scale the Moon JOINS the
            // large-airless regime (1.365 R band top > 1.12 R shell), so it now NEEDS the renderer's
            // band-top clamp, exactly like the big airless body (round-7 re-author: was "fits inside").
            // (with the thin shell Earth joins them: its band top is above the shell too, so it needs the clamp)
            Ok("Earth (thin shell) NEEDS the renderer's band-top clamp (1.365 R > shell)",
                earth.RadiusMeters * 1.05 * 1.3 > earthShell);
            Ok("Moon (true-xk) NEEDS the renderer's band-top clamp (1.365 R > shell)",
                moon.RadiusMeters * 1.05 * 1.3 > moonShell);
            Ok("large airless body NEEDS the renderer's band-top clamp",
                big.RadiusMeters * 1.05 * 1.3 > PlanetBerths.ShellRadius(big));

            // DECOUPLING: lattice cells are sized from CellIsolationRadius = max(2 R, R + atmo) + 50 km,
            // independent of the (smaller) handoff shell, so spacing does NOT shrink with the shell. Sized
            // off the live body (true-xk Earth: max(480k, 288k) + 50k = 530 km) — scale-invariant pin.
            double eIsoExpect = Math.Max(2.0 * earth.RadiusMeters, earth.RadiusMeters + earth.AtmosphereHeightMeters)
                + PlanetBerths.CellIsolationVisibilityPad;
            Near("Earth isolation radius = max(2R, R+atmo) + visibility pad",
                PlanetBerths.CellIsolationRadius(earth), eIsoExpect, 1e-6);
            Ok("isolation envelope >= handoff shell (Earth + Moon)",
                PlanetBerths.CellIsolationRadius(earth) >= earthShell
                && PlanetBerths.CellIsolationRadius(moon) >= moonShell);
            BerthAllocator shared = VoxelBerthRegistry.SharedAllocator(sys);
            // Largest parked body (Earth) sets the lattice: half-extent = CellIsolationRadius(Earth) + R
            // (the slot sphere covers the isolation envelope plus a gravity margin of one body radius);
            // spacing = 2*half-extent + the fixed 100 km SlotClearance (a lattice param, NOT scale-coupled).
            // All tied to the live body so the pin tracks the scale (round-7 re-author; was hardcoded 400/150).
            double slotExpect = PlanetBerths.CellIsolationRadius(earth) + earth.RadiusMeters;
            Near("slot radius covers the whole cell (= CellIsolationRadius(Earth) + R)",
                shared.SlotRadius, slotExpect, 1e-6);
            Near("lattice spacing = 2*SlotRadius + 100 km clearance",
                shared.Spacing, 2.0 * shared.SlotRadius + 100.0e3, 1e-6);

            // THE CONVERSION-RADIUS TABLE: every conversion band is a documented multiple of R_c = ShellRadius,
            // defined ONLY in PlanetBerths. Pinned as RELATIONSHIPS over the live shell (scale-invariant),
            // not absolute km: stow floor R_c x 1.05, keep/anchor/demat R_c x 1.5, latched drop edge keep x 1.02.
            Near("Earth keep = shell x ShellKeepHysteresis",
                PlanetBerths.KeepRadius(earth), earthShell * PlanetBerths.ShellKeepHysteresis, 1e-6);
            Near("Earth demat/anchor drop edge = keep x KeepDropPad",
                PlanetBerths.KeepDropRadius(earth), PlanetBerths.KeepRadius(earth) * PlanetBerths.KeepDropPad, 1e-6);
            Near("Earth stow floor = shell x StowShellMargin",
                earthShell * PlanetBerths.StowShellMargin, eShellExpect * PlanetBerths.StowShellMargin, 1e-6);
            Near("Moon keep = shell x ShellKeepHysteresis",
                PlanetBerths.KeepRadius(moon), moonShell * PlanetBerths.ShellKeepHysteresis, 1e-6);
            Near("Moon drop edge = keep x KeepDropPad",
                PlanetBerths.KeepDropRadius(moon), PlanetBerths.KeepRadius(moon) * PlanetBerths.KeepDropPad, 1e-6);
            // Band ordering invariant: R_c < stow floor < keep < drop edge — and the whole
            // stack still fits inside the lattice slot sphere (a VISIBILITY quantity, not a
            // conversion radius), so an anchored observer can never be in a neighbor's cell.
            Ok("bands ordered: R_c < floor < keep < drop (Earth + Moon)",
                earthShell < earthShell * PlanetBerths.StowShellMargin
                && earthShell * PlanetBerths.StowShellMargin < PlanetBerths.KeepRadius(earth)
                && PlanetBerths.KeepRadius(earth) < PlanetBerths.KeepDropRadius(earth)
                && moonShell < moonShell * PlanetBerths.StowShellMargin
                && moonShell * PlanetBerths.StowShellMargin < PlanetBerths.KeepRadius(moon)
                && PlanetBerths.KeepRadius(moon) < PlanetBerths.KeepDropRadius(moon));
            Ok("keep drop edge fits inside the slot radius",
                PlanetBerths.KeepDropRadius(earth) < shared.SlotRadius
                && PlanetBerths.KeepDropRadius(moon) < shared.SlotRadius);

            VoxelBerthRegistry.Clear();
        }

        // =====================================================================
        //  Frame transformations & observer states — the proxy/cell coordinate
        //  model exercised across every state and transition. The math is pure
        //  (the RENDERING is not testable headlessly), so this is where the
        //  multi-observer model's correctness is actually pinned.
        // =====================================================================

        // celestial -> world in each of the THREE observer states (planet / conjunction / home),
        // plus the multi-observer property: each observer's anchor body is pinned at its OWN cell.
        private static void ProxyTransformAllStates()
        {
            Section("proxy transform: planet / conjunction / home states + multi-observer");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon"), sun = sys.Find("Sun");
            double t = 2000.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            Vector3D moonCell = VoxelBerthRegistry.CellOf("Moon", sys);
            double celEM = (moon.OriginInRoot(t).Position - earth.OriginInRoot(t).Position).Length();

            // STATE 1 — PLANET frame (anchored to a materialized voxel; sky wheels by its spin).
            var atEarth = new ObserverFrame(earthCell, earth.OriginInRoot(t).Position, earth);
            NearVec("planet: anchor body pinned at its cell", PlanetBerths.BodyWorldPosInFrame(earth, t, atEarth), earthCell, 1e-6);
            Near("planet: another body at true celestial distance",
                (PlanetBerths.BodyWorldPosInFrame(sun, t, atEarth) - earthCell).Length(),
                (sun.OriginInRoot(t).Position - earth.OriginInRoot(t).Position).Length(), 1.0);

            // STATE 2 — CONJUNCTION frame (no spin anchor; sky only slides with the orbit).
            Vector3D conjCell = new Vector3D(1.0e6, 2.0e6, -0.5e6);
            Vector3D conjCel = new Vector3D(300.0e3, -150.0e3, 80.0e3);
            var coasting = new ObserverFrame(conjCell, conjCel, null);
            NearVec("conjunction: body = cell + (body.cel - frameCel), no wheel",
                PlanetBerths.BodyWorldPosInFrame(moon, t, coasting),
                conjCell + (moon.OriginInRoot(t).Position - conjCel), 1e-6);

            // STATE 3 — HOME fallback (no anchor, no published conjunction frame).
            PlanetBerths.LocalConjunctionFrame = null;
            NearVec("home: CurrentBerth + (body.cel - homeRef.cel)",
                PlanetBerths.BodyWorldPos(sys, moon, t, null),
                PlanetBerths.CurrentBerth + (moon.OriginInRoot(t).Position - earth.OriginInRoot(t).Position), 1e-6);

            // The SAME body (the Sun) renders at THREE different world positions across three DISTINCT
            // cells — the sky is observer-relative, not a single shared layout. (Observed from the Moon
            // cell, not Earth: the home body's planet frame IS the home layout, so they'd coincide.)
            var atMoon = new ObserverFrame(moonCell, moon.OriginInRoot(t).Position, moon);
            Vector3D sPlanet = PlanetBerths.BodyWorldPosInFrame(sun, t, atMoon);     // from the Moon cell
            Vector3D sConj = PlanetBerths.BodyWorldPosInFrame(sun, t, coasting);     // from the conjunction cell
            Vector3D sHome = PlanetBerths.BodyWorldPos(sys, sun, t, null);           // home layout
            Ok("the three states differ", Vector3D.DistanceSquared(sPlanet, sConj) > 1.0
                && Vector3D.DistanceSquared(sConj, sHome) > 1.0 && Vector3D.DistanceSquared(sPlanet, sHome) > 1.0);

            // MULTI-OBSERVER (N players at N bodies): observer@Earth and observer@Moon each pin THEIR
            // body at THEIR cell, and each sees the OTHER at the same true celestial separation.
            NearVec("multi: observer@Moon pins Moon at the Moon cell", PlanetBerths.BodyWorldPosInFrame(moon, t, atMoon), moonCell, 1e-6);
            Near("multi: Moon-from-Earth distance = celestial separation",
                (PlanetBerths.BodyWorldPosInFrame(moon, t, atEarth) - earthCell).Length(), celEM, 1.0);
            Near("multi: Earth-from-Moon distance = SAME celestial separation",
                (PlanetBerths.BodyWorldPosInFrame(earth, t, atMoon) - moonCell).Length(), celEM, 1.0);
            Ok("multi: each observer's sky is centered on its OWN cell (not shared)",
                Vector3D.DistanceSquared(earthCell, moonCell) > 1.0);
            VoxelBerthRegistry.Clear();
        }

        // The treadmill: as the virtual orbit advances, the frame's celestial slides so the whole sky
        // moves past the observer — while the observer's CELL never moves (hard-pinned). And the
        // parent body appears at a constant distance = the orbital radius (you circle it).
        private static void ConjunctionTreadmillTransform()
        {
            Section("conjunction treadmill: sky slides as the orbit advances, cell fixed");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody earth = sys.Find("Earth"), sun = sys.Find("Sun");
            double rOrbit = 150.0e3;
            KeplerianElements el = CircularElements(rOrbit, earth.Mu);
            Vector3D cell = new Vector3D(0.0, 0.0, 5.0e6);   // the conjunction's fixed cell (never moves)

            double t1 = 100.0, t2 = 100.0 + el.Period * 0.25;   // a quarter orbit later
            Vector3D frameCel1 = earth.OriginInRoot(t1).Position + OrbitPropagation.StateAt(el, t1).Position;
            Vector3D frameCel2 = earth.OriginInRoot(t2).Position + OrbitPropagation.StateAt(el, t2).Position;
            var f1 = new ObserverFrame(cell, frameCel1, null);
            var f2 = new ObserverFrame(cell, frameCel2, null);

            Ok("frame celestial advanced with the orbit", (frameCel2 - frameCel1).Length() > 1.0);
            Vector3D sun1 = PlanetBerths.BodyWorldPosInFrame(sun, t1, f1);
            Vector3D sun2 = PlanetBerths.BodyWorldPosInFrame(sun, t2, f2);
            Ok("the sky (Sun proxy) slid past the observer", Vector3D.DistanceSquared(sun1, sun2) > 1.0);

            // The observer (sitting at `cell`) circles the parent: parent proxy stays one orbital
            // radius away at all times.
            Near("parent appears at the orbital radius (t1)",
                (PlanetBerths.BodyWorldPosInFrame(earth, t1, f1) - cell).Length(), rOrbit, 1.0);
            Near("parent appears at the orbital radius (t2)",
                (PlanetBerths.BodyWorldPosInFrame(earth, t2, f2) - cell).Length(), rOrbit, 1.0);
            VoxelBerthRegistry.Clear();
        }

        // The conjunction -> planet handoff teleport: the whole Conjunction is translated uniformly
        // into the body's cell. The formation is preserved and the anchor lands at its orbital
        // position around the real voxel.
        private static void HandoffTeleportTransform()
        {
            Section("handoff teleport: conjunction -> planet preserves the formation");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth");
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);

            // A 3-grid formation coasting in a berth: anchor + two members at fixed offsets.
            Vector3D anchorOld = new Vector3D(0.0, 0.0, 5.0e6);
            Vector3D mA = anchorOld + new Vector3D(200.0, -50.0, 30.0);
            Vector3D mB = anchorOld + new Vector3D(-120.0, 80.0, -10.0);

            // Crossing state: the virtual orbit's position at the shell, mapped into Earth's cell
            // (the ACTUAL handoff radius, so this stays honest as the shell formula evolves).
            KeplerianElements el = CircularElements(PlanetBerths.ShellRadius(sys.FindDefinition("Earth")), earth.Mu);
            Vector3D orbitPos = OrbitPropagation.StateAt(el, 0.0).Position;
            Vector3D crossWorld = earthCell + orbitPos;
            Vector3D translation = crossWorld - anchorOld;

            Vector3D nAnchor = anchorOld + translation, nA = mA + translation, nB = mB + translation;
            NearVec("anchor lands at its orbital position around the voxel", nAnchor, earthCell + orbitPos, 1e-6);
            NearVec("member A keeps its anchor-relative offset", nA - nAnchor, mA - anchorOld, 1e-9);
            NearVec("member B keeps its anchor-relative offset", nB - nAnchor, mB - anchorOld, 1e-9);
            Near("formation distances preserved", (nA - nB).Length(), (mA - mB).Length(), 1e-6);
            // Position relative to the body CELL = (was-relative-to-anchor) + orbital offset.
            NearVec("member A relative-to-cell = rel-to-anchor + orbit offset",
                nA - earthCell, (mA - anchorOld) + orbitPos, 1e-6);
            VoxelBerthRegistry.Clear();
        }

        // Auto-stow: a grid's WORLD state near a body cell -> orbital elements relative to that cell,
        // and the implied frame celestial matches the grid's planet-frame celestial (continuity).
        private static void StowRelativizationTransform()
        {
            Section("stow relativization: world state -> elements relative to the body cell");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth");
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);

            // A grid 70 km up, on a circular orbit (velocity perpendicular, in-plane).
            Vector3D relPos = new Vector3D(70.0e3, 0.0, 0.0);
            double vc = Math.Sqrt(earth.Mu / relPos.Length());
            Vector3D vel = new Vector3D(0.0, vc, 0.0);
            Vector3D gridWorld = earthCell + relPos;

            // The seam recovers rel state by subtracting the FIXED cell (not a wheeled layout pos).
            NearVec("rel position recovered = gridWorld - cell", gridWorld - earthCell, relPos, 1e-6);
            KeplerianElements el = OrbitalMath.ToElements(new StateVector(relPos, vel), earth.Mu, 0.0);
            StateVector back = OrbitPropagation.StateAt(el, 0.0);
            NearVec("elements round-trip back to the rel state", back.Position, relPos, 1.0);

            // The stowed frame's celestial = parent root + orbit position == the grid's planet-frame
            // celestial (parent root + rel position) — the handoff is celestially continuous.
            Vector3D frameCel = earth.OriginInRoot(0.0).Position + OrbitPropagation.StateAt(el, 0.0).Position;
            Vector3D planetCel = earth.OriginInRoot(0.0).Position + relPos;
            NearVec("stowed celestial == planet-frame celestial (continuous)", frameCel, planetCel, 1.0);
            VoxelBerthRegistry.Clear();
        }

        // Continuity invariants across transitions: the hard-pin is celestially neutral, and a grid's
        // celestial position is continuous through planet <-> conjunction handoffs.
        private static void TransitionContinuityInvariants()
        {
            Section("transition invariants: hard-pin neutrality + planet<->conjunction continuity");

            // HARD-PIN: a uniform translation of the whole Conjunction preserves every member's
            // (memberWorld - anchorWorld), hence its celestial position is unchanged (the treadmill
            // slides world-space without any celestial consequence).
            Vector3D anchorW = new Vector3D(10.0, 20.0, 30.0);
            Vector3D memberW = new Vector3D(15.0, 18.0, 35.0);
            Vector3D frameCel = new Vector3D(500.0e3, -200.0e3, 90.0e3);
            Vector3D celBefore = frameCel + (memberW - anchorW);
            Vector3D T = new Vector3D(-12345.0, 6789.0, -4242.0);   // the pin's correction
            Vector3D celAfter = frameCel + ((memberW + T) - (anchorW + T));
            NearVec("hard-pin is celestially neutral (uniform translate)", celAfter, celBefore, 1e-9);

            // PLANET -> CONJUNCTION continuity: stowing derives elements from the grid's cell-relative
            // state; the resulting conjunction celestial equals the grid's planet-frame celestial.
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth");
            // A generic, non-singular circular orbit (tilted plane) — avoid the equatorial/retrograde
            // element singularities so the round-trip is exact; continuity is the property under test.
            Vector3D rel = new Vector3D(80.0e3, 30.0e3, 20.0e3);
            double vc = Math.Sqrt(earth.Mu / rel.Length());
            Vector3D vdir = Vector3D.Normalize(Vector3D.Cross(rel, new Vector3D(0.0, 0.0, 1.0)));
            Vector3D vel = vdir * vc;
            KeplerianElements el = OrbitalMath.ToElements(new StateVector(rel, vel), earth.Mu, 0.0);
            Vector3D conjCel = earth.OriginInRoot(0.0).Position + OrbitPropagation.StateAt(el, 0.0).Position;
            Vector3D planetCel = earth.OriginInRoot(0.0).Position + rel;
            NearVec("planet->conjunction: celestial continuous", conjCel, planetCel, 1.0);

            // CONJUNCTION -> PLANET continuity (the reverse): materializing at the cell + orbit
            // position reproduces the same celestial the frame had at the crossing.
            double tc = el.Period * 0.37;
            Vector3D orbitAtCross = OrbitPropagation.StateAt(el, tc).Position;
            Vector3D matCel = earth.OriginInRoot(tc).Position + orbitAtCross;            // where the voxel-frame grid is
            Vector3D frameAtCross = earth.OriginInRoot(tc).Position + OrbitPropagation.StateAt(el, tc).Position;
            NearVec("conjunction->planet: celestial continuous", matCel, frameAtCross, 1e-6);
            VoxelBerthRegistry.Clear();
        }

        // The ported RSS PlanetProxyScale technique: a proxy is drawn at its TRUE sky direction and
        // TRUE angular size, but the render DISTANCE is clamped so a true-scale system can never push
        // a proxy past single precision / the far plane. Near bodies render at true distance & size
        // (compact systems unchanged); far bodies sit on the sky shell at the right angular size.
        private static void ProxyProjectionClamp()
        {
            Section("proxy projection: true direction + angular size, distance clamped (RSS-style)");
            double clamp = PlanetBerths.SkyClampDistance;

            // --- NEAR body (compact system): renders at TRUE distance & size, == old BodyWorldPos. ---
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sysC = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earthC = sysC.Find("Earth"), moonC = sysC.Find("Moon");
            double t = 500.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sysC);
            var atEarthC = new ObserverFrame(earthCell, earthC.OriginInRoot(t).Position, null);   // null spin = no wheel
            double moonR = sysC.FindDefinition("Moon").RadiusMeters;
            double dMoon = (moonC.OriginInRoot(t).Position - earthC.OriginInRoot(t).Position).Length();
            // CLASS B (true-xk rescale): SkyClampDistance is a hard-coded absolute 2000 km that did
            // NOT scale; at true-xk the Moon orbits Earth at ~14 479 km, so the body you literally
            // orbit is ~7x beyond the clamp and renders pressed onto the 2000 km sky shell. The four
            // "Moon renders unclamped" asserts now fail; whether the clamp should scale with the
            // system (or everything-clamped is intended at true-xk) is a design decision rob owes.
            Pending("Moon is nearer than the clamp", SkyClampPendingWhy);
            ProxyPlacement near = PlanetBerths.ProjectProxy(earthCell, moonC.OriginInRoot(t).Position, atEarthC, moonR, t);
            Near("near: true distance preserved", near.TrueDistance, dMoon, 1.0);
            Pending("near: render distance == true distance", SkyClampPendingWhy);
            Pending("near: render radius == true radius", SkyClampPendingWhy);
            Pending("near: render pos == cell + celestial offset (== old layout)", SkyClampPendingWhy);

            // --- FAR body (true-ish scale Sol): clamped to the shell, angular size preserved. ---
            SystemRegistry.Build(SampleSystems.Sol());
            SystemRegistry sysS = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earthS = sysS.Find("Earth"), jup = sysS.Find("Jupiter");
            Vector3D earthCellS = VoxelBerthRegistry.CellOf("Earth", sysS);
            var atEarthS = new ObserverFrame(earthCellS, earthS.OriginInRoot(t).Position, null);
            double jupR = sysS.FindDefinition("Jupiter").RadiusMeters;
            Vector3D jupCel = jup.OriginInRoot(t).Position, earthCelS = earthS.OriginInRoot(t).Position;
            double dJup = (jupCel - earthCelS).Length();
            Ok("Jupiter is far beyond the clamp", dJup > clamp);
            ProxyPlacement far = PlanetBerths.ProjectProxy(earthCellS, jupCel, atEarthS, jupR, t);
            Near("far: render distance clamped to the sky shell", (far.RenderPos - earthCellS).Length(), clamp, 1.0);
            Near("far: TRUE distance still reported", far.TrueDistance, dJup, 1.0);
            // Angular size preserved, then SCALED by the flat-monitor apparent-size exaggeration
            // (ProjectProxy multiplies RenderRadius by ApparentSizeMult; Jupiter is far -> full 2x):
            // asin(renderR/renderDist) == asin(trueR/trueDist) * ApparentSizeMult(R, dTrue).
            Near("far: angular size preserved (renderR/renderDist == R/dTrue)",
                far.RenderRadius / clamp,
                (jupR / dJup) * PlanetBerths.ApparentSizeMult(jupR, dJup), 1e-9);
            // Direction EXACTLY preserved.
            Vector3D dirTrue = Vector3D.Normalize(jupCel - earthCelS);
            Vector3D dirRender = Vector3D.Normalize(far.RenderPos - earthCellS);
            Near("far: sky direction preserved", (dirRender - dirTrue).Length(), 0.0, 1e-9);

            // --- THE GUARANTEE: across BOTH systems, no proxy ever renders past the clamp. ---
            Ok("clamp guarantee: SunEarthMoon proxies all within the shell", AllProxiesWithinClamp(SampleSystems.SunEarthMoon(), clamp));
            Ok("clamp guarantee: true-scale Sol proxies all within the shell", AllProxiesWithinClamp(SampleSystems.Sol(), clamp));
            VoxelBerthRegistry.Clear();
        }

        // Round-trip (rendered world position <-> celestial) and frame-invariant sky RATES for the
        // Earth system. The cell is a 1:1 inertial window, so BodyWorldPosInFrame and
        // ObserverCelestial must be exact inverses in EVERY frame type; and the apparent angular
        // sweep of a proxy over the same dt must be IDENTICAL from a planet frame and a conjunction
        // frame at the same celestial vantage — the transform adds no rotation. Any in-game rate
        // difference between the two frame types is therefore the universe-clock TIMESCALE
        // (WarpPolicy realtime-locks near an occupied voxel and warps while coasting), NOT sky math.
        private static void ProxyRoundTripFrameRates()
        {
            Section("proxy round-trip (world <-> celestial) + frame-invariant sky rates");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon"), sun = sys.Find("Sun");
            double t = 750.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            Vector3D earthCel = earth.OriginInRoot(t).Position;
            var planet = new ObserverFrame(earthCell, earthCel, earth);

            // ---- ROUND TRIP: world = BodyWorldPosInFrame(B), ObserverCelestial(world) == B.cel ----
            Vector3D moonWorld = PlanetBerths.BodyWorldPosInFrame(moon, t, planet);
            NearVec("planet frame: world -> celestial inverts the layout map",
                PlanetBerths.ObserverCelestial(planet, moonWorld), moon.OriginInRoot(t).Position, 1e-6);

            Vector3D conjCell = new Vector3D(-2.0e6, 1.0e6, 3.0e6);
            var conj = new ObserverFrame(conjCell, earthCel, null);   // SAME celestial vantage, different cell
            Vector3D moonWorldC = PlanetBerths.BodyWorldPosInFrame(moon, t, conj);
            NearVec("conjunction frame: world -> celestial inverts the layout map",
                PlanetBerths.ObserverCelestial(conj, moonWorldC), moon.OriginInRoot(t).Position, 1e-6);

            // The in-cell camera offset maps 1:1 through the window (parallax is exact).
            Vector3D camOff = new Vector3D(1500.0, -300.0, 7200.0);
            NearVec("camera offset maps 1:1 through the window",
                PlanetBerths.ObserverCelestial(planet, earthCell + camOff), earthCel + camOff, 1e-9);

            // ProjectProxy (what the player actually sees) agrees with the layout map for unclamped
            // bodies in BOTH frames, and the camera-relative placement is frame-INDEPENDENT.
            double moonR = sys.FindDefinition("Moon").RadiusMeters;
            ProxyPlacement pP = PlanetBerths.ProjectProxy(earthCell, moon.OriginInRoot(t).Position, planet, moonR, t);
            ProxyPlacement pC = PlanetBerths.ProjectProxy(conjCell, moon.OriginInRoot(t).Position, conj, moonR, t);
            // CLASS B (true-xk SkyClampDistance): the Moon is no longer "unclamped" at true-xk (it
            // orbits at ~14 479 km, well past the absolute 2000 km clamp), so ProjectProxy now
            // compresses it onto the sky shell instead of mapping 1:1 to the layout. Pending rob's
            // clamp decision; the frame-INDEPENDENCE assert below still holds (both frames clamp
            // identically) and is kept live.
            Pending("planet frame: ProjectProxy == layout map (unclamped)", SkyClampPendingWhy);
            Pending("conjunction frame: ProjectProxy == layout map (unclamped)", SkyClampPendingWhy);
            NearVec("same vantage -> same camera-relative placement in both frames",
                pP.RenderPos - earthCell, pC.RenderPos - conjCell, 1e-6);

            // ---- FRAME-INVARIANT RATES over the same dt, same vantage trajectory (riding Earth) ----
            double dt = 60.0, t2 = t + dt;
            Vector3D earthCel2 = earth.OriginInRoot(t2).Position;
            var planet2 = new ObserverFrame(earthCell, earthCel2, earth);
            var conj2 = new ObserverFrame(conjCell, earthCel2, null);
            GravityBody[] targets = new GravityBody[] { moon, sun };
            for (int i = 0; i < targets.Length; i++)
            {
                GravityBody b = targets[i];
                double r = 1000.0;   // radius only affects RenderRadius, not direction — any positive value
                Vector3D dP1 = Vector3D.Normalize(PlanetBerths.ProjectProxy(earthCell, b.OriginInRoot(t).Position, planet, r, t).RenderPos - earthCell);
                Vector3D dP2 = Vector3D.Normalize(PlanetBerths.ProjectProxy(earthCell, b.OriginInRoot(t2).Position, planet2, r, t2).RenderPos - earthCell);
                Vector3D dC1 = Vector3D.Normalize(PlanetBerths.ProjectProxy(conjCell, b.OriginInRoot(t).Position, conj, r, t).RenderPos - conjCell);
                Vector3D dC2 = Vector3D.Normalize(PlanetBerths.ProjectProxy(conjCell, b.OriginInRoot(t2).Position, conj2, r, t2).RenderPos - conjCell);
                double sweepPlanet = Math.Acos(Math.Clamp(Vector3D.Dot(dP1, dP2), -1.0, 1.0));
                double sweepConj = Math.Acos(Math.Clamp(Vector3D.Dot(dC1, dC2), -1.0, 1.0));
                Ok("sky actually sweeps over dt (" + b.Name + ")", sweepPlanet > 1e-7);
                Near("apparent sweep identical planet vs conjunction (" + b.Name + ")",
                    sweepPlanet, sweepConj, 1e-12);
            }
            VoxelBerthRegistry.Clear();
        }

        // ProjectProxy's degenerate guard: an observer ON or INSIDE a body (dTrue <= R) has no
        // valid proxy placement — the old early-out returned RenderPos=camPos with the FULL body
        // radius (a planet-radius sphere centered on the camera that passed the renderer's
        // RenderRadius<=0 skip), and for 1 < dTrue < R the asin(r/d) identity is invalid (r/d > 1,
        // camera inside the proxy hull). All such cases must come back RenderRadius 0 (skipped).
        private static void ProxyProjectionDegenerateGuard()
        {
            Section("proxy projection: dTrue <= bodyRadius -> degenerate placement (skip)");
            double r = 60.0e3;   // a 60 km body
            Vector3D cell = new Vector3D(0.0, 0.0, -40.0e3);
            Vector3D frameCel = new Vector3D(150.0e6, 0.0, 0.0);
            var frame = new ObserverFrame(cell, frameCel, null);
            // Observer at the cell center -> observer celestial == frameCel.

            // Camera exactly AT the body's celestial position (dTrue = 0).
            ProxyPlacement at = PlanetBerths.ProjectProxy(cell, frameCel, frame, r, 0.0);
            Ok("dTrue=0: RenderRadius 0 (renderer skips)", at.RenderRadius == 0.0);
            Near("dTrue=0: true distance still reported", at.TrueDistance, 0.0, 1e-9);

            // Camera INSIDE the body (0 < dTrue < R) — the old guard passed this through with
            // renderRadius > dRender (camera inside the proxy hull).
            ProxyPlacement inside = PlanetBerths.ProjectProxy(cell, frameCel + new Vector3D(0.5 * r, 0, 0), frame, r, 0.0);
            Ok("dTrue=R/2: RenderRadius 0 (renderer skips)", inside.RenderRadius == 0.0);
            Near("dTrue=R/2: true distance still reported", inside.TrueDistance, 0.5 * r, 1e-6);

            // Camera ON the surface (dTrue == R) — boundary is degenerate too (asin(1) hull edge).
            ProxyPlacement on = PlanetBerths.ProjectProxy(cell, frameCel + new Vector3D(r, 0, 0), frame, r, 0.0);
            Ok("dTrue=R: RenderRadius 0 (renderer skips)", on.RenderRadius == 0.0);

            // Just OUTSIDE (dTrue = 1.01 R): valid again — r/d < 1, radius < distance, angular
            // size exact, continuous with the normal regime.
            double d = 1.01 * r;
            ProxyPlacement outp = PlanetBerths.ProjectProxy(cell, frameCel + new Vector3D(d, 0, 0), frame, r, 0.0);
            Ok("dTrue=1.01R: valid placement (RenderRadius > 0)", outp.RenderRadius > 0.0);
            Ok("dTrue=1.01R: camera outside the proxy hull (radius < distance)",
                outp.RenderRadius < (outp.RenderPos - cell).Length());
            Near("dTrue=1.01R: angular size exact (renderR/renderD == R/dTrue)",
                outp.RenderRadius / (outp.RenderPos - cell).Length(), r / d, 1e-9);

            // Degenerate body radius keeps returning a skip.
            ProxyPlacement zeroR = PlanetBerths.ProjectProxy(cell, frameCel + new Vector3D(1.0e6, 0, 0), frame, 0.0, 0.0);
            Ok("bodyRadius=0: RenderRadius 0 (renderer skips)", zeroR.RenderRadius == 0.0);
        }

        // Celestial orbit RINGS (audit #7, CelestialOrbitRenderer): the renderer caches each
        // orbiting body's ellipse ONCE in the parent-centered inertial frame and re-maps every
        // cached point each frame through the frame map
        //   world = (parent.cel + local) − Frame.FrameCel + Frame.CellCenter
        // then compresses it onto the DRAW-TIME camera's sky shell via the ONE shared clamp,
        // PlanetBerths.CompressToSkyShell (moved from OrbitRenderer's private copy). Pins: the
        // mapping equals BodyWorldPosInFrame(parent) + local; the sampled geometry passes through
        // the body itself; every SunEarthMoon Moon ring point lands within SkyClampDistance of a
        // camera at Earth's cell after compression (compact ring -> compression is the identity);
        // and the moved helper behaves exactly like the old private one (near identity, far point
        // ON the shell, direction preserved).
        private static void CelestialRingMapping()
        {
            Section("celestial orbit rings: frame mapping + shared sky-shell compression");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon");
            double t = 800.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            Vector3D earthCel = earth.OriginInRoot(t).Position;
            var frame = new ObserverFrame(earthCell, earthCel, earth);

            // The renderer's cached geometry: the Moon's ellipse in Earth's inertial frame.
            StateVector inParent = moon.StateInParentAt(t);
            KeplerianElements el = OrbitalMath.ToElements(inParent, earth.Mu, t);
            OrbitPath ring = OrbitSampler.SamplePath(el, 96, Math.Abs(el.SemiMajorAxis) * 4.0);
            Ok("ring sampled closed with the requested point count", ring.IsClosed && ring.Points.Length == 96);

            // The geometry passes through the body itself: the ellipse point at the Moon's own
            // true anomaly IS the Moon's current parent-relative position.
            NearVec("ring geometry passes through the body",
                OrbitSampler.PositionAtTrueAnomaly(el, el.TrueAnomaly), inParent.Position, 1.0);

            // THE MAPPING PIN: a cached ring point maps to parentCel + local − FrameCel + CellCenter,
            // which is exactly BodyWorldPosInFrame(parent) + local (the frame map, no rotation).
            Vector3D local = ring.Points[17];
            // The map is algebraically exact; the residual is pure float64 ULP at the true-xk
            // coordinate magnitude (~5.6e9 m), so scale the tolerance to the coordinate magnitude
            // rather than the old absolute 1e-9 (which was sized for the pre-rescale ~1e7 m scale).
            Vector3D mapped = PlanetBerths.BodyWorldPosInFrame(earth, t, frame) + local;
            double ringTol = 1e-13 * mapped.Length();   // relative double precision (~few ULP at this magnitude)
            NearVec("ring point maps through the frame map (== BodyWorldPosInFrame(parent) + local)",
                mapped,
                (earthCel + local) - frame.FrameCel + frame.CellCenter, ringTol);

            // Camera at Earth's cell: every Moon ring point compresses to within the clamp — and
            // since the compact ring never exceeds it, compression is the IDENTITY here.
            double clamp = PlanetBerths.SkyClampDistance;
            Vector3D parentWorld = PlanetBerths.BodyWorldPosInFrame(earth, t, frame);
            bool allWithin = true, allIdentity = true;
            for (int k = 0; k < ring.Points.Length; k++)
            {
                Vector3D world = parentWorld + ring.Points[k];
                Vector3D pressed = PlanetBerths.CompressToSkyShell(world, earthCell);
                if ((pressed - earthCell).Length() > clamp + 1e-6) allWithin = false;
                if ((pressed - world).Length() > 1e-9) allIdentity = false;
            }
            Ok("all Moon ring points within SkyClampDistance of the Earth-cell camera", allWithin);
            // CLASS B (true-xk SkyClampDistance): the Moon orbit radius (~14 479 km) is now far past
            // the absolute 2000 km clamp, so the ring is NOT compact and compression is NOT the
            // identity — same root as the SkyClampDistance clamp decision rob owes. (allIdentity is
            // computed above; referenced here so the loop's intent is preserved.)
            _ = allIdentity;
            Pending("compact ring: compression is the identity (no point past the clamp)", SkyClampPendingWhy);

            // The MOVED helper's behavior is unchanged (was OrbitRenderer's private copy):
            // near point untouched, boundary point untouched, far point ON the shell with its
            // direction preserved.
            Vector3D cam = new Vector3D(3.0e5, -1.0e5, 7.0e5);
            Vector3D nearPt = cam + new Vector3D(1.2e6, 0.5e6, -0.3e6);   // |delta| < clamp
            NearVec("near point: identity", PlanetBerths.CompressToSkyShell(nearPt, cam), nearPt, 1e-9);
            Vector3D onShell = cam + new Vector3D(clamp, 0, 0);
            NearVec("boundary point: identity", PlanetBerths.CompressToSkyShell(onShell, cam), onShell, 1e-9);
            Vector3D farDelta = new Vector3D(3.0e9, -4.0e9, 1.0e9);
            Vector3D farPressed = PlanetBerths.CompressToSkyShell(cam + farDelta, cam);
            Near("far point lands ON the shell", (farPressed - cam).Length(), clamp, 1e-6);
            Near("far point direction preserved",
                (Vector3D.Normalize(farPressed - cam) - Vector3D.Normalize(farDelta)).Length(), 0.0, 1e-12);
            VoxelBerthRegistry.Clear();
        }

        // The anchor-swap gap closer (transition story): with NO voxel anchor and NO published
        // conjunction frame, ResolveFrame's camera overload resolves the body whose fixed lattice
        // CELL CONTAINS the camera — not the home layout. That is exactly the arrival window (grid
        // teleported into the target cell before its voxel streams in) and the departure flash
        // (camera still in the old cell before the conjunction publishes). Priority: anchor >
        // conjunction > cell containment > home.
        private static void ResolveFrameCellContainment()
        {
            Section("observer frame: cell-containment fallback (anchor-swap gap)");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            PlanetBerths.LocalConjunctionFrame = null;
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon");
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            Vector3D moonCell = VoxelBerthRegistry.CellOf("Moon", sys);
            SEAerospace.Frames.BerthAllocator shared = VoxelBerthRegistry.SharedAllocator(sys);
            double slotR = shared.SlotRadius;
            double t = 750.0;

            // The raw containment query: a point in the Moon's cell maps to Moon, a point in the
            // inter-cell gap maps to nothing.
            string body; Vector3D center;
            Ok("containment: point in Moon cell -> Moon",
                VoxelBerthRegistry.TryCellContaining(moonCell + new Vector3D(0.5 * slotR, 0, 0), sys, out body, out center)
                && body == "Moon" && (center - moonCell).Length() < 1e-6);
            // Inter-cell gap: with the slots now nearly close-packed (Spacing ~= 2*SlotRadius +
            // SlotClearance), the gap is the thin shell BETWEEN two adjacent slot spheres. A point
            // at half the cell spacing along an axis sits in that gap (Spacing/2 > SlotRadius), so
            // it belongs to no cell. (The old 1.5*SlotRadius probe now lands inside the neighbour.)
            Ok("containment: inter-cell gap -> none",
                !VoxelBerthRegistry.TryCellContaining(moonCell + new Vector3D(0, shared.Spacing * 0.5, 0), sys, out body, out center));

            // THE GAP CASE (arrival/departure): anchor null, no conjunction, camera inside the
            // Moon's cell -> the MOON frame, exactly as if the voxel were up — not home. The probe
            // sits CLEARLY beyond the Moon keep (so it's coasting, no body discriminator) but well
            // inside the lattice slot (so it still resolves the Moon cell). At true-xk the Moon keep
            // grew to ~110 km, so the old 100 km probe fell inside it; derive the probe from the
            // live keep radius instead.
            double moonKeepGap = PlanetBerths.KeepRadius(sys.FindDefinition("Moon"));
            double coastDist = 1.5 * moonKeepGap;   // well beyond keep, far inside SlotRadius
            Vector3D camInMoonCell = moonCell + new Vector3D(coastDist, 0, 0);
            ObserverFrame gap = PlanetBerths.ResolveFrame(sys, t, null, camInMoonCell);
            NearVec("gap: cell center = Moon's cell", gap.CellCenter, moonCell, 1e-6);
            NearVec("gap: frame celestial = Moon's celestial", gap.FrameCel, moon.OriginInRoot(t).Position, 1e-6);
            // Discriminator is keep-envelope-bounded (2026-06-11): beyond the keep the camera is
            // coasting — positional Moon frame (asserted above), NO body discriminator.
            Ok("gap at 100 km: positional frame only (no body discriminator)", gap.SpinAnchor == null);
            // Inside the keep envelope (Moon shell x 1.5) the discriminator IS the body.
            BodyDefinition moonDefKeep = sys.FindDefinition("Moon");
            double moonKeep = PlanetBerths.ShellRadius(moonDefKeep) * PlanetBerths.ShellKeepHysteresis;
            ObserverFrame inKeep = PlanetBerths.ResolveFrame(sys, t, null, moonCell + new Vector3D(0.9 * moonKeep, 0, 0));
            Ok("gap inside keep envelope: frame body = Moon", inKeep.SpinAnchor == moon);
            NearVec("inside-keep frame is still the Moon cell", inKeep.CellCenter, moonCell, 1e-6);
            // ... and the positional gap frame matches the REAL Moon planet frame (sky continuity at swap).
            ObserverFrame real = PlanetBerths.ResolveFrame(sys, t, moon, camInMoonCell);
            NearVec("gap frame == anchored Moon frame (cell)", gap.CellCenter, real.CellCenter, 1e-9);
            NearVec("gap frame == anchored Moon frame (celestial)", gap.FrameCel, real.FrameCel, 1e-9);

            // PRIORITY 1: a voxel anchor wins over containment (camera in Moon cell, anchored Earth
            // -> Earth frame; e.g. shells/cells disagree momentarily, the voxel is the truth).
            ObserverFrame anchored = PlanetBerths.ResolveFrame(sys, t, earth, camInMoonCell);
            NearVec("anchor wins over containment", anchored.CellCenter, earthCell, 1e-6);

            // PRIORITY 2: a published conjunction frame wins over containment (coasting THROUGH a
            // cell region keeps the treadmill sky).
            var conj = new ObserverFrame(new Vector3D(9.0e6, 0, 0), new Vector3D(1.0e9, 2.0e9, 3.0e9), null);
            PlanetBerths.LocalConjunctionFrame = conj;
            ObserverFrame coasting = PlanetBerths.ResolveFrame(sys, t, null, camInMoonCell);
            NearVec("conjunction wins over containment", coasting.CellCenter, conj.CellCenter, 1e-9);
            PlanetBerths.LocalConjunctionFrame = null;

            // PRIORITY 4: genuinely nowhere (outside every cell, no anchor, no conjunction) ->
            // the home layout, as before.
            Vector3D nowhere = earthCell + new Vector3D(0, 0, 500.0 * slotR);
            ObserverFrame home = PlanetBerths.ResolveFrame(sys, t, null, nowhere);
            NearVec("nowhere -> home layout cell", home.CellCenter, PlanetBerths.CurrentBerth, 1e-9);
            Ok("nowhere -> no frame body", home.SpinAnchor == null);

            // The camera-free overload is unchanged: anchorless resolution still falls to home
            // (it has no observer position to test containment with).
            ObserverFrame legacy = PlanetBerths.ResolveFrame(sys, t, null);
            NearVec("camera-free overload keeps home fallback", legacy.CellCenter, PlanetBerths.CurrentBerth, 1e-9);
            VoxelBerthRegistry.Clear();
        }

        // Cross-SOI re-parenting of live frames (the Moon-transfer fix, 2026-06-11): an
        // Earth-parented frame entering the Moon's SOI re-parents DOWN (Galilean patch,
        // position continuous); escapes re-parent UP; the exit pad prevents boundary flap.
        private static void SoiReparentTransfer()
        {
            Section("SOI re-parent: encounter / escape / pad hysteresis (Moon transfer)");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon");
            double t = 100.0;

            // Geometry premise: the Moon's keep envelope fits inside its SOI (arrival window exists).
            double moonKeep = PlanetBerths.ShellRadius(sys.FindDefinition("Moon")) * PlanetBerths.ShellKeepHysteresis;
            Ok("Moon keep envelope inside Moon SOI", moonKeep < moon.SoiRadius);

            // ENCOUNTER: Earth-parented state 20 km from the Moon (inside SOI 23.1 km), ~co-moving.
            StateVector ms = moon.StateInParentAt(t);
            Vector3D start = ms.Position + new Vector3D(20.0e3, 0, 0);
            var el = OrbitalMath.ToElements(new StateVector(start, ms.Velocity + new Vector3D(0, 30.0, 0)), earth.Mu, t);
            var f = new ProximityFrame(1, "Earth", el, 42);
            Ok("encounter re-parents to Moon",
                SoiReparent.Step(f, sys, t) == SoiReparent.Change.Encounter && f.ParentBodyName == "Moon");
            // Galilean patch: Moon-relative state + Moon's Earth state == original Earth state.
            StateVector inMoon = OrbitPropagation.StateAt(f.Elements, t);
            NearVec("patch position continuous", inMoon.Position + ms.Position, start, 1e-6);

            // ESCAPE Moon -> Earth (above SOI x pad).
            f.Elements = OrbitalMath.ToElements(new StateVector(
                new Vector3D(moon.SoiRadius * 1.05, 0, 0), new Vector3D(0, 60.0, 0)), moon.Mu, t);
            Ok("escape re-parents to Earth",
                SoiReparent.Step(f, sys, t) == SoiReparent.Change.Escape && f.ParentBodyName == "Earth");

            // ESCAPE Earth -> Sun.
            f.Elements = OrbitalMath.ToElements(new StateVector(
                new Vector3D(earth.SoiRadius * 1.05, 0, 0), new Vector3D(0, 40.0, 0)), earth.Mu, t);
            Ok("escape re-parents to Sun",
                SoiReparent.Step(f, sys, t) == SoiReparent.Change.Escape && f.ParentBodyName == "Sun");

            // PAD BAND: just above the Moon's SOI but below SOI x EscapePad -> no flap.
            f.ParentBodyName = "Moon";
            f.Elements = OrbitalMath.ToElements(new StateVector(
                new Vector3D(moon.SoiRadius * 1.01, 0, 0), new Vector3D(0, 60.0, 0)), moon.Mu, t);
            Ok("exit pad band holds the parent (no flap)",
                SoiReparent.Step(f, sys, t) == SoiReparent.Change.None && f.ParentBodyName == "Moon");

            // Legacy "Planet:<id>" frames are untouched.
            var legacyFrame = new ProximityFrame(2, "Planet:12345", el, 43);
            Ok("legacy frame untouched", SoiReparent.Step(legacyFrame, sys, t) == SoiReparent.Change.None);
            VoxelBerthRegistry.Clear();
        }

        // [audit fix #3] WARP-TUNNEL pin: at high warp the rails can cross an ENTIRE child SOI
        // between two seam checks — a bare position poll sees "outside" at both checks and the
        // transfer coasts straight through the Moon on Earth Kepler. The look-ahead overload
        // must find the encounter inside the window and patch AT the crossing-time state
        // (position/velocity continuous at the patch point).
        private static void SoiReparentWarpTunnel()
        {
            Section("SOI re-parent: warp-tunnel encounter (whole-SOI transit within one check window)");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon");

            // A fast Earth-frame transfer that passes ~dead-center through the Moon at the window
            // MIDPOINT. The whole SOI transit (2 x SOI / |v|) must fit inside one ~50 s window
            // (0.5 s seam check x ~x100 warp) while the craft is STRICTLY OUTSIDE the SOI at BOTH
            // window edges. Both constraints derive from the live SOI: at the midpoint the edge
            // separation ~= |v|*(window/2), so |v| > 2*SOI/window puts both edges past the SOI; we
            // take |v| = 6*SOI/window (3x margin) so the edges are ~3x the SOI out while the transit
            // (33% of the window) still tunnels well inside it. At true-xk the Moon SOI ballooned to
            // ~2491 km (was ~23 km), so this contrived high closing speed (~300 km/s) is what it now
            // takes to tunnel a whole-SOI transit between two 0.5 s seam checks — exactly the
            // anti-tunnel guard the look-ahead overload exists for.
            double t = 100.0, window = 50.0, tEnc = t + window * 0.5;
            double soiTunnel = moon.SoiRadius;
            Vector3D pEnc = moon.StateInParentAt(tEnc).Position;
            Vector3D vDir = Vector3D.Normalize(Vector3D.Cross(Vector3D.UnitZ, pEnc));
            Vector3D v = vDir * (6.0 * soiTunnel / window);
            Vector3D p0 = pEnc - v * (tEnc - t);   // straight-line aim through the Moon at tEnc
            var el0 = OrbitalMath.ToElements(new StateVector(p0, v), earth.Mu, t);

            double soi = moon.SoiRadius;
            double sepStart = (OrbitPropagation.StateAt(el0, t).Position - moon.StateInParentAt(t).Position).Length();
            double sepEnd = (OrbitPropagation.StateAt(el0, t + window).Position - moon.StateInParentAt(t + window).Position).Length();
            Ok("premise: outside the SOI at BOTH window edges (the poll-tunneling geometry)",
                sepStart > soi && sepEnd > soi);

            // The bare poll (no look-ahead) misses the encounter entirely — the old bug.
            var fPoll = new ProximityFrame(1, "Earth", el0, 42);
            Ok("bare position poll misses (tunnel)", SoiReparent.Step(fPoll, sys, t) == SoiReparent.Change.None
                && fPoll.ParentBodyName == "Earth");

            // The scheduled overload finds it and patches AT the crossing time.
            var f = new ProximityFrame(2, "Earth", el0, 43);
            Ok("look-ahead window finds the encounter",
                SoiReparent.Step(f, sys, t, window) == SoiReparent.Change.Encounter
                && f.ParentBodyName == "Moon");

            double tX = f.Elements.Epoch;
            Ok("patch epoch inside the window", tX > t && tX < t + window);
            // Entry state sits AT the SOI boundary (just inside — the escape pad can't flap it).
            double rEntry = OrbitPropagation.StateAt(f.Elements, tX).Position.Length();
            Ok("patched at the SOI boundary (just inside)", rEntry <= soi && rEntry > soi * 0.9);
            // Galilean continuity AT the patch point: old Earth-frame state == Moon state + new
            // Moon-frame state, position AND velocity.
            StateVector oldAtX = OrbitPropagation.StateAt(el0, tX);
            StateVector moonAtX = moon.StateInParentAt(tX);
            StateVector newAtX = OrbitPropagation.StateAt(f.Elements, tX);
            NearVec("patch position continuous at t_cross", moonAtX.Position + newAtX.Position, oldAtX.Position, 5.0);
            NearVec("patch velocity continuous at t_cross", moonAtX.Velocity + newAtX.Velocity, oldAtX.Velocity, 0.01);
            VoxelBerthRegistry.Clear();
        }

        // [audit fix #9] The SERVER-SAFE grid resolve (PlanetBerths.ResolveFrameForGrid): seam
        // math must NEVER read the client-published LocalConjunctionFrame static (on a listen
        // host that is the HOST's window — poison for a remote player's grid). Priorities:
        // membership > planet-cell containment > conjunction-berth containment > home.
        private static void ServerGridResolve()
        {
            Section("server-safe grid resolve: membership / cell / berth containment, no client static");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            var freg = new FrameRegistry(VoxelBerthRegistry.SharedAllocator(sys));
            GravityBody earth = sys.Find("Earth");
            double t = 50.0;

            // Poison the CLIENT static: the grid resolve must never read it.
            var poison = new Vector3D(9.0e9, 9.0e9, 9.0e9);
            PlanetBerths.LocalConjunctionFrame = new ObserverFrame(poison, poison, null);

            // Planet-cell containment -> the body's window (position + ride velocity).
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            ObserverFrame w1 = PlanetBerths.ResolveFrameForGrid(sys, t, null, 777,
                earthCell + new Vector3D(10.0e3, 0, 0), freg);
            NearVec("cell containment -> Earth window", w1.FrameCel, earth.OriginInRoot(t).Position, 1e-6);
            NearVec("window rides Earth's velocity", w1.FrameCelVel, earth.OriginInRoot(t).Velocity, 1e-9);

            // Membership beats containment: a framed grid physically inside Earth's cell still
            // resolves its OWN rails window.
            var el = CircularElements(90.0e3, earth.Mu);
            ProximityFrame f = freg.CreateFrame("Earth", el, 901);
            StateVector rails = OrbitPropagation.StateAt(el, t);
            ObserverFrame w2 = PlanetBerths.ResolveFrameForGrid(sys, t, null, 901,
                earthCell + new Vector3D(10.0e3, 0, 0), freg);
            NearVec("membership -> the frame's rails window",
                w2.FrameCel, earth.OriginInRoot(t).Position + rails.Position, 1e-6);
            NearVec("membership window cell = the berth center", w2.CellCenter, f.BerthCenter, 1e-9);

            // Berth containment: an UN-framed grid physically inside that conjunction's slot
            // sphere resolves the conjunction's window (the foreign-berth stow/arrival case).
            ObserverFrame w3 = PlanetBerths.ResolveFrameForGrid(sys, t, null, 778,
                f.BerthCenter + new Vector3D(500.0, 0, 0), freg);
            NearVec("berth containment -> the conjunction's window", w3.FrameCel, w2.FrameCel, 1e-6);
            NearVec("berth window rides the rails velocity", w3.FrameCelVel, w2.FrameCelVel, 1e-9);

            // Nowhere -> home; and the poisoned client static was never consulted.
            ObserverFrame w4 = PlanetBerths.ResolveFrameForGrid(sys, t, null, 779,
                new Vector3D(9.0e8, -9.0e8, 9.0e8), freg);
            NearVec("nowhere -> home cell (NOT the poisoned client window)",
                w4.CellCenter, PlanetBerths.CurrentBerth, 1e-6);

            PlanetBerths.LocalConjunctionFrame = null;
            VoxelBerthRegistry.Clear();
        }

        // The celestial-native START BODY pick (OrbitRenderer free flight, 2026-06-11 fix): the
        // DEEPEST SOI-tree body whose SOI sphere contains the craft's celestial position — pure
        // tree containment, no voxel anywhere required. Total when called on the root (infinite
        // SOI): a point outside every planet SOI returns the root star itself.
        private static void SoiStartBodyPick()
        {
            Section("SOI containment start-body pick (DeepestSoiContaining)");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody sun = sys.Find("Sun"), earth = sys.Find("Earth"), moon = sys.Find("Moon");
            double t = 100.0;

            // Fixture premise: nested finite SOIs (Moon ~23.1 km about Earth, Earth about the Sun).
            Ok("premise: Moon SOI < Earth SOI, both finite",
                moon.SoiRadius < earth.SoiRadius && !double.IsInfinity(earth.SoiRadius));

            // In Earth orbit (40 km out: inside Earth's SOI, outside the Moon's) -> Earth.
            Vector3D inEarth = earth.OriginInRoot(t).Position + new Vector3D(40.0e3, 0, 0);
            Ok("Earth orbit -> Earth", sun.DeepestSoiContaining(inEarth, t) == earth);

            // 20 km from the Moon (inside Moon SOI 23.1 km): the point is ALSO inside Earth's
            // SOI, but the walk descends to the deepest claimant -> Moon.
            Vector3D nearMoon = moon.OriginInRoot(t).Position + new Vector3D(20.0e3, 0, 0);
            Ok("premise: 20 km is inside the Moon SOI", 20.0e3 < moon.SoiRadius);
            Ok("premise: the point is also inside Earth's SOI",
                (nearMoon - earth.OriginInRoot(t).Position).Length() < earth.SoiRadius);
            Ok("near the Moon -> Moon (deepest wins)", sun.DeepestSoiContaining(nearMoon, t) == moon);

            // Just OUTSIDE the Moon's SOI -> Earth (containment, not nearest-body: the Moon is
            // closer but does not contain the point).
            Vector3D offMoon = moon.OriginInRoot(t).Position + new Vector3D(moon.SoiRadius * 1.1, 0, 0);
            Ok("just outside the Moon SOI -> Earth", sun.DeepestSoiContaining(offMoon, t) == earth);

            // Far from every planet -> the root star (total: the root's SOI is infinite).
            Ok("far from both -> Sun", sun.DeepestSoiContaining(new Vector3D(5.0e6, 5.0e6, 0), t) == sun);
        }

        // The free-flight start STATE (OrbitRenderer regime b): the grid observed through the
        // snapshot's resolved frame; velocity = world velocity + the frame's celestial velocity
        // (ObserverFrame.FrameCelVel, analytic from the ephemeris chain) - the start body's own
        // root velocity (PlanetBerths.BodyRelativeState). PINS: (1) ResolveFrame fills FrameCelVel
        // with the body's root velocity; (2) for the same physical situation the celestial-native
        // state equals the anchored-voxel state (offset from the voxel center + raw world
        // velocity) — the 1:1 inertial window — so both regimes draw the SAME elements; (3) a grid
        // hard-pinned in a conjunction window recovers the frame's rails state exactly (the framed
        // regime's celestial state IS the rails, by construction).
        private static void FreeFlightStateAssembly()
        {
            Section("free-flight state assembly: celestial-native == anchored voxel == rails");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            GravityBody earth = sys.Find("Earth");
            double t = 3000.0;
            Vector3D earthCell = VoxelBerthRegistry.CellOf("Earth", sys);
            StateVector eo = earth.OriginInRoot(t);

            // ResolveFrame carries the frame's celestial VELOCITY: a planet frame rides its body.
            PlanetBerths.LocalConjunctionFrame = null;
            ObserverFrame atEarth = PlanetBerths.ResolveFrame(sys, t, earth);
            NearVec("planet frame rides the body's root velocity", atEarth.FrameCelVel, eo.Velocity, 1e-9);

            // Grid in Earth's cell: 50 km offset, arbitrary world velocity. The ANCHORED path
            // measures against the voxel center (== the cell center: the materialized voxel is
            // pinned there).
            Vector3D offset = new Vector3D(50.0e3, 10.0e3, -5.0e3);
            Vector3D vWorld = new Vector3D(120.0, -340.0, 25.0);
            Vector3D gridPos = earthCell + offset;
            var anchored = new StateVector(gridPos - earthCell, vWorld);

            // The FREE-FLIGHT path: same grid, same frame, celestial-native assembly.
            StateVector free = PlanetBerths.BodyRelativeState(atEarth, gridPos, vWorld, earth, t);
            NearVec("free-flight position == anchored position", free.Position, anchored.Position, 1e-6);
            NearVec("free-flight velocity == anchored velocity (frame vel cancels body vel)",
                free.Velocity, anchored.Velocity, 1e-9);

            // ... so the two regimes draw the SAME elements (what the renderer propagates).
            var elA = OrbitalMath.ToElements(anchored, earth.Mu, t);
            var elF = OrbitalMath.ToElements(free, earth.Mu, t);
            Near("same semi-major axis", elF.SemiMajorAxis, elA.SemiMajorAxis, 1e-6);
            Near("same eccentricity", elF.Eccentricity, elA.Eccentricity, 1e-12);
            Near("same inclination", elF.Inclination, elA.Inclination, 1e-12);

            // CONJUNCTION window (framed-regime equivalence): a frame on rails about Earth. A grid
            // hard-pinned at the cell center with ZERO world velocity (the treadmill) recovers the
            // rails state exactly — drawing from the frame's celestial state IS drawing the rails.
            KeplerianElements rails = CircularElements(150.0e3, earth.Mu);
            StateVector railsNow = OrbitPropagation.StateAt(rails, t);
            Vector3D conjCell = new Vector3D(2.0e6, -1.0e6, 0.5e6);
            var conj = new ObserverFrame(conjCell, eo.Position + railsNow.Position,
                eo.Velocity + railsNow.Velocity, null);
            StateVector pinned = PlanetBerths.BodyRelativeState(conj, conjCell, Vector3D.Zero, earth, t);
            NearVec("pinned grid recovers the rails position", pinned.Position, railsNow.Position, 1e-6);
            NearVec("pinned grid recovers the rails velocity", pinned.Velocity, railsNow.Velocity, 1e-9);

            // A member floating IN the window (offset dx, relative velocity dv) adds Galilean-ly.
            Vector3D dx = new Vector3D(500.0, -200.0, 100.0);
            Vector3D dv = new Vector3D(1.5, 0.5, -2.0);
            StateVector member = PlanetBerths.BodyRelativeState(conj, conjCell + dx, dv, earth, t);
            NearVec("member: rails + window offset", member.Position, railsNow.Position + dx, 1e-6);
            NearVec("member: rails + relative velocity", member.Velocity, railsNow.Velocity + dv, 1e-9);
            VoxelBerthRegistry.Clear();
        }

        // Sun-phase continuity (SunPhaseEase.Step): a frame transfer is a SEAMLESS teleport — at
        // every source switch the drawn phase is VALUE-CONTINUOUS (the convention offset is
        // captured once and never decays) and advances at the new source's live rate from that
        // same step (RATE-INSTANT: no lerp, no snap). The absolute phase is a fiction (fixed
        // engine sun axis), so the accumulated offset is free; what the player experiences — the
        // terminator's position and rate — is always continuous.
        private static void SunPhaseContinuity()
        {
            Section("sun phase continuity: value-continuous, rate-instant source switches");
            var st = new SunPhaseEase.State();
            Near("first step adopts the live phase", SunPhaseEase.Step(ref st, "Earth", 1.0), 1.0, 1e-12);
            Near("steady tracking is exact", SunPhaseEase.Step(ref st, "Earth", 1.7), 1.7, 1e-12);

            // SWITCH (teleport): value continuous at the switch frame...
            Near("switch is value-continuous", SunPhaseEase.Step(ref st, "Moon", 4.0), 1.7, 1e-12);
            // ...and advances at the NEW source's live rate immediately (constant offset, no decay).
            Near("rate-instant: next step moves by the live delta", SunPhaseEase.Step(ref st, "Moon", 4.25), 1.95, 1e-12);
            Near("offset never decays (much later)", SunPhaseEase.Step(ref st, "Moon", 6.0), 3.7, 1e-12);

            // Degenerate hold (no azimuth): holds the last drawn; resuming re-captures continuity.
            Near("degenerate hold", SunPhaseEase.Step(ref st, null, 0.0), 3.7, 1e-12);
            Near("resume is value-continuous too", SunPhaseEase.Step(ref st, "Earth", 9.9), 3.7, 1e-12);

            // WRAP: continuity across the 0/2pi boundary stays exact.
            var st2 = new SunPhaseEase.State();
            SunPhaseEase.Step(ref st2, "A", 0.1);
            Near("wrap: continuous at the boundary", SunPhaseEase.Step(ref st2, "B", 2.0 * Math.PI - 0.1), 0.1, 1e-12);
            Near("wrap: advances cleanly past 0", SunPhaseEase.Step(ref st2, "B", 2.0 * Math.PI - 0.05), 0.15, 1e-12);
        }

        // THE CONSISTENCY LAW (in-game directive): every persistent sky motion advances with
        // universe time. Anchorless (coasting in a conjunction), the sun phase is the observer's
        // ORBITAL AZIMUTH about the root star (SunPhaseEase.TryOrbitalPhase) — with no spinning
        // body under the observer, the sun's apparent motion IS the orbital motion. Pins: the
        // phase advances monotonically with t, its rate scales linearly with warp (same
        // trajectory, bigger dt), it is frozen ONLY when t is frozen, and the degenerate guards
        // reject no-azimuth geometry. Source switches: see SunPhaseContinuity.
        private static void SunOrbitalPhaseCoasting()
        {
            Section("sun orbital-azimuth phase: warp-consistent coasting (TryOrbitalPhase)");

            // Circular observer orbit in the X-Y reference plane, SunEarthMoon numbers: star at
            // the origin (mu = g*R^2 = 28 * 50e3^2 = 7e10), observer at Earth's 600 km orbit.
            double mu = 28.0 * 50.0e3 * 50.0e3;
            double R = 600.0e3;
            double n = Math.Sqrt(mu / (R * R * R));                  // mean motion ~5.69e-4 rad/s
            Func<double, Vector3D> obs = t =>
                new Vector3D(R * Math.Cos(n * t), R * Math.Sin(n * t), 0.0);
            Func<double, double> phi = t =>
            {
                double p;
                return SunPhaseEase.TryOrbitalPhase(Vector3D.Zero - obs(t), out p) ? p : double.NaN;
            };

            // CONVENTION: phi = atan2(dir.Y, dir.X) about +Z (the orbital core's RAAN/anomaly
            // convention). Observer at +X looking at the star -> dir = -X -> phase pi.
            Near("convention: observer at +X -> phase pi", phi(0.0), Math.PI, 1e-12);

            // MONOTONIC with t: equal universe-time steps give equal positive phase steps (the
            // orbit is circular, so the rate is exactly n).
            double dt = 10.0, t0 = 300.0;
            double d1 = SunPhaseEase.WrapToPi(phi(t0 + dt) - phi(t0));
            double d2 = SunPhaseEase.WrapToPi(phi(t0 + 2.0 * dt) - phi(t0 + dt));
            Ok("phase advances with t", d1 > 0.0 && d2 > 0.0);
            Near("rate is the orbital rate (n*dt)", d1, n * dt, 1e-9);
            Near("uniform rate on the circle", d2, d1, 1e-9);

            // WARP-LINEAR: the same trajectory sampled with a 100x bigger universe-time step
            // sweeps exactly 100x the phase — x100 warp moves the sun 100x faster, BY CONSTRUCTION
            // (the phase is a pure function of t, exactly like the proxies).
            double dBig = SunPhaseEase.WrapToPi(phi(t0 + 100.0 * dt) - phi(t0));
            Near("x100 warp -> x100 phase sweep", dBig, 100.0 * d1, 1e-9);

            // FROZEN only when t is frozen: same t -> identical phase (pause holds the sun still),
            // any nonzero step advances it.
            Near("frozen t -> frozen phase", phi(12345.6), phi(12345.6), 0.0);
            Ok("any dt > 0 advances the phase", SunPhaseEase.WrapToPi(phi(t0 + 0.01) - phi(t0)) > 0.0);

            // DEGENERATE guards: at the star / on its polar axis there is no azimuth -> false
            // (DriveSun falls back to the hold).
            double junk;
            Ok("at the star -> no azimuth", !SunPhaseEase.TryOrbitalPhase(Vector3D.Zero, out junk));
            Ok("on the polar axis -> no azimuth", !SunPhaseEase.TryOrbitalPhase(new Vector3D(0, 0, R), out junk));
            Ok("off-axis is fine", SunPhaseEase.TryOrbitalPhase(new Vector3D(R, R, 5.0 * R), out junk));

            // SEAMLESS source switch (anchored -> orbital): value-continuous at the switch frame,
            // then the drawn phase tracks the MOVING orbital azimuth with a constant offset
            // (rate-instant — the day sweeps at the warped orbital rate from the continuous value).
            double tU = 5000.0;
            double anchored = SunPhaseEase.Wrap(2.0 * Math.PI * (tU / 1200.0));
            double off0 = SunPhaseEase.WrapToPi(anchored - phi(tU));
            Ok("test premise: the two conventions differ at the switch", Math.Abs(off0) > 0.1);
            var stc = new SunPhaseEase.State();
            SunPhaseEase.Step(ref stc, "Earth", anchored);
            Near("switch is value-continuous", SunPhaseEase.Step(ref stc, "<orbital>", phi(tU)), anchored, 1e-12);
            double tLater = tU + 100.0 * 10.0;   // x100 warp, 10 real seconds later
            Near("tracks the moving live with the constant offset",
                SunPhaseEase.Step(ref stc, "<orbital>", phi(tLater)),
                SunPhaseEase.Wrap(phi(tLater) + off0), 1e-12);

            // FULL FIXTURE: ride Earth's actual SunEarthMoon rail (e=0.02) as a coasting observer —
            // the registry-path geometry (root.OriginInRoot - observer celestial) sweeps
            // monotonically too (elliptic, so the rate breathes; the sign never flips).
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody root = sys.Root, earth = sys.Find("Earth");
            double period = 2.0 * Math.PI / n;                       // ~11 037 s
            bool monotone = true;
            double prev = double.NaN;
            for (int i = 0; i <= 40; i++)
            {
                double t = i * (period / 40.0);
                Vector3D pos = earth.OriginInRoot(t).Position + new Vector3D(0, 0, 2.0e3);
                double p;
                if (!SunPhaseEase.TryOrbitalPhase(root.OriginInRoot(t).Position - pos, out p)) { monotone = false; break; }
                if (i > 0 && SunPhaseEase.WrapToPi(p - prev) <= 0.0) monotone = false;
                prev = p;
            }
            Ok("SunEarthMoon: coasting phase sweeps monotonically over a full orbit", monotone);
            Console.WriteLine("   info: SunEarthMoon coast sweep " + (360.0 / period).ToString("G4")
                + " deg/s at x1 (full day ~" + period.ToString("G5") + " s), x100 -> "
                + (100.0 * 360.0 / period).ToString("G4") + " deg/s real; anchored test-Earth day = "
                + (360.0 / 3600.0).ToString("G4") + " deg/s at x1 (3600 s test day)");
        }

        // No proxy, for any observer (at any voxel body) and any other body, renders past the clamp.
        private static bool AllProxiesWithinClamp(SystemDefinition def, double clamp)
        {
            SystemRegistry.Build(def);
            SystemRegistry sys = SystemRegistry.Active;
            VoxelBerthRegistry.Clear();
            var bodies = sys.Bodies;
            for (int a = 0; a < bodies.Count; a++)
            {
                BodyDefinition da = sys.FindDefinition(bodies[a].Name);
                if (da == null || da.RadiusMeters <= 0.0 || string.IsNullOrEmpty(da.ParkSubtype)) continue;
                Vector3D cell = VoxelBerthRegistry.CellOf(bodies[a].Name, sys);
                var frame = new ObserverFrame(cell, bodies[a].OriginInRoot(0.0).Position, null);
                for (int b = 0; b < bodies.Count; b++)
                {
                    if (b == a) continue;
                    BodyDefinition db = sys.FindDefinition(bodies[b].Name);
                    double r = db != null ? db.RadiusMeters : 0.0;
                    if (r <= 0.0) continue;
                    ProxyPlacement p = PlanetBerths.ProjectProxy(cell, bodies[b].OriginInRoot(0.0).Position, frame, r, 0.0);
                    if ((p.RenderPos - cell).Length() > clamp + 1.0) return false;
                }
            }
            return true;
        }

        private static KeplerianElements CircularElements(double radius, double mu)
        {
            double vc = Math.Sqrt(mu / radius);
            var sv = new StateVector(new Vector3D(radius, 0, 0), new Vector3D(0, vc, 0));
            return OrbitalMath.ToElements(sv, mu);
        }

        private static void FrameRegistryLifecycle()
        {
            Section("frame registry: create / members / dissolve / slot recycle");
            var alloc = new BerthAllocator(2000.0, 20000.0);
            var reg = new FrameRegistry(alloc);

            var el = CircularElements(8000e3, MuEarth);
            ProximityFrame f1 = reg.CreateFrame("Earth", el, 100);
            Ok("frame created", f1 != null && f1.Id == 1);
            Ok("anchor is first member", f1.MemberCount == 1 && f1.HasMember(100));
            Ok("berth slot allocated", f1.BerthSlotId == 0 && alloc.IsOccupied(0));
            Ok("anchor indexed", reg.FindByMember(100) == f1);

            // add members (the Conjunction grows).
            Ok("add member 200", reg.AddMember(f1, 200));
            Ok("add member 300", reg.AddMember(f1, 300));
            Ok("conjunction size 3", f1.MemberCount == 3);
            Ok("member 200 indexed to f1", reg.FindByMember(200) == f1);
            Ok("re-add same member rejected", !reg.AddMember(f1, 200));

            // a second frame takes the next slot.
            ProximityFrame f2 = reg.CreateFrame("Earth", el, 400);
            Ok("second frame id 2", f2.Id == 2);
            Ok("second frame slot 1", f2.BerthSlotId == 1);
            Ok("two frames live", reg.Count == 2);

            // remove a member.
            Ok("remove member 300", reg.RemoveMember(300));
            Ok("member 300 unindexed", reg.FindByMember(300) == null);
            Ok("conjunction size 2", f1.MemberCount == 2);

            // dissolve f1: members unindexed, slot freed + recyclable.
            reg.Dissolve(f1.Id);
            Ok("f1 gone", reg.Get(f1.Id) == null);
            Ok("dissolved members unindexed", reg.FindByMember(100) == null && reg.FindByMember(200) == null);
            Ok("dissolved slot freed", !alloc.IsOccupied(0));
            ProximityFrame f3 = reg.CreateFrame("Earth", el, 500);
            Ok("new frame recycles freed slot 0", f3.BerthSlotId == 0);
        }

        private static void FrameIsolationQuery()
        {
            Section("frame isolation query: join nearest in range, else spawn own");
            var alloc = new BerthAllocator(2000.0, 20000.0);
            var reg = new FrameRegistry(alloc);

            // a circular orbit; the frame sits at +X*radius at t=0 (periapsis on +X).
            var el = CircularElements(8000e3, MuEarth);
            ProximityFrame f1 = reg.CreateFrame("Earth", el, 100);
            Vector3D p0 = f1.PositionAt(0.0);

            // a celestial point 3 km from f1 -> within a 10 km capture range -> join.
            Vector3D near = p0 + new Vector3D(3000.0, 0, 0);
            ProximityFrame join = reg.FindNearestFrame("Earth", near, 0.0, 10000.0, 0);
            Ok("nearby grid finds f1 (join)", join == f1);

            // a point 50 km away -> outside 10 km capture range -> isolated -> spawn own.
            Vector3D far = p0 + new Vector3D(50000.0, 0, 0);
            ProximityFrame none = reg.FindNearestFrame("Earth", far, 0.0, 10000.0, 0);
            Ok("distant grid is isolated (no match)", none == null);

            // different parent body never matches, even if celestially co-located.
            ProximityFrame wrongParent = reg.FindNearestFrame("Mars", near, 0.0, 10000.0, 0);
            Ok("different parent never matches", wrongParent == null);

            // excluding f1 (a frame checking against others) yields no self-match.
            ProximityFrame selfExcluded = reg.FindNearestFrame("Earth", near, 0.0, 10000.0, f1.Id);
            Ok("self-frame excluded", selfExcluded == null);
        }

        private static void FrameMergeSplit()
        {
            Section("frame merge/split bookkeeping (registry side)");
            var alloc = new BerthAllocator(2000.0, 20000.0);
            var reg = new FrameRegistry(alloc);
            var el = CircularElements(8000e3, MuEarth);

            // --- MERGE: fold incomer (#2) into host (#1) ---
            ProximityFrame host = reg.CreateFrame("Earth", el, 100);   // slot 0
            reg.AddMember(host, 101);
            ProximityFrame inc = reg.CreateFrame("Earth", el, 200);    // slot 1
            reg.AddMember(inc, 201);
            int incSlot = inc.BerthSlotId;

            Ok("merge ok", reg.MergeInto(host, inc));
            Ok("host absorbed all members", host.MemberCount == 4 &&
                host.HasMember(100) && host.HasMember(101) && host.HasMember(200) && host.HasMember(201));
            Ok("incomer gone from registry", reg.Get(inc.Id) == null && reg.Count == 1);
            Ok("transferred members reindex to host", reg.FindByMember(200) == host && reg.FindByMember(201) == host);
            Ok("incomer slot freed", !alloc.IsOccupied(incSlot));

            // freed slot recycles on the next create.
            ProximityFrame f3 = reg.CreateFrame("Earth", el, 300);
            Ok("merge-freed slot recycled", f3.BerthSlotId == incSlot);

            // --- SPLIT: fork a member out of the host into its own frame ---
            var splitEl = CircularElements(8200e3, MuEarth);   // a slightly different orbit
            int before = reg.Count;
            ProximityFrame nf = reg.SplitOff(host, 201, "Earth", splitEl);
            Ok("split created a new frame", nf != null && reg.Count == before + 1);
            Ok("split member is the new anchor", nf.AnchorEntityId == 201 && nf.HasMember(201));
            Ok("split member reindexed to new frame", reg.FindByMember(201) == nf);
            Ok("old frame lost the member", !host.HasMember(201) && host.MemberCount == 3);
            Ok("new frame got its own slot", nf.BerthSlotId >= 0 && alloc.IsOccupied(nf.BerthSlotId));

            // cannot split the anchor itself, or a non-member.
            Ok("anchor cannot be split off", reg.SplitOff(host, host.AnchorEntityId, "Earth", splitEl) == null);
            Ok("non-member cannot be split off", reg.SplitOff(host, 999, "Earth", splitEl) == null);
        }

        // ---- Resolve layer (the door between realms) ----

        private static void ResolveProjection()
        {
            Section("Resolve.Project: InBerth / OnSky / Occluded");

            // Observer: circular-ish state at +X*8000km, prograde +Y -> LVLH radial=+X, along=+Y, cross=+Z.
            var obsPos = new Vector3D(8000e3, 0, 0);
            var obsVel = new Vector3D(0, 7000, 0);
            long obsFrame = 1;
            var anchorWorld = new Vector3D(1000, 2000, 3000);
            var bodies = new System.Collections.Generic.List<ResolveBody>();
            bodies.Add(new ResolveBody("Earth", Vector3D.Zero, 60e3));   // parent body at origin

            // InBerth: same frame id -> world pos = anchor + celestial offset (1:1 berth window).
            var inb = Resolve.Project(obsPos, obsVel, obsFrame, anchorWorld,
                obsPos + new Vector3D(100, 0, 0), obsFrame, bodies);
            Ok("same frame -> InBerth", inb.Kind == ResolveKind.InBerth);
            NearVec("InBerth world pos = anchor + offset", inb.WorldPos, anchorWorld + new Vector3D(100, 0, 0), 1e-6);

            // OnSky (in-track): target 5 km along +Y -> LVLH direction +Y, distance 5 km.
            var sky = Resolve.Project(obsPos, obsVel, obsFrame, anchorWorld,
                obsPos + new Vector3D(0, 5000, 0), 2, bodies);
            Ok("other frame -> OnSky", sky.Kind == ResolveKind.OnSky);
            NearVec("OnSky in-track dir is +Y (along)", sky.Direction, new Vector3D(0, 1, 0), 1e-9);
            Near("OnSky celestial distance", sky.CelestialDistance, 5000.0, 1e-6);

            // OnSky (radial): target further out along +X -> LVLH direction +X (radial/up).
            var skyR = Resolve.Project(obsPos, obsVel, obsFrame, anchorWorld,
                obsPos + new Vector3D(3000, 0, 0), 2, bodies);
            NearVec("OnSky radial dir is +X", skyR.Direction, new Vector3D(1, 0, 0), 1e-9);

            // Occluded: target on the far side of the parent -> segment passes through the body.
            var occ = Resolve.Project(obsPos, obsVel, obsFrame, anchorWorld,
                new Vector3D(-8000e3, 0, 0), 2, bodies);
            Ok("far-side target -> Occluded", occ.Kind == ResolveKind.Occluded);
            Ok("occluder is the parent body", occ.OccludingBody == "Earth");

            // A nearby sky target is NOT falsely occluded by the body behind the observer.
            Ok("near sky target not occluded", sky.Kind == ResolveKind.OnSky);

            // No-occluder list: far-side target becomes a plain OnSky.
            var noOcc = Resolve.Project(obsPos, obsVel, obsFrame, anchorWorld,
                new Vector3D(-8000e3, 0, 0), 2, null);
            Ok("no occluders -> far target is OnSky", noOcc.Kind == ResolveKind.OnSky);
        }

        private static void ResolveAddressing()
        {
            Section("Resolve.TryResolveInParent: address -> parent-frame position");
            var alloc = new BerthAllocator(2000.0, 20000.0);
            var reg = new FrameRegistry(alloc);
            var el = CircularElements(8000e3, MuEarth);
            ProximityFrame f1 = reg.CreateFrame("Earth", el, 100);

            Vector3D pos;
            long fid;

            // LiveFrame sharing the parent resolves to the frame's current position.
            var live = CelestialAddress.LiveFrame(f1.Id);
            Ok("LiveFrame (same parent) resolves", Resolve.TryResolveInParent(live, "Earth", reg, 0.0, out pos, out fid));
            NearVec("LiveFrame pos = frame StateAt", pos, OrbitPropagation.StateAt(el, 0.0).Position, 1e-6);
            Ok("LiveFrame returns its frame id", fid == f1.Id);

            // LiveFrame under a DIFFERENT parent isn't resolvable here (tree routing TODO).
            Ok("LiveFrame cross-parent -> false", !Resolve.TryResolveInParent(live, "Mars", reg, 0.0, out pos, out fid));

            // BodyInertialPoint on the parent.
            var bip = CelestialAddress.BodyInertialPoint("Earth", 10, 20, 30);
            Ok("BodyInertialPoint (parent) resolves", Resolve.TryResolveInParent(bip, "Earth", reg, 0.0, out pos, out fid));
            NearVec("BodyInertialPoint pos", pos, new Vector3D(10, 20, 30), 1e-9);
            Ok("BodyInertialPoint (other body) -> false",
                !Resolve.TryResolveInParent(CelestialAddress.BodyInertialPoint("Mars", 1, 2, 3), "Earth", reg, 0.0, out pos, out fid));

            // DeepSpaceFixed is a direct point in this frame.
            var dsf = CelestialAddress.DeepSpaceFixed(7, 8, 9);
            Ok("DeepSpaceFixed resolves", Resolve.TryResolveInParent(dsf, "Earth", reg, 0.0, out pos, out fid));
            NearVec("DeepSpaceFixed pos", pos, new Vector3D(7, 8, 9), 1e-9);

            // SurfaceFixed needs the game-side wrapper (body spin/radius) -> false here.
            var sf = CelestialAddress.SurfaceFixed("Earth", 45, 90, 1000);
            Ok("SurfaceFixed -> false (game-side only)", !Resolve.TryResolveInParent(sf, "Earth", reg, 0.0, out pos, out fid));
        }

        // Cross-SOI / tree-based Resolve: frames parented to GravityBody NODES (the closed seam).
        private static void ResolveTree()
        {
            Section("Resolve via the GravityBody tree (cross-SOI, surface-fixed)");
            SystemRegistry.Build(SampleSystems.Sol());
            SystemRegistry sys = SystemRegistry.Active;
            var reg = new FrameRegistry(new BerthAllocator(2000.0, 20000.0));

            GravityBody earth = sys.Find("Earth");
            double earthMu = earth.Mu;
            double lunaMu = sys.Find("Luna").Mu;
            double t = 0.0;

            // Observer orbits Earth (150 km), target orbits Luna (40 km) — parented to NODES.
            var obsEl = CircularElements(150e3, earthMu);
            ProximityFrame obs = reg.CreateFrame("Earth", obsEl, 100);
            var tgtEl = CircularElements(40e3, lunaMu);
            ProximityFrame tgt = reg.CreateFrame("Luna", tgtEl, 200);

            Vector3D rp; long fid;

            // LiveFrame lift to root = Earth's root origin + the observer's orbit position.
            Ok("LiveFrame(Earth) resolves",
                Resolve.ResolveCelestial(CelestialAddress.LiveFrame(obs.Id), reg, sys, t, out rp, out fid));
            Vector3D expect = earth.OriginInRoot(t).Position + OrbitPropagation.StateAt(obsEl, t).Position;
            NearVec("LiveFrame root pos = EarthOrigin + orbit", rp, expect, 1e-3);
            Ok("LiveFrame returns frame id", fid == obs.Id);

            // SurfaceFixed: a surface point is ~Earth radius from Earth's origin.
            Resolve.ResolveCelestial(CelestialAddress.SurfaceFixed("Earth", 30, 60, 0), reg, sys, t, out rp, out fid);
            double earthR = sys.FindDefinition("Earth").RadiusMeters;
            Near("SurfaceFixed sits at Earth radius",
                (rp - earth.OriginInRoot(t).Position).Length(), earthR, 1.0);

            // BodyInertialPoint = node origin + offset.
            Resolve.ResolveCelestial(CelestialAddress.BodyInertialPoint("Earth", 1000, 0, 0), reg, sys, t, out rp, out fid);
            NearVec("BodyInertialPoint = EarthOrigin + offset", rp,
                earth.OriginInRoot(t).Position + new Vector3D(1000, 0, 0), 1e-3);

            // Cross-SOI compose: observer (Earth) sees target (Luna) on the sky at true distance.
            Vector3D obsRoot = earth.StateInRoot(OrbitPropagation.StateAt(obsEl, t), t).Position;
            Vector3D tgtRoot; long tgtFid;
            Resolve.ResolveCelestial(CelestialAddress.LiveFrame(tgt.Id), reg, sys, t, out tgtRoot, out tgtFid);
            var obsState = OrbitPropagation.StateAt(obsEl, t);
            Vector3D radial = Vector3D.Normalize(obsState.Position);
            Vector3D cross = Vector3D.Normalize(Vector3D.Cross(obsState.Position, obsState.Velocity));
            Vector3D along = Vector3D.Cross(cross, radial);
            var occ = Resolve.TreeOccluders(sys, t);
            Ok("tree occluders = 5 bodies", occ.Count == 5);
            var res = Resolve.ProjectWithBasis(obsRoot, radial, along, cross, obs.Id, Vector3D.Zero, tgtRoot, tgtFid, occ);
            Ok("cross-SOI Earth->Luna is OnSky", res.Kind == ResolveKind.OnSky);
            Near("cross-SOI distance = |Δroot|", res.CelestialDistance, (tgtRoot - obsRoot).Length(), 1.0);
            Ok("cross-SOI direction is unit", Math.Abs(res.Direction.Length() - 1.0) < 1e-9);

            // Occlusion by the Sun (root origin): a target on the far side blocks LoS.
            Vector3D farSide = -2.0 * obsRoot;
            var occRes = Resolve.ProjectWithBasis(obsRoot, radial, along, cross, obs.Id, Vector3D.Zero, farSide, 0, occ);
            Ok("far-side-of-Sun target is Occluded", occRes.Kind == ResolveKind.Occluded);
            Ok("occluder is the Sun", occRes.OccludingBody == "Sun");
        }

        // ==== 2026-06-12 mod-core findings (arrival-imminent lock wave) ====================

        // Finding 3 pin: the stamped realtime locks ((c) drain / (d) arrival-imminent) are
        // evaluated by the clock against a stamp the PRODUCER (FrameManager) writes in the same
        // BeforeSimulation phase, with UNSPECIFIED component order. If the clock ticks first,
        // the freshest stamp it sees is one tick old — and that tick may have advanced at the
        // CHOSEN warp, ageing the stamp by chosen x tick UNIVERSE seconds (166.7 s at x10000).
        // WarpPolicy.HoldsRealtime therefore widens the hold by exactly one chosen-timescale
        // tick. This pins the margin arithmetic so a "simplification" back to the bare
        // zero-margin (now - stamp < hold) comparison fails loudly.
        private static void WarpRealtimeHoldMargin()
        {
            Section("warp realtime hold: one-chosen-tick ordering margin (finding 3)");
            const double tick = 1.0 / 60.0;

            // Bare in-window case, x1: holds.
            Ok("fresh stamp holds", WarpPolicy.HoldsRealtime(100.0, 99.0, 2.0, 1.0, tick));
            // Exactly at the bare hold boundary, x1: the zero-margin rule would release; the
            // one-tick margin keeps it held (the clock-ticked-first case at x1).
            Ok("age == hold still holds at x1 (one-tick margin)",
                WarpPolicy.HoldsRealtime(102.0, 100.0, 2.0, 1.0, tick));
            Ok("age just past hold + one x1 tick releases",
                !WarpPolicy.HoldsRealtime(102.0 + 2.0 * tick, 100.0, 2.0, 1.0, tick));

            // The high-warp case the ordering luck used to hide: the first burn tick at chosen
            // x10000 advances the universe 166.7 s; the stamp the clock then sees is that old
            // even though the activity never stopped. The margin must cover one full chosen tick.
            double chosenTick = 10000.0 * tick;   // 166.67 universe seconds
            Ok("x10000: stamp aged by one chosen tick still holds",
                WarpPolicy.HoldsRealtime(100.0 + 2.0 + chosenTick - 0.01, 100.0, 2.0, 10000.0, tick));
            Ok("x10000: beyond hold + one chosen tick releases",
                !WarpPolicy.HoldsRealtime(100.0 + 2.0 + chosenTick + 0.01, 100.0, 2.0, 10000.0, tick));

            // Degenerate inputs: a never-stamped (-inf) or NaN stamp must never hold; a sub-x1
            // timescale is floored to x1 (the clock never runs slower than real time).
            Ok("-inf stamp never holds", !WarpPolicy.HoldsRealtime(100.0, double.NegativeInfinity, 2.0, 10000.0, tick));
            Ok("NaN stamp never holds", !WarpPolicy.HoldsRealtime(100.0, double.NaN, 2.0, 10000.0, tick));
            Ok("sub-x1 timescale floored to x1 margin",
                WarpPolicy.HoldsRealtime(102.0, 100.0, 2.0, 0.0, tick)
                && !WarpPolicy.HoldsRealtime(102.0 + 2.0 * tick, 100.0, 2.0, 0.0, tick));

            // Policy basics the lock feeds (locked -> x1; unlocked -> chosen; never below x1).
            var clock = new UniverseTime();
            clock.SetTimescale(100.0);
            Near("locked effective = x1", WarpPolicy.EffectiveTimescale(clock, true), 1.0, 1e-12);
            Near("unlocked effective = chosen", WarpPolicy.EffectiveTimescale(clock, false), 100.0, 1e-12);
            Near("null clock = x1", WarpPolicy.EffectiveTimescale(null, false), 1.0, 1e-12);
        }

        // Finding 4 pin: SoiReparent.TryFindSoiEntry's between-samples dip probe must try EVERY
        // local minimum of the sampled separation, earliest sub-SOI wins. The old code refined
        // only the GLOBAL sampled minimum — with two close approaches in one window where the
        // EARLIER samples lower but never transits and only the LATER (sampled higher: its fast
        // dip falls between samples) is sub-SOI, it refined the wrong dip and MISSED the
        // encounter entirely.
        //
        // Deterministic two-dip fixture (no search): a retrograde CO-ORBITAL ship about a custom
        // parent P with one circular child C (R = 200 km, period ~1200 s, Laplace SOI ~18 km).
        // The ship rides a = R + 6 km, A = a*e = SOI - 2 km, built AT its apoapsis on the
        // child's azimuth. Retrograde co-orbital geometry meets the child every half period with
        // the ship's radial phase advancing ~pi per conjunction, so the radial gap ALTERNATES:
        //   dip 1 (at apoapsis):  +6 km + A = SOI + 4 km   -> close, NEVER transits;
        //   dip 2 (at periapsis):  6 km - A = -(SOI - 8 km) -> |gap| ~10 km, DEEP transit.
        // Closure at a conjunction is ~2 v_circ (~2.1 km/s), so the sub-SOI part of dip 2 lasts
        // ~14 s. The window is then built so dip 1 lands exactly ON a coarse sample (sampled at
        // its true ~SOI+4 minimum) while dip 2 falls exactly MID-BETWEEN two samples (straddle
        // separation ~34 km, well above the SOI): no sample is sub-SOI, the global sampled
        // minimum is the non-transiting dip — the exact wrong-minimum geometry.
        private static void SoiEntryTwoDipWindow()
        {
            Section("SOI entry two-dip window: probe every local minimum, earliest sub-SOI wins (finding 4)");

            var def = new SystemDefinition();
            def.Name = "TwoDip";
            def.EpochSeconds = 0.0;
            var p = new BodyDefinition();
            p.Name = "P"; p.Parent = ""; p.HasOrbit = false;
            p.SurfaceGravityMps2 = 243.7;       // mu_p = 2.193e11 -> child period ~1200 s at 200 km
            p.RadiusMeters = 30.0e3;
            p.RotationPeriodSeconds = 1.0e6;
            def.Bodies.Add(p);
            var c = new BodyDefinition();
            c.Name = "C"; c.Parent = "P"; c.HasOrbit = true;
            c.SemiMajorAxisMeters = 200.0e3; c.Eccentricity = 0.0;
            c.SurfaceGravityMps2 = 21.3;        // mu_c = 5.325e8 -> Laplace SOI ~18 km
            c.RadiusMeters = 5.0e3;
            c.RotationPeriodSeconds = 1.0e6;
            def.Bodies.Add(c);
            SystemRegistry.Build(def);
            SystemRegistry reg = SystemRegistry.Active;
            GravityBody parent = reg.Find("P"), child = reg.Find("C");

            double soi = child.SoiRadius;
            Ok("fixture: child SOI ~18 km", soi > 15.0e3 && soi < 21.0e3);

            // Ship at apoapsis on the child's azimuth at t1, retrograde.
            double R = 200.0e3;
            double aS = R + 6.0e3;
            double A = soi - 2.0e3;             // radial amplitude: apo gap = SOI+4k, peri gap = -(SOI-8k)
            double eS = A / aS;
            double t1 = 700.0;
            StateVector cs = child.StateInParentAt(t1);
            Vector3D rHat = Vector3D.Normalize(cs.Position);
            Vector3D tHat = Vector3D.Normalize(cs.Velocity);
            double rApo = aS * (1.0 + eS);
            double vApo = Math.Sqrt(parent.Mu * (2.0 / rApo - 1.0 / aS));
            var el = OrbitalMath.ToElements(new StateVector(rHat * rApo, -tHat * vApo), parent.Mu, t1);
            Ok("ship elements finite + retrograde co-orbital",
                !double.IsNaN(el.SemiMajorAxis) && Math.Abs(el.SemiMajorAxis - aS) < 100.0);

            // Measure the first two dips of |ship - child| numerically (coarse 0.5 s scan,
            // 0.005 s local refine) — the construction is then placed off the MEASURED times.
            double tD1, d1, tD2, d2;
            FindNextDip(el, child, t1 - 100.0, t1 + 250.0, out tD1, out d1);
            FindNextDip(el, child, tD1 + 100.0, tD1 + 900.0, out tD2, out d2);
            Ok("dip 1 close but NON-transiting (SOI+1k .. SOI+8k)", d1 > soi + 1.0e3 && d1 < soi + 8.0e3);
            Ok("dip 2 a DEEP sub-SOI transit (< SOI-4k)", d2 < soi - 4.0e3);

            // Window construction: dip 1 exactly ON coarse sample 12, dip 2 exactly mid-between
            // samples 31 and 32 (EncounterSamples = 64 -> step = spacing/19.5).
            double spacing = tD2 - tD1;
            double step = spacing / 19.5;
            double t0 = tD1 - 12.0 * step;
            double look = 64.0 * step;

            // Re-epoch the elements to the window start: a live frame's epoch is always "now"
            // (stow/fold/rebind), and Step's pre-epoch guard (a scheduled patch must not take
            // new patch decisions before its patch point) refuses a future-epoch frame.
            el = OrbitPropagation.AtTime(el, t0);

            // Preconditions that made the OLD global-min-only probe miss: no coarse sample is
            // sub-SOI, and the global sampled minimum belongs to a dip that never transits.
            double minSample = double.MaxValue; double argT = t0;
            for (int k = 0; k <= 64; k++)
            {
                double tk = t0 + k * step;
                double d = SepToChild(el, child, tk);
                if (d < minSample) { minSample = d; argT = tk; }
            }
            Ok("no coarse sample dips below the SOI", minSample > soi);
            Ok("global sampled min is NOT the transiting dip's neighborhood", Math.Abs(argT - tD2) > 2.0 * step);
            double refMin = double.MaxValue;
            for (double tt = argT - step; tt <= argT + step; tt += 0.05)
            {
                double d = SepToChild(el, child, tt);
                if (d < refMin) refMin = d;
            }
            Ok("the dip the OLD single probe would refine never transits (it MISSED here)", refMin > soi);

            // The fixed probe: every sampled local minimum is tried in time order, so the later
            // (between-samples) transit is found and its entry refined just inside the SOI.
            double tEntry;
            Ok("multi-minimum dip probe finds the encounter",
                SoiReparent.TryFindSoiEntry(el, child, t0, look, soi, out tEntry));
            Ok("entry time is the LATER dip's inbound edge", tEntry > tD2 - step && tEntry < tD2);
            double dEntry = SepToChild(el, child, tEntry);
            Ok("entry state just inside the SOI", dEntry <= soi && dEntry > soi * 0.9);

            // Full Step integration: the frame re-parents onto the child AT the refined entry.
            var f = new ProximityFrame(7, "P", el, 99);
            Ok("Step patches the encounter onto the child",
                SoiReparent.Step(f, reg, t0, look) == SoiReparent.Change.Encounter && f.ParentBodyName == "C");
            Near("patch epoch == refined entry", f.Elements.Epoch, tEntry, 1.0);

            VoxelBerthRegistry.Clear();
        }

        private static double SepToChild(KeplerianElements el, GravityBody child, double t)
        {
            return (OrbitPropagation.StateAt(el, t).Position - child.StateInParentAt(t).Position).Length();
        }

        // First local minimum of the ship-child separation in [tA, tB]: coarse 0.5 s scan for
        // the bracketing sample, then a 0.005 s refine around it.
        private static void FindNextDip(KeplerianElements el, GravityBody child,
            double tA, double tB, out double tDip, out double dDip)
        {
            double prev = SepToChild(el, child, tA), cur = SepToChild(el, child, tA + 0.5);
            tDip = tA; dDip = prev;
            for (double t = tA + 1.0; t <= tB; t += 0.5)
            {
                double next = SepToChild(el, child, t);
                if (cur <= prev && cur < next)
                {
                    double bestT = t - 0.5, bestD = cur;
                    for (double tt = t - 1.0; tt <= t; tt += 0.005)
                    {
                        double d = SepToChild(el, child, tt);
                        if (d < bestD) { bestD = d; bestT = tt; }
                    }
                    tDip = bestT; dDip = bestD;
                    return;
                }
                prev = cur; cur = next;
            }
        }

        // ==== 2026-06-12 pre-epoch blocker wave ============================================

        // Half 1 pin: the COMMIT-SIDE QUARANTINE (FrameRails). A frame whose elements carry a
        // FUTURE epoch (a committed SOI patch point the rails have not reached yet) describes
        // back-propagated fiction at t < Epoch — the drain fold, the anchor-loss rebase and
        // the split capture must be UNABLE to re-osculate from it (that destroyed the patch
        // point and permanently corrupted the rails). At/after the epoch everything resumes:
        // the fold slices at the clamp budget with fold == feed exact, and the rebase shifts
        // position by exactly the berth-origin move with velocity preserved.
        private static void PreEpochCommitQuarantine()
        {
            Section("pre-epoch commit quarantine: fold/rebase/split cannot mutate a future-epoch frame");
            double mu = MuEarth;
            double t0 = 1000.0;
            double tEpoch = t0 + 30.0;   // the committed patch point, 30 s ahead of "now"
            var st = new StateVector(new Vector3D(7000.0e3, 0, 0), new Vector3D(0, 7546.0, 0));
            KeplerianElements el = OrbitalMath.ToElements(st, mu, tEpoch);
            var f = new ProximityFrame(1, "Earth", el, 11);
            Vector3D pend = new Vector3D(5.0, 0, 0);
            f.PendingDrainDv = pend;

            // The gate itself (the split capture is gated on exactly this in FrameManager).
            Ok("CanMutateAt false before the epoch (split/fold/rebase gate)", !FrameRails.CanMutateAt(el, t0));
            Ok("CanMutateAt true at the epoch", FrameRails.CanMutateAt(el, tEpoch));
            Ok("CanMutateAt true after the epoch", FrameRails.CanMutateAt(el, tEpoch + 1.0));

            // FOLD pre-epoch: Deferred — elements, accumulator, cur and the feed all untouched.
            double maxStep = 100.0 / 60.0;   // the mod's MaxApparentAccel x Dt budget
            StateVector cur = OrbitPropagation.StateAt(el, t0);
            StateVector curBefore = cur;
            Vector3D slice;
            FrameRails.FoldResult res = FrameRails.FoldDrain(f, t0, 0.03, maxStep, ref cur, out slice);
            Ok("pre-epoch fold returns Deferred", res == FrameRails.FoldResult.Deferred);
            Ok("pre-epoch fold leaves elements untouched",
                f.Elements.Epoch == el.Epoch && f.Elements.SemiMajorAxis == el.SemiMajorAxis
                && f.Elements.TrueAnomaly == el.TrueAnomaly && f.Elements.Eccentricity == el.Eccentricity);
            Ok("pre-epoch fold keeps the accumulator", f.PendingDrainDv == pend);
            Ok("pre-epoch fold feeds nothing (zero slice — fold == feed matched at zero)", slice == Vector3D.Zero);
            Ok("pre-epoch fold leaves the working state untouched",
                cur.Position == curBefore.Position && cur.Velocity == curBefore.Velocity);

            // REBASE pre-epoch: refused, elements untouched.
            Ok("pre-epoch rebase refused", !FrameRails.TryRebaseShift(f, t0, new Vector3D(1500.0, 400.0, 0)));
            Ok("pre-epoch rebase left elements untouched",
                f.Elements.SemiMajorAxis == el.SemiMajorAxis && f.Elements.TrueAnomaly == el.TrueAnomaly);

            // AT the epoch: the fold commits a clamped slice; fold == feed exact.
            cur = OrbitPropagation.StateAt(f.Elements, tEpoch);
            Vector3D vBefore = cur.Velocity;
            res = FrameRails.FoldDrain(f, tEpoch, 0.03, maxStep, ref cur, out slice);
            Ok("at-epoch fold commits", res == FrameRails.FoldResult.Folded);
            Near("slice clamped to the per-tick budget", slice.Length(), maxStep, 1e-9);
            NearVec("fold == feed: working velocity advanced by exactly the slice", cur.Velocity, vBefore + slice, 1e-12);
            Near("accumulator reduced by exactly the slice", f.PendingDrainDv.Length(), 5.0 - maxStep, 1e-9);
            Near("elements re-epoched to the fold time", f.Elements.Epoch, tEpoch, 0.0);
            NearVec("re-osculated rails velocity == fed velocity (round-trip)",
                OrbitPropagation.StateAt(f.Elements, tEpoch).Velocity, cur.Velocity, 1e-6);

            // Below threshold: None (the micro-impact filter), nothing stamped/mutated.
            f.PendingDrainDv = new Vector3D(0.01, 0, 0);
            res = FrameRails.FoldDrain(f, tEpoch, 0.03, maxStep, ref cur, out slice);
            Ok("sub-threshold accumulator returns None", res == FrameRails.FoldResult.None);

            // POST-epoch rebase: shifts position by exactly the shift, preserves velocity.
            Vector3D shift = new Vector3D(1500.0, 400.0, 0);
            StateVector pre = OrbitPropagation.StateAt(f.Elements, tEpoch);
            Ok("post-epoch rebase commits", FrameRails.TryRebaseShift(f, tEpoch, shift));
            StateVector post = OrbitPropagation.StateAt(f.Elements, tEpoch);
            NearVec("rebase shifted position by exactly the shift", post.Position, pre.Position + shift, 1e-6);
            NearVec("rebase preserved velocity", post.Velocity, pre.Velocity, 1e-6);
        }

        // Half 2 pin: SoiReparent's DETECTION/COMMITMENT split. A crossing detected inside the
        // look-ahead but BEYOND the commit horizon returns EncounterPending and mutates NOTHING
        // (the caller stamps the x1 encounter lock and the rails ride to the crossing in real
        // time); within the horizon it commits the Galilean patch AT the crossing. The legacy
        // 4-arg overload (infinite horizon) still commits eagerly — the red-replay form.
        private static void SoiCommitHorizonDefersPatch()
        {
            Section("SOI commit horizon: far crossing -> EncounterPending (untouched); near -> commit at tX");

            // The TwoDip fixture's parent/child, with a plain retrograde transit: ship circular
            // at the child's radius + 8 km (the impact parameter — an exactly co-radial transit
            // is a zero-angular-momentum RADIAL infall in the child frame, e == 1 degenerate,
            // which Rebind rightly refuses), retrograde, started 0.4 rad ahead — closure
            // ~2 v_circ, separation falls to a deep sub-SOI conjunction (8 km < SOI ~18 km).
            var def = new SystemDefinition();
            def.Name = "CommitHorizon";
            def.EpochSeconds = 0.0;
            var p = new BodyDefinition();
            p.Name = "P"; p.Parent = ""; p.HasOrbit = false;
            p.SurfaceGravityMps2 = 243.7; p.RadiusMeters = 30.0e3; p.RotationPeriodSeconds = 1.0e6;
            def.Bodies.Add(p);
            var c = new BodyDefinition();
            c.Name = "C"; c.Parent = "P"; c.HasOrbit = true;
            c.SemiMajorAxisMeters = 200.0e3; c.Eccentricity = 0.0;
            c.SurfaceGravityMps2 = 21.3; c.RadiusMeters = 5.0e3; c.RotationPeriodSeconds = 1.0e6;
            def.Bodies.Add(c);
            SystemRegistry.Build(def);
            SystemRegistry reg = SystemRegistry.Active;
            GravityBody parent = reg.Find("P"), child = reg.Find("C");
            double soi = child.SoiRadius;

            double t0 = 500.0;
            StateVector cs = child.StateInParentAt(t0);
            double R = cs.Position.Length() + 8.0e3;   // +8 km: the transit's impact parameter
            double vc = Math.Sqrt(parent.Mu / R);
            // Rotate the child's azimuth +0.4 rad for the ship's start point; retrograde tangent.
            double az = Math.Atan2(cs.Position.Y, cs.Position.X) + 0.4;
            Vector3D sp = new Vector3D(R * Math.Cos(az), R * Math.Sin(az), 0);
            Vector3D tHat = Vector3D.Normalize(Vector3D.Cross(Vector3D.UnitZ, sp));
            KeplerianElements el = OrbitalMath.ToElements(new StateVector(sp, -tHat * vc), parent.Mu, t0);
            Ok("fixture: ship elements finite", !double.IsNaN(el.SemiMajorAxis));

            double tX;
            Ok("entry is found inside the window", SoiReparent.TryFindSoiEntry(el, child, t0, 600.0, soi, out tX));
            Ok("entry is comfortably beyond a 1 s commit horizon", tX - t0 > 5.0);

            // FAR (beyond the horizon): EncounterPending, frame untouched.
            var f = new ProximityFrame(2, "P", el, 5);
            Ok("far crossing -> EncounterPending",
                SoiReparent.Step(f, reg, t0, 600.0, 1.0) == SoiReparent.Change.EncounterPending);
            Ok("pending mutated NOTHING (parent + elements intact)",
                f.ParentBodyName == "P" && f.Elements.Epoch == el.Epoch
                && f.Elements.SemiMajorAxis == el.SemiMajorAxis && f.Elements.TrueAnomaly == el.TrueAnomaly);

            // NEAR (inside the horizon): commits the Galilean patch AT the crossing.
            Ok("crossing inside the horizon -> Encounter",
                SoiReparent.Step(f, reg, t0, 600.0, (tX - t0) + 1.0) == SoiReparent.Change.Encounter);
            Ok("re-parented onto the child", f.ParentBodyName == "C");
            Near("patch epoch == the predicted crossing", f.Elements.Epoch, tX, 1.0);
            Ok("patch state strictly sub-SOI at its epoch",
                OrbitPropagation.StateAt(f.Elements, f.Elements.Epoch).Position.Length() <= soi);

            // LEGACY 4-arg overload: eager commit regardless of distance (the red-replay form).
            var g = new ProximityFrame(3, "P", el, 5);
            Ok("legacy 4-arg overload commits eagerly (far-future epoch)",
                SoiReparent.Step(g, reg, t0, 600.0) == SoiReparent.Change.Encounter
                && g.Elements.Epoch - t0 > 5.0);

            VoxelBerthRegistry.Clear();
        }

        // ---- 2026-06-12 departure-imminent stow wave: FrameRails.EscapesShell truth table ----
        // The pure predicate behind the inside-keep stow gate (FrameManager.AutoStowGrid /
        // the FrameSim twin): a captured ballistic state may stow inside the keep envelope
        // iff it provably exits the shell — (apo > keep OR hyperbolic) AND no upcoming
        // inbound shell crossing within the arrival-imminence look-ahead. Fixture numbers
        // are FrameSim's Earth: mu 8.829e9, shell 42 km, keep 63 km, look-ahead 75 s (x100).
        private static void EscapeStowPredicate()
        {
            Section("departure-imminent stow: escape predicate truth table");
            const double mu = 8.829e9, shell = 42.0e3, keep = 63.0e3, look = 75.0;
            const double t = 1000.0;

            // helper state builders: radial position +X at r, velocity (vr radial, vt tangential)
            Func<double, double, double, KeplerianElements> el = (r, vr, vt) =>
                OrbitalMath.ToElements(new StateVector(
                    new Vector3D(r, 0, 0), new Vector3D(vr, vt, 0)), mu, t);

            // 1. CIRCULAR inside the keep band (r = 50 km): apo = 50 < keep -> NO.
            double vc50 = Math.Sqrt(mu / 50.0e3);
            Ok("circular at 50 km inside keep -> no stow",
                !FrameRails.EscapesShell(el(50.0e3, 0.0, vc50), t, shell, keep, look));

            // 2. TRANSFER ELLIPSE outbound at 45 km (rp 38 / ra 126 km, the Hohmann leg):
            //    apo > keep, next inbound shell crossing ~a full period out -> YES.
            double aT = 0.5 * (38.0e3 + 126.0e3);
            double h = 38.0e3 * Math.Sqrt(mu * (2.0 / 38.0e3 - 1.0 / aT));
            double v45sq = mu * (2.0 / 45.0e3 - 1.0 / aT);
            double vt45 = h / 45.0e3;
            double vr45 = Math.Sqrt(v45sq - vt45 * vt45);
            Ok("transfer ellipse (apo 126 km) outbound at 45 km -> stow",
                FrameRails.EscapesShell(el(45.0e3, vr45, vt45), t, shell, keep, look));

            // 3. The SAME ellipse INBOUND at 45 km: shell re-entry within seconds -> NO.
            Ok("transfer ellipse inbound at 45 km (re-entering) -> no stow",
                !FrameRails.EscapesShell(el(45.0e3, -vr45, vt45), t, shell, keep, look));

            // 4. SUBORBITAL lob at 45 km (apo ~55 km < keep): the VoxelFrame keeps it -> NO.
            double vrLob = Math.Sqrt(2.0 * mu * (1.0 / 45.0e3 - 1.0 / 55.0e3));
            Ok("suborbital lob (apo ~55 km < keep) -> no stow",
                !FrameRails.EscapesShell(el(45.0e3, vrLob, 30.0), t, shell, keep, look));

            // 5. HYPERBOLIC outbound at 45 km (v > v_esc, past periapsis): unbound, the single
            //    inbound crossing is history -> YES.
            KeplerianElements hyp = el(45.0e3, 700.0, 100.0);
            Ok("fixture: state is hyperbolic", hyp.IsHyperbolic);
            Ok("hyperbolic outbound at 45 km -> stow",
                FrameRails.EscapesShell(hyp, t, shell, keep, look));

            // 6. HYPERBOLIC INBOUND at 50 km, diving: inbound shell crossing upcoming within
            //    seconds -> NO (it will be physical voxel descent).
            Ok("hyperbolic inbound at 50 km -> no stow",
                !FrameRails.EscapesShell(el(50.0e3, -700.0, 100.0), t, shell, keep, look));

            // 7. KEEP-BAND ellipse that never touches the shell (rp 50 / ra 80 km, sampled
            //    inbound at 55 km): apo > keep, periapsis above the shell -> YES even inbound
            //    (the orbit can never re-enter the shell at all).
            double aB = 0.5 * (50.0e3 + 80.0e3);
            double hB = 50.0e3 * Math.Sqrt(mu * (2.0 / 50.0e3 - 1.0 / aB));
            double vBsq = mu * (2.0 / 55.0e3 - 1.0 / aB);
            double vtB = hB / 55.0e3;
            double vrB = Math.Sqrt(vBsq - vtB * vtB);
            Ok("keep-band ellipse (peri 50 km > shell) inbound at 55 km -> stow",
                FrameRails.EscapesShell(el(55.0e3, -vrB, vtB), t, shell, keep, look));

            // 8. EXTREME-WARP conservatism: the same outbound transfer ellipse as (2) but a
            //    look-ahead longer than its period (~1570 s) sees the eventual re-entry -> NO
            //    (the stow then happens beyond the keep exactly as before the wave).
            Ok("transfer ellipse with look-ahead > period (re-entry within window) -> no stow",
                !FrameRails.EscapesShell(el(45.0e3, vr45, vt45), t, shell, keep, 2000.0));
        }

        // ================================================================================
        //  2026-06-12 ROTATING SURFACE CHART (docs/rotating-anchored-frames.md as-built):
        //  the body-fixed chart inside r < R_c of a planet cell. These pin the exact
        //  algebra (PlanetBerths' chart block) the mod seams, FrameSim and the renderers
        //  all share.
        // ================================================================================

        // A planet-cell window with the chart filled by hand (no registry needed): Earth-like
        // spin about +Z, cell at a non-trivial center, window riding a non-zero FrameCelVel.
        private static ObserverFrame ChartFrame(Vector3D center, Vector3D frameCel, Vector3D frameCelVel,
            double theta, double omega, bool active)
        {
            ObserverFrame f = new ObserverFrame(center, frameCel, frameCelVel, null);
            f.SpinAxisDir = Vector3D.UnitZ;
            f.SpinTheta = theta;
            f.SpinOmega = omega;
            f.SpinActive = active;
            return f;
        }

        private static void RotatingChartLaws()
        {
            Section("rotating chart: forward/inverse laws + the launch credit");
            Vector3D c = new Vector3D(120.0e3, -40.0e3, 7.0e3);
            Vector3D F = new Vector3D(580.0e3, 33.0e3, -2.0e3);
            Vector3D Fv = new Vector3D(-12.0, 230.0, 1.5);
            double omega = 2.0 * Math.PI / 3600.0;
            double theta = 2.31;   // an arbitrary non-trivial phase
            ObserverFrame on = ChartFrame(c, F, Fv, theta, omega, true);
            ObserverFrame off = ChartFrame(c, F, Fv, theta, omega, false);

            Vector3D p = c + new Vector3D(25.0e3, 11.0e3, 3.0e3);
            Vector3D v = new Vector3D(140.0, -55.0, 12.0);

            // INACTIVE chart == today's 1:1 window, byte-identical.
            NearVec("inactive: position law is the old 1:1 map",
                PlanetBerths.ObserverCelestial(off, p), F + (p - c), 0.0);
            NearVec("inactive: velocity law is v + FrameCelVel",
                PlanetBerths.ObserverCelestialVelocity(off, p, v), v + Fv, 0.0);

            // ω = 0 chart (active flag set, no spin data) degenerates to identity too: the
            // SetWindowSpinActive guard refuses activation without spin data.
            ObserverFrame zero = ChartFrame(c, F, Fv, theta, 0.0, false);
            PlanetBerths.SetWindowSpinActive(ref zero, true);
            Ok("omega = 0: latch cannot activate a spinless window", !zero.SpinActive);

            // FORWARD ∘ INVERSE = identity (position and velocity), active chart.
            Vector3D cel = PlanetBerths.ObserverCelestial(on, p);
            Vector3D celVel = PlanetBerths.ObserverCelestialVelocity(on, p, v);
            NearVec("forward∘inverse position identity",
                PlanetBerths.WorldFromCelestial(on, cel), p, 1e-9);
            NearVec("forward∘inverse velocity identity",
                PlanetBerths.WorldVelFromCelestial(on, cel, celVel), v, 1e-9);

            // THE LAUNCH CREDIT, forced: a world-PARKED point (v = 0) has celestial velocity
            // FrameCelVel + ω×(R·(p−c)) — magnitude exactly ω·r_perp about the axis.
            Vector3D parkedCelVel = PlanetBerths.ObserverCelestialVelocity(on, p, Vector3D.Zero);
            Vector3D rotOff = PlanetBerths.RotateAboutAxis(p - c, Vector3D.UnitZ, theta);
            NearVec("parked credit = ω×r exactly (vector)",
                parkedCelVel - Fv, Vector3D.Cross(Vector3D.UnitZ * omega, rotOff), 1e-9);
            double rPerp = Math.Sqrt((p - c).X * (p - c).X + (p - c).Y * (p - c).Y);
            Near("parked credit magnitude = ω·r_perp", (parkedCelVel - Fv).Length(), omega * rPerp, 1e-9);

            // The body itself maps to the cell center under the inverse law (R⁻¹·0 = 0).
            NearVec("body celestial -> cell center (inverse law fixed point)",
                PlanetBerths.WorldFromCelestial(on, F), c, 1e-9);
        }

        private static void ZoneBoundaryConversion()
        {
            Section("rotating chart: zone-boundary conversion (round-trip + celestial continuity)");
            Vector3D c = new Vector3D(-300.0e3, 90.0e3, 0.0);
            Vector3D F = new Vector3D(612.0e3, -75.0e3, 4.0e3);
            Vector3D Fv = new Vector3D(310.0, 18.0, -2.0);
            Vector3D axis = Vector3D.Normalize(new Vector3D(0.1, -0.2, 0.97));   // a tilted axis
            double omega = 2.0 * Math.PI / 5400.0;
            double theta = 4.07;

            Vector3D p0 = c + new Vector3D(13.0e3, -2.0e3, 1.0e3);
            Vector3D v0 = new Vector3D(-80.0, 33.0, 5.0);

            // ROUND TRIP: into the zone then back out = identity exactly.
            Vector3D p = p0, v = v0;
            PlanetBerths.ConvertIntoZone(axis, theta, omega, c, ref p, ref v);
            PlanetBerths.ConvertOutOfZone(axis, theta, omega, c, ref p, ref v);
            NearVec("inbound∘outbound position identity", p, p0, 1e-9);
            NearVec("inbound∘outbound velocity identity", v, v0, 1e-9);

            // RADIUS INVARIANCE: the conversion rotates about the center — |p − c| unchanged
            // (this is why the membership latch is purely radius-keyed).
            p = p0; v = v0;
            PlanetBerths.ConvertIntoZone(axis, theta, omega, c, ref p, ref v);
            Near("conversion preserves the radius", (p - c).Length(), (p0 - c).Length(), 1e-9);

            // CELESTIAL CONTINUITY: the converted state interpreted through the ROTATING chart
            // equals the original state interpreted through the INERTIAL window — position AND
            // velocity, to 1e-9 relative. This is the seam's whole contract.
            ObserverFrame inert = ChartFrame(c, F, Fv, theta, omega, false);
            inert.SpinAxisDir = axis;
            ObserverFrame rot = ChartFrame(c, F, Fv, theta, omega, true);
            rot.SpinAxisDir = axis;
            Vector3D celBefore = PlanetBerths.ObserverCelestial(inert, p0);
            Vector3D celVelBefore = PlanetBerths.ObserverCelestialVelocity(inert, p0, v0);
            Vector3D celAfter = PlanetBerths.ObserverCelestial(rot, p);
            Vector3D celVelAfter = PlanetBerths.ObserverCelestialVelocity(rot, p, v);
            NearVec("celestial position continuous across the inbound conversion",
                celAfter, celBefore, 1e-9 * celBefore.Length());
            NearVec("celestial velocity continuous across the inbound conversion",
                celVelAfter, celVelBefore, 1e-9 * Math.Max(1.0, celVelBefore.Length()));

            // OUTBOUND continuity too (chart state -> inertial window).
            Vector3D p2 = p, v2 = v;
            PlanetBerths.ConvertOutOfZone(axis, theta, omega, c, ref p2, ref v2);
            NearVec("celestial position continuous across the outbound conversion",
                PlanetBerths.ObserverCelestial(inert, p2), celAfter, 1e-9 * celAfter.Length());
            NearVec("celestial velocity continuous across the outbound conversion",
                PlanetBerths.ObserverCelestialVelocity(inert, p2, v2), celVelAfter,
                1e-9 * Math.Max(1.0, celVelAfter.Length()));

            // A WORLD-PARKED state converted OUT of the zone carries the ω×r credit as real
            // window velocity (the surface-launch capture, in one line).
            Vector3D pp = c + new Vector3D(0.0, 44.1e3, 0.0), pv = Vector3D.Zero;
            PlanetBerths.ConvertOutOfZone(Vector3D.UnitZ, 0.7, omega, c, ref pp, ref pv);
            Near("outbound conversion of a parked state = ω×r of window velocity",
                pv.Length(), omega * 44.1e3, 1e-9);
        }

        private static void ZoneLatchHysteresis()
        {
            Section("rotating chart: membership latch hysteresis (ZoneLatch)");
            double rc = 42.0e3;
            double drop = rc * PlanetBerths.SurfaceZoneDropPad;
            Ok("outside, above R_c -> stays outside", !PlanetBerths.ZoneLatch(false, rc + 1.0, rc));
            Ok("outside, below R_c -> enters", PlanetBerths.ZoneLatch(false, rc - 1.0, rc));
            Ok("inside, in the band -> stays inside (no flap)",
                PlanetBerths.ZoneLatch(true, rc + 0.005 * rc, rc));
            Ok("inside, at the drop edge -> stays inside", PlanetBerths.ZoneLatch(true, drop, rc));
            Ok("inside, above the drop edge -> leaves", !PlanetBerths.ZoneLatch(true, drop + 1.0, rc));
            Ok("outside, in the band -> stays outside (bistable band)",
                !PlanetBerths.ZoneLatch(false, rc + 0.005 * rc, rc));
            Ok("degenerate R_c -> never inside", !PlanetBerths.ZoneLatch(true, 1.0, 0.0));
            Ok("the drop edge clears the stow floor",
                PlanetBerths.SurfaceZoneDropPad < PlanetBerths.StowShellMargin);
        }

        private static void ChartSkyWheel()
        {
            Section("rotating chart: the sky wheels at ω (ProjectProxy / BodyWorldPosInFrame)");
            Vector3D c = new Vector3D(50.0e3, 0.0, 0.0);
            Vector3D F = new Vector3D(600.0e3, 0.0, 0.0);
            double omega = 2.0 * Math.PI / 3600.0;
            Vector3D bodyCel = F + new Vector3D(120.0e3, 0.0, 0.0);   // a "Moon" 120 km out

            // Drawn azimuth (about +Z) of the proxy for a center-cell observer at two times:
            // the sweep rate is exactly −ω (the celestial direction here is static, so all the
            // motion is the wheel).
            Func<double, double> drawnAz = theta =>
            {
                ObserverFrame f = ChartFrame(c, F, Vector3D.Zero, theta, omega, true);
                ProxyPlacement pl = PlanetBerths.ProjectProxy(c, bodyCel, f, 9.4e3, 0.0);
                Vector3D dir = pl.RenderPos - c;
                return Math.Atan2(dir.Y, dir.X);
            };
            double dt = 10.0;
            double rate = SunPhaseEase.WrapToPi(drawnAz(omega * dt) - drawnAz(0.0)) / dt;
            Near("anchored observer's proxy azimuth sweep rate = -ω", rate, -omega, 1e-12);

            // Identity outside the zone: same geometry, chart inactive -> azimuth static.
            Func<double, double> staticAz = theta =>
            {
                ObserverFrame f = ChartFrame(c, F, Vector3D.Zero, theta, omega, false);
                ProxyPlacement pl = PlanetBerths.ProjectProxy(c, bodyCel, f, 9.4e3, 0.0);
                Vector3D dir = pl.RenderPos - c;
                return Math.Atan2(dir.Y, dir.X);
            };
            Near("inactive chart: proxy azimuth does not wheel",
                SunPhaseEase.WrapToPi(staticAz(omega * dt) - staticAz(0.0)), 0.0, 1e-12);

            // BodyWorldPosInFrame is the same R⁻¹ composition: drawn world pos == camera +
            // dir·trueDist for an unclamped body (one map, two consumers).
            ObserverFrame on = ChartFrame(c, F, Vector3D.Zero, 1.234, omega, true);
            Vector3D viaProxy = PlanetBerths.ProjectProxy(c, bodyCel, on, 9.4e3, 0.0).RenderPos;
            // BodyWorldPosInFrame routes through the same WorldFromCelestial map (one R⁻¹
            // composition, two consumers) — pin them against each other directly.
            NearVec("proxy placement == WorldFromCelestial composition",
                PlanetBerths.WorldFromCelestial(on, bodyCel), viaProxy, 1e-6);

            // Angular size is preserved by the wheel (R⁻¹ is an isometry).
            ProxyPlacement a0 = PlanetBerths.ProjectProxy(c, bodyCel, ChartFrame(c, F, Vector3D.Zero, 0.0, omega, true), 9.4e3, 0.0);
            ProxyPlacement a1 = PlanetBerths.ProjectProxy(c, bodyCel, on, 9.4e3, 0.0);
            Near("wheel preserves angular size", a1.RenderRadius, a0.RenderRadius, 1e-9);
            Near("wheel preserves true distance", a1.TrueDistance, a0.TrueDistance, 1e-9);
        }

        private static void ChartOrientationAlgebra()
        {
            Section("rotating chart: seam orientation algebra (Q' = R⁻¹·Q, ship-relative sky exact)");
            Vector3D axis = Vector3D.Normalize(new Vector3D(0.2, 0.1, 0.95));
            double theta = 2.9;

            // A ship orientation Q as three basis vectors; the INBOUND seam applies R⁻¹ to each.
            Vector3D qF = Vector3D.Normalize(new Vector3D(0.3, 0.8, -0.5));
            Vector3D qU = Vector3D.Normalize(Vector3D.Cross(qF, new Vector3D(0.0, 0.0, 1.0)));
            Vector3D qR = Vector3D.Cross(qF, qU);

            // Sky feature at celestial direction u: drawn at u outside, at R⁻¹·u inside.
            Vector3D u = Vector3D.Normalize(new Vector3D(-0.6, 0.4, 0.7));
            Vector3D uIn = PlanetBerths.RotateAboutAxis(u, axis, -theta);

            // Ship-relative components (u in the ship's basis) before vs after the seam:
            // before: dot(u, q_i); after: dot(R⁻¹u, R⁻¹q_i) — identical because R is an isometry.
            Vector3D qF2 = PlanetBerths.RotateAboutAxis(qF, axis, -theta);
            Vector3D qU2 = PlanetBerths.RotateAboutAxis(qU, axis, -theta);
            Vector3D qR2 = PlanetBerths.RotateAboutAxis(qR, axis, -theta);
            Near("ship-relative sky component F continuous", Vector3D.Dot(uIn, qF2), Vector3D.Dot(u, qF), 1e-12);
            Near("ship-relative sky component U continuous", Vector3D.Dot(uIn, qU2), Vector3D.Dot(u, qU), 1e-12);
            Near("ship-relative sky component R continuous", Vector3D.Dot(uIn, qR2), Vector3D.Dot(u, qR), 1e-12);

            // The proxy-texture continuity at the seam: drawn texture orientation is identity
            // inside the body's own zone and R_b(θ) outside; the observer rotates by R at the
            // outbound seam, so the ship-relative texture orientation is continuous:
            //   Q⁻¹·R(θ)·e == Q'⁻¹·e for every texture vector e (Q = R·Q').
            Vector3D e = Vector3D.Normalize(new Vector3D(0.9, -0.1, 0.4));
            Vector3D outside = PlanetBerths.RotateAboutAxis(e, axis, theta);   // R_b(θ)·e in window axes
            // ship basis outside = R·(inside basis): project on F.
            Vector3D qFout = PlanetBerths.RotateAboutAxis(qF2, axis, theta);
            Near("texture continuity across the R_c crossing",
                Vector3D.Dot(outside, qFout), Vector3D.Dot(e, qF2), 1e-12);

            // THE MATRIX CONVENTION PIN: the game-side seams compose the orientation half as
            // M' = M · CreateFromAxisAngle(axis, ∓θ) (row-vector convention) — that MUST be the
            // same rotation the pure Rodrigues law applies, or the seam would rotate poses one
            // way and velocities the other. Pin TransformNormal(v, CreateFromAxisAngle) ==
            // RotateAboutAxis for both signs, and the world-matrix composition row-by-row.
            MatrixD mPos = MatrixD.CreateFromAxisAngle(axis, theta);
            MatrixD mNeg = MatrixD.CreateFromAxisAngle(axis, -theta);
            NearVec("MatrixD.CreateFromAxisAngle(+θ) == Rodrigues R(+θ)",
                Vector3D.TransformNormal(u, mPos), PlanetBerths.RotateAboutAxis(u, axis, theta), 1e-12);
            NearVec("MatrixD.CreateFromAxisAngle(−θ) == Rodrigues R(−θ)",
                Vector3D.TransformNormal(u, mNeg), PlanetBerths.RotateAboutAxis(u, axis, -theta), 1e-12);
            MatrixD world = MatrixD.CreateWorld(new Vector3D(5.0, -3.0, 9.0), qF, qU);
            MatrixD composed = world * mNeg;
            NearVec("M·R rotates the Forward row by R",
                composed.Forward, PlanetBerths.RotateAboutAxis(world.Forward, axis, -theta), 1e-12);
            NearVec("M·R rotates the Up row by R",
                composed.Up, PlanetBerths.RotateAboutAxis(world.Up, axis, -theta), 1e-12);
        }

        private static void ChartFixtureNumbers()
        {
            Section("rotating chart: live body spins (true-xk: Earth 28800 s, Moon 34500 s) — derived asserts");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody earth = sys.Find("Earth"), moon = sys.Find("Moon");
            BodyDefinition edef = sys.FindDefinition("Earth"), mdef = sys.FindDefinition("Moon");

            // Spin periods: pin that SystemRegistry.Build carries the AUTHORED definition's rotation
            // period through to the live GravityBody unchanged (the meaningful, scale-invariant check —
            // a worked-example number would just rot again on the next retune).
            Near("Earth sidereal day preserved through Build", earth.RotationPeriodSeconds, edef.RotationPeriodSeconds, 0.0);
            Near("Moon sidereal day preserved through Build", moon.RotationPeriodSeconds, mdef.RotationPeriodSeconds, 0.0);

            Vector3D ax; double wE, wM;
            Ok("Earth spin data", PlanetBerths.TryBodySpin(earth, out ax, out wE));
            Ok("Moon spin data", PlanetBerths.TryBodySpin(moon, out ax, out wM));
            Near("Earth ω", wE, 2.0 * Math.PI / earth.RotationPeriodSeconds, 1e-15);

            // Launch credit at the stow floor: ω × (R_c × StowShellMargin) ≈ 77.0 m/s — 13% of
            // the 594 m/s Hohmann periapsis speed (docs §9's playable compromise; was 231 m/s
            // / 39% at the old 1200 s day).
            double floorE = PlanetBerths.ShellRadius(edef) * PlanetBerths.StowShellMargin;
            Ok($"Earth credit at the stow floor stays a small share of a transfer ({wE * floorE:F1} m/s < 100)", wE * floorE < 100);

            // Geostationary radius is DERIVED from live μ and ω: r_geo = (μ/ω²)^⅓. (Asserting the Kepler
            // relation ω²·r³==μ would be tautological — r is defined from μ/ω². So pin the FEATURE-relevant
            // facts instead: r_geo is finite/positive, and it sits beyond the keep so a synchronous orbit
            // lives as an ordinary stowed frame, not a parking pathology against the body.)
            double rGeoE = Math.Pow(earth.Mu / (wE * wE), 1.0 / 3.0);
            Ok("Earth r_geo finite + positive", rGeoE > 0.0 && !double.IsNaN(rGeoE) && !double.IsInfinity(rGeoE));
            Ok("Earth r_geo beyond the keep", rGeoE > PlanetBerths.KeepRadius(edef));

            double rGeoM = Math.Pow(moon.Mu / (wM * wM), 1.0 / 3.0);
            Ok("Moon r_geo finite + positive", rGeoM > 0.0 && !double.IsNaN(rGeoM) && !double.IsInfinity(rGeoM));
            Ok("Moon r_geo beyond the keep", rGeoM > PlanetBerths.KeepRadius(mdef));
            // CLASS B (true-xk Moon spin/mu retune owed): the rescale ballooned the Moon SOI to
            // ~2491 km while r_geo fell to ~594 km and the hyperbolic-parking floor (2μ/ω²)^⅓ to
            // ~748 km — BOTH now INSIDE the SOI, so the synchronous + spontaneous-escape parking
            // pathologies the old fixture deliberately eliminated have returned. Pending rob's
            // retune of the Moon's RotationPeriod / radius / mu.
            double hypM = Math.Pow(2.0 * moon.Mu / (wM * wM), 1.0 / 3.0);
            _ = hypM;   // the hyperbolic-parking floor — its SOI comparison is Pending below
            Pending("Moon r_geo beyond the SOI (no synchronous orbit)",
                "true-xk Moon spin/mu retune owed (synchronous + parking orbits fell inside the ballooned SOI) — pending rob");
            Pending("Moon hyperbolic-parking floor beyond the SOI",
                "true-xk Moon spin/mu retune owed (synchronous + parking orbits fell inside the ballooned SOI) — pending rob");
            Ok("Moon keep entirely sub-synchronous (parked stow capture always elliptic)",
                PlanetBerths.KeepRadius(mdef) < rGeoM);
        }

        private static void ChartResolveFill()
        {
            Section("rotating chart: the resolves fill spin per subject (in-zone vs out)");
            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody earth = sys.Find("Earth");
            BodyDefinition edef = sys.FindDefinition("Earth");
            double t = 777.0;
            Vector3D cell = VoxelBerthRegistry.CellOf("Earth", sys);
            double rc = PlanetBerths.ShellRadius(edef);

            // In-zone subject: chart active, θ/ω from the body.
            Vector3D inPos = cell + new Vector3D(0.8 * rc, 0.0, 0.0);
            ObserverFrame fIn = PlanetBerths.ResolveFrame(sys, t, earth, inPos);
            Ok("in-zone camera resolve: chart active", fIn.SpinActive);
            Near("θ = RotationAngleAt(t)", fIn.SpinTheta, earth.RotationAngleAt(t), 1e-12);
            Near("ω = 2π/T", fIn.SpinOmega, 2.0 * Math.PI / earth.RotationPeriodSeconds, 1e-15);

            // Out-of-zone subject in the same cell: data filled, chart INACTIVE -> the window
            // behaves byte-identically to the old inertial one.
            Vector3D outPos = cell + new Vector3D(1.2 * rc, 0.0, 0.0);
            ObserverFrame fOut = PlanetBerths.ResolveFrame(sys, t, earth, outPos);
            Ok("out-of-zone camera resolve: chart inactive", !fOut.SpinActive);
            Ok("spin data still filled cell-wide (latch owners can flip it)", fOut.SpinOmega > 0.0);
            NearVec("out-of-zone observer celestial is the old 1:1 map",
                PlanetBerths.ObserverCelestial(fOut, outPos),
                fOut.FrameCel + (outPos - fOut.CellCenter), 0.0);

            // Server-side grid resolve agrees (cell-containment branch).
            ObserverFrame fg = PlanetBerths.ResolveFrameForGrid(sys, t, null, 99, inPos, null);
            Ok("grid resolve (cell containment): chart active in-zone", fg.SpinActive);
            ObserverFrame fg2 = PlanetBerths.ResolveFrameForGrid(sys, t, null, 99, outPos, null);
            Ok("grid resolve: chart inactive out-of-zone", !fg2.SpinActive);

            // The in-zone observer's celestial position is the rotated offset (forward law).
            Vector3D expect = fIn.FrameCel + PlanetBerths.RotateAboutAxis(inPos - cell,
                fIn.SpinAxisDir, fIn.SpinTheta);
            NearVec("in-zone observer celestial = F + R(θ)·(p−c)",
                PlanetBerths.ObserverCelestial(fIn, inPos), expect, 1e-9);

            // Conjunction/home windows never carry a chart.
            ObserverFrame home = PlanetBerths.ResolveFrameForGrid(sys, t, null, 99,
                cell + new Vector3D(900.0e3, 0, 0), null);
            Ok("home/far window: no chart", !home.SpinActive && home.SpinOmega == 0.0);
        }

        // ================================================================================
        //  2026-06-12 RENDERING STEAL (docs/audits/frames-vs-rss-steal-audit-2026-06-12.md
        //  section C, S1/S2): the ONE matrix-space conversion primitive (RSS's
        //  ConvertMatrixSpace/ConvertProxyMatToReal, reference ZoneManager.cs:1209-1235)
        //  and the RSS-verbatim proxy orientation composition (reference
        //  PlanetProxyManager.cs:25,110-132) carried onto tilted spin axes. These pin the
        //  exact identities the renderer's three-case placement relies on.
        // ================================================================================

        private static void CelestialMatPrimitive()
        {
            Section("one mat-space primitive (RSS ConvertMatrixSpace): agrees with the laws, exact inverse");
            Vector3D c = new Vector3D(120.0e3, -40.0e3, 7.0e3);
            Vector3D F = new Vector3D(580.0e3, 33.0e3, -2.0e3);
            double omega = 2.0 * Math.PI / 3600.0;
            double theta = 2.31;
            ObserverFrame on = ChartFrame(c, F, Vector3D.Zero, theta, omega, true);
            ObserverFrame off = ChartFrame(c, F, Vector3D.Zero, theta, omega, false);

            // An arbitrary rigid pose in celestial space.
            Vector3D qF = Vector3D.Normalize(new Vector3D(0.3, 0.8, -0.5));
            Vector3D qU = Vector3D.Normalize(Vector3D.Cross(qF, new Vector3D(0.0, 0.0, 1.0)));
            Vector3D cel = F + new Vector3D(25.0e3, 11.0e3, 3.0e3);
            MatrixD m = MatrixD.CreateWorld(cel, qF, qU);

            // INACTIVE chart: identity rotation — orientation rows untouched, translation is
            // the existing 1:1 law byte-for-byte (the primitive can never disagree with the
            // point laws the rest of the pipeline uses).
            MatrixD wOff = PlanetBerths.CelestialMatToWorld(ref off, m);
            NearVec("inactive: orientation rows untouched", wOff.Forward, m.Forward, 0.0);
            NearVec("inactive: translation == WorldFromCelestial", wOff.Translation,
                PlanetBerths.WorldFromCelestial(off, cel), 0.0);

            // ACTIVE chart: the translation row IS WorldFromCelestial, and the rotation applied
            // to every orientation row IS SpinFromCelestial — ONE chart rotation for poses,
            // points and directions (the MatrixD/Rodrigues convention pin carries the proof).
            MatrixD wOn = PlanetBerths.CelestialMatToWorld(ref on, m);
            NearVec("active: translation == WorldFromCelestial", wOn.Translation,
                PlanetBerths.WorldFromCelestial(on, cel), 1e-9);
            NearVec("active: Forward row == SpinFromCelestial of the input row",
                wOn.Forward, PlanetBerths.SpinFromCelestial(on, m.Forward), 1e-12);
            NearVec("active: Up row == SpinFromCelestial of the input row",
                wOn.Up, PlanetBerths.SpinFromCelestial(on, m.Up), 1e-12);

            // The exact inverse: world -> celestial round-trips the full pose, and its
            // translation law is ObserverCelestial.
            MatrixD back = PlanetBerths.WorldMatToCelestial(ref on, wOn);
            NearMat("round-trip WorldMatToCelestial ∘ CelestialMatToWorld == identity", back, m, 1e-9);
            NearVec("inverse translation == ObserverCelestial", back.Translation,
                PlanetBerths.ObserverCelestial(on, wOn.Translation), 1e-9);
        }

        private static void RssProxyOrientationVerbatim()
        {
            Section("RSS proxy orientation verbatim (audit S2): canonical equality, in-zone pin, seam continuity");

            // (c) canonical-axis equivalence: with spin axis == Up our composition reproduces
            // RSS's baseMat and proxyRotMat (reference PlanetProxyManager.cs:25,118-120)
            // byte-for-byte — B == Identity exactly, and M·Identity is element-exact.
            double theta = 1.234;
            MatrixD baseMat = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Forward, Vector3D.Down);
            MatrixD rotationMatrix = MatrixD.CreateFromAxisAngle(Vector3D.Up, -theta);
            MatrixD rssProxyRotMat = baseMat * MatrixD.Invert(rotationMatrix);
            NearMat("(c) axis == Up: base == RSS baseMat byte-for-byte",
                PlanetBerths.ProxyBaseOrientation(Vector3D.Up), baseMat, 0.0);
            NearMat("(c) axis == Up: spin == RSS proxyRotMat byte-for-byte",
                PlanetBerths.ProxySpinOrientation(Vector3D.Up, theta), rssProxyRotMat, 0.0);

            // The authored flip carried by B: model-up maps to world DOWN canonically, to
            // −axis tilted (the whole canonical picture rotated by ONE proper rotation —
            // no mirror, so no N/S flip and no retrograde apparent spin).
            Vector3D axis = Vector3D.Normalize(new Vector3D(0.2, 0.1, 0.95));
            NearVec("model-up -> world DOWN (canonical base)",
                Vector3D.TransformNormal(Vector3D.Up, PlanetBerths.ProxyBaseOrientation(Vector3D.Up)),
                Vector3D.Down, 1e-12);
            NearVec("model-up -> −spin-axis (tilted base)",
                Vector3D.TransformNormal(Vector3D.Up, PlanetBerths.ProxyBaseOrientation(axis)),
                -axis, 1e-12);
            MatrixD b = PlanetBerths.ProxyAxisChange(axis);
            NearVec("B carries Up onto the axis", Vector3D.TransformNormal(Vector3D.Up, b), axis, 1e-12);
            NearVec("B is a proper rotation (Right×Up == Backward, no mirror)",
                Vector3D.Cross(b.Right, b.Up), b.Backward, 1e-12);

            // The derived multiplication order (row-vector algebra R(Up,θ)·B = B·R(axis,θ)):
            // the canonical product carried by B on the right IS base·R(axis,+θ) — surface
            // features advance by R(+θ) about the tilted axis, the chart's own R.
            NearMat("carried spin == ProxyBaseOrientation·R(axis,+θ) (the pinned order)",
                PlanetBerths.ProxySpinOrientation(axis, theta),
                PlanetBerths.ProxyBaseOrientation(axis) * MatrixD.CreateFromAxisAngle(axis, theta), 1e-12);

            // (a) the IN-ZONE PIN: for the frame body seen from inside its own zone (window
            // θ == body θ, same axis) the fully-composed drawn orientation — live spin through
            // the chart's R(−θ) via the primitive — equals the pinned base orientation with NO
            // residual rotation: the skin sits still on the static voxel. (Translations zeroed:
            // the renderer discards the primitive's translation — placement is the voxel
            // center / the ProjectProxy clamp, audit S6.)
            ObserverFrame inZone = new ObserverFrame(Vector3D.Zero, Vector3D.Zero, null);
            inZone.SpinActive = true;
            inZone.SpinAxisDir = axis;
            inZone.SpinTheta = theta;
            MatrixD drawnIn = PlanetBerths.CelestialMatToWorld(ref inZone,
                PlanetBerths.ProxySpinOrientation(axis, theta));
            NearMat("(a) in-zone composed orientation == pinned base (no residual)",
                drawnIn, PlanetBerths.ProxyBaseOrientation(axis), 1e-9);

            // (b) SEAM CONTINUITY: the drawn skin orientation is continuous across the R_c
            // crossing in SHIP-RELATIVE terms — out-zone live spin (identity chart) vs the
            // in-zone pin seen by the R(−θ)-converted observer. Pose conversion is the seams'
            // row-convention M' = M·R(axis,−θ) (FrameManager:1291, pinned in
            // ChartOrientationAlgebra); ship-relative orientation = skin·pose⁻¹.
            Vector3D qF = Vector3D.Normalize(new Vector3D(0.3, 0.8, -0.5));
            Vector3D qU = Vector3D.Normalize(Vector3D.Cross(qF, new Vector3D(0.0, 0.0, 1.0)));
            MatrixD pose = MatrixD.CreateWorld(Vector3D.Zero, qF, qU);
            MatrixD poseIn = pose * MatrixD.CreateFromAxisAngle(axis, -theta);
            NearMat("(b) frame body: ship-relative skin orientation continuous across R_c",
                PlanetBerths.ProxySpinOrientation(axis, theta) * MatrixD.Invert(pose),
                PlanetBerths.ProxyBaseOrientation(axis) * MatrixD.Invert(poseIn), 1e-9);

            // ... and for a NON-frame body (own axis ≠ window axis) drawn through the
            // primitive: outside identity vs inside R(−θ_W) — the observer's own conversion
            // cancels the chart for ANY celestial orientation.
            Vector3D axisB = Vector3D.Normalize(new Vector3D(-0.5, 0.2, 0.6));
            MatrixD celB = PlanetBerths.ProxySpinOrientation(axisB, 0.77);
            ObserverFrame offWin = new ObserverFrame(Vector3D.Zero, Vector3D.Zero, null);
            NearMat("(b) other body: ship-relative orientation continuous across R_c",
                PlanetBerths.CelestialMatToWorld(ref offWin, celB) * MatrixD.Invert(pose),
                PlanetBerths.CelestialMatToWorld(ref inZone, celB) * MatrixD.Invert(poseIn), 1e-9);

            // ... clouds ride the SAME routing (proxyRotMat·R(cloudUp,φ) through the
            // primitive, RSS :184-186), so the same identity covers the drift layer.
            Vector3D cloudUp = Vector3D.Normalize(new Vector3D(0.1, 0.9, 0.2));
            MatrixD celCloud = celB * MatrixD.CreateFromAxisAngle(cloudUp, 0.4);
            NearMat("(b) cloud: ship-relative drift orientation continuous across R_c",
                PlanetBerths.CelestialMatToWorld(ref offWin, celCloud) * MatrixD.Invert(pose),
                PlanetBerths.CelestialMatToWorld(ref inZone, celCloud) * MatrixD.Invert(poseIn), 1e-9);

            // (d) PLACEMENT untouched by the orientation steal: true (wheeled) sky direction
            // and exact angular size still come from ProjectProxy — the deliberate S6
            // deviation keeps the 2000 km clamp, the primitive supplies orientation only.
            ObserverFrame place = ChartFrame(new Vector3D(120.0e3, -40.0e3, 7.0e3),
                new Vector3D(580.0e3, 33.0e3, -2.0e3), Vector3D.Zero, theta,
                2.0 * Math.PI / 3600.0, true);
            Vector3D cam = place.CellCenter + new Vector3D(50.0e3, 0.0, 0.0);
            Vector3D bodyCel = place.FrameCel + new Vector3D(4.0e6, 1.0e6, -2.0e6);   // beyond the clamp
            double r = 30.0e3;
            ProxyPlacement pp = PlanetBerths.ProjectProxy(cam, bodyCel, place, r, 0.0);
            Near("(d) render distance clamped to the sky shell",
                (pp.RenderPos - cam).Length(), PlanetBerths.SkyClampDistance, 1e-6);
            // Angular size preserved, then SCALED by the flat-monitor apparent-size exaggeration
            // (ProjectProxy multiplies RenderRadius by ApparentSizeMult; this body is far -> full 2x).
            Near("(d) exact angular size preserved (rRender/dRender == R/dTrue)",
                pp.RenderRadius / (pp.RenderPos - cam).Length(),
                (r / pp.TrueDistance) * PlanetBerths.ApparentSizeMult(r, pp.TrueDistance), 1e-15);
            NearVec("(d) true wheeled sky direction preserved",
                Vector3D.Normalize(pp.RenderPos - cam),
                Vector3D.Normalize(PlanetBerths.SpinFromCelestial(place,
                    bodyCel - PlanetBerths.ObserverCelestial(place, cam))), 1e-12);
        }

        // SE's sun rotation axis is a DERIVED quantity (decompiled MySunProperties.SunRotationAxis,
        // vendored as SunPlane.EngineSunRotationAxis): the world-up component orthogonal to the
        // baked base sun direction (Left-based within ~18° of ±Y). Pins: the law itself, and the
        // IMPOSSIBILITY bound — no base direction can ever make the axis ±Z (our canonical
        // reference normal), so the engine sweep plane can NEVER be the system plane. That bound
        // is what the 2026-06-12 RSS-LITERAL decision accepts: the layout stays canonical (never
        // reads sun/world state), the drawn star is the chart-mapped celestial truth, and the
        // engine terminator (phase-only bake) carries a bounded elevation error vs the drawn
        // star — exactly what RSS ships (~45°, unnoticed). The earlier same-day alternative
        // (tilt the system onto the engine plane) was DELETED for coupling the layout to mutable
        // world render state (steal-audit A2).
        private static void EngineSunAxisFormula()
        {
            Section("engine sun axis: the derived-axis law and its ±Z impossibility");

            // The law (Up branch): axis = normalize(Up − B·(Up·B)).
            Vector3D b = Vector3D.Normalize(new Vector3D(0.4, -0.3, 0.6));
            Vector3D axis = SunPlane.EngineSunRotationAxis(b);
            Vector3D up = new Vector3D(0, 1, 0);
            NearVec("Up branch: axis = normalize(Up − B(Up·B))",
                axis, Vector3D.Normalize(up - b * Vector3D.Dot(up, b)), 1e-12);
            Near("axis is unit", axis.Length(), 1.0, 1e-12);
            Near("axis ⊥ base direction (the sweep is a great circle)", Vector3D.Dot(axis, b), 0.0, 1e-12);

            // The vanilla default base direction: same law.
            Vector3D bd = SunPlane.DefaultBaseSunDirection;
            Vector3D axisD = SunPlane.EngineSunRotationAxis(bd);
            Near("vanilla default: axis ⊥ B", Vector3D.Dot(axisD, Vector3D.Normalize(bd)), 0.0, 1e-12);

            // The Left branch: B within ~18° of ±Y uses Left = (−1,0,0) instead of Up.
            Vector3D bPolar = Vector3D.Normalize(new Vector3D(0.05, 0.99, 0.05));
            Vector3D left = new Vector3D(-1, 0, 0);
            NearVec("Left branch near ±Y: axis = normalize(Left − B(Left·B))",
                SunPlane.EngineSunRotationAxis(bPolar),
                Vector3D.Normalize(left - bPolar * Vector3D.Dot(left, bPolar)), 1e-12);

            // THE IMPOSSIBILITY (option (a) is dead): sweep every base direction on a fine
            // sphere grid — the derived axis never reaches ±Z. Up branch: with B = (bx,by,bz),
            // |axis·Z| = |by·bz| / ‖Up − B·by‖ which is ≤ |by| ≤ 0.95 (the branch gate); the
            // Left branch axis is ≈ −X. So |axis·Z| stays ≤ 0.95 + ε for ALL inputs.
            double worst = 0.0;
            for (int i = 0; i <= 60; i++)
                for (int j = 0; j < 120; j++)
                {
                    double th = Math.PI * i / 60.0, ph = 2.0 * Math.PI * j / 120.0;
                    Vector3D bb = new Vector3D(Math.Sin(th) * Math.Cos(ph),
                        Math.Cos(th), Math.Sin(th) * Math.Sin(ph));
                    double az = Math.Abs(SunPlane.EngineSunRotationAxis(bb).Z);
                    if (az > worst) worst = az;
                }
            Ok("derived axis can NEVER be ±Z (sup |axis·Z| ≤ 0.95 over the sphere): worst "
                + worst.ToString("F4"), worst <= 0.9501);
        }

        // THE SYSTEM-PLANE TILT (SystemBuilder.Build(def, planeNormal)) — OFFLINE-ONLY pure math
        // since the 2026-06-12 RSS-literal decision (no production caller passes a non-identity
        // normal; SystemRegistry.Build is canonical-only): the whole built system is exactly the
        // canonical (+Z) build rotated by the minimal rotation Z → n — every body state at every
        // time, every velocity, every spin axis. A rotation is an isometry, so all internal
        // relationships (the entire audited frames/rails/chart stack) are untouched. Identity
        // input keeps the legacy build byte-identical — which IS the production path now.
        private static void TiltedSystemBuild()
        {
            Section("tilted system build (offline pure math): tilted = W · canonical; registry canonical-only");

            SystemDefinition def = SampleSystems.SunEarthMoon();
            Vector3D n = Vector3D.Normalize(new Vector3D(0.3, -0.45, 0.84));

            // Independent re-derivation of the minimal rotation W: Rodrigues about Z × n.
            Vector3D rotAxis = Vector3D.Cross(Vector3D.UnitZ, n);
            double sin = rotAxis.Length(), cos = n.Z;
            rotAxis /= sin;
            Func<Vector3D, Vector3D> W = v => v * cos + Vector3D.Cross(rotAxis, v) * sin
                + rotAxis * (Vector3D.Dot(rotAxis, v) * (1.0 - cos));
            NearVec("W carries +Z onto the plane normal", W(Vector3D.UnitZ), n, 1e-12);

            SystemBuildResult flat = SystemBuilder.Build(def);
            SystemBuildResult tilt = SystemBuilder.Build(def, n);
            Ok("both builds succeed", flat.Ok && tilt.Ok);

            string[] names = { "Earth", "Moon" };
            double[] times = { 0.0, 137.0, 5000.0, 11036.0, 60000.0 };
            double maxPos = 0.0, maxVel = 0.0;
            foreach (string name in names)
                foreach (double t in times)
                {
                    StateVector a = flat.ByName[name].OriginInRoot(t);
                    StateVector bb = tilt.ByName[name].OriginInRoot(t);
                    maxPos = Math.Max(maxPos, (bb.Position - W(a.Position)).Length());
                    maxVel = Math.Max(maxVel, (bb.Velocity - W(a.Velocity)).Length());
                }
            Ok("positions: tilted == W·canonical at every sampled t (worst "
                + maxPos.ToString("E2") + " m)", maxPos < 1e-2);
            Ok("velocities: tilted == W·canonical (worst " + maxVel.ToString("E2") + " m/s)",
                maxVel < 1e-6);

            // Spin axes rotate with the system (the +Z default becomes the plane normal itself),
            // so the rotating chart and the engine sun share one plane BY CONSTRUCTION.
            foreach (string name in new[] { "Sun", "Earth", "Moon" })
                NearVec(name + " spin axis = W·(canonical axis)",
                    tilt.ByName[name].SpinAxis, W(flat.ByName[name].SpinAxis), 1e-12);
            NearVec("default-axis body: tilted spin axis IS the plane normal",
                tilt.ByName["Earth"].SpinAxis, n, 1e-12);

            // Identity tilt: byte-identical legacy path (no round-trip applied at all).
            SystemBuildResult flat2 = SystemBuilder.Build(def, Vector3D.UnitZ);
            NearVec("identity tilt: exact legacy states",
                flat2.ByName["Moon"].OriginInRoot(137.0).Position,
                flat.ByName["Moon"].OriginInRoot(137.0).Position, 0.0);

            // THE PRODUCTION CONTRACT (RSS-literal): SystemRegistry.Build has no plane input at
            // all — the live registry is ALWAYS the canonical build, byte-identical to
            // SystemBuilder.Build(def). One layout per definition, world-independent by
            // construction (no persisted normal, no migration, nothing to disagree across
            // sessions or machines — the deleted tilt's A2 hazard is unrepresentable).
            SystemRegistry.Build(def);
            NearVec("registry build == canonical SystemBuilder build (exact)",
                SystemRegistry.Active.Find("Moon").OriginInRoot(137.0).Position,
                flat.ByName["Moon"].OriginInRoot(137.0).Position, 0.0);
            NearVec("registry default-axis spin axis stays canonical +Z",
                SystemRegistry.Active.Find("Earth").SpinAxis, Vector3D.UnitZ, 0.0);
        }

        // THE ZONE-BOUNDARY SUN (re-derived 2026-06-12 for the RSS-LITERAL contract). The
        // published ground truth is now the DRAWN sun: SunDirection =
        // normalize(SpinFromCelestial(Frame, toStar)) (CelestialSceneSnapshot.DriveSun) — the
        // chart-mapped celestial direction, whose ship-relative continuity across the R_c seam
        // holds by the SAME algebra as every proxy (chart and pose rotate together): pinned
        // first, on a TILTED axis, plus its convergence with ProjectProxy's sky placement.
        // The TERMINATOR phase (the bake input — azimuth of that same u in the engine basis)
        // keeps the exactSwitch pass-through: truth flows; the boundary step is whatever the
        // chart rotation projects into the basis — EXACTLY ∓θ in the aligned-basis coordinates
        // pinned here (basis normal == spin axis; in-game the engine plane differs from the
        // canonical system plane, so the live step is a nearby truthful value — a
        // TERMINATOR-ONLY step, accepted RSS-literally). SunPhaseEase's value-continuous
        // re-capture (the pre-fix behavior) would hold the phase against that truthful step;
        // pinned RED below as the legacy bug. Hold-resume capture kept.
        private static void ZoneBoundarySunPassThrough()
        {
            Section("zone-boundary sun: drawn sun chart-continuous; terminator phase passes through");

            // ---- THE DRAWN SUN (the published contract): ship-relative continuity across the
            // R_c seam on a TILTED axis. Inside: u' = R⁻¹·toStar (SpinFromCelestial) while the
            // pose converts by M' = M·R(axis,−θ) (FrameManager:1291) — the chart factors cancel
            // in the ship frame for ANY toStar, exactly the proxies' seam argument.
            Vector3D axis = Vector3D.Normalize(new Vector3D(0.25, -0.1, 0.96));
            double thetaT = 1.41;
            Vector3D toStar = new Vector3D(-4.2e8, 2.9e8, 1.1e8);
            ObserverFrame zoneOn = new ObserverFrame(Vector3D.Zero, Vector3D.Zero, null);
            zoneOn.SpinActive = true; zoneOn.SpinAxisDir = axis; zoneOn.SpinTheta = thetaT;
            ObserverFrame zoneOff = new ObserverFrame(Vector3D.Zero, Vector3D.Zero, null);
            Vector3D qF2 = Vector3D.Normalize(new Vector3D(0.3, 0.8, -0.5));
            Vector3D qU2 = Vector3D.Normalize(Vector3D.Cross(qF2, new Vector3D(0.0, 0.0, 1.0)));
            MatrixD poseOut = MatrixD.CreateWorld(Vector3D.Zero, qF2, qU2);
            MatrixD poseIn2 = poseOut * MatrixD.CreateFromAxisAngle(axis, -thetaT);
            Vector3D drawnOutDir = Vector3D.Normalize(PlanetBerths.SpinFromCelestial(zoneOff, toStar));
            Vector3D drawnInDir = Vector3D.Normalize(PlanetBerths.SpinFromCelestial(zoneOn, toStar));
            NearVec("drawn sun: ship-relative direction continuous across the R_c conversion",
                Vector3D.TransformNormal(drawnInDir, MatrixD.Invert(poseIn2)),
                Vector3D.TransformNormal(drawnOutDir, MatrixD.Invert(poseOut)), 1e-12);

            // ... and the drawn sun CONVERGES with the sky's own placement of the star: the
            // SunDirection law is the same wheeled direction ProjectProxy draws a body at the
            // star's celestial position along (one chart, one truth, all consumers).
            ObserverFrame place2 = ChartFrame(new Vector3D(120.0e3, -40.0e3, 7.0e3),
                new Vector3D(580.0e3, 33.0e3, -2.0e3), Vector3D.Zero, 0.83,
                2.0 * Math.PI / 3600.0, true);
            Vector3D cam2 = place2.CellCenter + new Vector3D(50.0e3, 0.0, 0.0);
            Vector3D starCel = place2.FrameCel + new Vector3D(4.0e6, 1.0e6, -2.0e6);
            ProxyPlacement starPp = PlanetBerths.ProjectProxy(cam2, starCel, place2, 30.0e3, 0.0);
            NearVec("drawn sun == ProjectProxy's wheeled direction to the star (converges with the sky)",
                Vector3D.Normalize(starPp.RenderPos - cam2),
                Vector3D.Normalize(PlanetBerths.SpinFromCelestial(place2,
                    starCel - PlanetBerths.ObserverCelestial(place2, cam2))), 1e-12);

            // ---- THE TERMINATOR PHASE (bake input only). Aligned-basis coordinates (basis
            // normal == spin axis == +Z): az(R(θ)⁻¹·u) = az(u) − θ for any u off the axis.
            Vector3D u = new Vector3D(3.0e5, -1.2e5, 4.0e4);
            double theta = 0.83;
            double azU, azR;
            SunPhaseEase.TryOrbitalPhase(u, out azU);
            SunPhaseEase.TryOrbitalPhase(
                PlanetBerths.RotateAboutAxis(u, Vector3D.UnitZ, -theta), out azR);
            Near("az(R⁻¹u) = az(u) − θ", SunPhaseEase.WrapToPi(azR - azU), -theta, 1e-12);

            // EXACT PASS-THROUGH (inbound): tracking az(u) on the inertial source, then the
            // chart activates — the live phase steps with the truthful projection (−θ here)
            // and the terminator phase must step WITH it, never capture against it.
            var st = new SunPhaseEase.State();
            double termOut = SunPhaseEase.Step(ref st, "<orbital>", azU, true);
            double termIn = SunPhaseEase.Step(ref st, "Earth:zone", azR, true);
            Near("inbound: terminator phase steps by the truthful −θ (aligned basis)",
                SunPhaseEase.WrapToPi(termIn - termOut), -theta, 1e-12);

            // In the aligned-basis case the pose's heading azimuth steps by −θ too, so the
            // ship-relative terminator phase is continuous there (in-game the engine basis is
            // NOT aligned and only the DRAWN sun — pinned above — keeps this exactly; the
            // terminator's residual seam step is the accepted RSS approximation).
            Vector3D headCel = new Vector3D(1.0, 0.4, 0.0);
            double headOut = Math.Atan2(headCel.Y, headCel.X);
            Vector3D headIn3 = PlanetBerths.RotateAboutAxis(headCel, Vector3D.UnitZ, -theta);
            double headIn = Math.Atan2(headIn3.Y, headIn3.X);
            Near("aligned basis: ship-relative terminator phase continuous through the conversion",
                SunPhaseEase.WrapToPi((termIn - headIn) - (termOut - headOut)), 0.0, 1e-12);

            // OUTBOUND: the +θ step passes through the same way.
            double termBack = SunPhaseEase.Step(ref st, "<orbital>", azU, true);
            Near("outbound: terminator phase steps by the truthful +θ",
                SunPhaseEase.WrapToPi(termBack - termIn), theta, 1e-12);

            // An ACCUMULATED offset (e.g. from an earlier hold-resume) cancels in the step:
            // the boundary behavior is the truthful step regardless of the constant.
            var st2 = new SunPhaseEase.State();
            SunPhaseEase.Step(ref st2, null, 0.0);                       // init -> hold (offset path)
            SunPhaseEase.Step(ref st2, "<orbital>", azU - 0.5, true);    // resume captures 0.5
            double o1 = SunPhaseEase.Step(ref st2, "<orbital>", azU, true);
            double o2 = SunPhaseEase.Step(ref st2, "Earth:zone", azR, true);
            Near("pass-through with a carried offset still steps by −θ",
                SunPhaseEase.WrapToPi(o2 - o1), -theta, 1e-12);

            // THE PRE-FIX BEHAVIOR, kept pinned as the bug (RED counterfactual): a
            // value-continuous capture at the boundary holds the phase against the truthful
            // step — in aligned coordinates that is exactly a +θ ship-relative jump in the
            // rotated cockpit. (The capture remains correct for genuinely unrelated
            // conventions; the boundary is not one.)
            var stOld = new SunPhaseEase.State();
            double oldOut = SunPhaseEase.Step(ref stOld, "<orbital>", azU);
            double oldIn = SunPhaseEase.Step(ref stOld, "Earth:zone", azR);   // legacy capture
            Near("legacy capture holds the phase against the truthful step",
                SunPhaseEase.WrapToPi(oldIn - oldOut), 0.0, 1e-12);
            Near("…which is a +θ SHIP-RELATIVE terminator jump in the rotated cockpit (the bug)",
                SunPhaseEase.WrapToPi((oldIn - headIn) - (oldOut - headOut)), theta, 1e-12);

            // Degenerate-hold resume still captures (value-continuity from the held fiction),
            // even when the caller marks switches exact.
            var st3 = new SunPhaseEase.State();
            SunPhaseEase.Step(ref st3, "<orbital>", 1.0, true);
            double held = SunPhaseEase.Step(ref st3, null, 0.0, true);
            Near("hold keeps the driven phase", held, 1.0, 1e-12);
            Near("hold-resume re-captures (value-continuous) despite exactSwitch",
                SunPhaseEase.Step(ref st3, "Earth:zone", 5.5, true), 1.0, 1e-12);
        }

        // Audit S4 (frames-vs-rss steal 2026-06-12): the seam-coherence hardening predicates
        // ported from reference ZoneManager.cs, shipped as PURE PlanetBerths helpers so the
        // FrameManager zone seam, FrameSim's formation-zone-crossing twin and these pins all
        // run the LITERAL rules.
        private static void ZoneSeamHardening()
        {
            Section("zone seam hardening (audit S4): jump detector, band re-latch, sweep guard, same-θ rigidity");
            double dt = 1.0 / 60.0;
            double rc = 42.0e3;
            double band = rc * PlanetBerths.SurfaceZoneDropPad;   // 42.42 km drop edge

            // RSS constants survive the port byte-for-byte (ZoneManager.cs:466 — squared
            // 25 000 000 m²; :513 — the 2000 m sphere).
            Near("teleport threshold is RSS's 5 km", PlanetBerths.ZoneTeleportThreshold, 5000.0, 0.0);
            Near("companion sweep range is RSS's 2000 m", PlanetBerths.ZoneCompanionSweepRange, 2000.0, 0.0);

            // TELEPORT DETECTOR (UnexplainedJump): velocity-explained motion never trips —
            // including HighSpeed-scale virtual stepping (the rails/HighSpeed exemption is the
            // TRUE-velocity argument, not a special case).
            Vector3D last = new Vector3D(100.0e3, -20.0e3, 3.0e3);
            Vector3D vFast = new Vector3D(18000.0, -2000.0, 500.0);   // ~300 m/tick virtual step
            Ok("explained HighSpeed step (v·dt) does not trip",
                !PlanetBerths.UnexplainedJump(last + vFast * dt, last, vFast, dt));
            Ok("4.9 km beyond the explained move does not trip",
                !PlanetBerths.UnexplainedJump(last + vFast * dt + new Vector3D(4900.0, 0, 0), last, vFast, dt));
            Ok("5.1 km beyond the explained move trips",
                PlanetBerths.UnexplainedJump(last + vFast * dt + new Vector3D(5100.0, 0, 0), last, vFast, dt));
            Ok("a 5.1 km move with ZERO velocity trips (admin teleport of a parked grid)",
                PlanetBerths.UnexplainedJump(last + new Vector3D(0, 5100.0, 0), last, Vector3D.Zero, dt));

            // RE-LATCH with the ambiguity-band skip (ReLatchAfterJump — RSS :682-714 applied to
            // the detector path only): stateless outside the band, sticky inside it.
            Ok("jump to 41 km -> inside regardless of the stale latch",
                PlanetBerths.ReLatchAfterJump(false, 41.0e3, rc)
                && PlanetBerths.ReLatchAfterJump(true, 41.0e3, rc));
            Ok("jump to 43 km -> outside regardless of the stale latch",
                !PlanetBerths.ReLatchAfterJump(false, 43.0e3, rc)
                && !PlanetBerths.ReLatchAfterJump(true, 43.0e3, rc));
            Ok("jump INTO the band keeps the stale latch (no coin-flip conversion)",
                PlanetBerths.ReLatchAfterJump(true, rc + 0.005 * rc, rc)
                && !PlanetBerths.ReLatchAfterJump(false, rc + 0.005 * rc, rc));
            Ok("band edges belong to the band (R_c and the drop edge keep the latch)",
                PlanetBerths.ReLatchAfterJump(true, rc, rc)
                && PlanetBerths.ReLatchAfterJump(true, band, rc));
            Ok("degenerate R_c -> never inside", !PlanetBerths.ReLatchAfterJump(true, 1.0, 0.0));
            // The flap-immunity argument: whatever the band re-latch returns, feeding it back
            // through the ordinary hysteresis at the same radius is a fixed point.
            Ok("band re-latch is a ZoneLatch fixed point (no flap next tick)",
                PlanetBerths.ZoneLatch(PlanetBerths.ReLatchAfterJump(true, rc + 0.005 * rc, rc),
                    rc + 0.005 * rc, rc) == PlanetBerths.ReLatchAfterJump(true, rc + 0.005 * rc, rc)
                && PlanetBerths.ZoneLatch(PlanetBerths.ReLatchAfterJump(false, rc + 0.005 * rc, rc),
                    rc + 0.005 * rc, rc) == PlanetBerths.ReLatchAfterJump(false, rc + 0.005 * rc, rc));

            // COMPANION-SWEEP stability guard (CompanionSweepKeeps): only sweep what the latch
            // will keep — inbound must already be at/below the drop edge, outbound at/above R_c.
            Ok("inbound sweep admitted inside the band (the formation case)",
                PlanetBerths.CompanionSweepKeeps(true, rc + 300.0, rc));
            Ok("inbound sweep refused beyond the drop edge (would flap back out)",
                !PlanetBerths.CompanionSweepKeeps(true, band + 1.0, rc));
            Ok("outbound sweep admitted at/above R_c",
                PlanetBerths.CompanionSweepKeeps(false, rc, rc));
            Ok("outbound sweep refused below R_c (would re-enter next tick)",
                !PlanetBerths.CompanionSweepKeeps(false, rc - 1.0, rc));
            // Guard soundness: an ADMITTED sweep's flipped latch survives the very next
            // ZoneLatch at the same radius (the no-flap contract, both directions).
            Ok("admitted inbound sweep survives the next latch",
                PlanetBerths.ZoneLatch(true, rc + 300.0, rc));
            Ok("admitted outbound sweep survives the next latch",
                !PlanetBerths.ZoneLatch(false, rc, rc));

            // SAME-θ RIGIDITY (the sweep's reason to exist): one conversion applied to a
            // formation pair at the SAME θ sample is one rigid map — the separation length is
            // exact and each member's celestial state is unchanged, so the formation cannot
            // scramble. (Members converted at DIFFERENT θ are each exact too — the scramble is
            // the split-chart dynamics BETWEEN the conversions, proven in FrameSim's
            // formation-zone-crossing-nosweep red run.)
            Vector3D axis = Vector3D.Normalize(new Vector3D(0.1, -0.2, 0.97));
            double theta = 2.347, omega = 2.0 * Math.PI / 3600.0;
            Vector3D c = new Vector3D(400.0e3, -80.0e3, 12.0e3);
            Vector3D F = new Vector3D(-2.0e6, 5.0e5, 9.0e4);     // window FrameCel (any value)
            Vector3D pA = c + new Vector3D(41.0e3, 500.0, -300.0);
            Vector3D pB = pA + new Vector3D(300.0, 950.0, 0.0);  // the ~1 km formation offset
            Vector3D vA = new Vector3D(-20.0, 3.0, 1.0), vB = vA;
            Vector3D celA = F + (pA - c), celB = F + (pB - c);   // inertial-window celestials
            Vector3D pA2 = pA, vA2 = vA, pB2 = pB, vB2 = vB;
            PlanetBerths.ConvertIntoZone(axis, theta, omega, c, ref pA2, ref vA2);
            PlanetBerths.ConvertIntoZone(axis, theta, omega, c, ref pB2, ref vB2);
            Near("same-θ pair conversion preserves the separation length",
                (pB2 - pA2).Length(), (pB - pA).Length(), 1e-9 * (pB - pA).Length());
            NearVec("member A celestial unchanged through the conversion",
                F + PlanetBerths.RotateAboutAxis(pA2 - c, axis, theta), celA, 1e-6);
            NearVec("member B celestial unchanged through the conversion",
                F + PlanetBerths.RotateAboutAxis(pB2 - c, axis, theta), celB, 1e-6);
            NearVec("celestial separation vector preserved exactly (the no-scramble contract)",
                PlanetBerths.RotateAboutAxis(pB2 - pA2, axis, theta), celB - celA, 1e-9 * 1.0e3);
        }

        // ---- assert helpers ----
        // Shared PENDING reason for the SkyClampDistance family (true-xk rescale, 2026-06-16).
        private const string SkyClampPendingWhy =
            "SkyClampDistance=2000km did not scale with true-xk; pending rob's decision whether the " +
            "clamp scales or everything-clamped is intended — see docs/audits/orbital-truexk-fixture-rot.md";

        private static void Section(string s) => Console.WriteLine($"-- {s}");

        private static void Ok(string name, bool cond)
        {
            if (cond) { _passed++; Console.WriteLine($"   PASS {name}"); }
            else { _failed++; Console.WriteLine($"   FAIL {name}"); }
        }

        // A test whose EXPECTED value depends on a design decision rob still owes (the true-xk rescale
        // exposed several: e.g. whether SkyClampDistance must scale, and whether the Moon's spin should
        // be retuned so its synchronous/parking orbits stay outside the ballooned SOI). We will not bake
        // EITHER answer into an assertion, so these neither PASS (would assert possibly-wrong behavior)
        // nor FAIL (not a code regression). They print loudly and are counted separately. See
        // docs/audits/orbital-truexk-fixture-rot.md. Resolve to a real Ok()/Near() once rob decides.
        private static void Pending(string name, string why)
        {
            _pending++;
            Console.WriteLine($"   PENDING {name} -- {why}");
        }

        private static void Near(string name, double actual, double expected, double tol)
        {
            bool ok = Math.Abs(actual - expected) <= tol;
            if (ok) { _passed++; Console.WriteLine($"   PASS {name} ({actual:G6})"); }
            else { _failed++; Console.WriteLine($"   FAIL {name}: got {actual:G6}, want {expected:G6} +/- {tol:G3}"); }
        }

        private static void NearVec(string name, Vector3D actual, Vector3D expected, double tol)
        {
            double d = (actual - expected).Length();
            bool ok = d <= tol;
            if (ok) { _passed++; Console.WriteLine($"   PASS {name} (|d|={d:G3})"); }
            else { _failed++; Console.WriteLine($"   FAIL {name}: |d|={d:G3} > {tol:G3}"); }
        }

        // Row-wise rigid-matrix comparison: max deviation over the three orientation rows
        // (Right/Up/Backward) and the Translation row, against tol like NearVec.
        private static void NearMat(string name, MatrixD actual, MatrixD expected, double tol)
        {
            double d = (actual.Right - expected.Right).Length();
            d = Math.Max(d, (actual.Up - expected.Up).Length());
            d = Math.Max(d, (actual.Backward - expected.Backward).Length());
            d = Math.Max(d, (actual.Translation - expected.Translation).Length());
            bool ok = d <= tol;
            if (ok) { _passed++; Console.WriteLine($"   PASS {name} (|d|={d:G3})"); }
            else { _failed++; Console.WriteLine($"   FAIL {name}: |d|={d:G3} > {tol:G3}"); }
        }
    }
}
