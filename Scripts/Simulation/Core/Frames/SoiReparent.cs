using SEAerospace.Orbital;
using SEAerospace.SystemDef;

namespace SEAerospace.Frames
{
    /// <summary>
    /// Cross-SOI re-parenting for a LIVE ProximityFrame on rails — the patched-conic patch
    /// point (docs/patched-conics.md) applied to the frame's virtual orbit instead of a
    /// predicted trajectory. Without this a frame created around one body stays parented to
    /// it FOREVER: an Earth-parented transfer flying out to the Moon coasts straight through
    /// the Moon on pure Earth two-body Kepler — no lunar bend, and (because the seam's
    /// auto-materialize only ever tests <c>frame.ParentBodyName</c>'s shell) no Moon arrival,
    /// ever. This was the "can't transfer to the Moon frame" bug (2026-06-11).
    ///
    /// One step checks the frame's CURRENT celestial state against the SOI tree and applies
    /// at most one patch, exactly like <see cref="PatchedConic.Propagate"/>'s reparenting:
    ///   ENCOUNTER — the frame is inside a CHILD's SOI: re-parent down. Galilean patch
    ///               (position continuous, v_rel = v − v_child), elements re-osculated about
    ///               the child's mu.
    ///   ESCAPE    — the frame climbed above its parent NODE's own SOI (× a small exit pad so
    ///               a boundary-skimming orbit can't flap between parents): re-parent up to
    ///               the grandparent, same Galilean patch.
    /// Throttled by the caller (the FrameManager seam check, ~0.5 s) — one SOI level per
    /// step is plenty at that cadence. Legacy "Planet:&lt;entityId&gt;"-parented frames have no
    /// tree node and are left untouched (same skip the auto-materialize seam applies).
    ///
    /// Degenerate-element guard: the patch only COMMITS when the re-osculated elements are
    /// finite — otherwise the frame keeps its old parent and retries next step (never trade
    /// a live orbit for a dead NaN frame). Game-free (orbital/system core + VRageMath only),
    /// so the Moon-transfer handoff chain is exercised offline. C# 6.
    /// </summary>
    public static class SoiReparent
    {
        /// <summary>What a <see cref="Step"/> did to the frame.</summary>
        public enum Change
        {
            None,             // no boundary crossed (or nothing applicable) — frame untouched
            Encounter,        // entered a child's SOI -> re-parented DOWN to the child
            Escape,           // left the parent node's SOI -> re-parented UP to the grandparent
            EncounterPending, // an SOI crossing is DETECTED inside the look-ahead but lies
                              // BEYOND the commit horizon — NOTHING mutated. The caller
                              // stamps the encounter-imminent realtime lock (lock (e)) and
                              // lets the rails reach the crossing in real time; the patch
                              // commits on a later step once the crossing is near (2026-06-12
                              // pre-epoch blocker fix, half 2: far-future element epochs are
                              // never stamped in the first place).
        }

        /// <summary>
        /// Exit pad on the ESCAPE test: a frame re-parents UP only above SoiRadius × this,
        /// while ENCOUNTER (down) triggers strictly below SoiRadius — the small hysteresis
        /// band keeps a boundary-skimming orbit from flapping between parents every step.
        /// </summary>
        public const double EscapePad = 1.02;

        // Coarse samples across the encounter look-ahead window (per child per step). The window
        // refinement (bisection on the SOI crossing + a ternary dip probe around the sampled
        // minimum) does the precision work, so this only needs to be dense enough that an SOI
        // transit is unlikely to fit entirely between two samples.
        private const int EncounterSamples = 64;

        /// <summary>
        /// Evaluate ONE patch point for <paramref name="frame"/> at universe time
        /// <paramref name="t"/> and apply it (mutates ParentBodyName / Elements /
        /// VirtualVelocity). Returns what happened. Safe no-op on null/legacy/degenerate
        /// input. This overload is the position-poll form (no look-ahead) kept for the
        /// offline sim twins; the live seam calls the look-ahead overload below.
        /// </summary>
        public static Change Step(ProximityFrame frame, SystemRegistry reg, double t)
        {
            return Step(frame, reg, t, 0.0);
        }

