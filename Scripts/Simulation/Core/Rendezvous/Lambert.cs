using System;

namespace SEAerospace.Rendezvous
{
    /// <summary>
    /// Lambert's problem: given two position vectors r1, r2 about a body of parameter
    /// mu and a time-of-flight tof, find the transfer orbit connecting them — i.e. the
    /// departure velocity v1 (at r1) and arrival velocity v2 (at r2). This is the heart
    /// of the "plot intercept" button: the intercept burn is v1 - myVelocity, and the
    /// arrival relative velocity (for a flyby or a follow-up matching burn) is
    /// v2 - targetVelocity.
    ///
    /// Formulation: universal-variable / Stumpff-function solver (Curtis, "Orbital
    /// Mechanics for Engineering Students", Algorithm 5.2). Robust across elliptic and
    /// hyperbolic transfers, single-revolution (revs = 0). The prograde/retrograde flag
    /// chooses the short-way vs long-way transfer plane sense by the sign of the
    /// transfer angle's out-of-plane component.
    ///
    /// Pure math, VRage.Math only, C# 6 — matches the orbital core style.
    /// </summary>
    public static class Lambert
    {
        private const int MaxIter = 200;
        private const double Tol = 1e-8;   // tolerance on the universal-anomaly iteration

        /// <summary>
        /// Solve Lambert(r1, r2, tof). On success fills v1 (velocity at r1) and v2
        /// (velocity at r2) and returns true. Returns false (v1=v2=zero) if the geometry
        /// is degenerate (collinear r1/r2, non-positive tof) or the iteration fails to
        /// converge.
        /// </summary>
        /// <param name="prograde">
        /// true = assume the transfer's angular momentum points the "prograde" way
        /// (+Z component &gt;= 0 chooses the short way for the common equatorial-ish case);
        /// false = retrograde / long way. The planner tries both and keeps the cheaper.
        /// </param>
        /// <remarks>
        /// Scope limit (M1): single-revolution only (revs = 0). Multi-rev transfers (needed
        /// for cheap long-horizon rendezvous) are NOT solved — this overload keys the
        /// short/long-way sense off WORLD +Z, which is only correct for near-equatorial
        /// transfer planes. For a steeply inclined or polar transfer, use the overload that
        /// takes an explicit <c>desiredNormal</c> (the intended orbit normal) so the branch
        /// is chosen relative to the actual transfer plane, not a fixed axis.
        /// </remarks>
        public static bool Solve(Vector3D r1, Vector3D r2, double tof, double mu,
            bool prograde, out Vector3D v1, out Vector3D v2)
        {
            // World +Z preserves the original behaviour for every existing caller.
            return Solve(r1, r2, tof, mu, prograde, Vector3D.UnitZ, out v1, out v2);
        }

