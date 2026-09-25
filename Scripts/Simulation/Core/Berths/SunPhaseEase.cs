using System;

namespace SEAerospace
{
    /// <summary>
    /// Day-fraction phase sources for the engine-sun TERMINATOR drive
    /// (<c>CelestialSceneSnapshot.DriveSun</c>'s GameDateTime bake). Pure phase math (no game
    /// references) so the offline suite can pin it. NOTE (2026-06-12 RSS-literal): the phase
    /// drives the lit terminator ONLY — the DRAWN sun is the chart-mapped celestial direction
    /// (<c>CelestialSceneSnapshot.SunDirection</c>) and never passes through this class; its
    /// ship-relative continuity holds by the chart algebra alone, like every proxy.
    ///
    /// THE CONSISTENCY LAW: every persistent sky motion (sun phase, proxy orbits, spins, clouds)
    /// advances with UNIVERSE time. If time is warping, the sun must move accordingly — a held
    /// terminator under proxies sweeping at x100 is a bug.
    ///
    ///  - INSIDE the surface zone (rotating chart): the azimuth of the chart's view of the star
    ///    (az of R⁻¹·toStar in the engine basis) — sweeping at the SOLAR rate |ω − n|, the
    ///    body-fixed observer's day/night.
    ///  - EVERYWHERE ELSE (above the zone, coasting in a conjunction / transition gap / a
    ///    spinless body): the observer's ORBITAL-AZIMUTH phase (<see cref="TryOrbitalPhase"/>).
    ///    With no spinning ground, the sun's apparent motion IS the orbital motion: as the
    ///    observer's celestial position advances around the star (the conjunction treadmill), the
    ///    azimuth sweeps at the warped orbital rate — fast at x100, slow at x1, frozen only when
    ///    universe time is frozen. Exactly consistent with the proxies by construction (same t,
    ///    same celestial positions).
    ///
    /// SOURCE SWITCHES ARE SEAMLESS — VALUE-CONTINUOUS, RATE-INSTANT (2026-06-11 decision,
    /// replacing the earlier 4 s real-time decay ease): a frame transfer is a teleport the player
    /// must NEVER notice. The teleport preserves the observer's celestial vantage, so the whole
    /// sky is already identical across it; the sun phase must be too. But two UNRELATED phase
    /// CONVENTIONS (e.g. a body's rotation angle vs an orbital azimuth) differ by an arbitrary
    /// constant, so at such a source switch the offset (drawn − newLive) is captured ONCE and
    /// kept — the drawn phase is exactly continuous through the switch and advances at the new
    /// source's live (universe-time) rate from that very frame. No decay: the offset persists
    /// until the next switch.
    ///
    /// EXACT SWITCHES PASS THROUGH (2026-06-12 zone-boundary fix; re-derived same day for the
    /// RSS-literal sun): when the two sources are CO-CALIBRATED members of one exact family —
    /// every azimuth-of-(chart view of the star) source is, since the in-zone az(R⁻¹·toStar)
    /// and the inertial az(toStar) are azimuths of the SAME truthful vector before/after the
    /// chart rotation — the switch must NOT re-capture: truth flows, and a value-continuous
    /// capture would hold the phase and suppress the truthful boundary step forever after. The
    /// step the terminator takes at the zone boundary is whatever the chart rotation R∓¹
    /// projects into the engine's sweep basis — exactly ∓θ when the basis normal IS the spin
    /// axis (the offline canonical-coordinates case, pinned), and a nearby truthful value when
    /// the engine plane differs from the system plane (the live RSS-literal case: a
    /// TERMINATOR-ONLY step, accepted — the drawn sun is chart-mapped and seam-continuous
    /// ship-relative regardless). The caller marks these with <c>exactSwitch</c>: the existing
    /// offset is KEPT (constant offsets cancel in the step) and the live phase's own step flows
    /// through. Resuming from a degenerate HOLD always re-captures — the held value is a
    /// fiction with no live counterpart to be exact against.
    /// </summary>
    public static class SunPhaseEase
    {
        private const double TwoPi = 2.0 * Math.PI;

        /// <summary>
        /// Continuity state. The default value means "never stepped": the first <see cref="Step"/>
        /// adopts its live phase directly (offset 0 — nothing is on screen yet).
        /// </summary>
        public struct State
        {
            /// <summary>A first step has initialized <see cref="Drawn"/>.</summary>
            public bool Init;
            /// <summary>The current day/night source id (body name / orbital), null while holding.</summary>
            public string Source;
            /// <summary>Constant convention offset added to the live phase, captured at the last
            /// source switch so the drawn phase was continuous there. Never decays.</summary>
            public double Offset;
            /// <summary>The phase driven last step, radians [0, 2pi).</summary>
            public double Drawn;
        }

