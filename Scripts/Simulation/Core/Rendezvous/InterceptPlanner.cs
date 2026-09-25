using System;
using SEAerospace.Orbital;
using SEAerospace.Frames;

namespace SEAerospace.Rendezvous
{
    /// <summary>
    /// Options controlling the intercept sweep. All have sane defaults; a caller can
    /// override the arrival-time window or ask for a velocity-matching arrival burn.
    /// </summary>
    /// <summary>What the arrival-time sweep optimizes for. Surfaced to the player as
    /// "fastest" vs "most efficient" (the porkchop trade, reduced to named options).</summary>
    public enum InterceptObjective
    {
        MinDeltaV,   // cheapest transfer in the window (efficient; slower)
        Fastest      // earliest feasible arrival (aggressive; pricier)
    }

    public class InterceptOptions
    {
        /// <summary>Fastest (earliest arrival) vs MinDeltaV (cheapest). Default cheapest.</summary>
        public InterceptObjective Objective = InterceptObjective.MinDeltaV;

        /// <summary>Δv budget cap (m/s); a candidate costing more is rejected. 0 = no cap.
        /// Keeps "fastest" from picking an absurd near-zero-tof burn just to shave seconds.</summary>
        public double MaxDeltaVMps = 0.0;

        /// <summary>Earliest time-of-flight to consider, as a fraction of the target period
        /// (or a wall-clock floor for hyperbolic targets). Keeps Lambert away from the
        /// near-zero-tof singularity.</summary>
        public double MinTofSeconds = 60.0;

        /// <summary>Latest arrival, as a horizon in seconds from "now". Defaults to ~1.5
        /// target periods if the target is elliptic (set via Plan when 0).</summary>
        public double HorizonSeconds = 0.0;

        /// <summary>Number of arrival-time samples swept across [now+minTof, now+horizon].</summary>
        public int ArrivalSamples = 240;

        /// <summary>If true, append a second burn at arrival that nulls the relative
        /// velocity (full rendezvous). If false, a single departure burn (flyby/intercept)
        /// — the auto-merge handles the rest. Δv ranking includes the arrival burn when on.</summary>
        public bool MatchArrivalVelocity = false;

        /// <summary>Try a phasing plan and pick it if cheaper than the best direct Lambert.</summary>
        public bool ConsiderPhasing = true;

        /// <summary>Max laps the phasing solver may drift before the matching burn.</summary>
        public int MaxPhasingRevs = 5;
    }

