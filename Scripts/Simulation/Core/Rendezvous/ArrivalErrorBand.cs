using SEAerospace.Frames;

namespace SEAerospace.Rendezvous
{
    /// <summary>
    /// The honest error band on an intercept's predicted arrival miss, produced by
    /// <see cref="InterceptBand.BandFor"/>. A point-estimate miss (<see cref="MissNominal"/>)
    /// plus the target-uncertainty-derived 1-σ/2-σ envelope — the capture bubble's numeric
    /// backing.
    /// </summary>
    public struct ArrivalErrorBand
    {
        public bool Valid;             // false if the plan had no solution
        public double MissNominal;     // the planner's point-estimate miss (m)
        public double OneSigma;        // propagated 1-σ position uncertainty at arrival (m)
        public double MissLower1Sigma; // max(0, nominal - 1σ)
        public double MissUpper1Sigma; // nominal + 1σ
        public double MissUpper2Sigma; // nominal + 2σ (the conservative capture-commit edge)
    }

    /// <summary>
    /// Decoupled "capture bubble" arithmetic over a <see cref="ManeuverPlan"/> and a SCALAR
    /// position uncertainty — the band math that used to live on the (now-deleted) Rendezvous
    /// Target. It needs only the estimated orbit's POSITION sigma and its growth, never the
    /// whole tracking Target, so it takes those directly:
    ///   • <paramref name="sigma"/>  — 1-σ position error (m) of the estimate AT
    ///     <paramref name="lastFixSeconds"/> (the moment of the last fix).
    ///   • <paramref name="growthMps"/> — linear growth of the 1-σ per second after the fix
    ///     (meters of added 1-σ per second; the dominant along-track decay, first-order). 0 for
    ///     an exact / Known orbit.
    /// The propagated 1-σ at time t is therefore sigma + growthMps·max(0, t − lastFixSeconds)
    /// (non-decreasing — knowledge only decays while coasting). This is the same coarse
    /// linearization the catalog's growth model uses, kept identical so results are bit-for-bit.
    /// Pure arithmetic on the plan + the scalar sigma; the plan is not re-solved. C# 6.
    /// </summary>
    public static class InterceptBand
    {
        /// <summary>The propagated 1-σ position uncertainty (m) at absolute time
        /// <paramref name="t"/>: sigma + growthMps·max(0, t − lastFixSeconds). Never shrinks
        /// below the fix-time sigma (knowledge only decays while coasting).</summary>
        public static double SigmaAt(double sigma, double growthMps, double lastFixSeconds, double t)
        {
            double dt = t - lastFixSeconds;
            if (dt < 0.0) dt = 0.0;
            return sigma + growthMps * dt;
        }

        /// <summary>
        /// Widen a plan's predicted arrival into an HONEST error band using the target's scalar
        /// uncertainty. The planner reports a point-estimate miss assuming the orbit is exact;
        /// this turns it into "arrive at ~<c>MissNominal</c> m, ±<c>OneSigma</c> m (1-σ)", where
        /// the band is the uncertainty propagated to the plan's arrival epoch
        /// (<see cref="SigmaAt"/>). It is the "capture bubble" the UX doc draws — fat for a
        /// fuzzy contact, tightening as the track refines (sigma/growth shrink) and as arrival
        /// nears (less coast since the fix). Pure arithmetic; the plan is not re-solved.
        /// </summary>
        public static ArrivalErrorBand BandFor(ManeuverPlan plan, double sigma, double growthMps,
            double lastFixSeconds)
        {
            ArrivalErrorBand band = new ArrivalErrorBand();
            if (plan == null)
            {
                band.Valid = false;
                return band;
            }
            // M1 fix: on a NoSolution plan, Arrival is default (ArrivalTime = 0), so SigmaAt would
            // be evaluated at t=0 — a meaningless band off a non-existent arrival. Early-return a
            // fully-zeroed/invalid band BEFORE touching plan.Arrival.
            if (plan.Status != PlanStatus.Ok)
            {
                band.Valid = false;
                return band;
            }
            band.Valid = true;
            band.MissNominal = plan.Arrival.MissDistance;
            double oneSigma = SigmaAt(sigma, growthMps, lastFixSeconds, plan.Arrival.ArrivalTime);
            band.OneSigma = oneSigma;
            // 1-σ / 2-σ outer edges of the miss: the nominal plus the propagated position
            // uncertainty (the contact could really be that much farther off than planned).
            band.MissUpper1Sigma = band.MissNominal + oneSigma;
            band.MissUpper2Sigma = band.MissNominal + 2.0 * oneSigma;
            // Inner edge can't be negative (you can't be closer than zero).
            double lower = band.MissNominal - oneSigma;
            band.MissLower1Sigma = lower < 0.0 ? 0.0 : lower;
            return band;
        }

        /// <summary>
        /// Is the intercept COMMITTABLE at the given confidence? True when the WHOLE 1-σ (or
        /// 2-σ if <paramref name="twoSigma"/>) error band fits inside the capture range — i.e.
        /// even the unlucky 1-/2-σ outcome still arrives within capture. This is the
        /// "bubble fits inside the capture range, the intercept is committable" gate of the
        /// acquisition doc §3: a fuzzy contact is NOT committable until tracked/pinged enough
        /// that its bubble has tightened below the capture radius.
        /// </summary>
        public static bool IsCommittable(ManeuverPlan plan, RendezvousParams caps,
            double sigma, double growthMps, double lastFixSeconds, bool twoSigma)
        {
            if (plan == null || plan.Status != PlanStatus.Ok) return false;
            if (caps == null) caps = RendezvousParams.Default;
            ArrivalErrorBand band = BandFor(plan, sigma, growthMps, lastFixSeconds);
            double outer = twoSigma ? band.MissUpper2Sigma : band.MissUpper1Sigma;
            return outer <= caps.EnterRangeMeters;
        }
    }
}