        /// <summary>
        /// As <see cref="Step(ProximityFrame,SystemRegistry,double)"/>, but the ENCOUNTER test is
        /// a SCHEDULED event over [t, t + <paramref name="lookahead"/>] (universe seconds), not a
        /// bare position poll — AND the patch is COMMITTED AT DETECTION no matter how far out the
        /// crossing is. This is the LEGACY EAGER-COMMIT form, kept only for the offline pins /
        /// red replays (the live seam passes a commit horizon — see the overload below): an
        /// eagerly committed crossing stamps an element epoch up to the whole look-ahead in the
        /// FUTURE, and any rails mutation taken before the rails reach it re-osculates from
        /// back-propagated fiction (the 2026-06-12 pre-epoch blocker; see FrameRails).
        /// </summary>
        public static Change Step(ProximityFrame frame, SystemRegistry reg, double t, double lookahead)
        {
            return Step(frame, reg, t, lookahead, double.PositiveInfinity);
        }

        /// <summary>
        /// The live seam form: the ENCOUNTER test is a SCHEDULED event over
        /// [t, t + <paramref name="lookahead"/>] (universe seconds), not a bare position poll —
        /// at high warp the rails can cross an entire child SOI (Moon SOI 23.1 km in the test
        /// fixture) between two ~0.5 s seam checks, so the frame would coast straight through
        /// the Moon on parent Kepler with no patch — the same poll-tunneling bug the
        /// auto-materialize seam already solved with TryTimeToRadius + a warp-scaled look-ahead.
        /// Here the child moves too, so the crossing is found by coarse-sampling the
        /// frame-vs-child separation with the LITERAL propagators (the child's ephemeris is an
        /// opaque IEphemeris — ClosestApproach.Find needs two KeplerianElements, so its contract
        /// does not fit), refining the first sub-SOI entry by bisection, and probing the sampled
        /// minima for between-samples dips.
        ///
        /// DETECTION vs COMMITMENT (2026-06-12 pre-epoch blocker fix, half 2): detection uses
        /// the full chosen-warp <paramref name="lookahead"/> (anti-tunnel), but the Galilean
        /// patch only COMMITS when the crossing is within <paramref name="commitHorizon"/>
        /// universe seconds of t. A farther crossing returns
        /// <see cref="Change.EncounterPending"/> WITHOUT mutating the frame: the caller stamps
        /// the encounter-imminent x1 lock (lock (e)) and the rails ride to the crossing in real
        /// time, exactly like the arrival path — so a committed element epoch is never more
        /// than ~the commit horizon in the future, and the pre-epoch quarantine (FrameRails)
        /// only ever spans ~1 s. The commit itself is applied with the state evaluated AT THE
        /// CROSSING TIME (elements re-osculated at epoch tX), so the patched conic is
        /// position-continuous at the patch point — re-binding with the state at t would graft
        /// the encounter hyperbola onto where the frame is NOW.
        /// ESCAPE stays a position poll: firing it one check late is benign (the parent elements
        /// remain valid outside the SOI) and the EscapePad hysteresis must keep its semantics.
        /// </summary>
        public static Change Step(ProximityFrame frame, SystemRegistry reg, double t, double lookahead,
            double commitHorizon)
        {
            double secondsToCrossing;
            return Step(frame, reg, t, lookahead, commitHorizon, out secondsToCrossing);
        }