        /// <summary>
        /// Continuity step: returns the phase to drive the sun with, radians [0, 2pi).
        /// <paramref name="source"/> identifies the day/night source (frame body's name, or the
        /// orbital-azimuth id); null/empty = degenerate (no azimuth exists) → hold the last drawn
        /// phase. <paramref name="livePhase"/> is the source's live universe-time phase. On a
        /// source CHANGE the offset re-captures so the drawn value is exactly continuous —
        /// UNLESS <paramref name="exactSwitch"/> is true: the two sources are co-calibrated
        /// members of one exact family, the existing offset is kept and the live phase's own
        /// step passes through (the zone boundary's truthful projected step; see the class
        /// doc). Resuming from a degenerate hold always re-captures.
        /// There is no time parameter because nothing here smooths — rate changes are instant.
        /// </summary>
        public static double Step(ref State state, string source, double livePhase,
            bool exactSwitch = false)
        {
            bool hasSource = !string.IsNullOrEmpty(source);

            if (!state.Init)
            {
                state.Init = true;
                state.Source = hasSource ? source : null;
                state.Offset = 0.0;
                state.Drawn = Wrap(hasSource ? livePhase : 0.0);
                return state.Drawn;
            }

            if (!hasSource)
            {
                // Degenerate (no star / no azimuth): hold the last drawn phase.
                state.Source = null;
                return state.Drawn;
            }

            if (state.Source == null)
            {
                // Resume from a degenerate hold: ALWAYS capture — the held value is a fiction
                // with no live counterpart, so continuity from it is the only contract left.
                state.Offset = WrapToPi(state.Drawn - livePhase);
                state.Source = source;
            }
            else if (state.Source != source)
            {
                // Source switch. Unrelated conventions: capture the offset so the drawn phase
                // is value-continuous — the player must never notice a transfer. EXACT family
                // (exactSwitch): keep the offset; the live step (the zone boundary's truthful
                // projected step) passes through — truth flows, never held.
                if (!exactSwitch) state.Offset = WrapToPi(state.Drawn - livePhase);
                state.Source = source;
            }

            state.Drawn = Wrap(livePhase + state.Offset);
            return state.Drawn;
        }

        /// <summary>
        /// The anchorless day phase: the azimuth of the observer→star direction about the system
        /// reference plane, radians [0, 2pi). <paramref name="toStar"/> is the root star's
        /// celestial position minus the observer's celestial position (root-frame, same t as
        /// everything else in the scene — that sameness is the whole consistency guarantee).
        ///
        /// CONVENTION: φ = atan2(toStar.Y, toStar.X) — the angle in the X–Y reference plane from
        /// +X toward +Y, i.e. right-handed about +Z. This is the convention the rest of the
        /// orbital core already implies: classical elements measure RAAN/anomalies in the X–Y
        /// plane exactly this way (<c>OrbitalMath.ToElements</c>: node = cross(UnitZ, h),
        /// raan = atan2(n.Y, n.X)), and <c>GravityBody.RotationAngleAt</c> is a right-handed
        /// rotation about the default +Z <c>SpinAxis</c> — so for a prograde orbit/spin BOTH
        /// phase sources advance in the same sense with t.
        ///
        /// WHY THIS IS PHYSICAL: with no spinning body under the observer, the sun's apparent
        /// motion is purely the observer's orbital motion around the star; this azimuth advances
        /// exactly as fast as the observer's celestial position sweeps around the star — i.e. at
        /// the warped orbital rate, satisfying the consistency law (frozen only when t is frozen).
        ///
        /// Returns false (phase 0) when no azimuth is defined: the observer is at the star, or so
        /// close to its polar (+Z) axis that the in-plane component is numerically meaningless —
        /// the caller should hold its last phase (degenerate last resort).
        /// </summary>
        public static bool TryOrbitalPhase(Vector3D toStar, out double phase)
        {
            phase = 0.0;
            double planarSq = toStar.X * toStar.X + toStar.Y * toStar.Y;
            double lenSq = planarSq + toStar.Z * toStar.Z;
            // Degenerate: at the star (sub-meter) or the in-plane component is < 1e-6 of the
            // distance (within ~0.2 arcsec of the pole axis) — atan2 of noise would spin the sun.
            if (lenSq < 1.0 || planarSq < lenSq * 1e-12) return false;
            phase = Wrap(Math.Atan2(toStar.Y, toStar.X));
            return true;
        }

        /// <summary>Wrap an angle to [0, 2pi).</summary>
        public static double Wrap(double angle)
        {
            double f = angle / TwoPi;
            return (f - Math.Floor(f)) * TwoPi;
        }

        /// <summary>Wrap an angle to (−pi, pi] — the shortest-path signed offset.</summary>
        public static double WrapToPi(double angle)
        {
            double a = Wrap(angle);
            return a > Math.PI ? a - TwoPi : a;
        }
    }
}
