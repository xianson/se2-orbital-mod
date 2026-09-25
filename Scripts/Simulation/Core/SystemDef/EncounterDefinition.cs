
namespace SEAerospace.SystemDef
{
    /// <summary>
    /// The materialization status of an adopted encounter (.research/encounter-stable-orbits.md §2).
    /// A latent encounter is pure DATA riding its charted orbit; it only becomes real grids on
    /// rendezvous, and a looted/destroyed one stays consumed forever.
    /// </summary>
    public enum EncounterStatus
    {
        /// <summary>Orbit + detectable, NO berth/footprint. The default after assignment.</summary>
        Latent = 0,
        /// <summary>A player rendezvoused; the snapshot grids are spawned into a real berth.</summary>
        Materialized = 1,
        /// <summary>Looted / destroyed; skipped at materialize, never comes back.</summary>
        Consumed = 2,
    }

    /// <summary>
    /// A plain serializable description of ONE adopted encounter — an external (SE / MES) NPC spawn
    /// folded into a LATENT CONJUNCTION (.research/encounter-stable-orbits.md): we ADOPT the spawn,
    /// assign it a STABLE ORBIT somewhere in the system (mostly the belt, occasionally a real
    /// intercept course), and let it ride exactly like a charted asteroid — orbit + detectable +
    /// warps with the clock, NO berth/footprint until a player rendezvouses.
    ///
    /// This is the encounter counterpart of <see cref="AsteroidDefinition"/>, kept DELIBERATELY
    /// SEPARATE (rationale in <see cref="EncounterRegistry"/>): an encounter carries a RICHER,
    /// MUTABLE state an asteroid never needs — how to re-materialize the real grids (a snapshot
    /// handle), the roll-to-rendezvous flag, and the looted/consumed lifecycle. The orbit half is
    /// the SAME fix-set discipline as a roid / body: classical elements about a PARENT body,
    /// a / e / i / RAAN / argP + mean anomaly at epoch M0. No stored period — it follows from the
    /// parent μ by Kepler-3 at build time.
    ///
    /// POCO rules for SE serialization: public fields, parameterless ctor, no game types. SI units;
    /// angles in DEGREES in the config, converted to radians by the registry.
    ///
    /// SNAPSHOT NOTE (eyes-on step, NOT built here): the real-grid <c>MyObjectBuilder_CubeGrid[]</c>
    /// snapshot blob (§3) is a GAME type that cannot live in this game-free POCO; the registry entry
    /// carries an opaque <see cref="SnapshotKey"/> string placeholder that the eyes-on adoption hook
    /// will map to the stored builder blob. See the TODO hooks in <see cref="EncounterRegistry"/>.
    /// </summary>
    public class EncounterDefinition
    {
        /// <summary>Unique encounter id (the catalog key tail: "ENC:&lt;Id&gt;"). Required.</summary>
        public string Id = "";

        /// <summary>Name of the parent body this encounter orbits (a body in the system) — the
        /// player's current SOI body at capture, fallback the root star (§1.1). Required.</summary>
        public string ParentBodyName = "";

        // ---- orbital elements (REAL elements about the parent, assigned by OrbitAssigner) -----

        public double SemiMajorAxisMeters = 0.0;
        public double Eccentricity = 0.0;
        public double InclinationDeg = 0.0;
        public double RaanDeg = 0.0;
        public double ArgPeriapsisDeg = 0.0;
        public double MeanAnomalyAtEpochDeg = 0.0;

        /// <summary>The universe-time epoch (seconds) the <see cref="MeanAnomalyAtEpochDeg"/> M0 was
        /// chosen/validated against. OrbitAssigner picks + anti-adjacency-checks M0 treating it as the
        /// anomaly at the ASSIGNMENT time (a runtime `now`), so the registry MUST bake the ephemeris at
        /// that same epoch — baking at the fixed system epoch instead drifts the charted shadow off the
        /// captured grid by n·(now − systemEpoch) and silently breaks anti-adjacency + the intercept
        /// window. NaN = "unset" (an authored/offline def, or an old save) → the registry falls back to
        /// the system epoch, the pre-fix behaviour. Deliberately NOT range-checked in TryValidate (NaN is
        /// the valid sentinel). Round-trips through XmlSerializer for persistence.</summary>
        public double EpochSeconds = double.NaN;