        /// <summary>
        /// As the 5-arg live overload, but ALSO reports (<paramref name="secondsToCrossing"/>) how far
        /// out the detected SOI crossing is when the result is <see cref="Change.EncounterPending"/> —
        /// the universe seconds from <paramref name="t"/> to the predicted sub-SOI entry. NaN for every
        /// other result (no scheduled crossing to grade). The live seam uses this to GRADE the
        /// encounter-imminent warp cap (lock (e)): warp glides toward x1 as the crossing nears instead
        /// of slamming to x1 across the whole detection window. Behaviour is otherwise identical — the
        /// patch/commit/escape decisions are unchanged; only the pending-crossing dt is surfaced.
        /// </summary>
        public static Change Step(ProximityFrame frame, SystemRegistry reg, double t, double lookahead,
            double commitHorizon, out double secondsToCrossing)
        {
            secondsToCrossing = double.NaN;
            if (frame == null || reg == null) return Change.None;
            GravityBody node = reg.Find(frame.ParentBodyName);
            if (node == null) return Change.None;   // legacy "Planet:<id>" frame — no tree node

            // PRE-EPOCH GUARD (2026-06-12): a look-ahead encounter patch re-binds the frame AT
            // THE FUTURE CROSSING TIME (elements epoch tX > now) — until the rails actually
            // reach tX, StateAt back-propagates the child-relative encounter hyperbola, whose
            // position is (correctly) still OUTSIDE the child's SOI. Taking patch decisions off
            // that back-propagated state flapped the frame straight back OUT (the ESCAPE poll
            // saw r > SOI x pad and re-parented up, the next pass's look-ahead re-patched down,
            // ~2 Galilean re-osculations per second walking the frame up the tree) whenever the
            // rails advanced slower than the patch lead — exactly the arrival-imminent x1 lock's
            // final approach. A pre-epoch frame has already committed to its patch point: no new
            // patch decisions until the rails reach it.
            if (t < frame.Elements.Epoch) return Change.None;

            StateVector cel = OrbitPropagation.StateAt(frame.Elements, t);
            if (!IsFinite(cel.Position) || !IsFinite(cel.Velocity)) return Change.None;

            // ENCOUNTER: inside a child's SOI now, or crossing into it within the look-ahead
            // window -> re-parent down at the crossing-time state. Child SOIs under one parent
            // are disjoint (Laplace SOI << orbit spacing for any sane system), so first hit wins.
            for (int i = 0; i < node.Children.Count; i++)
            {
                GravityBody child = node.Children[i];
                if (child == null) continue;
                double soi = child.SoiRadius;
                if (soi <= 0.0 || double.IsInfinity(soi)) continue;

                Vector3D childPos = child.StateInParentAt(t).Position;
                if ((cel.Position - childPos).Length() < soi)
                {
                    // Already inside: patch at the current state (the original poll behavior).
                    StateVector rel = node.ConvertStateTo(child, cel, t);
                    if (Rebind(frame, child, rel, t)) return Change.Encounter;
                    continue;
                }

                if (lookahead <= 0.0) continue;
                double tX;
                if (!TryFindSoiEntry(frame.Elements, child, t, lookahead, soi, out tX)) continue;

                // DETECTED but beyond the commit horizon: commit nothing — the caller grades
                // lock (e)'s warp cap on the crossing dt and the rails reach the crossing in real
                // time (half 2; see the doc).
                if (tX - t > commitHorizon) { secondsToCrossing = tX - t; return Change.EncounterPending; }

                // Patch AT the predicted crossing: state of frame and child both evaluated at tX.
                StateVector celX = OrbitPropagation.StateAt(frame.Elements, tX);
                if (!IsFinite(celX.Position) || !IsFinite(celX.Velocity)) continue;
                StateVector relX = node.ConvertStateTo(child, celX, tX);
                if (Rebind(frame, child, relX, tX)) return Change.Encounter;
            }

            // ESCAPE: above this node's own SOI (with the exit pad) -> re-parent up.
            GravityBody parent = node.Parent;
            if (parent == null) return Change.None;                    // root frame — nowhere up
            double nodeSoi = node.SoiRadius;
            if (nodeSoi <= 0.0 || double.IsInfinity(nodeSoi)) return Change.None;
            if (cel.Position.Length() <= nodeSoi * EscapePad) return Change.None;
            StateVector inParent = node.ConvertStateTo(parent, cel, t);
            return Rebind(frame, parent, inParent, t) ? Change.Escape : Change.None;
        }

