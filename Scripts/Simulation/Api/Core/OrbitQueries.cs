using System;
using SEAerospace.Orbital;

namespace SEAerospace.Api
{
    /// <summary>
    /// The pure orbit questions the public API answers (OrbitalApi.Orbits): elements from a state and back, apsides and the
    /// times to them, an orbit sampled into a caller's buffer. Engine-free (the offline ApiTests compile it), no allocation,
    /// no locks: every answer is a function of its arguments. Angles in radians, SI units, positions about the orbit's body
    /// in the solar system's inertial axes.
    /// </summary>
    public static class OrbitQueries
    {
        /// <summary>An element set from its eight numbers (the API's flat form).</summary>
        public static KeplerianElements Elements(double a, double e, double i, double raan, double argp, double nu, double epoch, double mu)
            => new KeplerianElements { SemiMajorAxis = a, Eccentricity = e, Inclination = i, Raan = raan, ArgPeriapsis = argp, TrueAnomaly = nu, Epoch = epoch, Mu = mu };

        /// <summary>A set the conic math can use: all finite, mu &gt; 0, e &gt;= 0 and not the parabolic knife-edge, and a's sign
        /// matching the regime (a &gt; 0 elliptic, a &lt; 0 hyperbolic).</summary>
        public static bool Usable(KeplerianElements el)
        {
            if (!Finite(el.SemiMajorAxis) || !Finite(el.Eccentricity) || !Finite(el.Inclination) || !Finite(el.Raan)
                || !Finite(el.ArgPeriapsis) || !Finite(el.TrueAnomaly) || !Finite(el.Epoch) || !Finite(el.Mu)) return false;
            if (!(el.Mu > 0) || el.Eccentricity < 0 || Math.Abs(el.Eccentricity - 1.0) < 1e-9) return false;
            return el.Eccentricity < 1.0 ? el.SemiMajorAxis > 0 : el.SemiMajorAxis < 0;
        }

        /// <summary>Where the orbit is at universe time t (two-body, on rails: no sphere-of-influence change, no burn).</summary>
        public static bool Propagate(KeplerianElements el, double t, out Vector3D position, out Vector3D velocity)
        {
            position = velocity = default;
            if (!Usable(el) || !Finite(t)) return false;
            var s = OrbitPropagation.StateAt(el, t);
            position = s.Position; velocity = s.Velocity;
            return Finite(position) && Finite(velocity);
        }

        /// <summary>The osculating elements of a state about a body of parameter mu, epoch t. A radial state (straight up or down,
        /// or at rest) gets the mod's capture nudge (a needle-thin ellipse, not NaN: RendezvousPlot.Capture).</summary>
        public static bool FromState(Vector3D position, Vector3D velocity, double mu, double t, out KeplerianElements el)
        {
            el = default;
            if (!Finite(position) || !Finite(velocity) || !(mu > 0) || !Finite(mu) || !Finite(t) || position.LengthSquared() < 1.0) return false;
            el = SEAerospace.Frames.RendezvousPlot.Capture(new StateVector(position, velocity), mu, t);
            return Usable(el);
        }

        /// <summary>The orbit's apsides and timing: periapsis and apoapsis radius (m; apoapsis +infinity when hyperbolic), period (s;
        /// +infinity when hyperbolic), and the time from the elements' epoch to the next periapsis and apoapsis (s). Elliptic: the next
        /// forward passage, in [0, period). Hyperbolic: the one periapsis, negative when already past; apoapsis NaN.</summary>
        public static bool Events(KeplerianElements el, out double periapsisRadius, out double apoapsisRadius, out double period,
            out double timeToPeriapsis, out double timeToApoapsis)
        {
            periapsisRadius = apoapsisRadius = period = timeToPeriapsis = timeToApoapsis = double.NaN;
            if (!Usable(el)) return false;
            periapsisRadius = el.PeriapsisRadius;
            bool ell = el.IsElliptic;
            apoapsisRadius = ell ? el.ApoapsisRadius : double.PositiveInfinity;
            period = ell ? el.Period : double.PositiveInfinity;
            timeToPeriapsis = OrbitPropagation.TimeToPeriapsis(el);
            timeToApoapsis = ell ? OrbitPropagation.TimeToApoapsis(el) : double.NaN;
            return Finite(periapsisRadius) && Finite(timeToPeriapsis);
        }

        /// <summary>The orbit sampled evenly in TIME over [t0, t1] into xyz (x, y, z per point: xyz.Length / 3 points, the first at
        /// t0 and the last at t1). Returns the points written (0: nothing usable, or room for fewer than 2).</summary>
        public static int SampleByTime(KeplerianElements el, double t0, double t1, double[] xyz)
        {
            if (xyz == null || !Usable(el) || !Finite(t0) || !Finite(t1)) return 0;
            int n = xyz.Length / 3;
            if (n < 2) return 0;
            for (int k = 0; k < n; k++)
            {
                double t = t0 + (t1 - t0) * k / (n - 1);
                var p = OrbitPropagation.StateAt(el, t).Position;
                xyz[3 * k] = p.X; xyz[3 * k + 1] = p.Y; xyz[3 * k + 2] = p.Z;
            }
            return n;
        }

        /// <summary>The orbit's SHAPE into xyz (x, y, z per point; xyz.Length / 3 points), as the mod's own map draws it: an
        /// ellipse evenly in eccentric anomaly, one loop from periapsis (close it yourself: the last point is not the first); a
        /// hyperbola evenly in hyperbolic anomaly, out to maxRadius on both legs (m; +infinity or 0: a fixed wide arc).
        /// Returns the points written.</summary>
        public static int SampleShape(KeplerianElements el, double maxRadius, double[] xyz)
        {
            if (xyz == null || !Usable(el)) return 0;
            int n = xyz.Length / 3;
            double e = el.Eccentricity;
            if (el.IsElliptic)
            {
                if (n < 3) return 0;
                for (int k = 0; k < n; k++)
                {
                    double nu = OrbitalMath.EccentricToTrueAnomaly(OrbitalMath.TwoPi * k / n, e);
                    Put(xyz, k, OrbitSampler.PositionAtTrueAnomaly(el, nu));
                }
                return n;
            }
            if (n < 2) return 0;
            double hMax = 3.0;
            if (maxRadius > 0 && Finite(maxRadius))
            {
                double coshH = (1.0 - maxRadius / el.SemiMajorAxis) / e;   // (a < 0: above 1 beyond periapsis)
                if (coshH > 1.0) hMax = Math.Log(coshH + Math.Sqrt(coshH * coshH - 1.0));
            }
            for (int k = 0; k < n; k++)
            {
                double h = -hMax + 2.0 * hMax * k / (n - 1);
                Put(xyz, k, OrbitSampler.PositionAtTrueAnomaly(el, OrbitalMath.HyperbolicToTrueAnomaly(h, e)));
            }
            return n;
        }

        static void Put(double[] xyz, int k, Vector3D p) { xyz[3 * k] = p.X; xyz[3 * k + 1] = p.Y; xyz[3 * k + 2] = p.Z; }

        public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        public static bool Finite(Vector3D v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
    }
}
