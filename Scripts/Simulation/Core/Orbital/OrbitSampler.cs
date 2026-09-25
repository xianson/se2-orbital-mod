using System;

namespace SEAerospace.Orbital
{
    /// <summary>A traced orbit ready to draw: inertial-frame points + whether to close the loop.</summary>
    public struct OrbitPath
    {
        public Vector3D[] Points;  // positions tracing the conic, in the BodyInertial frame
        public bool IsClosed;      // ellipse -> true (loop back to Points[0]); hyperbola -> false (open arc)
    }

    /// <summary>A point on an ephemeris at a specific time.</summary>
    public struct EphemerisSample
    {
        public double Time;
        public StateVector State;
    }

    /// <summary>
    /// Sampling orbits for DRAWING and for kinematic ephemeris queries.
    ///
    /// The right way to draw a conic: sample uniformly in ECCENTRIC (elliptic) or
    /// HYPERBOLIC anomaly, NOT in true anomaly. Uniform-anomaly sampling places
    /// points densely where the curve bends hardest (near periapsis) and sparsely on
    /// the lazy far arc — smooth at low point counts. Uniform true-anomaly clumps and
    /// renders lumpy on eccentric orbits (RSS's failure). Hyperbolas are drawn as an
    /// OPEN arc clipped to a bound (SOI / interaction shell), never faked as an
    /// ellipse (Real Orbits' failure).
    ///
    /// The curve is static in the BodyInertial frame (the body moves along it; the
    /// ellipse doesn't move) — sample once per element change, then transform the
    /// fixed point set into render/proxy/LVLH space each frame.
    /// </summary>
    public static class OrbitSampler
    {
        /// <summary>
        /// Trace the orbit for rendering. <paramref name="count"/> points; for
        /// hyperbolas, <paramref name="maxRadius"/> clips the open arc (default: a wide
        /// arc through the strongly-curved region).
        /// </summary>
        public static OrbitPath SamplePath(KeplerianElements el, int count,
            double maxRadius = double.PositiveInfinity)
            => el.IsElliptic ? SampleEllipse(el, count) : SampleHyperbola(el, count, maxRadius);

        private static OrbitPath SampleEllipse(KeplerianElements el, int count)
        {
            if (count < 3) count = 3;
            var pts = new Vector3D[count];
            for (int k = 0; k < count; k++)
            {
                double e = OrbitalMath.TwoPi * k / count;                      // uniform eccentric anomaly
                double nu = OrbitalMath.EccentricToTrueAnomaly(e, el.Eccentricity);
                pts[k] = PositionAtTrueAnomaly(el, nu);
            }
            return new OrbitPath { Points = pts, IsClosed = true };
        }

        private static OrbitPath SampleHyperbola(KeplerianElements el, int count, double maxRadius)
        {
            if (count < 2) count = 2;
            double e = el.Eccentricity;

            // Bound the arc by hyperbolic anomaly. If a maxRadius is given (SOI / shell),
            // solve r = a(1 - e cosh H) for H; else span a wide, strongly-curved arc.
            double hMax;
            if (double.IsPositiveInfinity(maxRadius))
            {
                hMax = 3.0;
            }
            else
            {
                double coshH = (1.0 - maxRadius / el.SemiMajorAxis) / e; // a < 0, so this is > 1
                hMax = coshH > 1.0 ? Acosh(coshH) : 3.0;
            }

            var pts = new Vector3D[count];
            for (int k = 0; k < count; k++)
            {
                double H = -hMax + 2.0 * hMax * k / (count - 1);              // uniform hyperbolic anomaly
                double nu = OrbitalMath.HyperbolicToTrueAnomaly(H, e);
                pts[k] = PositionAtTrueAnomaly(el, nu);
            }
            return new OrbitPath { Points = pts, IsClosed = false };
        }

        /// <summary>Inertial-frame position at a given true anomaly (orbit shape point).</summary>
        public static Vector3D PositionAtTrueAnomaly(KeplerianElements el, double nu)
        {
            el.TrueAnomaly = nu;
            return OrbitalMath.ToState(el).Position;
        }

        /// <summary>Periapsis marker position (for GPS/HUD).</summary>
        public static Vector3D PeriapsisPosition(KeplerianElements el)
            => PositionAtTrueAnomaly(el, 0.0);

        /// <summary>Apoapsis marker position (elliptic only).</summary>
        public static Vector3D ApoapsisPosition(KeplerianElements el)
            => PositionAtTrueAnomaly(el, Math.PI);

        /// <summary>
        /// Kinematic sampling: states at uniform TIME steps over [t0, t1]. Use for
        /// "where will it be in N seconds" dots (which cluster near apoapsis where the
        /// body moves slowly) and for proxy/marker placement of bodies on rails.
        /// Works on any <see cref="IEphemeris"/>.
        /// </summary>
        public static EphemerisSample[] SampleByTime(IEphemeris eph, double t0, double t1, int count)
        {
            if (count < 1) count = 1;
            // SE2 port: Keen's whitelist analyzer (VRS1001) bans `new T[n]` for a script-defined T
            // (array types have no containing assembly, so they miss the "own script" exemption).
            // List<T>.ToArray() produces the same array without naming the array type.
            var outp = new List<EphemerisSample>(count);
            double denom = Math.Max(1, count - 1);
            for (int k = 0; k < count; k++)
            {
                double t = t0 + (t1 - t0) * (k / denom);
                outp.Add(new EphemerisSample { Time = t, State = eph.StateAt(t) });
            }
            return outp.ToArray();
        }

        // .NET Framework 4.8's System.Math lacks Acosh.
        private static double Acosh(double x) => Math.Log(x + Math.Sqrt(x * x - 1.0));
    }
}