    /// <summary>
    /// The unified "plot intercept" entry point. Given my current state, the target's
    /// orbit, mu, the current time, and the capture thresholds, returns a ManeuverPlan:
    /// a departure burn (optionally a matching arrival burn) plus an honest arrival
    /// prediction. The player never reasons about Lambert or phasing — they get an
    /// executable burn and "arrive HERE in T, this close, at this relative speed".
    ///
    /// Strategy:
    ///   DIRECT  — sweep candidate arrival times; for each, solve Lambert(myPos,
    ///             targetPos(t_arr), tof) both short- and long-way; the departure burn is
    ///             v1 - myVel, the arrival rel-velocity is v2 - targetVel(t_arr). Score by
    ///             total Δv and keep the cheapest that arrives within EnterRangeMeters
    ///             (Lambert hits the point exactly, so the real selection is min Δv).
    ///   PHASING — when my orbit and the target's are nearly the same (a direct Lambert
    ///             across the orbit is wildly expensive), instead drop/raise into a phasing
    ///             orbit, drift N laps to close the phase angle, then circularize back.
    ///             Chosen over direct when its total Δv is lower.
    ///
    /// Pure math; the only "game" types are KeplerianElements/StateVector (orbital core)
    /// and RendezvousParams (capture thresholds). C# 6 throughout.
    /// </summary>
    public static class InterceptPlanner
    {
        /// <summary>
        /// Plan an intercept from my state at time <paramref name="now"/> to the target
        /// orbit. <paramref name="caps"/> supplies EnterRangeMeters / EnterRelSpeedMps (the
        /// forgiving capture goal). Never throws — returns a NoSolution plan on failure.
        /// </summary>
        public static ManeuverPlan Plan(StateVector myState, KeplerianElements targetOrbit,
            double mu, double now, RendezvousParams caps, InterceptOptions options)
        {
            if (caps == null) caps = RendezvousParams.Default;
            if (options == null) options = new InterceptOptions();

            // My current osculating orbit (epoch = now) for propagation / phasing.
            KeplerianElements myOrbit = OrbitalMath.ToElements(myState, mu, now);

            double horizon = options.HorizonSeconds;
            if (horizon <= 0.0)
            {
                // Default: ~1.5 target periods (elliptic) so we always span at least one
                // full synodic opportunity; a wall-clock fallback for hyperbolic targets.
                horizon = targetOrbit.IsElliptic ? 1.5 * targetOrbit.Period : 6.0 * 3600.0;
            }

            ManeuverPlan direct = PlanDirect(myState, myOrbit, targetOrbit, mu, now, caps, options, horizon);

            ManeuverPlan phasing = null;
            ManeuverPlan transferPhase = null;
            if (options.ConsiderPhasing && myOrbit.IsElliptic && targetOrbit.IsElliptic)
            {
                phasing = PlanPhasing(myState, myOrbit, targetOrbit, mu, now, caps, options);
                // Decoupled cheap path for DIFFERENT orbits: Hohmann to the target's orbit,
                // then phase. Beats a single direct Lambert (which overpays to hit a timing).
                transferPhase = PlanTransferPhase(myState, myOrbit, targetOrbit, mu, now, caps, options);
            }

            // Pick the cheaper feasible plan.
            ManeuverPlan best = null;
            if (direct != null && direct.Status == PlanStatus.Ok) best = direct;
            if (phasing != null && phasing.Status == PlanStatus.Ok)
            {
                if (best == null || phasing.TotalDeltaV < best.TotalDeltaV) best = phasing;
            }
            if (transferPhase != null && transferPhase.Status == PlanStatus.Ok)
            {
                if (best == null || transferPhase.TotalDeltaV < best.TotalDeltaV) best = transferPhase;
            }

            if (best == null)
                return ManeuverPlan.NoSolution("No feasible intercept found in the search window.");
            return best;
        }

        /// <summary>Convenience overload with default options.</summary>
        public static ManeuverPlan Plan(StateVector myState, KeplerianElements targetOrbit,
            double mu, double now, RendezvousParams caps)
        {
            return Plan(myState, targetOrbit, mu, now, caps, new InterceptOptions());
        }

        /// <summary>
        /// Both options to show the player: FASTEST (earliest arrival you can AFFORD) and
        /// EFFICIENT (cheapest Δv, slower) — the porkchop trade as two named choices.
        /// "Fastest" is meaningless without a Δv budget (the cheapest fast transfer needs
        /// absurd Δv), so it is capped: pass <paramref name="maxDeltaVBudget"/> = the ship's
        /// available Δv, or 0 to auto-derive ~4× the efficient cost. The efficient sweep
        /// uses a longer horizon + finer sampling so the cheap (Hohmann/phasing-ish) window
        /// is in range. Either may come back NoSolution.
        /// </summary>
        public static void PlanChoices(StateVector myState, KeplerianElements targetOrbit,
            double mu, double now, RendezvousParams caps, bool matchArrivalVelocity,
            double maxDeltaVBudget, out ManeuverPlan fastest, out ManeuverPlan efficient)
        {
            // Efficient first — its cost anchors a sane "fastest you can afford" cap.
            InterceptOptions eff = new InterceptOptions();
            eff.Objective = InterceptObjective.MinDeltaV;
            eff.MatchArrivalVelocity = matchArrivalVelocity;
            if (targetOrbit.IsElliptic) eff.HorizonSeconds = 4.0 * targetOrbit.Period;
            eff.ArrivalSamples = 480;   // finer sweep to resolve the cheap window
            eff.MaxPhasingRevs = 24;    // many cheap laps allowed -> transfer+phase nears pure Hohmann
            efficient = Plan(myState, targetOrbit, mu, now, caps, eff);

            double cap = maxDeltaVBudget;
            if (cap <= 0.0 && efficient.Status == PlanStatus.Ok)
                cap = 4.0 * efficient.TotalDeltaV;   // afford up to 4x the cheapest to go sooner

            InterceptOptions fast = new InterceptOptions();
            fast.Objective = InterceptObjective.Fastest;
            fast.MatchArrivalVelocity = matchArrivalVelocity;
            fast.MaxDeltaVMps = cap;
            // Exclude the phasing/transfer-phase branches from the FASTEST sweep. Those branches always
            // minimize Δv (and ignore Objective + MaxDeltaVMps), so Plan()'s cheaper-wins selection could
            // return a cheap multi-lap PHASING plan — which by construction arrives LATER — mislabeled as
            // "fastest", and the rich readout would then silently drop the fast line (it requires fast to
            // beat efficient by 10%). Phasing is the slow-but-cheap path and can never legitimately be the
            // fastest, so dropping it here loses nothing and makes fast a pure earliest-arrival direct sweep.
            fast.ConsiderPhasing = false;
            fastest = Plan(myState, targetOrbit, mu, now, caps, fast);
        }