        // Sampled-separation scratch for TryFindSoiEntry (no per-call alloc). The runtime is
        // single-threaded server tick (the seam check), so a static scratch is safe.
        private static readonly double[] SweepSep = new double[EncounterSamples + 1];

        // Find the FIRST time in (t, t + lookahead] at which the orbit enters the child's SOI
        // sphere (separation < soi). Coarse forward sampling with the literal propagators; the
        // first below-SOI sample is bisected back against the previous sample for the entry
        // time. If no sample dips below, EVERY LOCAL MINIMUM of the sampled separation is
        // probed in time order with a ternary search (a fast transit can enter AND exit between
        // two samples) — the EARLIEST minimum that refines sub-SOI wins, and its entry is
        // bisected back from the known-above neighborhood start.
        //
        // WRONG-MINIMUM FIX (2026-06-12, mod-core finding #4): the probe used to refine ONLY the
        // GLOBAL sampled minimum. With two close approaches inside one window (a huge window at
        // high warp spans several conjunctions) where the EARLIER approach samples lower but
        // never transits, and only the LATER — sampled higher because its fast dip falls between
        // samples — is sub-SOI, the ternary refined the wrong dip, found nothing below the SOI,
        // and the encounter was MISSED entirely (the rails coasted straight through the child).
        // Probing every sampled local minimum makes the detection correct regardless of how the
        // window is sized. Pinned offline (Orbital.Tests SoiEntryTwoDipWindow).
        //
        // internal (not private) so the offline pins can exercise it directly; not part of the
        // mod's public surface.
        internal static bool TryFindSoiEntry(KeplerianElements el, GravityBody child,
            double t, double lookahead, double soi, out double tEntry)
        {
            tEntry = double.NaN;
            double step = lookahead / EncounterSamples;
            if (step <= 0.0 || double.IsNaN(step) || double.IsInfinity(step)) return false;

            // Full coarse sweep first (no early return: a sub-SOI SAMPLE may still be preceded
            // by an EARLIER between-samples dip, which must win).
            for (int k = 0; k <= EncounterSamples; k++)
            {
                double d = Separation(el, child, t + k * step);
                if (double.IsNaN(d)) return false;
                SweepSep[k] = d;
            }

            // Candidates in TIME ORDER, earliest hit wins:
            //   - a sub-SOI sample: bisect its entry against the previous sample (above-SOI —
            //     an earlier below sample or a hit dip probe would already have returned);
            //   - a local minimum of the sampled separation: ternary-probe its one-step
            //     neighborhood for a between-samples dip. (The <= on the trailing edge takes
            //     the FIRST sample of a flat plateau; the leading-edge < keeps a plateau from
            //     re-probing every sample.)
            for (int k = 0; k <= EncounterSamples; k++)
            {
                if (SweepSep[k] < soi)
                {
                    if (k == 0) { tEntry = t; return true; }   // inside at window start (caller pre-checks)
                    tEntry = BisectEntry(el, child, t + (k - 1) * step, t + k * step, soi);
                    return !double.IsNaN(tEntry);
                }
                bool isMin;
                if (k == 0) isMin = SweepSep[0] <= SweepSep[1];
                else if (k == EncounterSamples) isMin = SweepSep[k] < SweepSep[k - 1];
                else isMin = SweepSep[k] < SweepSep[k - 1] && SweepSep[k] <= SweepSep[k + 1];
                if (!isMin) continue;
                if (ProbeDip(el, child, t, lookahead, soi, t + k * step, step, out tEntry))
                    return true;
            }
            return false;
        }

