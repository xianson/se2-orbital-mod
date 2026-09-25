using System.Collections.Generic;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// Built-in DISCRETE charted asteroids for the test system — a handful of large, named,
    /// persistent rocks (the "discrete first" half of rob's D1; procedural belt POPULATIONS come
    /// later). Each is a latent conjunction (orbit + detectable, no berth/voxel until rendezvous;
    /// docs/charted-asteroids-encounters.md), orbiting the Sun in a loose main belt at true ×k
    /// distances (13.3–16.3 M km, OUTSIDE Mars at 8.586e9 and well outside Earth at 5.635e9), with
    /// varied e / i / phase — sane, distinct targets to detect, plot, and (later) rendezvous with and
    /// mine. Distances are true ×k (same scale as the planet orbits); rock RADII stay gameplay-sized.
    /// </summary>
    public static class SampleAsteroids
    {
        /// <summary>The built-in discrete asteroid set for SampleSystems.SunEarthMoon.</summary>
        public static List<AsteroidDefinition> Default()
        {
            List<AsteroidDefinition> roids = new List<AsteroidDefinition>();

            // TRUE-PROPORTION main belt (rob 2026-06-16): a = real-belt-AU x k, same scale as the
            // planets, so the belt (Vesta 2.36 .. Ceres/Pallas 2.77 AU => 13.3-16.3 M km) sits OUTSIDE
            // Mars (8.586 M km), like Sol's. Rock RADII left at gameplay size, NOT scaled to true km.
            // Semi-major axes at full ×k (main belt, beyond Mars at 8.586e9). Un-halved 2026-06-18 to
            // match the restored planet orbits + belt (the "halved ×2" put these inside Earth's orbit).
            roids.Add(Make("Ceres", "Sun", 1.5609e10, 0.08, 6.0, 30.0, 12.0, 40.0, 380.0, 101, "Ice"));
            roids.Add(Make("Pallas", "Sun", 1.6342e10, 0.15, 12.0, 80.0, 200.0, 150.0, 240.0, 102, "Iron"));
            roids.Add(Make("Juno", "Sun", 1.5046e10, 0.05, 3.0, 200.0, 60.0, 250.0, 300.0, 103, "Silicate"));
            roids.Add(Make("Vesta", "Sun", 1.3299e10, 0.10, 9.0, 300.0, 330.0, 20.0, 460.0, 104, "Nickel"));

            return roids;
        }

        private static AsteroidDefinition Make(string id, string parent, double a, double e,
            double iDeg, double raanDeg, double argpDeg, double m0Deg, double radius, int seed,
            string composition)
        {
            AsteroidDefinition d = new AsteroidDefinition();
            d.Id = id;
            d.ParentBodyName = parent;
            d.SemiMajorAxisMeters = a;
            d.Eccentricity = e;
            d.InclinationDeg = iDeg;
            d.RaanDeg = raanDeg;
            d.ArgPeriapsisDeg = argpDeg;
            d.MeanAnomalyAtEpochDeg = m0Deg;
            d.RepresentativeRadiusMeters = radius;
            d.VoxelSeed = seed;
            d.Composition = composition;
            return d;
        }
    }
}