        // ---- DIRECT Lambert sweep ---------------------------------------------------

        private static ManeuverPlan PlanDirect(StateVector myState, KeplerianElements myOrbit,
            KeplerianElements targetOrbit, double mu, double now, RendezvousParams caps,
            InterceptOptions options, double horizon)
        {
            Vector3D r1 = myState.Position;
            Vector3D myVel = myState.Velocity;

            int n = options.ArrivalSamples;
            if (n < 8) n = 8;
            double tofMin = options.MinTofSeconds;
            if (tofMin < 1.0) tofMin = 1.0;
            if (horizon <= tofMin) return null;

            double span = horizon - tofMin;
            double bestCost = double.PositiveInfinity;   // Δv of the chosen candidate
            double bestScore = double.PositiveInfinity;  // objective score we minimize
            Maneuver bestDepart = new Maneuver();
            Maneuver bestArrive = new Maneuver();
            bool haveArriveBurn = false;
            ArrivalPrediction bestArrival = new ArrivalPrediction();
            bool found = false;

            for (int i = 0; i <= n; i++)
            {
                double tof = tofMin + span * (i / (double)n);
                double tArr = now + tof;
                StateVector sTarget = OrbitPropagation.StateAt(targetOrbit, tArr);
                Vector3D r2 = sTarget.Position;

                // Try both transfer senses; keep the best by the chosen objective.
                EvaluateLambert(r1, r2, tof, mu, true, myVel, sTarget.Velocity, now, tArr,
                    options, caps, ref bestCost, ref bestScore, ref bestDepart, ref bestArrive,
                    ref haveArriveBurn, ref bestArrival, ref found);
                EvaluateLambert(r1, r2, tof, mu, false, myVel, sTarget.Velocity, now, tArr,
                    options, caps, ref bestCost, ref bestScore, ref bestDepart, ref bestArrive,
                    ref haveArriveBurn, ref bestArrival, ref found);
            }

            if (!found) return null;

            ManeuverPlan plan = new ManeuverPlan();
            plan.Status = PlanStatus.Ok;
            plan.Kind = PlanKind.Direct;
            plan.Maneuvers.Add(bestDepart);
            if (haveArriveBurn) plan.Maneuvers.Add(bestArrive);
            plan.Arrival = bestArrival;
            plan.TotalDeltaV = bestCost;
            plan.Note = options.MatchArrivalVelocity
                ? "Direct Lambert intercept with arrival velocity match (full rendezvous)."
                : "Direct Lambert intercept (single departure burn; auto-merge finishes).";
            return plan;
        }

