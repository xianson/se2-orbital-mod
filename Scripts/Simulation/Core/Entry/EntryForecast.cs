using System;
using SEAerospace.Orbital;

namespace SEAerospace.Entry
{
    /// <summary>
    /// ENTRY FORECASTS on a planned path (game-free; Tests/EntryTests). 2026-10-18, the UI audit (U3): the map marked the
    /// entry only on the orbit you are on now (EntryHost.Prediction), so a planned deorbit leg showed nothing; a pass that
    /// stays above the air said nothing either; where a path comes down was never told.
    /// </summary>
    public static class EntryForecast
    {
        /// <summary>The longest stretch of a leg forecast (s): as EntryHost's own prediction.</summary>
        public const double MaxHorizon = 6 * 3600;

        /// <summary>The pass a leg of the path (its conic, from t0 to t1) makes through the band: null when it does not brake
        /// in it before t1 (no band, the periapsis above it, under the cap all the way).</summary>
        public static Pass ForLeg(KeplerianElements el, Band b, Vector3D w, double cap, double t0, double t1, double beta)
        {
            if (!b.IsValid || !(t1 > t0) || !(el.PeriapsisRadius < b.Top)) return null;
            var p = Reentry.Predict(el, b, w, cap, t0, Math.Min(t1 - t0, MaxHorizon), 0, beta);
            if (!p.Braked || double.IsNaN(p.EnterTime) || p.EnterTime > t1) return null;
            return p;
        }

        /// <summary>How far (m) the periapsis is above the entry interface (the band's top): NaN when it already dips into
        /// the band, or the body has none. "Pe 84 km: above the air (needs < 19 km)".</summary>
        public static double AirGap(double periapsisRadius, Band b)
            => b.IsValid && periapsisRadius >= b.Top ? periapsisRadius - b.Top : double.NaN;

        /// <summary>
        /// Where a path comes to the ground (radius R): after a pass into the air, from the orbit at the border (its first
        /// inbound crossing of R within maxAfter seconds; else the border point itself); for a plain conic, its first inbound
        /// crossing of R in [t0, t1]. The position is about the body (inertial), at time tLand. Drag below the border is not
        /// flown: an estimate (short of the conic's point when the air is thick).
        /// </summary>
        public static bool Landing(KeplerianElements el, double t0, double t1, double R, Pass pass, double maxAfter, out double tLand, out Vector3D at)
        {
            tLand = double.NaN; at = default;
            if (!(R > 0)) return false;
            if (pass != null && pass.Landing && pass.HasBottomEl && !double.IsNaN(pass.BottomTime))
            {
                double tb = pass.BottomTime;
                if (OrbitPropagation.TryFirstInboundCrossing(pass.BottomEl, R, tb, tb + maxAfter, out double th)) tLand = th;
                else tLand = tb;
                at = OrbitPropagation.StateAt(pass.BottomEl, tLand).Position;
                return Finite(at);
            }
            if (!(el.PeriapsisRadius < R) || !(t1 > t0)) return false;
            if (!OrbitPropagation.TryFirstInboundCrossing(el, R, t0, t1 + 1e-6, out double tc)) return false;
            tLand = tc;
            at = OrbitPropagation.StateAt(el, tc).Position;
            return Finite(at);
        }

        static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
    }
}
