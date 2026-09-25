namespace SEAerospace.Frames
{
    /// <summary>
    /// Configurable, STICKY rendezvous/merge thresholds. ENTER (merge) is close AND slow;
    /// EXIT (split) is DISTANCE-ONLY — separate enter/exit RANGES give Schmitt-trigger
    /// hysteresis so two frames don't thrash at the boundary: merge only when tightly close
    /// AND slow; once merged, stay merged until clearly far. The ENTER rel-speed is the
    /// "similarity"/flyby gate (it decides whether an approach is a rendezvous or a pass —
    /// it never ejects an existing member). There is NO rel-speed exit (rob 2026-06-16:
    /// "no speed exit") — membership is purely volumetric, so a member moving fast relative
    /// to the anchor stays in the conjunction (formation cohesion) until it drifts out of
    /// range. A dwell requires the merge conditions to hold briefly so a transient graze (a
    /// fast flyby clipping the range) doesn't trigger a pointless merge-then-resplit.
    ///
    /// Plain POCO (public fields, parameterless ctor) so it serializes straight into the
    /// world config alongside the system definition.
    /// </summary>
    public class RendezvousParams
    {
        public double EnterRangeMeters = 10000.0;  // merge when separation < this
        public double ExitRangeMeters = 15000.0;   // split when separation > this (>= EnterRange; < SlotRadius 20 km)
        public double EnterRelSpeedMps = 1000.0;   // merge when |Δv| < this — ORBITAL rel-speed (km/s-scale), so large; the flyby gate, NOT an exit
        public double EnterDwellSeconds = 2.0;     // merge conditions must hold this long first (0 = instant)

        public RendezvousParams() { }

        public static RendezvousParams Default { get { return new RendezvousParams(); } }

        /// <summary>True if exit range ≥ enter range > 0, the enter rel-speed > 0, and dwell ≥ 0.</summary>
        public bool Validate(out string error)
        {
            error = null;
            if (EnterRangeMeters <= 0.0 || ExitRangeMeters < EnterRangeMeters)
            { error = "ExitRangeMeters must be >= EnterRangeMeters > 0"; return false; }
            if (EnterRelSpeedMps <= 0.0)
            { error = "EnterRelSpeedMps must be > 0"; return false; }
            if (EnterDwellSeconds < 0.0)
            { error = "EnterDwellSeconds must be >= 0"; return false; }
            return true;
        }
    }
}