        // One Lambert candidate; updates the running best if cheaper. Out-params packed to
        // stay C# 6 (no tuples).
        private static void EvaluateLambert(Vector3D r1, Vector3D r2, double tof, double mu,
            bool prograde, Vector3D myVel, Vector3D targetVel, double now, double tArr,
            InterceptOptions options, RendezvousParams caps,
            ref double bestCost, ref double bestScore, ref Maneuver bestDepart, ref Maneuver bestArrive,
            ref bool haveArriveBurn, ref ArrivalPrediction bestArrival, ref bool found)
        {
            Vector3D v1, v2;
            if (!Lambert.Solve(r1, r2, tof, mu, prograde, out v1, out v2)) return;

            Vector3D departDv = v1 - myVel;
            Vector3D arrivalRelVel = v2 - targetVel; // before any matching burn
            double cost = departDv.Length();
            Vector3D arriveDv = Vector3D.Zero;
            if (options.MatchArrivalVelocity)
            {
                arriveDv = targetVel - v2; // null the relative velocity at arrival
                cost += arriveDv.Length();
            }

            // Δv budget gate (keeps "fastest" sane).
            if (options.MaxDeltaVMps > 0.0 && cost > options.MaxDeltaVMps) return;

            // C1 defense-in-depth: do NOT trust "Lambert targets the exact point" — actually FLY
            // the candidate. Build the transfer's osculating elements from (r1, solved v1, mu)
            // at epoch = now, then propagate to tArr; StateAt advances by (tArr - now) == tof, the
            // SAME time base the planner/solver uses (now is the departure epoch, tArr the arrival
            // time). The flown miss is the distance from that propagated position to the intended
            // target position r2. For benign transfers this is ~0 (existing tests unaffected); for
            // a near-parabolic / numerically-degenerate transfer that slipped Lambert's own gates
            // it is large, so we reject it here instead of shipping a "0 m miss" that flies off.
            KeplerianElements transferEl = OrbitalMath.ToElements(new StateVector(r1, v1), mu, now);
            StateVector flownArr = OrbitPropagation.StateAt(transferEl, tArr);
            Vector3D flownPos = flownArr.Position;
            if (double.IsNaN(flownPos.X) || double.IsNaN(flownPos.Y) || double.IsNaN(flownPos.Z)
                || double.IsInfinity(flownPos.X) || double.IsInfinity(flownPos.Y) || double.IsInfinity(flownPos.Z))
                return;
            double flownMiss = (flownPos - r2).Length();
            // Reject by the FLOWN miss against the acceptance gate: a transfer whose actual flown
            // arrival is outside capture range is not a real intercept, regardless of what the
            // closed-form solve "promised".
            if (flownMiss > caps.EnterRangeMeters) return;

            // Score by objective: Fastest minimizes arrival time, MinDeltaV minimizes Δv.
            double score = options.Objective == InterceptObjective.Fastest ? tof : cost;
            if (score >= bestScore) return;
            bestScore = score;

            bestCost = cost;
            found = true;

            Maneuver depart = new Maneuver(0.0, departDv);
            FillComponents(ref depart, r1, myVel);
            bestDepart = depart;

            if (options.MatchArrivalVelocity)
            {
                Maneuver arrive = new Maneuver(tArr - now, arriveDv);
                FillComponents(ref arrive, r2, v2);
                bestArrive = arrive;
                haveArriveBurn = true;
            }
            else haveArriveBurn = false;

            ArrivalPrediction pred = new ArrivalPrediction();
            pred.ArrivalTime = tArr;
            pred.TimeFromNowSeconds = tArr - now;
            pred.MissDistance = flownMiss; // C1: the ACTUAL flown miss, not an assumed 0
            pred.RelativeVelocity = options.MatchArrivalVelocity ? Vector3D.Zero : arrivalRelVel;
            pred.RelativeSpeed = pred.RelativeVelocity.Length();
            bestArrival = pred;
        }

        // ---- TRANSFER + PHASE (different orbits, the cheap general path) ------------

