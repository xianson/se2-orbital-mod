using SEAerospace.Orbital;

namespace SEAerospace.Frames
{
    /// <summary>
    /// The COMMIT-SIDE QUARANTINE for a frame's rails (2026-06-12 pre-epoch blocker fix,
    /// half 1 — see also SoiReparent's commit horizon, half 2).
    ///
    /// A cross-SOI encounter patch is committed AT the predicted crossing time tX
    /// (<see cref="SoiReparent"/>: elements re-osculated with Epoch = tX), so until the
    /// rails actually reach tX the frame's elements describe a FUTURE state:
    /// <c>StateAt(Elements, t)</c> for t &lt; Epoch back-propagates the child-relative
    /// encounter conic to where the frame "would have been" had the child's gravity
    /// applied all along — a fiction that is harmless to DISPLAY for the sub-second
    /// pre-epoch window, but must never be RE-OSCULATED from: folding a drain slice, a
    /// rebase shift or a split capture out of that fiction with a fresh epoch = t
    /// DESTROYS the committed patch point and permanently corrupts the rails (and the
    /// resulting "still outside the SOI" back-propagated position immediately
    /// ESCAPE-flaps the frame back off its new parent).
    ///
    /// Rule: every rails MUTATION is gated on t &gt;= Elements.Epoch
    /// (<see cref="CanMutateAt"/>). The commit horizon (half 2) bounds the pre-epoch
    /// window to ~two seam checks (~1 s), so the deferrals here are sub-second:
    ///   - the drain fold DEFERS (<see cref="FoldResult.Deferred"/>): no fold and no
    ///     member feed — the fold==feed invariant stays matched at zero; the raw drain
    ///     keeps zeroing the anchor and accumulating upstream, and slicing resumes at
    ///     the epoch, so a burn loses at most the window of TIMING, never its delta-v;
    ///   - the anchor-loss rebase shift is refused (<see cref="TryRebaseShift"/>); the
    ///     caller holds it PENDING and re-applies it at the epoch;
    ///   - splits are forbidden pre-epoch (the caller gates on <see cref="CanMutateAt"/>;
    ///     a rim event delayed one or two ticks is invisible).
    ///
    /// Game-free (orbital core + VRageMath only) so the quarantine is pinned offline
    /// (Orbital.Tests PreEpochCommitQuarantine) and twinned literally in tools\FrameSim.
    /// C# 6.
    /// </summary>
    public static class FrameRails
    {
        /// <summary>What <see cref="FoldDrain"/> did with the drain accumulator.</summary>
        public enum FoldResult
        {
            None,      // accumulator at/below the fold threshold — nothing to do
            Deferred,  // pre-epoch quarantine: accumulator kept, NO fold and NO feed
            Refused,   // degenerate re-osculation: accumulator kept, retry next tick
            Folded,    // a slice committed into the elements (feed the SAME slice)
        }

        /// <summary>
        /// True when the rails may be MUTATED (re-osculated) at time <paramref name="t"/>:
        /// the elements' epoch has been reached. False = the frame is riding to a
        /// committed FUTURE patch point and every fold/rebase/split must wait (see the
        /// class doc — pre-epoch states are back-propagated fiction).
        /// </summary>
        public static bool CanMutateAt(KeplerianElements elements, double t)
        {
            return t >= elements.Epoch;
        }

