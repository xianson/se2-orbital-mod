using System;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// Assigns a STABLE ORBIT to an adopted encounter (.research/encounter-stable-orbits.md §1 + §4):
    /// given a triggering player + a parent body, it samples sane, stable classical elements scaled
    /// to the parent, applies the ANTI-ADJACENCY guard ("not an orbit right next to us"), and ROLLS
    /// the §4 chance to place the encounter on a real intercept course with the player instead of
    /// just floating in the belt.
    ///
    /// FULLY GAME-FREE + DETERMINISTIC (the offline-provable core): it consumes PRIMITIVES — the
    /// parent μ / radius / SOI, whether the parent is the heliocentric root, the player's
    /// parent-relative position + orbit, a per-encounter seed and the assignment time — and produces
    /// an <see cref="EncounterDefinition"/>. The eyes-on adoption hook (EncounterRegistry H1) reads
    /// these primitives off the live <c>GravityBody</c> / player and calls in. NO <c>Math.Random</c> /
    /// <c>DateTime</c>: every draw is a pure hash of (seed, channel), reusing the SAME splitmix-style
    /// avalanche the <see cref="BeltGenerator"/> already ships, so the same seed always re-rolls the
    /// same orbit (replayable / debuggable, design §1.3).
    ///
    /// THE TWO REGIMES (§1.2):
    ///  A) Heliocentric (parent == root star): drop into the asteroid belt band the player already
    ///     hunts roids in (a 1.183e10–1.860e10 m (2.06–3.27 AU × k), e ≤ 0.20, i ≤ 15° — DERIVED from
    ///     <see cref="BeltGenerator"/>.BeltParams so it tracks the belt; OUTSIDE Mars, far outside Earth).
    ///  B) Planet-orbit (parent == a planet/moon): scale a to sit comfortably above the surface and
    ///     well inside the SOI (a ∈ [max(3·R, R_park+50 km), 0.6·SOI], e ≤ 0.15, i ≤ 30°).
    ///
    /// ANTI-ADJACENCY GUARD (the core of rob's intent): after sampling, reject-and-resample if the
    /// encounter's position at M0 lands within <see cref="ExclusionRadiusMeters"/> of the player's
    /// parent-relative position. A handful of resamples is cheap; after N tries we bail to the last
    /// sample (which is still a valid stable orbit — the guard is best-effort, never an infinite loop).
    /// </summary>
    public static class OrbitAssigner
    {
        // ── Tunable constants (one block, .research/encounter-stable-orbits.md §7) ────────────────────
        /// <summary>Fraction of encounters placed on a real intercept course (§4). Default 0.15.</summary>
        public const double DefaultInterceptChance = 0.15;

        /// <summary>The future window an intercept TCA must land in (§4), seconds. 300–900 s.</summary>
        public const double InterceptLeadSecondsMin = 300.0;
        public const double InterceptLeadSecondsMax = 900.0;

        /// <summary>±band on a vs the player's orbit for intercept construction (§4). Default 0.10.</summary>
        public const double InterceptABandFraction = 0.10;

        /// <summary>Anti-adjacency: no encounter is assigned within this of the player at M0. 50 km.</summary>
        public const double ExclusionRadiusMeters = 50000.0;

        // Heliocentric belt band — DERIVED from the live BeltGenerator defaults so a floating
        // Sun-parented encounter always lands IN the asteroid belt (beyond Mars), never drifting from
        // it again. (Was a hardcoded 420-900 km — the old miniature test scale — which, after the
        // system was rescaled to true ×k, placed every Sun-parented floating encounter ~30-60× DEEPER
        // than the Sun's own 26,230 km radius, i.e. inside the star.)
        private static readonly BeltGenerator.BeltParams HelioBelt = new BeltGenerator.BeltParams();
        public static readonly double HelioAMinMeters = Math.Min(HelioBelt.SemiMajorMinMeters, HelioBelt.SemiMajorMaxMeters);
        public static readonly double HelioAMaxMeters = Math.Max(HelioBelt.SemiMajorMinMeters, HelioBelt.SemiMajorMaxMeters);
        public static readonly double HelioEMax = HelioBelt.EccentricityMax;
        public static readonly double HelioIMaxDeg = HelioBelt.InclinationMaxDeg;

        // Planet-orbit band shape.
        public const double PlanetAMinRadiiMultiple = 3.0;       // lower a >= 3·R_parent ...
        public const double PlanetAMinParkClearMeters = 50.0e3;  // ... and >= R_parent + 50 km
        public const double PlanetAMaxSoiFraction = 0.6;         // upper a <= 0.6·SOI_parent
        public const double PlanetEMax = 0.15;
        public const double PlanetIMaxDeg = 30.0;

        /// <summary>Max anti-adjacency resamples before bailing to the last valid sample.</summary>
        public const int MaxAdjacencyResamples = 16;

        /// <summary>Coarse M0 grid the intercept search walks looking for a windowed close approach.</summary>
        public const int InterceptM0Samples = 72;
        /// <summary>ClosestApproach window sample count for the intercept TCA search.</summary>
        public const int InterceptApproachSamples = 256;
        private const double DegToRad = Math.PI / 180.0;
        private const double TwoPi = 2.0 * Math.PI;

        /// <summary>
        /// The parent-body context the assigner needs — game-free primitives the eyes-on hook fills
        /// from a live <c>GravityBody</c> + its <c>BodyDefinition</c>.
        /// </summary>
        public struct ParentContext
        {
            public string Name;          // parent body name (Entry.ParentBodyName)
            public double Mu;            // parent gravitational parameter, m^3/s^2
            public double RadiusMeters;  // parent surface radius (R_parent); 0 for the star
            public double SoiRadius;     // parent SOI radius; +inf for the root star
            public bool IsRoot;          // true == heliocentric regime (belt band), false == planet orbit
        }

        /// <summary>
        /// Assign a stable orbit to an encounter about <paramref name="parent"/>, triggered by a
        /// player at <paramref name="playerParentRelativePos"/> orbiting on
        /// <paramref name="playerOrbit"/> (both expressed about the SAME parent). <paramref name="seed"/>
        /// is the per-encounter deterministic seed (spawn entity id + capture tick — §1.3);
        /// <paramml name="now"/> is the assignment time (the element epoch). Rolls the §4 intercept
        /// chance and, on a hit, phases the orbit onto a real windowed close approach with the player.
        ///
        /// Returns a fully-populated <see cref="EncounterDefinition"/> (Status = Latent, empty
        /// SnapshotKey — the eyes-on hook stamps Id/SnapshotKey/size). Never throws.
        /// </summary>
        public static EncounterDefinition Assign(string id, ParentContext parent,
            Vector3D playerParentRelativePos, KeplerianElements playerOrbit, int seed, double now,
            double interceptChance)
        {
            EncounterDefinition d = new EncounterDefinition();
            d.Id = id;
            d.ParentBodyName = parent.Name;
            d.Status = EncounterStatus.Latent;
            d.SnapshotKey = "";
            // Stamp the epoch M0 is chosen/validated against (this assignment's `now`), so the registry
            // bakes the ephemeris at the same epoch it was validated at — both branches below return d.
            d.EpochSeconds = now;

            // §4 roll: one deterministic draw decides float-vs-intercept. Channel reserved so tuning
            // the chance doesn't reshuffle the sampled orbit of a floating encounter.
            bool intercept = Rand01(seed, ChRoll) < Clamp(interceptChance, 0.0, 1.0);

            if (intercept && TryAssignIntercept(d, parent, playerParentRelativePos, playerOrbit, seed, now))
            {
                d.IsIntercept = true;
                return d;
            }

            // Float in the belt / planet orbit, with the anti-adjacency guard.
            d.IsIntercept = false;
            AssignFloating(d, parent, playerParentRelativePos, seed, now);
            return d;
        }

        /// <summary>Overload using the <see cref="DefaultInterceptChance"/>.</summary>
        public static EncounterDefinition Assign(string id, ParentContext parent,
            Vector3D playerParentRelativePos, KeplerianElements playerOrbit, int seed, double now)
        {
            return Assign(id, parent, playerParentRelativePos, playerOrbit, seed, now, DefaultInterceptChance);
        }

        // ── FLOATING assignment (§1.2 + the anti-adjacency guard) ─────────────────────────────────────
        private static void AssignFloating(EncounterDefinition d, ParentContext parent,
            Vector3D playerPos, int seed, double now)
        {
            double aMin, aMax, eMax, iMaxDeg;
            BandFor(parent, out aMin, out aMax, out eMax, out iMaxDeg);

            // Sample, then reject-and-resample on adjacency. Each resample uses a fresh channel block
            // keyed by the attempt index, so it is deterministic AND uncorrelated across attempts.
            int attempt = 0;
            for (; attempt < MaxAdjacencyResamples; attempt++)
            {
                SampleElements(d, parent, seed, attempt, aMin, aMax, eMax, iMaxDeg, now);
                if (!ViolatesAdjacency(d, parent, playerPos, now)) return;
            }
            // Bail: last sample stands (a valid stable orbit; the guard is best-effort, never a hang).
        }

        // Sample one stable element set into the definition for a given attempt index.
        private static void SampleElements(EncounterDefinition d, ParentContext parent, int seed,
            int attempt, double aMin, double aMax, double eMax, double iMaxDeg, double now)
        {
            // Channels are offset by a per-attempt block so resamples are independent streams.
            int b = ChAttemptBase + attempt * ChAttemptStride;
            double a = Lerp(aMin, aMax, Rand01(seed, b + 0));
            double e = eMax * Rand01(seed, b + 1);
            double iDeg = iMaxDeg * Rand01(seed, b + 2);
            double raanDeg = 360.0 * Rand01(seed, b + 3);
            double argpDeg = 360.0 * Rand01(seed, b + 4);
            double m0Deg = 360.0 * Rand01(seed, b + 5);

            d.SemiMajorAxisMeters = a;
            d.Eccentricity = e;
            d.InclinationDeg = iDeg;
            d.RaanDeg = raanDeg;
            d.ArgPeriapsisDeg = argpDeg;
            d.MeanAnomalyAtEpochDeg = m0Deg;
            // RepresentativeRadius / Loud are stamped by the eyes-on hook from the captured cluster;
            // leave the POCO defaults (a small derelict) for the offline core.
        }

        // ── INTERCEPT assignment (§4) ─────────────────────────────────────────────────────────────────
        // Construct an orbit NEAR the player's own (a within ±band, similar i) so the two genuinely
        // cross, then SEARCH M0 for the phase whose closest approach with the player lands inside the
        // future window [now+min, now+max] and is the smallest. Uses the shipped ClosestApproach kernel
        // (no new orbital math). Returns false if no windowed approach was found (caller falls back to
        // floating) — so an intercept roll never silently produces a non-intercept that claims to be one.
        private static bool TryAssignIntercept(EncounterDefinition d, ParentContext parent,
            Vector3D playerPos, KeplerianElements playerOrbit, int seed, double now)
        {
            if (playerOrbit.Mu <= 0.0 || !(playerOrbit.SemiMajorAxis > 0.0)) return false;

            double pa = playerOrbit.SemiMajorAxis;
            // a within ±band of the player's a (so the orbits cross); e/i near the player's, jittered.
            double aLo = pa * (1.0 - InterceptABandFraction);
            double aHi = pa * (1.0 + InterceptABandFraction);
            double a = Lerp(aLo, aHi, Rand01(seed, ChIcptA));
            double e = Clamp(playerOrbit.Eccentricity + (Rand01(seed, ChIcptE) - 0.5) * 0.05, 0.0, 0.30);
            double iDeg = (playerOrbit.Inclination / DegToRad)
                          + (Rand01(seed, ChIcptI) - 0.5) * 5.0;
            if (iDeg < 0.0) iDeg = -iDeg;
            double raanDeg = playerOrbit.Raan / DegToRad + (Rand01(seed, ChIcptRaan) - 0.5) * 10.0;
            double argpDeg = playerOrbit.ArgPeriapsis / DegToRad + (Rand01(seed, ChIcptArgp) - 0.5) * 10.0;

            double leadMid = Lerp(InterceptLeadSecondsMin, InterceptLeadSecondsMax, Rand01(seed, ChIcptLead));
            double windowStart = now + InterceptLeadSecondsMin;
            double windowEnd = now + InterceptLeadSecondsMax;
            double horizon = InterceptLeadSecondsMax - InterceptLeadSecondsMin;

            // Walk a coarse M0 grid; for each, bake the candidate orbit and find its deepest close
            // approach to the player within the window. Keep the M0 with the smallest in-window miss.
            double bestMiss = double.PositiveInfinity;
            double bestM0Deg = double.NaN;
            for (int k = 0; k < InterceptM0Samples; k++)
            {
                double m0Deg = 360.0 * k / InterceptM0Samples;
                KeplerianElements cand = MakeElements(a, e, iDeg, raanDeg, argpDeg, m0Deg,
                    playerOrbit.Mu, now);
                ApproachEvent ae = ClosestApproach.Find(playerOrbit, cand, windowStart, horizon,
                    InterceptApproachSamples);
                if (ae.Found && ae.MissDistance < bestMiss)
                {
                    bestMiss = ae.MissDistance;
                    bestM0Deg = m0Deg;
                }
            }

            // Require a genuinely CLOSE windowed approach (within the exclusion radius is "intercept",
            // mirroring the engagement shell the §6 warp lock keys off). If the best phase still misses
            // by a lot, this geometry can't intercept — fall back to floating.
            //
            // SCALE LIMITATION (round-8 #2, LATENT — this branch is currently unreached: the sole caller,
            // EncounterAdoptionCore, passes NoPlayerOrbit() (Mu==0), so the Mu<=0 guard above always bails
            // to floating). The coarse InterceptM0Samples grid with NO phase bisection only resolves a
            // sub-ExclusionRadius approach on PLANET-SOI-scale player orbits (empirically ~78% at a~6.6e5 m,
            // minMiss ~12 km). On a HELIOCENTRIC orbit (true-xk a ~5.6e9 m) the 600 s window is ~7e-6 of the
            // period and the grid steps ~1.3e9 m/sample, so the best windowed miss is ~2e-2·a (~10^5 km) —
            // far past the 50 km bar — and it ALWAYS falls back to floating. Do NOT "fix" this by loosening
            // the bar (that would accept a fake 100,000 km "intercept"); the real fix is to bisect M0 around
            // the best coarse bucket (and/or scale InterceptM0Samples + window with a) so a true close
            // approach is located at any scale. Deferred while the branch is dormant; a future caller that
            // supplies a real heliocentric player orbit MUST add that refinement first.
            if (double.IsNaN(bestM0Deg) || bestMiss > ExclusionRadiusMeters) return false;

            d.SemiMajorAxisMeters = a;
            d.Eccentricity = Clamp(e, 0.0, 0.30);
            d.InclinationDeg = Clamp(Abs(iDeg), 0.0, 180.0);
            d.RaanDeg = Wrap360(raanDeg);
            d.ArgPeriapsisDeg = Wrap360(argpDeg);
            d.MeanAnomalyAtEpochDeg = Wrap360(bestM0Deg);

            // The intercept must still be FAR NOW (it shows up as a distant contact closing on you —
            // §4): verify the anti-adjacency guard at t = now. If the closest phase is somehow adjacent
            // now, this isn't the "comes to you from afar" shape we want — fall back to floating.
            if (ViolatesAdjacency(d, parent, playerPos, now)) return false;

            // leadMid is informational (the intended lead) — kept implicit via the windowed search.
            return true;
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────────

        // The sampling band for the parent's regime (§1.2).
        private static void BandFor(ParentContext parent, out double aMin, out double aMax,
            out double eMax, out double iMaxDeg)
        {
            if (parent.IsRoot)
            {
                aMin = HelioAMinMeters; aMax = HelioAMaxMeters;
                eMax = HelioEMax; iMaxDeg = HelioIMaxDeg;
                return;
            }
            // Planet orbit: above the surface/park clearance, well inside the SOI.
            double lower = Math.Max(PlanetAMinRadiiMultiple * parent.RadiusMeters,
                                    parent.RadiusMeters + PlanetAMinParkClearMeters);
            double upper = PlanetAMaxSoiFraction * parent.SoiRadius;
            if (!(upper > lower) || double.IsInfinity(upper))
            {
                // Degenerate / unbounded SOI (shouldn't happen for a planet, but be safe): a thin band
                // just above the lower bound so the result is always a valid bound orbit.
                upper = lower * 1.5;
            }
            aMin = lower; aMax = upper;
            eMax = PlanetEMax; iMaxDeg = PlanetIMaxDeg;
        }

        // True if the encounter's position at M0 is within the exclusion radius of the player (§1.2).
        private static bool ViolatesAdjacency(EncounterDefinition d, ParentContext parent,
            Vector3D playerPos, double now)
        {
            KeplerianElements el = MakeElements(d.SemiMajorAxisMeters, d.Eccentricity, d.InclinationDeg,
                d.RaanDeg, d.ArgPeriapsisDeg, d.MeanAnomalyAtEpochDeg, parent.Mu, now);
            Vector3D pos = OrbitalMath.ToState(el).Position;
            return (pos - playerPos).LengthSquared() < ExclusionRadiusMeters * ExclusionRadiusMeters;
        }

        // Build a propagatable element set from degrees + mean-anomaly-at-epoch (the same conversion
        // EncounterRegistry/AsteroidRegistry bake) at element epoch == now.
        private static KeplerianElements MakeElements(double aMeters, double e, double iDeg,
            double raanDeg, double argpDeg, double m0Deg, double mu, double epoch)
        {
            double m0 = m0Deg * DegToRad;
            double nu = OrbitalMath.MeanToTrueAnomaly(m0, e);
            KeplerianElements el = new KeplerianElements();
            el.SemiMajorAxis = aMeters;
            el.Eccentricity = e;
            el.Inclination = iDeg * DegToRad;
            el.Raan = raanDeg * DegToRad;
            el.ArgPeriapsis = argpDeg * DegToRad;
            el.TrueAnomaly = nu;
            el.Mu = mu;
            el.Epoch = epoch;
            return el;
        }

        // ── deterministic pseudo-random (NO Math.Random / DateTime) — splitmix-style avalanche, the
        // same discipline BeltGenerator ships. A small set of fixed channels keeps each draw an
        // independent, reproducible stream off (seed, channel).

        // Fixed channel ids (kept apart so tuning one knob never reshuffles another draw).
        private const int ChRoll = 0;           // float-vs-intercept roll
        private const int ChAttemptBase = 100;  // floating resample block base
        private const int ChAttemptStride = 8;  // channels per resample attempt
        private const int ChIcptA = 10, ChIcptE = 11, ChIcptI = 12, ChIcptRaan = 13,
                          ChIcptArgp = 14, ChIcptLead = 15;

        private static double Rand01(int seed, int channel)
        {
            ulong h = Hash((uint)seed, (uint)channel);
            return (h >> 11) * (1.0 / 9007199254740992.0); // top 53 bits → [0,1)
        }

        private static ulong Hash(uint seed, uint channel)
        {
            unchecked
            {
                ulong z = (ulong)seed * 0x9E3779B97F4A7C15UL;
                z ^= ((ulong)channel + 0x94D049BB133111EBUL) * 0xD6E8FEB86659FD93UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z = z ^ (z >> 31);
                return z;
            }
        }

        private static double Lerp(double a, double b, double u) { return a + (b - a) * u; }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static double Abs(double v) { return v < 0.0 ? -v : v; }
        private static double Wrap360(double deg)
        {
            deg %= 360.0;
            if (deg < 0.0) deg += 360.0;
            return deg;
        }
    }
}