        // Decouple "change orbit" from "match phase": Hohmann from my orbit to the
        // target's orbit (cheap), arriving ON the target's track at some phase offset,
        // then hand off to the phasing solver to close the angle. Much cheaper than a
        // single direct Lambert, which overpays to also hit a specific arrival time.
        // v1 scope: near-circular, COPLANAR orbits (the common rendezvous case). Inclined
        // (combined plane change at the node) is left to direct Lambert for now.
        private static ManeuverPlan PlanTransferPhase(StateVector myState, KeplerianElements myOrbit,
            KeplerianElements targetOrbit, double mu, double now, RendezvousParams caps,
            InterceptOptions options)
        {
            if (myOrbit.Eccentricity > 0.05 || targetOrbit.Eccentricity > 0.05) return null;

            StateVector st = OrbitalMath.ToState(targetOrbit);
            Vector3D nMe = Vector3D.Cross(myState.Position, myState.Velocity);
            Vector3D nTg = Vector3D.Cross(st.Position, st.Velocity);
            if (nMe.LengthSquared() < 1e-6 || nTg.LengthSquared() < 1e-6) return null;
            double planeCos = Vector3D.Dot(Vector3D.Normalize(nMe), Vector3D.Normalize(nTg));
            if (planeCos < 0.999) return null; // not coplanar -> skip (direct Lambert handles it)

            double r1 = myState.Radius;
            double r2 = targetOrbit.SemiMajorAxis;
            if (r2 <= 0.0) return null;
            if (Math.Abs(r1 - r2) / r2 < 0.005) return null; // already co-orbital -> PlanPhasing

            double v1 = myState.Speed;
            double at = 0.5 * (r1 + r2);
            double vp = Math.Sqrt(mu * (2.0 / r1 - 1.0 / at));
            double v2c = Math.Sqrt(mu / r2);
            double tof = Math.PI * Math.Sqrt(at * at * at / mu);

            // 1) departure burn (tangential): raise/lower into the transfer ellipse.
            Vector3D dir1 = Vector3D.Normalize(myState.Velocity);
            Vector3D departDv = dir1 * (vp - v1);
            var afterDepart = new StateVector(myState.Position, myState.Velocity + departDv);
            KeplerianElements transferOrbit = OrbitalMath.ToElements(afterDepart, mu, now);

            // 2) arrival + circularize (tangential): now I'm on the target's circular track.
            StateVector atArr = OrbitPropagation.StateAt(transferOrbit, now + tof);
            Vector3D dirArr = Vector3D.Normalize(atArr.Velocity);
            Vector3D circDv = dirArr * (v2c - atArr.Speed);
            var onOrbit = new StateVector(atArr.Position, atArr.Velocity + circDv);

            // 3) phase from the now-matched orbit to the target (reuses the phasing solver).
            KeplerianElements onOrbitEl = OrbitalMath.ToElements(onOrbit, mu, now + tof);
            ManeuverPlan ph = PlanPhasing(onOrbit, onOrbitEl, targetOrbit, mu, now + tof, caps, options);
            if (ph == null || ph.Status != PlanStatus.Ok) return null;

            ManeuverPlan plan = new ManeuverPlan();
            plan.Status = PlanStatus.Ok;
            plan.Kind = PlanKind.TransferPhase;

            Maneuver md = new Maneuver(0.0, departDv); FillComponents(ref md, myState.Position, myState.Velocity);
            plan.Maneuvers.Add(md);
            Maneuver mc = new Maneuver(tof, circDv); FillComponents(ref mc, atArr.Position, atArr.Velocity);
            plan.Maneuvers.Add(mc);
            for (int i = 0; i < ph.Maneuvers.Count; i++)
            {
                Maneuver pm = ph.Maneuvers[i];
                pm.TimeFromNowSeconds += tof; // phasing happens AFTER the transfer
                plan.Maneuvers.Add(pm);
            }
            plan.TotalDeltaV = departDv.Length() + circDv.Length() + ph.TotalDeltaV;

            ArrivalPrediction pred = ph.Arrival;
            pred.TimeFromNowSeconds += tof; // arrival relative to the original "now"
            plan.Arrival = pred;            // ArrivalTime is already absolute
            plan.Note = "Transfer (Hohmann) to the target's orbit, then phase to the target.";
            return plan;
        }

        // ---- PHASING (similar-orbit catch-up) ---------------------------------------

