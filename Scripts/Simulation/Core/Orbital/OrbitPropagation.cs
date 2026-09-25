using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// Time-domain propagation and event solvers on two-body orbits. This is the
    /// "where will it be / when does it get there" layer the rails, the time-warp,
    /// and (critically) the interaction-shell scheduling all consume — e.g.
    /// "time of next shell crossing is an analytic Kepler solve" is
    /// <see cref="TryTimeToRadius"/>.
    /// </summary>
    public static class OrbitPropagation
    {
        /// <summary>Elements advanced to absolute time t (uses the element epoch).</summary>
        public static KeplerianElements AtTime(KeplerianElements el, double time)
            => OrbitalMath.Propagate(el, time - el.Epoch);

        /// <summary>Cartesian state at absolute time t.</summary>
        public static StateVector StateAt(KeplerianElements el, double time)
            => OrbitalMath.ToState(AtTime(el, time));

        /// <summary>
        /// Seconds from the element epoch until the orbit reaches true anomaly nuTarget.
        /// Elliptic: the NEXT forward occurrence (result in [0, period)). Hyperbolic:
        /// the single signed time it crosses nuTarget (negative if already past).
        /// </summary>
        public static double TimeToTrueAnomaly(KeplerianElements el, double nuTarget)
        {
            double m0 = OrbitalMath.TrueToMeanAnomaly(el.TrueAnomaly, el.Eccentricity);
            double mt = OrbitalMath.TrueToMeanAnomaly(nuTarget, el.Eccentricity);
            double dM = mt - m0;
            if (el.IsElliptic)
            {
                dM %= OrbitalMath.TwoPi;
                if (dM < 0) dM += OrbitalMath.TwoPi;
            }
            return dM / el.MeanMotion;
        }

        /// <summary>Seconds to the next periapsis passage.</summary>
        public static double TimeToPeriapsis(KeplerianElements el) => TimeToTrueAnomaly(el, 0.0);

        /// <summary>Seconds to the next apoapsis passage (elliptic only; NaN otherwise).</summary>
        public static double TimeToApoapsis(KeplerianElements el)
            => el.IsElliptic ? TimeToTrueAnomaly(el, Math.PI) : double.NaN;

        /// <summary>
        /// When does the orbit cross a given radius? Two crossings per pass:
        /// <paramref name="tOutbound"/> while moving away (rdot &gt; 0),
        /// <paramref name="tInbound"/> while moving toward (rdot &lt; 0). Both are
        /// signed times from the epoch (elliptic results are the next forward
        /// occurrence; hyperbolic may be negative if already past). Returns false if
        /// the radius is never reached: below periapsis (any regime); above apoapsis
        /// (elliptic); or beyond the asymptotic reach |nu| &lt; nu_inf (hyperbolic).
        /// This is the interaction-shell scheduler: feed R_shell, schedule the handoff.
        /// </summary>
        public static bool TryTimeToRadius(KeplerianElements el, double radius,
            out double tOutbound, out double tInbound)
        {
            tOutbound = tInbound = double.NaN;
            if (radius <= 0) return false;

            double e = el.Eccentricity;
            // Circular: radius is constant (= a); a "crossing" is undefined → no handoff to
            // schedule. Also avoids the 1/e divide-by-zero (which produced a NaN that slipped
            // past the range check into Acos for near-circular orbits). [audit H1/shipped-H3]
            if (e < 1e-9) return false;

            double cosNu = (el.SemiLatusRectum / radius - 1.0) / e;
            // The negated form also rejects NaN (NaN fails both comparisons): radius below
            // periapsis (cosNu > 1) or, for elliptic, above apoapsis (cosNu < -1).
            if (!(cosNu >= -1.0 && cosNu <= 1.0)) return false;

            // Hyperbolic/parabolic: the trajectory only spans |nu| < nu_inf = acos(-1/e). A
            // radius requiring nu at/beyond the asymptote (cosNu <= -1/e, i.e. r → ∞) is never
            // actually reached — return false instead of a spurious +∞ time. [audit H1/H4]
            if (!el.IsElliptic && cosNu <= -1.0 / e) return false;

            double nu = Math.Acos(cosNu);
            double to = TimeToTrueAnomaly(el, nu);    // 0 < nu < pi  -> moving outward
            double ti = TimeToTrueAnomaly(el, -nu);   // -pi < nu < 0 -> moving inward
            if (IsNotFinite(to) || IsNotFinite(ti)) return false; // belt-and-braces vs ±∞

            tOutbound = to;
            tInbound = ti;
            return true;
        }

        private static bool IsNotFinite(double x)
        {
            return double.IsNaN(x) || double.IsInfinity(x);
        }
    }
}
