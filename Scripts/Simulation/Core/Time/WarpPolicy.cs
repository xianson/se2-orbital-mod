namespace SEAerospace.Time
{
    /// <summary>
    /// The time-warp governing rule as pure logic (docs/architecture-proximity-frames.md,
    /// "Time warp policy"), REVISED 2026-06-12 per rob's directive ("allow a warp speed in
    /// planet voxel range (limit amount per fractional altitude) and do high speed implied
    /// speed jumps, limit being 1x in atmosphere at all"): warp is unrestricted while
    /// everything is on rails; near a planet the old blanket "materialized → x1" hold is
    /// replaced by an ALTITUDE-LADDERED CAP — the higher you are above the atmosphere, the
    /// more warp is allowed (<see cref="AltitudeCap"/>), with x1 ABSOLUTE inside the
    /// atmosphere. The cap never mutates the chosen warp (the no-auto-warp law): it only
    /// caps the effective rate DOWN; the choice resumes as you climb.
    ///
    /// This helper does NOT query the game. The caller measures the controlled grid's
    /// fractional altitude (<see cref="FractionalAltitude"/>) / decides the lock conditions
    /// and passes them in; the policy turns them into the maximum allowed timescale.
    ///
    /// C# 6, game-free, stateless.
    /// </summary>
    public static class WarpPolicy
    {
        /// <summary>Real-time multiplier — the forced cap when anything is materialized.</summary>
        public const double Realtime = 1.0;

        /// <summary>No cap from altitude (the f &gt;= <see cref="AltBand4Top"/> band, and the
        /// "no constraint applies at all" return of the cap evaluation).</summary>
        public const double Unlimited = double.PositiveInfinity;

        // ---- THE ALTITUDE → MAX-RUNG TABLE (rob will tune by feel; seed values) -------------
        // Fractional altitude f runs 0 at the top of the atmosphere band (airless bodies: at
        // the R_c shell) to 1 at the keep edge; each band allows warp up to a LADDER RUNG.
        // IN ATMOSPHERE the cap is x1 always — absolute (rob's words) — which the caller
        // enforces BEFORE consulting this table (f<0 never reaches it; the atmosphere test is
        // a radius/air-density condition, not an f band).
        //
        // LOOSENED 2026-06-17 (rob "altitude cap is too aggressive"): the f band is STRUCTURALLY
        // THIN — it spans only the R_c..1.5·R_c shell (keep = R_c × ShellKeepHysteresis), so f
        // saturates over a half-R_c slab — and the old rungs (x2/x10/x50/x100, unlimited only
        // above f=0.85) capped warp to a fraction of the now-x1M ladder across almost the whole
        // cell. Rungs raised ~100× and the unlimited band pulled in to f≥0.65, so warp opens up
        // fast as you climb off the surface (descents are still caught by the atmosphere x1 floor
        // and the graded arrival cap; this table only governs a real grid coasting UP in the cell).
        public const double AltBand1Top = 0.10;   // f <  0.10 -> x10
        public const double AltBand2Top = 0.25;   // f <  0.25 -> x100
        public const double AltBand3Top = 0.45;   // f <  0.45 -> x1000
        public const double AltBand4Top = 0.65;   // f <  0.65 -> x10000
        public const double AltCap1 = 10.0;       // f >= 0.65 -> Unlimited (by altitude)
        public const double AltCap2 = 100.0;
        public const double AltCap3 = 1000.0;
        public const double AltCap4 = 10000.0;

        /// <summary>
        /// Fractional altitude of radius <paramref name="r"/> on the band from
        /// <paramref name="floorRadius"/> (top of the atmosphere; airless bodies pass R_c) to
        /// <paramref name="keepRadius"/> (the keep edge where the voxel lets go):
        /// f = clamp((r − floor) / (keep − floor), 0, 1). Degenerate band (keep &lt;= floor)
        /// or NaN input: 0 below/at the keep, 1 above it (never an exception, never NaN).
        /// </summary>
        public static double FractionalAltitude(double r, double floorRadius, double keepRadius)
        {
            if (double.IsNaN(r)) return 0.0;
            if (keepRadius <= floorRadius) return r > keepRadius ? 1.0 : 0.0;
            double f = (r - floorRadius) / (keepRadius - floorRadius);
            return f < 0.0 ? 0.0 : (f > 1.0 ? 1.0 : f);
        }

        /// <summary>The max allowed ladder rung for fractional altitude <paramref name="f"/>
        /// (the table above). NaN is treated as f = 0 (the most restrictive band — defensive,
        /// same spirit as the realtime floor everywhere else).</summary>
        public static double AltitudeCap(double f)
        {
            if (double.IsNaN(f) || f < AltBand1Top) return AltCap1;
            if (f < AltBand2Top) return AltCap2;
            if (f < AltBand3Top) return AltCap3;
            if (f < AltBand4Top) return AltCap4;
            return Unlimited;
        }

        /// <summary>
        /// Resolve the timescale the clock should actually run at under an explicit CAP (the
        /// altitude ladder / a x1 lock expressed as cap = <see cref="Realtime"/> /
        /// <see cref="Unlimited"/> when nothing constrains): min(chosen, cap), never below x1,
        /// WITHOUT mutating the clock's stored choice — the no-auto-warp law's "caps only cap
        /// down". Returns 1 for a null clock; a NaN/sub-x1 cap is floored to x1 (defensive).
        /// </summary>
        public static double EffectiveTimescale(UniverseTime clock, double cap)
        {
            if (clock == null) return Realtime;
            double c = (double.IsNaN(cap) || cap < Realtime) ? Realtime : cap;
            double ts = clock.Timescale;
            return ts < c ? ts : c;
        }

        /// <summary>
        /// Max allowed timescale given the BOOL lock condition (the pre-altitude-ladder form,
        /// kept for the offline twins — TimeTest / Orbital.Tests pin it; the live clock now
        /// evaluates a graded cap through <see cref="EffectiveTimescale(UniverseTime,double)"/>):
        ///   any materialized => <see cref="Realtime"/> (x1);
        ///   all on rails     => <paramref name="requestedStep"/> (but never below x1).
        /// </summary>
        public static double MaxTimescale(bool anyMaterialized, double requestedStep)
        {
            if (anyMaterialized) return Realtime;
            return requestedStep < Realtime ? Realtime : requestedStep;
        }

        /// <summary>
        /// Resolve the timescale the clock should actually run at: clamp the clock's current
        /// requested <see cref="UniverseTime.Timescale"/> down to the policy cap. While anything
        /// is materialized this returns x1 WITHOUT mutating the clock's stored request, so the
        /// player's chosen warp is restored automatically once everything is back on rails.
        /// Returns 1 for a null clock.
        /// </summary>
        public static double EffectiveTimescale(UniverseTime clock, bool anyMaterialized)
        {
            if (clock == null) return Realtime;
            double cap = MaxTimescale(anyMaterialized, clock.Timescale);
            double ts = clock.Timescale;
            return ts < cap ? ts : cap;
        }

        /// <summary>
        /// Stamp-based realtime-hold predicate WITH the component-ordering margin — the shared
        /// rule behind the clock's stamped locks (the thrust-drain hold (c) and the
        /// arrival-imminent hold (d)). A producer (FrameManager, BeforeSimulation) stamps
        /// <paramref name="stampSeconds"/> = universe-now while its real-time activity is live;
        /// the clock holds x1 while the stamp is younger than <paramref name="holdSeconds"/>.
        ///
        /// THE MARGIN (regression guard, 2026-06-12): both components run in BeforeSimulation
        /// with UNSPECIFIED relative order. If the clock ticks BEFORE the producer, the freshest
        /// stamp it can see is one full tick old — and if that previous tick advanced at the
        /// CHOSEN warp (the first burn/approach tick at x10000), "one tick old" is
        /// chosen x tickSeconds UNIVERSE seconds old (166.7 s at x10000), which a bare
        /// 2 s hold would judge expired even though the activity is continuous. The hold is
        /// therefore widened by exactly one chosen-timescale tick, so the predicate is correct
        /// under EITHER component order. Pinned offline (Orbital.Tests WarpRealtimeHoldMargin).
        /// All inputs in seconds; a sub-x1 timescale is floored to x1 (the clock never runs
        /// slower than real time). A NaN/-infinity stamp never holds.
        /// </summary>
        public static bool HoldsRealtime(double nowSeconds, double stampSeconds,
            double holdSeconds, double chosenTimescale, double tickSeconds)
        {
            double ts = chosenTimescale < 1.0 ? 1.0 : chosenTimescale;
            double margin = ts * (tickSeconds > 0.0 ? tickSeconds : 0.0);
            return nowSeconds - stampSeconds < holdSeconds + margin;
        }
    }
}