        // When my orbit ~ target's orbit, a direct Lambert across the shared track is
        // absurd. Instead: stay on (essentially) the same orbit but change my PERIOD so I
        // drift relative to the target, close the along-track phase angle over N laps,
        // then restore my period with a matching burn. We model this at the point I am now
        // (treating it as the maneuver point): a single tangential Δv sets a phasing period
        // T_phase such that after N of my phasing laps the target has come back to my
        // position; a second equal-and-opposite tangential Δv circularizes me onto the
        // target's orbit (rendezvous).
        private static ManeuverPlan PlanPhasing(StateVector myState, KeplerianElements myOrbit,
            KeplerianElements targetOrbit, double mu, double now, RendezvousParams caps,
            InterceptOptions options)
        {
            // Only sensible when the orbits are genuinely similar (size + shape + plane).
            double aMe = myOrbit.SemiMajorAxis;
            double aTg = targetOrbit.SemiMajorAxis;
            if (aMe <= 0.0 || aTg <= 0.0) return null;
            double aRel = Math.Abs(aMe - aTg) / aTg;
            double planeCos = Math.Cos(myOrbit.Inclination) * Math.Cos(targetOrbit.Inclination)
                + Math.Sin(myOrbit.Inclination) * Math.Sin(targetOrbit.Inclination)
                  * Math.Cos(myOrbit.Raan - targetOrbit.Raan);
            // require <2% sma difference, low eccentricity, near-coplanar
            if (aRel > 0.02) return null;
            if (myOrbit.Eccentricity > 0.02 || targetOrbit.Eccentricity > 0.02) return null;
            if (planeCos < 0.999) return null;

            double r = myState.Radius;
            double vCirc = myState.Speed;
            double Ttarget = targetOrbit.Period;

            // Along-track phase angle to close: angular position of target minus mine,
            // measured in my orbit plane (positive = target is ahead of me). For near-
            // circular coplanar orbits the argument of latitude difference is the phase.
            StateVector sTg = OrbitPropagation.StateAt(targetOrbit, now);
            double phase = SignedPhaseAngle(myState.Position, sTg.Position, myState.Velocity);
            double dphi = Wrap(phase); // (-pi, pi]: pick the cheaper closing sense

            // M3 fix — base the phasing period on the RELATIVE mean-motion closure, and
            // SELECT N by the TRUE along-track residual, not the target period alone.
            //
            // The old model used T_phase = T_target*(1 - dphi/(2piN)), which closes the gap
            // only against the TARGET'S rate and tacitly assumes my baseline mean motion
            // n_me == n_target. With |Δa|/a up to the 2% gate, n_me differs from n_target by
            // ~3%; before the phasing burn even starts (and the closure is referenced from my
            // current osculating state) that mismatch means the naive N did not minimize the
            // real closure. The honest closing rate is the RELATIVE mean motion: I must change
            // my mean motion to n_phase so that, drifting for t = N*T_phase, the relative
            // along-track angle (n_phase - n_target)*t nulls the lead dphi:
            //     (n_phase - n_target) * (N * T_phase) = -dphi - 2pi*k
            // with n_phase = 2pi/T_phase this gives T_phase = (2pi*N + dphi)/(N*n_target)
            // referenced to the target's rate, identical algebra to the old form but now we
            // also RE-PROPAGATE each candidate against the actual target orbit (relative
            // motion, not the circular approximation) and choose the N whose true ALONG-TRACK
            // residual is smallest. Δv breaks near-ties. NOTE (verified offline): in the
            // strict in-gate regime the relative-rate Tphase nulls the along-track residual
            // for EVERY feasible N, so this coincides with the old Δv-only pick there; the
            // change keeps the model honest and robust if the gate is ever widened.
            double bestCost = double.PositiveInfinity;
            double bestAlong = double.PositiveInfinity;   // true re-propagated along-track residual (m)
            int bestRevs = 0;
            double bestTphase = 0.0;

            int maxRevs = options.MaxPhasingRevs;
            if (maxRevs < 1) maxRevs = 1;
            for (int N = 1; N <= maxRevs; N++)
            {
                // I do N laps on the phasing orbit and return to my reference point at
                // t = N*Tphase. For the target to be AT that point then, it must sweep
                // (2piN - dphi) in that time: n_target * (N*Tphase) = 2piN - dphi.
                double Tphase = Ttarget * (1.0 - dphi / (2.0 * Math.PI * N));
                if (Tphase <= 0.0) continue;

                // Semi-major axis of the phasing orbit from its period.
                double aPhase = Math.Pow(mu * (Tphase / (2.0 * Math.PI)) * (Tphase / (2.0 * Math.PI)), 1.0 / 3.0);
                // The phasing orbit must still clear the body and not be absurd.
                if (aPhase <= 0.0) continue;
                // Speed needed at my current radius on the phasing ellipse (vis-viva).
                double vPhaseSq = mu * (2.0 / r - 1.0 / aPhase);
                if (vPhaseSq <= 0.0) continue;
                double vPhase = Math.Sqrt(vPhaseSq);

                // Tangential burn to enter, and an equal-magnitude burn to leave (back to
                // circular). Both along the velocity direction.
                double dvEnter = Math.Abs(vPhase - vCirc);
                double dvExit = Math.Abs(vCirc - vPhase);
                double cost = dvEnter + dvExit;

                // TRUE closure: re-propagate this candidate (apply the enter burn, coast
                // N*Tphase) against the ACTUAL target orbit and measure the ALONG-TRACK
                // residual — the part of the miss the choice of N actually governs. The
                // remaining (radial) miss is the |Δa| offset between the two near-circular
                // orbits and is N-independent, so selecting on the along-track component is
                // what makes N minimize the real closure (the genuine relative motion n_me vs
                // n_target enters via the propagator). The old Δv-only pick ignored this and
                // could choose an N whose along-track residual was larger.
                double driftN = N * Tphase;
                Vector3D enterDirN = Vector3D.Normalize(myState.Velocity) * (vPhase - vCirc);
                double alongResidual = PhasingAlongTrackResidual(myState, mu, now, enterDirN, driftN, targetOrbit);

                // SELECT by the along-track residual first; Δv breaks near-ties (within 1 m).
                bool better;
                if (Math.Abs(alongResidual - bestAlong) > 1.0)
                    better = alongResidual < bestAlong;
                else
                    better = cost < bestCost;
                if (better)
                {
                    bestAlong = alongResidual;
                    bestCost = cost;
                    bestRevs = N;
                    bestTphase = Tphase;
                }
            }

            if (double.IsInfinity(bestCost) || bestRevs == 0) return null;

            // Build the two tangential burns.
            Vector3D velDir = Vector3D.Normalize(myState.Velocity);
            double aPhaseBest = Math.Pow(
                mu * (bestTphase / (2.0 * Math.PI)) * (bestTphase / (2.0 * Math.PI)), 1.0 / 3.0);
            double vPhaseBest = Math.Sqrt(mu * (2.0 / r - 1.0 / aPhaseBest));
            double enterScalar = vPhaseBest - vCirc; // signed: <0 = slow down (drop), >0 = speed up
            Vector3D enterDv = velDir * enterScalar;
            Vector3D exitDv = velDir * (-enterScalar); // restore

            double driftTime = bestRevs * bestTphase;
            double tArr = now + driftTime;

            ManeuverPlan plan = new ManeuverPlan();
            plan.Status = PlanStatus.Ok;
            plan.Kind = PlanKind.Phasing;

            Maneuver m1 = new Maneuver(0.0, enterDv);
            FillComponents(ref m1, myState.Position, myState.Velocity);
            Maneuver m2 = new Maneuver(driftTime, exitDv);
            // The phasing orbit is periodic and returns to this same point after N laps, so
            // the exit-burn reference frame is (to the near-circular model's accuracy) the
            // same position/velocity as entry — same tangential sense.
            FillComponents(ref m2, myState.Position, myState.Velocity);
            plan.Maneuvers.Add(m1);
            plan.Maneuvers.Add(m2);
            plan.TotalDeltaV = bestCost;

            // Honest arrival prediction by actually propagating the phasing plan.
            ArrivalPrediction pred = PredictPhasingArrival(myState, mu, now, enterDv, driftTime,
                targetOrbit);
            plan.Arrival = pred;
            plan.Note = "Phasing: " + bestRevs + " lap(s) on a period-adjusted orbit, then circularize.";
            return plan;
        }