        /// <summary>
        /// The fold==feed drain slice ([audit fix #6]) behind the pre-epoch quarantine:
        /// process at most <paramref name="maxStepDv"/> of
        /// <see cref="ProximityFrame.PendingDrainDv"/> — fold it into the elements at
        /// <paramref name="t"/> AND report the SAME slice for the member apparent-force
        /// feed. Mutates NOTHING unless it returns <see cref="FoldResult.Folded"/>; on
        /// Folded, <paramref name="cur"/> is advanced to the post-fold state and
        /// <paramref name="slice"/> carries the delta-v the members must be compensated
        /// for (feed −slice/dt). Pre-epoch returns Deferred (accumulator kept; slicing
        /// resumes at the epoch). A degenerate re-osculation returns Refused — never
        /// trade a live orbit for a NaN frame; the caller should warn if that persists.
        /// </summary>
        public static FoldResult FoldDrain(ProximityFrame frame, double t, double foldThreshold,
            double maxStepDv, ref StateVector cur, out Vector3D slice)
        {
            slice = Vector3D.Zero;
            if (frame == null) return FoldResult.None;
            double pendLen = frame.PendingDrainDv.Length();
            if (!IsFinite(pendLen) || pendLen <= foldThreshold) return FoldResult.None;
            if (!CanMutateAt(frame.Elements, t)) return FoldResult.Deferred;

            Vector3D s = pendLen > maxStepDv
                ? frame.PendingDrainDv * (maxStepDv / pendLen)
                : frame.PendingDrainDv;
            Vector3D newVel = cur.Velocity + s;
            if (!IsFinite(cur.Position) || !IsFinite(newVel)) return FoldResult.Refused;
            KeplerianElements folded = OrbitalMath.ToElements(
                new StateVector(cur.Position, newVel), frame.Elements.Mu, t);
            if (!IsFinite(folded.SemiMajorAxis) || !IsFinite(folded.MeanMotion)) return FoldResult.Refused;
            // The a/MeanMotion check is PARTIAL: on an exact-radial re-osculation (h = r×v → 0) energy (a,
            // MeanMotion) stays finite while Inclination/Raan/ArgP/nu come back NaN (Acos(0/0)). Committing
            // that poisons the rails — every later ToState is NaN. Round-trip through ToState to catch a NaN
            // in ANY element field (mirrors the SoiReparent re-osculation guard).
            StateVector chkFold = OrbitalMath.ToState(folded);
            if (!IsFinite(chkFold.Position) || !IsFinite(chkFold.Velocity)) return FoldResult.Refused;

            frame.Elements = folded;
            frame.PendingDrainDv -= s;
            cur = new StateVector(cur.Position, newVel);
            slice = s;
            return FoldResult.Folded;
        }

        /// <summary>
        /// The anchor-loss rebase: shift the orbit by <paramref name="shift"/> (the berth
        /// origin's move across an anchor election) by re-osculating the shifted state at
        /// <paramref name="t"/>. Returns false WITHOUT mutating when pre-epoch (the
        /// caller keeps the shift pending and re-applies at the epoch) or when the
        /// re-osculation is degenerate (mirrors the original skip — the orbit is kept
        /// unshifted rather than traded for a NaN frame).
        /// </summary>
        public static bool TryRebaseShift(ProximityFrame frame, double t, Vector3D shift)
        {
            if (frame == null || !IsFinite(shift)) return false;
            if (!CanMutateAt(frame.Elements, t)) return false;
            StateVector reb = OrbitPropagation.StateAt(frame.Elements, t);
            Vector3D rebPos = reb.Position + shift;
            if (!IsFinite(rebPos) || !IsFinite(reb.Velocity)) return false;
            KeplerianElements re = OrbitalMath.ToElements(
                new StateVector(rebPos, reb.Velocity), frame.Elements.Mu, t);
            if (!IsFinite(re.SemiMajorAxis) || !IsFinite(re.MeanMotion)) return false;
            // Partial guard (see FoldDrain): catch a NaN inclination/raan/argp/nu from an exact-radial
            // re-osculation before it poisons the rails — keep the unshifted orbit, the documented skip.
            StateVector chkReb = OrbitalMath.ToState(re);
            if (!IsFinite(chkReb.Position) || !IsFinite(chkReb.Velocity)) return false;
            frame.Elements = re;
            return true;
        }

        // ---- DEPARTURE-IMMINENT STOW: the escape predicate (2026-06-12) ----------------

