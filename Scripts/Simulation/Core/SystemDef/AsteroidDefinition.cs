
namespace SEAerospace.SystemDef
{
    /// <summary>
    /// A plain serializable description of ONE asteroid — a charted celestial that is, by
    /// design, a LATENT CONJUNCTION (docs/charted-asteroids-encounters.md): it has a real
    /// orbit and is detectable + warps with the clock, but is NOT a gravitating body. It owns
    /// NO SOI, NO berth, and NO voxel until a player rendezvouses; only then is a roid voxel
    /// materialized into a berth. So this is deliberately NOT a <see cref="BodyDefinition"/>:
    /// it carries an orbit + a representative size + a voxel hint, and nothing gravitational
    /// (no surface gravity, no SOI) — adding asteroids to the <see cref="GravityBody"/> SOI
    /// tree would pollute patched-conics with thousands of negligible wells.
    ///
    /// The orbit is REAL classical elements about a PARENT body (the same fix-set discipline
    /// as <see cref="BodyDefinition"/>): a / e / i / RAAN(Omega) / argPeriapsis(omega) and the
    /// mean anomaly at epoch M0. No stored period — it follows from the parent's mu by
    /// Kepler-3 at build time, exactly like a body's child orbit.
    ///
    /// POCO rules for SE serialization: public fields, parameterless ctor, no game types.
    /// SI units; angles in DEGREES in the config, converted to radians by the registry.
    /// </summary>
    public class AsteroidDefinition
    {
        /// <summary>Unique asteroid id (the catalog key tail: "ROID:&lt;Id&gt;"). Required.</summary>
        public string Id = "";

        /// <summary>Name of the parent body this asteroid orbits (a body in the system). Required.</summary>
        public string ParentBodyName = "";

        // ---- orbital elements (REAL elements about the parent) ----------------

        public double SemiMajorAxisMeters = 0.0;
        public double Eccentricity = 0.0;
        public double InclinationDeg = 0.0;
        public double RaanDeg = 0.0;
        public double ArgPeriapsisDeg = 0.0;
        public double MeanAnomalyAtEpochDeg = 0.0;

        // ---- size / materialization hints -------------------------------------

        /// <summary>Representative radius in meters — the optical cross-section (πR²) the
        /// detection driver sees, and the size of the voxel roid materialized on rendezvous.
        /// Asteroid scale (tens to a few hundred metres), NOT a planet.</summary>
        public double RepresentativeRadiusMeters = 200.0;

        /// <summary>Deterministic seed for the procedural roid voxel materialized on rendezvous
        /// (CreateProceduralVoxelMap). 0 = derive from Id at materialize time.</summary>
        public int VoxelSeed = 0;

        /// <summary>Optional ore/composition hint (free text for now; drives the materialized
        /// voxel's ore the latent-conjunction materializer will honour later).</summary>
        public string Composition = "";

        /// <summary>True for a PROCEDURAL BELT MEMBER (one of the many roids a
        /// <see cref="BeltGenerator"/> scatters across a semi-major-axis band), false for a
        /// DISCRETE named roid (the handful of authored persistent rocks in
        /// <see cref="SampleAsteroids"/>). Drives detection scaling (docs/charted-asteroids-encounters.md
        /// §1 "detection must not flood"): discrete roids are ALWAYS emitted as detection candidates,
        /// belt members are RANGE-GATED to the nearest observers so a band of dozens-to-hundreds never
        /// re-introduces the per-sweep candidate flood / HUD spam. Default false (discrete).</summary>
        public bool BeltMember = false;

        public AsteroidDefinition() { }

        /// <summary>Basic structural validity in isolation (parent existence is checked by the
        /// registry against the live system).</summary>
        public bool TryValidate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(Id)) { error = "Asteroid has empty Id."; return false; }
            if (string.IsNullOrEmpty(ParentBodyName)) { error = "Asteroid '" + Id + "' has empty ParentBodyName."; return false; }
            // Finiteness FIRST: NaN passes every range comparison below (NaN<0 / NaN>=1 are both false),
            // so a corrupt persisted element would validate then bake a NaN ephemeris into detection.
            if (!Finite(SemiMajorAxisMeters) || !Finite(Eccentricity) || !Finite(InclinationDeg)
                || !Finite(RaanDeg) || !Finite(ArgPeriapsisDeg) || !Finite(MeanAnomalyAtEpochDeg)
                || !Finite(RepresentativeRadiusMeters))
            { error = "Asteroid '" + Id + "' has a non-finite (NaN/Inf) orbital element."; return false; }
            if (SemiMajorAxisMeters <= 0.0) { error = "Asteroid '" + Id + "' has non-positive SemiMajorAxisMeters."; return false; }
            if (Eccentricity < 0.0 || Eccentricity >= 1.0) { error = "Asteroid '" + Id + "' has eccentricity outside [0,1)."; return false; }
            if (InclinationDeg < 0.0 || InclinationDeg > 180.0) { error = "Asteroid '" + Id + "' has inclination outside [0,180]."; return false; }
            if (RepresentativeRadiusMeters <= 0.0) { error = "Asteroid '" + Id + "' has non-positive RepresentativeRadiusMeters."; return false; }
            return true;
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }
}