        // Propagate "apply enterDv now, coast driftTime on the phasing orbit" and compare
        // to the target — the honest miss/rel-speed the phasing plan actually achieves
        // (before the circularizing burn nulls the relative velocity).
        private static ArrivalPrediction PredictPhasingArrival(StateVector myState, double mu,
            double now, Vector3D enterDv, double driftTime, KeplerianElements targetOrbit)
        {
            StateVector afterBurn = new StateVector(myState.Position, myState.Velocity + enterDv);
            KeplerianElements phaseOrbit = OrbitalMath.ToElements(afterBurn, mu, now);
            StateVector sMeArr = OrbitPropagation.StateAt(phaseOrbit, now + driftTime);
            StateVector sTgArr = OrbitPropagation.StateAt(targetOrbit, now + driftTime);

            ArrivalPrediction pred = new ArrivalPrediction();
            pred.ArrivalTime = now + driftTime;
            pred.TimeFromNowSeconds = driftTime;
            pred.MissDistance = (sTgArr.Position - sMeArr.Position).Length();
            // After the exit burn we're back on (near) the target's orbit, so the relative
            // velocity is ~0; report the post-circularization figure.
            pred.RelativeVelocity = Vector3D.Zero;
            pred.RelativeSpeed = 0.0;
            return pred;
        }