        /// <summary>
        /// The DEPARTURE-IMMINENT STOW predicate (2026-06-12, the outbound mirror of the
        /// arrival-imminent lock): may a grid coasting INSIDE a body's keep envelope (but
        /// above the shell) stow onto rails NOW, instead of crawling out past the keep at
        /// x1 first? True iff the would-be captured ballistic state PROVABLY exits the
        /// shell and does not return within the look-ahead:
        ///   (a) the orbit EXITS the keep envelope: hyperbolic (unbound), or an elliptic
        ///       apoapsis above <paramref name="keepRadius"/>. A suborbital lob
        ///       (apo &lt; keep) or a circular orbit inside the keep band stays the
        ///       VoxelFrame's — today's behavior, unchanged;
        ///   (b) NO upcoming INBOUND shell crossing within
        ///       [t, t + <paramref name="inboundLookahead"/>]: an arc that re-enters the
        ///       shell inside the look-ahead must stay physical (the VoxelFrame owns
        ///       re-entry/descent). A hyperbolic state whose single inbound crossing is
        ///       already PAST is climbing out and passes; an orbit whose periapsis is
        ///       above the shell never re-enters at all and passes.
        ///
        /// PING-PONG / LOCK-(d) INVARIANT (the interaction with the arrival-imminent
        /// wave, documented here because this predicate is what guarantees it): callers
        /// MUST pass inboundLookahead &gt;= ArrivalImminenceLookahead() (the CHOSEN-warp
        /// arrival window). Then a frame created from a passing state CANNOT trigger
        /// AutoMaterializeFrame's scheduled path at creation:
        ///   - it is outside the shell (the caller's r &gt; shell x margin floor), so the
        ///     inside-shell release path cannot fire;
        ///   - its next inbound shell crossing is beyond the arrival-imminence window, so
        ///     no lock-(d) stamp fires on the first seam checks, and the below-surface
        ///     EMERGENCY act cannot fire either (the surface dip is later than the shell
        ///     crossing, and EmergencyActWindow &lt;= ArrivalImminenceLookahead at every
        ///     chosen warp).
        /// The earliest possible release is therefore &gt;= the look-ahead away in
        /// universe time and goes through the NORMAL imminent -&gt; lock -&gt; x1 -&gt;
        /// precise path, which places the grids INSIDE the shell — where the stow
        /// floor (r &gt; shell x margin) refuses an immediate re-stow. Both ends of the
        /// release/stow loop stay closed. At extreme chosen warps the window can exceed
        /// a transfer ellipse's period and the predicate simply refuses — the stow then
        /// happens beyond the keep exactly as before (conservative, never wrong).
        /// </summary>
        public static bool EscapesShell(KeplerianElements el, double t, double shellRadius,
            double keepRadius, double inboundLookahead)
        {
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return false;
            if (shellRadius <= 0.0 || keepRadius <= 0.0) return false;
            if (el.IsElliptic)
            {
                double apo = el.ApoapsisRadius;
                if (!IsFinite(apo) || apo <= keepRadius) return false;   // captive/suborbital arc
            }
            double tOutRel, tInRel;
            if (!OrbitPropagation.TryTimeToRadius(el, shellRadius, out tOutRel, out tInRel))
                return true;   // periapsis above the shell: the orbit NEVER re-enters it
            double tIn = NextInboundCrossing(el, tInRel, t);
            if (!IsFinite(tIn)) return false;                            // degenerate period — refuse
            return tIn < t                                               // hyperbolic, crossing already past
                || tIn > t + inboundLookahead;                           // re-entry beyond the look-ahead
        }

        /// <summary>The next ABSOLUTE inbound crossing time at/after <paramref name="t"/>
        /// (elliptic recurs every period; hyperbolic is the single crossing, possibly
        /// already past — returned un-advanced). NaN on a degenerate period. (Mirror of
        /// FrameManager.NextInboundCrossing, kept here so the game-free predicate is
        /// self-contained and offline-pinned.)</summary>
        private static double NextInboundCrossing(KeplerianElements el, double tInRel, double t)
        {
            double abs = el.Epoch + tInRel;
            if (el.IsElliptic)
            {
                double period = el.Period;
                if (!IsFinite(period) || period <= 0.0) return double.NaN;
                if (abs < t) abs += System.Math.Ceiling((t - abs) / period) * period;
            }
            return abs;
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