        /// <summary>
        /// Lambert solve with an explicit <paramref name="desiredNormal"/> setting the
        /// prograde/retrograde sense (M1 fix). The short-vs-long-way branch is chosen from
        /// the sign of (r1 x r2) . desiredNormal instead of a hard-coded world axis, so the
        /// sense is correct for ANY transfer-plane orientation (inclined/polar), not just
        /// near-equatorial. <paramref name="prograde"/> true means "angular momentum aligned
        /// with desiredNormal"; false means anti-aligned (long way). desiredNormal need not
        /// be unit length; a zero/degenerate normal falls back to world +Z.
        /// </summary>
        public static bool Solve(Vector3D r1, Vector3D r2, double tof, double mu,
            bool prograde, Vector3D desiredNormal, out Vector3D v1, out Vector3D v2)
        {
            v1 = Vector3D.Zero;
            v2 = Vector3D.Zero;
            if (tof <= 0.0 || mu <= 0.0) return false;

            double r1m = r1.Length();
            double r2m = r2.Length();
            if (r1m < 1e-6 || r2m < 1e-6) return false;

            if (desiredNormal.LengthSquared() < 1e-18) desiredNormal = Vector3D.UnitZ;

            // Transfer angle dTheta in [0, 2pi). The component of (r1 x r2) along the DESIRED
            // orbit normal disambiguates short vs long way for the requested sense.
            Vector3D cross = Vector3D.Cross(r1, r2);
            double senseDot = Vector3D.Dot(cross, desiredNormal);
            double dot = Vector3D.Dot(r1, r2);
            double dTheta = Math.Acos(Clamp(dot / (r1m * r2m), -1.0, 1.0));
            if (prograde)
            {
                if (senseDot < 0.0) dTheta = OrbitalTwoPi - dTheta;
            }
            else
            {
                if (senseDot >= 0.0) dTheta = OrbitalTwoPi - dTheta;
            }

            double sinDt = Math.Sin(dTheta);
            // A == 0 means the transfer angle is 0 or pi (collinear) -> plane undefined.
            if (Math.Abs(sinDt) < 1e-12) return false;
            double A = sinDt * Math.Sqrt(r1m * r2m / (1.0 - Math.Cos(dTheta)));
            if (Math.Abs(A) < 1e-12) return false;

            // Solve y(z) via the universal variable z (z>0 elliptic, z<0 hyperbolic,
            // z=0 parabolic). F(z) = tof(z) - tof = 0, by Newton with a numeric/analytic
            // derivative, bracketed in z by first marching to a sign change.
            double z = 0.0;
            double ratio = 1.0;
            int iter = 0;
            double yz = 0.0;
            while (iter < MaxIter)
            {
                yz = Y(z, A, r1m, r2m);
                // If y goes negative, push z up until y >= 0 (Curtis' guard).
                if (A > 0.0 && yz < 0.0)
                {
                    double zLow = z;
                    while (Y(zLow, A, r1m, r2m) < 0.0) zLow += 0.1;
                    z = zLow;
                    yz = Y(z, A, r1m, r2m);
                }

                double Fz = TimeOfFlight(z, A, yz, mu) - tof;
                double dFz = TimeOfFlightDeriv(z, A, yz, r1m, r2m, mu);
                if (Math.Abs(dFz) < 1e-30)
                {
                    // H2 fix: a vanishing derivative stalls Newton. ONLY accept the current z
                    // if F(z) is actually at the root (within a tof-scaled tolerance); otherwise
                    // this is an unconverged stall, not a solution, so fail cleanly. Without this
                    // the break shipped whatever z it held, bypassing the non-convergence guard
                    // below — the robustness backstop behind C1/C2.
                    if (Math.Abs(Fz) <= Tol * Math.Max(1.0, tof)) break;
                    return false;
                }
                ratio = Fz / dFz;
                z -= ratio;
                iter++;
                if (Math.Abs(ratio) <= Tol) break;
            }
            if (iter >= MaxIter && Math.Abs(ratio) > Tol * 100.0) return false;

            yz = Y(z, A, r1m, r2m);
            if (yz <= 0.0 || double.IsNaN(yz) || double.IsInfinity(yz)) return false;

            // Lagrange coefficients from the converged y(z).
            double f = 1.0 - yz / r1m;
            double g = A * Math.Sqrt(yz / mu);
            double gdot = 1.0 - yz / r2m;
            if (Math.Abs(g) < 1e-30) return false;

            v1 = (r2 - f * r1) / g;
            v2 = (gdot * r2 - r1) / g;

            if (IsBad(v1, r1m, mu) || IsBad(v2, r2m, mu)) { v1 = Vector3D.Zero; v2 = Vector3D.Zero; return false; }

            // C1 fix: reject NEAR-PARABOLIC transfers (e ~ 1). KeplerianElements documents the
            // e ~ 1 regime as unsupported; OrbitalMath.ToElements -> OrbitPropagation.StateAt
            // cannot fly such an osculating orbit accurately, so the solver would hand back a
            // "valid" v1 whose flown trajectory misses the target by thousands of km. Compute the
            // eccentricity from the standard eccentricity-vector formula on (r1, v1, mu):
            //     e_vec = ((|v|^2 - mu/r) r - (r.v) v) / mu,   e = |e_vec|
            // and bail if |e - 1| < 1e-3 (the unsupported band) rather than ship a bad transfer.
            double v1sq = v1.LengthSquared();
            Vector3D eVec = ((v1sq - mu / r1m) * r1 - Vector3D.Dot(r1, v1) * v1) / mu;
            double e = eVec.Length();
            if (Math.Abs(e - 1.0) < 1e-3) { v1 = Vector3D.Zero; v2 = Vector3D.Zero; return false; }

            return true;
        }

        // y(z) = r1 + r2 + A (z S(z) - 1) / sqrt(C(z))
        private static double Y(double z, double A, double r1m, double r2m)
        {
            double C = StumpffC(z);
            double S = StumpffS(z);
            double sqrtC = Math.Sqrt(C);
            if (sqrtC < 1e-30) sqrtC = 1e-30;
            return r1m + r2m + A * (z * S - 1.0) / sqrtC;
        }