        // ---- size / detection ------------------------------------------------

        /// <summary>Representative radius in meters — the optical cross-section (πR²) the detection
        /// driver sees (from the captured cluster's bounding sphere). Inherits whatever the detection
        /// tuning lands on (same path as a roid / frame). Default a small derelict.</summary>
        public double RepresentativeRadiusMeters = 50.0;

        /// <summary>True if an NPC with a live broadcasting antenna can be a LOUD emitter (seen
        /// sharply regardless of cross-section). Default false (silent derelict). Honoured by the
        /// detection wiring exactly like a frame's loud-reveal.</summary>
        public bool Loud = false;

        // ---- roll-to-rendezvous (§4) -----------------------------------------

        /// <summary>The §4 roll result: false = floats in the belt (you only meet it if you chase
        /// it), true = phased onto a real intercept course with the triggering player's orbit within
        /// the near-future window. A tunable fraction (<see cref="OrbitAssigner.DefaultInterceptChance"/>)
        /// roll true. Default false.</summary>
        public bool IsIntercept = false;

        // ---- mutable lifecycle / materialization state -----------------------

        /// <summary>The materialization lifecycle (§2/§3). Latent at assignment; flips to
        /// Materialized on rendezvous and Consumed when looted/destroyed. MUTABLE — this is the
        /// core reason the registry is separate from the immutable asteroid catalog.</summary>
        public EncounterStatus Status = EncounterStatus.Latent;

        /// <summary>Opaque handle the eyes-on adoption hook maps to the captured
        /// <c>MyObjectBuilder_CubeGrid[]</c> snapshot blob used to re-materialize the real grids on
        /// rendezvous (§3). Game-free placeholder ONLY — the offline core never spawns; the blob and
        /// the snapshot/despawn/respawn paths are the in-game eyes-on step (see EncounterRegistry
        /// TODO hooks). Empty for a synthetic / not-yet-captured entry.</summary>
        public string SnapshotKey = "";

        public EncounterDefinition() { }

        /// <summary>Basic structural validity in isolation (parent existence is checked by the
        /// registry against the live system). Mirrors <see cref="AsteroidDefinition.TryValidate"/>.</summary>
        public bool TryValidate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(Id)) { error = "Encounter has empty Id."; return false; }
            if (string.IsNullOrEmpty(ParentBodyName)) { error = "Encounter '" + Id + "' has empty ParentBodyName."; return false; }
            // Finiteness FIRST: the persisted XML is untrusted (a corrupt/partial write deserializes
            // here), and every range check below is a comparison that NaN silently passes (NaN<0, NaN>=1
            // are both false) — so without this a NaN element would validate, then bake a NaN ephemeris
            // that pushes a NaN BodyInertialPoint into the detection sweep (invariant 6).
            if (!Finite(SemiMajorAxisMeters) || !Finite(Eccentricity) || !Finite(InclinationDeg)
                || !Finite(RaanDeg) || !Finite(ArgPeriapsisDeg) || !Finite(MeanAnomalyAtEpochDeg)
                || !Finite(RepresentativeRadiusMeters))
            { error = "Encounter '" + Id + "' has a non-finite (NaN/Inf) orbital element."; return false; }
            if (SemiMajorAxisMeters <= 0.0) { error = "Encounter '" + Id + "' has non-positive SemiMajorAxisMeters."; return false; }
            if (Eccentricity < 0.0 || Eccentricity >= 1.0) { error = "Encounter '" + Id + "' has eccentricity outside [0,1)."; return false; }
            if (InclinationDeg < 0.0 || InclinationDeg > 180.0) { error = "Encounter '" + Id + "' has inclination outside [0,180]."; return false; }
            if (RepresentativeRadiusMeters <= 0.0) { error = "Encounter '" + Id + "' has non-positive RepresentativeRadiusMeters."; return false; }
            return true;
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }
}
