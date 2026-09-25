using System;
using System.Collections.Generic;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// Procedurally scatters a BELT of latent-conjunction asteroids across a semi-major-axis band
    /// about a parent body (docs/charted-asteroids-encounters.md §1 "a belt is a population"). Each
    /// produced <see cref="AsteroidDefinition"/> is a BELT MEMBER (<c>BeltMember = true</c>) — a real
    /// charted orbit + representative size, detectable + warping with the clock, but NO SOI / berth /
    /// voxel until rendezvous, exactly like the discrete named roids — the ONLY difference being that
    /// belt members are RANGE-GATED in detection (the discrete set is always emitted) so a band of
    /// dozens-to-hundreds never re-introduces the per-sweep candidate flood / HUD spam.
    ///
    /// FULLY DETERMINISTIC — reproducible bit-for-bit across client and server from a single integer
    /// <see cref="BeltParams.Seed"/>. NO <c>Math.Random</c> / <c>DateTime.Now</c> (non-deterministic
    /// AND not guaranteed whitelisted): every random draw is a pure hash of (member index, seed,
    /// channel), so the same seed always expands to the same belt. The band sits at true ×k main-belt
    /// distances (1.183e10–1.860e10 m, 2.06–3.27 AU × k), OUTSIDE Mars (8.586e9) and far outside Earth
    /// (5.635e9); tune the band/count/density via <see cref="BeltParams"/>.
    /// </summary>
    public static class BeltGenerator
    {
        /// <summary>Tunable belt-band spec. SI metres for distances, DEGREES for angles (converted to
        /// radians by <see cref="AsteroidRegistry"/> like every other authored element). Defaults sit at
        /// the true ×k main belt (beyond Mars) alongside the discrete set in <see cref="SampleAsteroids"/>:
        /// a modest, low-e, low-i belt about the Sun.</summary>
        public sealed class BeltParams
        {
            /// <summary>Parent body every belt member orbits (the Sun for the main belt).</summary>
            public string ParentBodyName = "Sun";

            /// <summary>Inner edge of the semi-major-axis band (m). SOL-PROPORTIONAL: 2.1 x Earth's
            /// orbit (Earth a = 5.635 M km in SunEarthMoon at true scale, rob 2026-06-16), matching
            /// Sol's main belt inner edge at ~2.06 AU — OUTSIDE Mars (8.586 M km). Default 11.83 M km.</summary>
            public double SemiMajorMinMeters = 1.183e10;  // 2.06 AU × k — main-belt inner edge, OUTSIDE
                                                          // Mars (8.586e9). (Un-halved 2026-06-18: the
                                                          // "halved ×2" put the belt inside Earth's orbit
                                                          // once planet orbits were restored to full ×k.)

            /// <summary>Outer edge of the semi-major-axis band (m). SOL-PROPORTIONAL: 3.3 x Earth's
            /// orbit, matching Sol's main belt outer edge at ~3.27 AU (well inside where Jupiter,
            /// 5.2 AU, would sit). Default 18.60 M km.</summary>
            public double SemiMajorMaxMeters = 1.860e10;  // 3.27 AU × k — main-belt outer edge (un-halved 2026-06-18)

            /// <summary>Max eccentricity; each member draws e in [0, eMax]. Default 0.2.</summary>
            public double EccentricityMax = 0.2;

            /// <summary>Max inclination (deg); each member draws i in [0, iMax]. Default 15°.</summary>
            public double InclinationMaxDeg = 15.0;

            /// <summary>Smallest representative radius (m) a member can be sampled at. Default 40 m.</summary>
            public double RadiusMinMeters = 40.0;

            /// <summary>Largest representative radius (m) a member can be sampled at — small-to-medium
            /// rocks (NOT a planet). Default 220 m. Radii are biased toward the small end.</summary>
            public double RadiusMaxMeters = 220.0;

            /// <summary>How many members to ATTEMPT. The actual count is <c>Count × SpawnChance</c>
            /// in expectation (each attempt admitted with probability <see cref="SpawnChance"/>).
            /// Default 150 (the Sol-proportional band is wider + farther out than the old straddle-Earth
            /// band, so more members keep a similar linear density). Tune for feel/perf.</summary>
            public int Count = 150;

            /// <summary>Per-member admission probability in [0,1] — the density knob. 1.0 admits every
            /// attempt (~<see cref="Count"/> roids); 0.5 thins the belt to ~half; 0.0 yields an empty
            /// belt. Deterministic per index. Default 1.0.</summary>
            public double SpawnChance = 1.0;

            /// <summary>Master seed. Same seed ⇒ same belt on every client/server. Default 1337.</summary>
            public int Seed = 1337;

            /// <summary>Id prefix for generated members ("&lt;prefix&gt;&lt;index&gt;"). Default "Belt-".</summary>
            public string IdPrefix = "Belt-";

            /// <summary>Optional composition hint stamped on every member. Default "Mixed".</summary>
            public string Composition = "Mixed";
        }

        /// <summary>
        /// Expand a belt spec into its member definitions (all <c>BeltMember = true</c>). Returns an
        /// empty list (never null) for a degenerate/zero spec. Each member's orbital elements + size
        /// are pure deterministic functions of (index, <see cref="BeltParams.Seed"/>), so the belt is
        /// identical on every machine without any sync.
        /// </summary>
        public static List<AsteroidDefinition> Generate(BeltParams p)
        {
            List<AsteroidDefinition> roids = new List<AsteroidDefinition>();
            if (p == null || p.Count <= 0) return roids;

            double aMin = Math.Min(p.SemiMajorMinMeters, p.SemiMajorMaxMeters);
            double aMax = Math.Max(p.SemiMajorMinMeters, p.SemiMajorMaxMeters);
            if (aMin <= 0.0) return roids;
            double eMax = Clamp(p.EccentricityMax, 0.0, 0.95);
            double iMax = Clamp(p.InclinationMaxDeg, 0.0, 180.0);
            double rMin = Math.Min(p.RadiusMinMeters, p.RadiusMaxMeters);
            double rMax = Math.Max(p.RadiusMinMeters, p.RadiusMaxMeters);
            if (rMin <= 0.0) rMin = 1.0;
            double spawn = Clamp(p.SpawnChance, 0.0, 1.0);

            for (int i = 0; i < p.Count; i++)
            {
                // Density knob: deterministic admission. Channel 0 reserved for the spawn roll so
                // tuning SpawnChance doesn't reshuffle the orbits of admitted members.
                if (Rand01(p.Seed, i, 0) >= spawn) continue;

                double a = Lerp(aMin, aMax, Rand01(p.Seed, i, 1));
                double e = eMax * Rand01(p.Seed, i, 2);
                double iDeg = iMax * Rand01(p.Seed, i, 3);
                double raanDeg = 360.0 * Rand01(p.Seed, i, 4);
                double argpDeg = 360.0 * Rand01(p.Seed, i, 5);
                double m0Deg = 360.0 * Rand01(p.Seed, i, 6);
                // Radius biased small (square the uniform draw) — many small rocks, few mid ones.
                double rU = Rand01(p.Seed, i, 7);
                double radius = Lerp(rMin, rMax, rU * rU);
                // Voxel seed for the rock materialized on rendezvous — deterministic, distinct per member.
                int voxelSeed = unchecked((int)Hash((uint)p.Seed, (uint)i, 8u)) & 0x7FFFFFFF;

                AsteroidDefinition d = new AsteroidDefinition();
                d.Id = p.IdPrefix + i;
                d.ParentBodyName = p.ParentBodyName;
                d.SemiMajorAxisMeters = a;
                d.Eccentricity = e;
                d.InclinationDeg = iDeg;
                d.RaanDeg = raanDeg;
                d.ArgPeriapsisDeg = argpDeg;
                d.MeanAnomalyAtEpochDeg = m0Deg;
                d.RepresentativeRadiusMeters = radius;
                d.VoxelSeed = voxelSeed;
                d.Composition = p.Composition;
                d.BeltMember = true;
                roids.Add(d);
            }

            return roids;
        }

        // ── Deterministic pseudo-random (NO Math.Random / DateTime) ────────────────────────────────
        // A small integer avalanche hash of (seed, index, channel) → a uniform double in [0,1). Each
        // (index, channel) is an independent stream, so different orbital elements of the same member
        // are uncorrelated, and the whole belt is reproducible from the seed alone.

        // A uniform double in [0,1) for member `index`, draw `channel`, under `seed`.
        private static double Rand01(int seed, int index, uint channel)
        {
            ulong h = Hash((uint)seed, (uint)index, channel);
            // Top 53 bits → double in [0,1) (mantissa width), avoiding float bias.
            return (h >> 11) * (1.0 / 9007199254740992.0); // 2^53
        }

        // Mix three 32-bit lanes into a well-distributed 64-bit value (splitmix64-style finalizer
        // over a cheap combine). Pure; no state, no allocation.
        private static ulong Hash(uint seed, uint index, uint channel)
        {
            unchecked
            {
                ulong z = (ulong)seed * 0x9E3779B97F4A7C15UL;
                z ^= ((ulong)index + 0x9E3779B97F4A7C15UL) * 0xBF58476D1CE4E5B9UL;
                z ^= ((ulong)channel + 0x94D049BB133111EBUL) * 0xD6E8FEB86659FD93UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z = z ^ (z >> 31);
                return z;
            }
        }

        private static double Lerp(double a, double b, double u) { return a + (b - a) * u; }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