        // tof(z) = [ (y/C)^1.5 S + A sqrt(y) ] / sqrt(mu)
        private static double TimeOfFlight(double z, double A, double y, double mu)
        {
            double C = StumpffC(z);
            double S = StumpffS(z);
            double yc = y / C;
            return (Math.Pow(yc, 1.5) * S + A * Math.Sqrt(y)) / Math.Sqrt(mu);
        }

        // dT/dz (Curtis eq. 5.43): the analytic derivative used by Newton.
        private static double TimeOfFlightDeriv(double z, double A, double y,
            double r1m, double r2m, double mu)
        {
            double C = StumpffC(z);
            double S = StumpffS(z);
            double sqrtMu = Math.Sqrt(mu);
            if (Math.Abs(z) < 1e-6)
            {
                // z -> 0 limit (Curtis): avoids 0/0 in the general expression.
                double y0 = Y(0.0, A, r1m, r2m);
                double sqrtY0 = Math.Sqrt(y0);
                return (Math.Sqrt(2.0) / 40.0) * Math.Pow(y0, 1.5)
                       + (A / 8.0) * (sqrtY0 + A * Math.Sqrt(1.0 / (2.0 * y0)));
            }
            double sqrtC = Math.Sqrt(C);
            double yc = y / C;
            double term1 = Math.Pow(yc, 1.5)
                           * (1.0 / (2.0 * z) * (C - 1.5 * S / C) + 0.75 * S * S / C);
            double term2 = (A / 8.0)
                           * (3.0 * S / C * Math.Sqrt(y) + A * sqrtC / Math.Sqrt(y));
            return (term1 + term2) / sqrtMu;
        }

        // Stumpff functions. C(z) = (1-cos sqrt z)/z for z>0; (cosh sqrt(-z)-1)/(-z) for z<0; 1/2 at 0.
        private static double StumpffC(double z)
        {
            if (z > 1e-8)
            {
                double sq = Math.Sqrt(z);
                return (1.0 - Math.Cos(sq)) / z;
            }
            if (z < -1e-8)
            {
                double sq = Math.Sqrt(-z);
                return (Math.Cosh(sq) - 1.0) / (-z);
            }
            // series near 0
            return 0.5 - z / 24.0 + z * z / 720.0;
        }

        // S(z) = (sqrt z - sin sqrt z)/(sqrt z)^3 for z>0; (sinh sqrt(-z) - sqrt(-z))/(sqrt(-z))^3 for z<0.
        private static double StumpffS(double z)
        {
            if (z > 1e-8)
            {
                double sq = Math.Sqrt(z);
                return (sq - Math.Sin(sq)) / (sq * sq * sq);
            }
            if (z < -1e-8)
            {
                double sq = Math.Sqrt(-z);
                return (Math.Sinh(sq) - sq) / (sq * sq * sq);
            }
            return 1.0 / 6.0 - z / 120.0 + z * z / 5040.0;
        }

        private const double OrbitalTwoPi = 2.0 * Math.PI;

        private static double Clamp(double x, double lo, double hi)
        {
            return x < lo ? lo : (x > hi ? hi : x);
        }

        // Speed of light: a hard physical ceiling no orbital transfer velocity can exceed.
        private const double SpeedOfLight = 2.998e8; // m/s

        // A velocity is "bad" if non-finite OR implausibly large. C2 fix: the old gate only
        // rejected NaN/Inf, so a FINITE-but-absurd v1 (1e18..1e60 m/s) — which a degenerate
        // geometry can produce — passed as ok=true, then downstream OrbitalMath.ToElements gave
        // MeanMotion = +inf and M = NaN, detonating the hyperbolic Kepler solver. We now reject
        // any speed above the larger of: the speed of light (a hard physical ceiling) and a
        // generous multiple of the local escape speed sqrt(2 mu / r) (so a legitimately fast but
        // sane near-body transfer is still accepted). Anything past that is non-physical garbage.
        private static bool IsBad(Vector3D v, double rMag, double mu)
        {
            if (double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z))
                return true;
            double escape = (rMag > 0.0 && mu > 0.0) ? Math.Sqrt(2.0 * mu / rMag) : 0.0;
            double bound = Math.Max(SpeedOfLight, 1000.0 * escape);
            double speed = v.Length();
            return speed > bound || double.IsNaN(speed) || double.IsInfinity(speed);
        }
    }
}
