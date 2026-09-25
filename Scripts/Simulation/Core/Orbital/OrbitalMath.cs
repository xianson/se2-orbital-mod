using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// Two-body conversions and Kepler propagation, valid for elliptic AND
    /// hyperbolic regimes. Pure functions, no game/engine dependency (VRage.Math
    /// only). Algorithms follow Curtis / Vallado.
    /// </summary>
    public static class OrbitalMath
    {
        public const double TwoPi = 2.0 * Math.PI;
        private const double Eps = 1e-11;        // near-zero guard for e and node magnitude
        private const double NewtonTol = 1e-12;  // Kepler solver tolerance (radians)
        private const int NewtonMax = 100;

        // ---- State -> Elements -------------------------------------------------

        /// <summary>Osculating Keplerian elements from a Cartesian state about a body of parameter mu.</summary>
        public static KeplerianElements ToElements(StateVector sv, double mu, double epoch = 0.0)
        {
            Vector3D r = sv.Position, v = sv.Velocity;
            double rMag = r.Length(), vMag = v.Length();

            Vector3D h = Vector3D.Cross(r, v);           // specific angular momentum
            double hMag = h.Length();
            Vector3D nVec = Vector3D.Cross(Vector3D.UnitZ, h); // node vector (points at ascending node)
            double nMag = nVec.Length();

            // eccentricity vector (points at periapsis)
            Vector3D eVec = (Vector3D.Cross(v, h) / mu) - (r / rMag);
            double e = eVec.Length();

            double energy = vMag * vMag / 2.0 - mu / rMag;
            double a = Math.Abs(e - 1.0) > Eps ? -mu / (2.0 * energy) : double.PositiveInfinity;

            double i = Math.Acos(Clamp(h.Z / hMag, -1.0, 1.0));

            double raan;
            if (nMag > Eps) { raan = Math.Atan2(nVec.Y, nVec.X); if (raan < 0) raan += TwoPi; }
            else raan = 0.0; // equatorial: node undefined

            double argp;
            if (nMag > Eps && e > Eps)
            {
                argp = Math.Acos(Clamp(Vector3D.Dot(nVec, eVec) / (nMag * e), -1.0, 1.0));
                if (eVec.Z < 0) argp = TwoPi - argp;
            }
            else if (e > Eps)
            {
                // equatorial + eccentric: longitude of periapsis measured from x-axis
                argp = Math.Atan2(eVec.Y, eVec.X); if (argp < 0) argp += TwoPi;
                if (h.Z < 0) argp = TwoPi - argp;
            }
            else argp = 0.0; // circular: periapsis undefined

            double nu;
            if (e > Eps)
            {
                nu = Math.Acos(Clamp(Vector3D.Dot(eVec, r) / (e * rMag), -1.0, 1.0));
                if (Vector3D.Dot(r, v) < 0) nu = TwoPi - nu;
            }
            else if (nMag > Eps)
            {
                // circular inclined: argument of latitude (node -> r)
                nu = Math.Acos(Clamp(Vector3D.Dot(nVec, r) / (nMag * rMag), -1.0, 1.0));
                if (r.Z < 0) nu = TwoPi - nu;
            }
            else
            {
                // circular equatorial: true longitude
                nu = Math.Atan2(r.Y, r.X); if (nu < 0) nu += TwoPi;
            }

            return new KeplerianElements
            {
                SemiMajorAxis = a,
                Eccentricity = e,
                Inclination = i,
                Raan = raan,
                ArgPeriapsis = argp,
                TrueAnomaly = nu,
                Mu = mu,
                Epoch = epoch,
            };
        }

        // ---- Elements -> State -------------------------------------------------

        /// <summary>Cartesian state from Keplerian elements.</summary>
        public static StateVector ToState(KeplerianElements el)
        {
            double e = el.Eccentricity, nu = el.TrueAnomaly, mu = el.Mu;
            double p = el.SemiLatusRectum;

            double cNu = Math.Cos(nu), sNu = Math.Sin(nu);
            double rPf = p / (1.0 + e * cNu);
            var rPerifocal = new Vector3D(rPf * cNu, rPf * sNu, 0.0);
            double sqrtMuP = Math.Sqrt(mu / p);
            var vPerifocal = new Vector3D(-sqrtMuP * sNu, sqrtMuP * (e + cNu), 0.0);

            // Perifocal (PQW) -> inertial basis vectors via the 3-1-3 rotation.
            double cO = Math.Cos(el.Raan), sO = Math.Sin(el.Raan);
            double ci = Math.Cos(el.Inclination), si = Math.Sin(el.Inclination);
            double cw = Math.Cos(el.ArgPeriapsis), sw = Math.Sin(el.ArgPeriapsis);

            var P = new Vector3D(cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, sw * si);
            var Q = new Vector3D(-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, cw * si);

            Vector3D r = rPerifocal.X * P + rPerifocal.Y * Q;
            Vector3D v = vPerifocal.X * P + vPerifocal.Y * Q;
            return new StateVector(r, v);
        }

        // ---- Kepler propagation ------------------------------------------------

        /// <summary>Advance the elements by dt seconds along the orbit (analytic, on-rails).</summary>
        public static KeplerianElements Propagate(KeplerianElements el, double dt)
        {
            double M0 = TrueToMeanAnomaly(el.TrueAnomaly, el.Eccentricity);
            double M = M0 + el.MeanMotion * dt;
            double nu = MeanToTrueAnomaly(M, el.Eccentricity);

            el.TrueAnomaly = el.IsElliptic ? Wrap2Pi(nu) : nu;
            el.Epoch += dt;
            return el;
        }

        // ---- Anomaly conversions ----------------------------------------------

        /// <summary>True anomaly -> mean anomaly (dispatches on regime).</summary>
        public static double TrueToMeanAnomaly(double nu, double e)
        {
            if (e < 1.0)
            {
                double E = 2.0 * Math.Atan2(Math.Sqrt(1.0 - e) * Math.Sin(nu / 2.0),
                                            Math.Sqrt(1.0 + e) * Math.Cos(nu / 2.0));
                return E - e * Math.Sin(E);
            }
            else
            {
                // Hyperbolic true anomaly is bounded by the asymptote |nu| < acos(-1/e);
                // beyond it the orbit doesn't exist. Guard so a bad caller gets +/-inf,
                // not a NaN that detonates a downstream Math.Sign().
                double arg = Math.Sqrt((e - 1.0) / (e + 1.0)) * Math.Tan(nu / 2.0);
                if (arg <= -1.0) return double.NegativeInfinity;
                if (arg >= 1.0) return double.PositiveInfinity;
                double H = 2.0 * Atanh(arg);
                return e * Math.Sinh(H) - H;
            }
        }

        /// <summary>Mean anomaly -> true anomaly (solves Kepler, dispatches on regime).</summary>
        public static double MeanToTrueAnomaly(double M, double e)
        {
            return e < 1.0
                ? EccentricToTrueAnomaly(SolveKeplerElliptic(M, e), e)
                : HyperbolicToTrueAnomaly(SolveKeplerHyperbolic(M, e), e);
        }

        /// <summary>Eccentric anomaly E -> true anomaly (elliptic).</summary>
        public static double EccentricToTrueAnomaly(double E, double e)
            => 2.0 * Math.Atan2(Math.Sqrt(1.0 + e) * Math.Sin(E / 2.0),
                                Math.Sqrt(1.0 - e) * Math.Cos(E / 2.0));

        /// <summary>Hyperbolic anomaly H -> true anomaly (hyperbolic).</summary>
        public static double HyperbolicToTrueAnomaly(double H, double e)
            => 2.0 * Math.Atan2(Math.Sqrt(e + 1.0) * Math.Sinh(H / 2.0),
                                Math.Sqrt(e - 1.0) * Math.Cosh(H / 2.0));

        /// <summary>Asymptote true anomaly of a hyperbola: the limit |nu| approaches (acos(-1/e)).</summary>
        public static double AsymptoteTrueAnomaly(double e) => Math.Acos(-1.0 / e);

        /// <summary>Solve M = E - e sin E for the eccentric anomaly E (Newton-Raphson).</summary>
        public static double SolveKeplerElliptic(double M, double e)
        {
            // C2 hardening: a NaN/Inf mean anomaly (e.g. from a garbage state whose
            // MeanMotion overflowed) must propagate as NaN, not detonate the solver or
            // wrap into a bogus finite answer. Returning NaN lets upstream NaN guards reject.
            if (double.IsNaN(M) || double.IsInfinity(M)) return double.NaN;
            M = Wrap2PiSigned(M);
            double E = M + e * Math.Sin(M); // good initial guess
            for (int k = 0; k < NewtonMax; k++)
            {
                double f = E - e * Math.Sin(E) - M;
                double fp = 1.0 - e * Math.Cos(E);
                double dE = f / fp;
                E -= dE;
                if (Math.Abs(dE) < NewtonTol) break;
            }
            return E;
        }

        /// <summary>Solve M = e sinh H - H for the hyperbolic anomaly H (safeguarded Newton).</summary>
        public static double SolveKeplerHyperbolic(double M, double e)
        {
            // C2 hardening: guard BEFORE any sign/division on M. A finite-but-absurd velocity
            // into ToElements yields MeanMotion = +inf and M = NaN; return NaN (not throw) so
            // upstream NaN guards reject the bad state.
            if (double.IsNaN(M) || double.IsInfinity(M)) return double.NaN;
            if (M == 0.0) return 0.0;

            // NEAR-PARABOLIC FIX (rails-return-radial): the old linear seed H0 = M/(e-1) blows up
            // as e -> 1+ (e = 1.0002, M ~ 1.5 -> H0 ~ 1e4 -> sinh overflow -> NaN), permanently
            // killing a perfectly valid frame's rails. Solve |M| = e sinh H - H on a GUARANTEED
            // bracket instead. f is odd, so solve for |M| and restore the sign; f is strictly
            // increasing (f' = e cosh H - 1 > 0 for e > 1) and convex on H > 0, and the exact
            // inequalities sinh H >= H and sinh H >= H + H^3/6 (H >= 0) give
            //   asinh(m/e)  <=  H  <=  min( m/(e-1), cbrt(6 m/e) ),
            // a bracket that stays bounded for every regime: the asinh side is Vallado's
            // log-scaled seed for large m, the cube-root side is the near-parabolic (Barker-like)
            // seed where the e-1 linear term vanishes. Newton steps that would leave the bracket
            // fall back to bisection, so the iteration can neither overflow sinh nor diverge.
            double sgn = M < 0.0 ? -1.0 : 1.0;
            double m = Math.Abs(M);

            double lo = Asinh(m / e);
            double hi = Math.Pow(6.0 * m / e, 1.0 / 3.0);
            double eMinus1 = e - 1.0;
            if (eMinus1 > 0.0 && m / eMinus1 < hi) hi = m / eMinus1;
            if (hi < lo) hi = lo;            // float-rounding guard: keep a valid bracket
            // sinh overflows past ~710; physical |M| (<= ~1e6) gives hi <= ~182, so this clamp
            // only binds on astronomically absurd inputs (where any finite answer is acceptable).
            if (lo > 705.0) lo = 705.0;
            if (hi > 705.0) hi = 705.0;

            // Start at the asinh lower bound: for large m it is within one Newton step of the
            // root (the old log seed's regime); near-parabolic cases bisect into the basin.
            double H = lo;
            for (int k = 0; k < NewtonMax; k++)
            {
                double f = e * Math.Sinh(H) - H - m;
                if (f > 0.0) hi = H; else lo = H;        // maintain the bracket (f increasing)
                double fp = e * Math.Cosh(H) - 1.0;
                double dH = f / fp;
                double Hn = H - dH;
                // Safeguard: a Newton step outside the live bracket (the near-parabolic flat
                // region around H=0 has fp -> e-1 -> 0) bisects instead.
                if (!(Hn > lo && Hn < hi)) { Hn = 0.5 * (lo + hi); dH = Hn - H; }
                H = Hn;
                if (Math.Abs(dH) < NewtonTol) break;
            }
            return sgn * H;
        }

        // ---- helpers -----------------------------------------------------------

        private static double Clamp(double x, double lo, double hi)
            => x < lo ? lo : (x > hi ? hi : x);

        /// <summary>Wrap to [0, 2pi).</summary>
        public static double Wrap2Pi(double x)
        {
            x %= TwoPi;
            if (x < 0) x += TwoPi;
            return x;
        }

        /// <summary>Wrap to (-pi, pi] (for the elliptic Kepler solve).</summary>
        private static double Wrap2PiSigned(double x)
        {
            x %= TwoPi;
            if (x > Math.PI) x -= TwoPi;
            else if (x < -Math.PI) x += TwoPi;
            return x;
        }

        // .NET Framework 4.8's System.Math lacks the inverse hyperbolics; provide them.
        private static double Atanh(double x) => 0.5 * Math.Log((1.0 + x) / (1.0 - x));

        // asinh(x) = ln(x + sqrt(x^2 + 1)). Used by the hyperbolic Kepler seed; for x^2 overflow
        // (x ~ 1e154+) it returns +inf, which the solver's bracket clamp then bounds — never NaN.
        private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1.0));
    }
}