        // The ALONG-TRACK component (m) of the phasing miss at arrival — the part N governs
        // (the orthogonal radial/out-of-plane miss is the N-independent |Δa| offset). Used by
        // the M3 selection so the chosen N minimizes the closure N can actually affect.
        // Project the position miss onto the target's velocity direction at arrival.
        private static double PhasingAlongTrackResidual(StateVector myState, double mu,
            double now, Vector3D enterDv, double driftTime, KeplerianElements targetOrbit)
        {
            StateVector afterBurn = new StateVector(myState.Position, myState.Velocity + enterDv);
            KeplerianElements phaseOrbit = OrbitalMath.ToElements(afterBurn, mu, now);
            StateVector sMeArr = OrbitPropagation.StateAt(phaseOrbit, now + driftTime);
            StateVector sTgArr = OrbitPropagation.StateAt(targetOrbit, now + driftTime);
            Vector3D miss = sTgArr.Position - sMeArr.Position;
            double vmag = sTgArr.Velocity.Length();
            if (vmag < 1e-9) return miss.Length();
            Vector3D along = sTgArr.Velocity / vmag;
            return Math.Abs(Vector3D.Dot(miss, along));
        }

        // ---- helpers ---------------------------------------------------------------

        // Decompose a world Δv into the TRUE LVLH basis for display (M4 fix):
        //   prograde = v̂              (along velocity)
        //   normal   = (r×v)̂          (orbit normal — the real out-of-plane direction)
        //   radial   = normal × pro    (in-plane, completes a right-handed triad; ≈ +r̂ outward)
        // Both position and velocity are required — the orbit normal cannot be formed from
        // velocity alone, so the old world-Z basis mislabeled "radial"/"normal" (only
        // "prograde" was ever correct). referencePos/referenceVel set the frame at the burn.
        private static void FillComponents(ref Maneuver m, Vector3D referencePos, Vector3D referenceVel)
        {
            double vmag = referenceVel.Length();
            if (vmag < 1e-9) return;
            Vector3D pro = referenceVel / vmag;

            Vector3D h = Vector3D.Cross(referencePos, referenceVel); // orbit angular momentum r×v
            Vector3D nrm;
            if (h.LengthSquared() < 1e-12)
            {
                // Degenerate (radial/zero state): fall back to a stable arbitrary normal so
                // the triad stays orthonormal rather than NaN.
                nrm = Vector3D.Cross(pro, Vector3D.UnitZ);
                if (nrm.LengthSquared() < 1e-12) nrm = Vector3D.Cross(pro, Vector3D.UnitX);
            }
            else nrm = h;
            nrm = Vector3D.Normalize(nrm);
            // radial completes the right-handed triad (normal × prograde); for a circular/
            // tangential reference velocity this points radially outward (+r̂).
            Vector3D rad = Vector3D.Normalize(Vector3D.Cross(nrm, pro));

            m.Prograde = Vector3D.Dot(m.DeltaV, pro);
            m.Radial = Vector3D.Dot(m.DeltaV, rad);
            m.Normal = Vector3D.Dot(m.DeltaV, nrm);
        }

        // Signed along-track phase angle of 'target' relative to 'me' about the orbit
        // normal (me x v). Positive = target ahead of me in the direction of motion.
        private static double SignedPhaseAngle(Vector3D myPos, Vector3D targetPos, Vector3D myVel)
        {
            Vector3D nrm = Vector3D.Cross(myPos, myVel);
            if (nrm.LengthSquared() < 1e-12) return 0.0;
            nrm = Vector3D.Normalize(nrm);
            Vector3D a = Vector3D.Normalize(myPos);
            Vector3D b = Vector3D.Normalize(targetPos);
            double cos = Clamp(Vector3D.Dot(a, b), -1.0, 1.0);
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

        private static double Clamp(double x, double lo, double hi)
        {
            return x < lo ? lo : (x > hi ? hi : x);
        }
    }
}
