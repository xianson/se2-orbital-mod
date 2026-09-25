using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// A full classical (Keplerian) orbital element set, valid for BOTH elliptic
    /// (0 &lt;= e &lt; 1) and hyperbolic (e &gt; 1) regimes. Angles in RADIANS.
    /// SI units (meters, seconds, m^3/s^2).
    ///
    /// This is the thing Real Orbits lacks (it has no RAAN / argument of periapsis
    /// and NaNs on e&gt;=1) and Real Solar Systems mis-specifies (Euler pitch/roll/yaw
    /// + a free-floating period). Here: one canonical set, one mu, valid across the
    /// conic regimes. The near-parabolic knife-edge (e == 1 exactly) is unsupported
    /// in v0 by design — real orbits are elliptic or hyperbolic.
    /// </summary>
    public struct KeplerianElements
    {
        public double SemiMajorAxis;   // a, meters. >0 elliptic, <0 hyperbolic.
        public double Eccentricity;    // e, dimensionless. [0,1) ellipse, >1 hyperbola.
        public double Inclination;     // i, radians [0, pi].
        public double Raan;            // Omega, right ascension of ascending node, radians.
        public double ArgPeriapsis;    // omega, argument of periapsis, radians.
        public double TrueAnomaly;     // nu, radians.
        public double Mu;              // gravitational parameter GM, m^3/s^2.
        public double Epoch;           // seconds; the time at which TrueAnomaly holds.

        public bool IsHyperbolic => Eccentricity > 1.0;
        public bool IsElliptic => Eccentricity < 1.0;

        /// <summary>Semi-latus rectum p = a(1 - e^2). Finite and positive in both
        /// regimes (hyperbolic: a&lt;0 and (1-e^2)&lt;0 cancel). The robust size scalar.</summary>
        public double SemiLatusRectum => SemiMajorAxis * (1.0 - Eccentricity * Eccentricity);

        /// <summary>Periapsis radius (closest approach to the central body).</summary>
        public double PeriapsisRadius => SemiMajorAxis * (1.0 - Eccentricity);

        /// <summary>Apoapsis radius. Elliptic only; +infinity for hyperbolic.</summary>
        public double ApoapsisRadius
            => IsElliptic ? SemiMajorAxis * (1.0 + Eccentricity) : double.PositiveInfinity;

        /// <summary>Specific orbital energy = -mu/2a. Negative elliptic, positive hyperbolic.</summary>
        public double SpecificEnergy => -Mu / (2.0 * SemiMajorAxis);

        /// <summary>Orbital period (elliptic only); NaN for hyperbolic.</summary>
        public double Period
            => IsElliptic
                ? 2.0 * Math.PI * Math.Sqrt(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis / Mu)
                : double.NaN;

        /// <summary>Mean motion n = sqrt(mu / |a|^3), radians/second.</summary>
        public double MeanMotion
            => Math.Sqrt(Mu / Math.Abs(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));

        /// <summary>Hyperbolic excess speed v_inf = sqrt(-mu/a); 0 for elliptic.</summary>
        public double HyperbolicExcessSpeed
            => IsHyperbolic ? Math.Sqrt(-Mu / SemiMajorAxis) : 0.0;

        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        public override string ToString()
            => IsHyperbolic
                ? $"HYP a={SemiMajorAxis:E3}m e={Eccentricity:F4} i={Deg(Inclination):F2} " +
                  $"O={Deg(Raan):F2} w={Deg(ArgPeriapsis):F2} nu={Deg(TrueAnomaly):F2} vInf={HyperbolicExcessSpeed:F1}"
                : $"ELL a={SemiMajorAxis:E3}m e={Eccentricity:F4} i={Deg(Inclination):F2} " +
                  $"O={Deg(Raan):F2} w={Deg(ArgPeriapsis):F2} nu={Deg(TrueAnomaly):F2} T={Period:F0}s";
    }
}
