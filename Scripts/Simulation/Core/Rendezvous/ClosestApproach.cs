using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.Rendezvous
{
    /// <summary>
    /// One closest-approach (conjunction) event between two orbits: the absolute time of
    /// closest approach, the miss distance, and the relative velocity there.
    /// </summary>
    public struct ApproachEvent
    {
        public double Time;             // absolute time of closest approach (seconds)
        public double MissDistance;     // |r_target - r_me| at that time (meters)
        public Vector3D RelativeVelocity; // v_target - v_me at that time (m/s)
        public double RelativeSpeed;    // |RelativeVelocity| (m/s)
        public bool Found;              // false = no approach located in the window

        public static ApproachEvent None
        {
            get
            {
                ApproachEvent e = new ApproachEvent();
                e.Found = false;
                e.Time = double.NaN;
                e.MissDistance = double.PositiveInfinity;
                e.RelativeSpeed = double.PositiveInfinity;
                e.RelativeVelocity = Vector3D.Zero;
                return e;
            }
        }
    }

    /// <summary>
    /// TCA / closest-approach predictor. Given two orbits about the same body (same mu)
    /// and a search window [t0, t0 + horizon], finds where and when the two come nearest.
    ///
    /// Method: closest approach occurs where the range-rate g(t) = dot(r_rel, v_rel) = 0
    /// transitioning from negative (closing) to positive (opening). We coarse-sample g
    /// over the window, bracket each negative->positive zero crossing, and refine it by
    /// bisection. Among all bracketed minima we keep the one with the smallest miss
    /// distance (and optionally return all of them).
    ///
    /// This is the foundational "how close will I get, and how fast" feedback for ANY
    /// plan — direct Lambert, phasing, or a manual node — independent of how the plan was
    /// produced. Pure math, VRage.Math + the orbital core only, C# 6.
    /// </summary>
    public static class ClosestApproach
    {
        /// <summary>
        /// The globally DEEPEST (smallest-miss) closest approach of <paramref name="me"/> to
        /// <paramref name="target"/> anywhere in [t0, t0+horizon] — NOT necessarily the earliest.
        /// H1: this scans the whole window and returns the single approach with the smallest miss;
        /// an earlier but shallower conjunction is intentionally NOT returned here (callers rely on
        /// "deepest over the window"). For the EARLIEST/NEXT approach from a given time, use
        /// <see cref="FindNext"/> (or <see cref="FindAll"/>, which is time-ordered).
        /// <paramref name="samples"/> is the number of coarse buckets across the window (more =
        /// less chance of skipping a brief deep approach). Returns ApproachEvent.None if no
        /// closing-to-opening crossing exists in the window (e.g. monotonically separating).
        /// </summary>
        public static ApproachEvent Find(KeplerianElements me, KeplerianElements target,
            double t0, double horizon, int samples)
        {
            List<ApproachEvent> all = FindAll(me, target, t0, horizon, samples);
            ApproachEvent best = ApproachEvent.None;
            for (int i = 0; i < all.Count; i++)
            {
                if (!best.Found || all[i].MissDistance < best.MissDistance)
                    best = all[i];
            }

            // M2 fix: ALWAYS compare against the dense sampled minimum, not just as a
            // last-resort fallback. A brief deep pass between coarse samples (g dipping
            // negative and back positive inside one bucket) can be missed by the bracketed
            // crossings or refined to the wrong, shallower minimum; the dense resample is
            // the safety net that guarantees a missed deep crossing is still surfaced. It is
            // also the endpoint-minimum fallback when no crossing was bracketed at all.
            ApproachEvent dense = SampledMinimum(me, target, t0, horizon, samples);
            if (!best.Found || (dense.Found && dense.MissDistance < best.MissDistance))
                best = dense;
            return best;
        }

        /// <summary>
        /// The EARLIEST (next) bracketed closest approach at or after <paramref name="tFrom"/>
        /// within [t0, t0+horizon]. H1: this is the "next conjunction" entry point — distinct
        /// from <see cref="Find"/>, which returns the DEEPEST approach over the window regardless
        /// of time. Returns ApproachEvent.None if no closing-to-opening crossing exists after
        /// tFrom. Backed by <see cref="FindAll"/>, which is guaranteed time-ordered.
        /// </summary>
        public static ApproachEvent FindNext(KeplerianElements me, KeplerianElements target,
            double t0, double horizon, int samples, double tFrom)
        {
            List<ApproachEvent> all = FindAll(me, target, t0, horizon, samples);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Time >= tFrom) return all[i]; // FindAll is in ascending time order
            }
            return ApproachEvent.None;
        }

        /// <summary>
        /// All bracketed local closest approaches in the window, in ASCENDING time order (the
        /// fine grid is walked forward, so result[i].Time is strictly increasing). Useful for
        /// "next few conjunctions" displays and the EARLIEST-approach <see cref="FindNext"/>.
        /// Each is refined by bisection on the range-rate.
        /// </summary>
        public static List<ApproachEvent> FindAll(KeplerianElements me, KeplerianElements target,
            double t0, double horizon, int samples)
        {
            List<ApproachEvent> result = new List<ApproachEvent>();
            if (samples < 2) samples = 2;
            if (horizon <= 0.0) return result;

            // M2 fix: sub-sample each coarse bucket so a brief deep pass that both enters and
            // exits within one bucket (g: + -> - -> +) is not skipped. We walk a finer grid
            // (sub steps per coarse bucket) and bracket EVERY negative->positive crossing of
            // the range-rate g(t) = r_rel . v_rel, which is exactly the set of range minima
            // (closing turning to opening). A pos->neg crossing is a range MAXIMUM and is
            // ignored; the next neg->pos after it catches the following minimum, so detecting
            // all neg->pos crossings on the fine grid covers the brief-deep-pass case the
            // coarse grid missed.
            const int sub = 8;                  // fine steps per coarse bucket
            int fine = samples * sub;
            double fdt = horizon / fine;
            double tPrev = t0;
            double gPrev = RangeRate(me, target, tPrev);
            for (int i = 1; i <= fine; i++)
            {
                double tCur = (i == fine) ? (t0 + horizon) : (t0 + i * fdt);
                double gCur = RangeRate(me, target, tCur);

                // A minimum is a g sign change from negative (closing) to positive (opening).
                if (gPrev < 0.0 && gCur >= 0.0)
                {
                    double tca = RefineBisection(me, target, tPrev, tCur);
                    result.Add(Evaluate(me, target, tca));
                }
                tPrev = tCur;
                gPrev = gCur;
            }
            return result;
        }

        /// <summary>Relative range-rate g(t) = dot(r_rel, v_rel); zero at an extremum of range.</summary>
        public static double RangeRate(KeplerianElements me, KeplerianElements target, double t)
        {
            StateVector sMe = OrbitPropagation.StateAt(me, t);
            StateVector sTg = OrbitPropagation.StateAt(target, t);
            Vector3D rRel = sTg.Position - sMe.Position;
            Vector3D vRel = sTg.Velocity - sMe.Velocity;
            return Vector3D.Dot(rRel, vRel);
        }

        /// <summary>Build a full ApproachEvent (miss + rel velocity) at a known time.</summary>
        public static ApproachEvent Evaluate(KeplerianElements me, KeplerianElements target, double t)
        {
            StateVector sMe = OrbitPropagation.StateAt(me, t);
            StateVector sTg = OrbitPropagation.StateAt(target, t);
            Vector3D rRel = sTg.Position - sMe.Position;
            Vector3D vRel = sTg.Velocity - sMe.Velocity;
            ApproachEvent e = new ApproachEvent();
            e.Found = true;
            e.Time = t;
            e.MissDistance = rRel.Length();
            e.RelativeVelocity = vRel;
            e.RelativeSpeed = vRel.Length();
            return e;
        }

        // Bisection on the range-rate g between a closing (g<0) and opening (g>0) sample.
        private static double RefineBisection(KeplerianElements me, KeplerianElements target,
            double tLo, double tHi)
        {
            double gLo = RangeRate(me, target, tLo);
            for (int k = 0; k < 80; k++)
            {
                double tMid = 0.5 * (tLo + tHi);
                double gMid = RangeRate(me, target, tMid);
                if (Math.Abs(tHi - tLo) < 1e-4) return tMid;
                if ((gLo < 0.0) == (gMid < 0.0)) { tLo = tMid; gLo = gMid; }
                else tHi = tMid;
            }
            return 0.5 * (tLo + tHi);
        }

        // Global minimum over a dense resample — endpoint-safe fallback.
        private static ApproachEvent SampledMinimum(KeplerianElements me, KeplerianElements target,
            double t0, double horizon, int samples)
        {
            int n = samples * 4;
            double dt = horizon / n;
            double bestMiss = double.PositiveInfinity;
            double bestT = t0;
            for (int i = 0; i <= n; i++)
            {
                double t = t0 + i * dt;
                StateVector sMe = OrbitPropagation.StateAt(me, t);
                StateVector sTg = OrbitPropagation.StateAt(target, t);
                double miss = (sTg.Position - sMe.Position).Length();
                if (miss < bestMiss) { bestMiss = miss; bestT = t; }
            }
            return Evaluate(me, target, bestT);
        }
    }
}