        // Ternary-probe one sampled minimum's one-step neighborhood for a between-samples
        // sub-SOI dip; on a hit, bisect the entry back from the neighborhood's start (a coarse
        // sample, known above-SOI). Returns false (tEntry NaN) when the refined dip never goes
        // below the SOI — the caller moves on to the next local minimum.
        private static bool ProbeDip(KeplerianElements el, GravityBody child,
            double t, double lookahead, double soi, double minT, double step, out double tEntry)
        {
            tEntry = double.NaN;
            double lo = minT - step > t ? minT - step : t;
            double hi = minT + step < t + lookahead ? minT + step : t + lookahead;
            double aboveT = lo;
            for (int k = 0; k < 48 && hi - lo > 1e-3; k++)
            {
                double m1 = lo + (hi - lo) / 3.0;
                double m2 = hi - (hi - lo) / 3.0;
                double d1 = Separation(el, child, m1);
                double d2 = Separation(el, child, m2);
                if (double.IsNaN(d1) || double.IsNaN(d2)) return false;
                if (d1 < soi)
                {
                    tEntry = BisectEntry(el, child, aboveT, m1, soi);
                    return !double.IsNaN(tEntry);
                }
                if (d2 < soi)
                {
                    tEntry = BisectEntry(el, child, aboveT, m2, soi);
                    return !double.IsNaN(tEntry);
                }
                if (d1 < d2) hi = m2; else { lo = m1; aboveT = m1; }
            }
            return false;
        }

        // Bisect the SOI entry between a known-outside time and a known-inside time. Returns
        // the inside end of the final bracket, so the patch state is strictly sub-SOI (the
        // escape test's EscapePad hysteresis then cannot immediately flap it back out).
        private static double BisectEntry(KeplerianElements el, GravityBody child,
            double tAbove, double tBelow, double soi)
        {
            for (int k = 0; k < 60; k++)
            {
                if (tBelow - tAbove < 1e-4) break;
                double mid = 0.5 * (tAbove + tBelow);
                double d = Separation(el, child, mid);
                if (double.IsNaN(d)) return double.NaN;
                if (d < soi) tBelow = mid; else tAbove = mid;
            }
            return tBelow;
        }

        // |frame - child| in the shared parent frame at time t; NaN on any non-finite state.
        private static double Separation(KeplerianElements el, GravityBody child, double t)
        {
            StateVector s = OrbitPropagation.StateAt(el, t);
            Vector3D c = child.StateInParentAt(t).Position;
            if (!IsFinite(s.Position) || !IsFinite(c)) return double.NaN;
            return (s.Position - c).Length();
        }

        // Commit the patch: re-osculate the relative state about the new parent's mu and
        // swap the frame onto it. Refuses degenerate states/elements (frame keeps its old
        // parent and the caller's next step retries).
        private static bool Rebind(ProximityFrame frame, GravityBody newParent, StateVector rel, double t)
        {
            if (!IsFinite(rel.Position) || !IsFinite(rel.Velocity)) return false;
            if (rel.Position.LengthSquared() < 1.0) return false;      // at the new parent's center
            KeplerianElements el = OrbitalMath.ToElements(rel, newParent.Mu, t);
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return false;
            // Guard the FULL committed element set, not just a/MeanMotion: a (near-)radial relative
            // state has h = r×v ≈ 0, so Inclination = Acos(NaN) = NaN while energy (and thus a /
            // MeanMotion) stays finite — the partial guard would commit NaN i/ω/ν and poison the rails.
            // Round-tripping back to state catches a NaN in ANY field without enumerating them.
            StateVector chk = OrbitalMath.ToState(el);
            if (!IsFinite(chk.Position) || !IsFinite(chk.Velocity)) return false;
            frame.ParentBodyName = newParent.Name;
            frame.Elements = el;
            frame.VirtualVelocity = rel.Velocity;
            return true;
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static bool IsFinite(Vector3D v)
        {
            return IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
        }
    }
}
