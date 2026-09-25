using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// The live set of adopted ENCOUNTERS for this session — a parallel LATENT-CONJUNCTION catalog
    /// to <see cref="AsteroidRegistry"/> (.research/encounter-stable-orbits.md §2). Each entry is a
    /// stable orbit about a parent body, detectable + warping with the universe clock, but with NO
    /// gravity, NO SOI, NO berth and NO real grids until a player rendezvouses. An asteroid is
    /// SYNTHETIC (we generate it); an encounter is CAPTURED (an external SE / MES spawner made it
    /// real and we adopted it onto an assigned orbit).
    ///
    /// WHY PARALLEL, NOT RIDING <see cref="AsteroidRegistry"/> (design §2):
    ///  • Different lifecycle/state — an encounter entry is MUTABLE: it must remember how to
    ///    re-materialize the real grids (a snapshot handle), the roll-to-rendezvous flag, and the
    ///    looted/consumed status. Bolting that onto the deliberately-immutable asteroid Entry would
    ///    muddy a clean type and its offline tests.
    ///  • Different population semantics — asteroids are an authored/seeded belt built ONCE at load;
    ///    encounters are ADOPTED ON THE FLY as SE/MES fire them, so this registry GROWS at runtime
    ///    (<see cref="Add"/>) from the adoption hook, it isn't built once and frozen.
    ///  • Same machinery where it matters — both bake elements with the parent μ via the SAME
    ///    <see cref="KeplerianEphemeris"/>, and both emit detection candidates as a parent-relative
    ///    BodyInertialPoint recomputed every sweep, so an encounter warps + runs the ladder/glimpse/
    ///    OD path with no detection changes beyond one more loop in BuildCandidates (the "ENC:" loop,
    ///    a near-verbatim copy of the "ROID:" loop).
    ///
    /// Game-free by design (references only the orbital core + the POCO model + VRageMath), so it is
    /// offline-testable like the asteroid catalog. <c>SystemRuntime</c> publishes an EMPTY registry
    /// at load (encounters are adopted at runtime, none exist on a fresh world) and clears it on
    /// unload, mirroring <see cref="AsteroidRegistry"/>.
    ///
    /// ── TODO HOOKS — the IN-GAME / DS EYES-ON adoption + materialize step (NOT built here) ──────────
    /// The offline-provable core (registry + orbit assignment + roll + detection wiring) is complete.
    /// The following touch live entities / frames and MUST be built with in-game eyes-on, not blind:
    ///  (H1) ADOPTION HOOK — MyAPIGateway.Entities.OnEntityAdd → NPC-ownership discriminate + debounce
    ///       → OrbitAssigner.Assign(...) → roll-to-rendezvous → snapshot the captured grids to an
    ///       MyObjectBuilder_CubeGrid[] blob, store it keyed by Entry.SnapshotKey, CLOSE the originals
    ///       → EncounterRegistry.Active.Add(entry). (design §3 / §3.1 — the MES lifecycle verdict.)
    ///  (H2) SNAPSHOT STORE — the game-type blob behind SnapshotKey (a sidecar keyed by id). The POCO
    ///       carries only the opaque key; the blob lives in a game-side store the hook owns.
    ///  (H3) MATERIALIZE-ON-RENDEZVOUS — when a player's frame reaches an entry's conjunction sphere:
    ///       allocate a berth, re-spawn the snapshot grids at the charted position, set Status =
    ///       Materialized. This is the SAME latent-frame materialize hook the asteroid path needs
    ///       (ProximityFrame at BerthSlotId = -1 until rendezvous) — frames change, eyes-on.
    ///  (H4) DE-MATERIALIZE-ON-LEAVE — re-snapshot current state, close grids, Status = Latent.
    ///  (H5) CONSUME — looted/destroyed → Status = Consumed (persists; skipped at materialize).
    ///  (H6) PERSISTENCE — serialize the orbit + metadata to world storage + the snapshot blobs (§5);
    ///       latent ⇒ encounter file, materialized ⇒ existing frame persistence.
    /// </summary>
    public sealed class EncounterRegistry
    {
        private const double DegToRad = Math.PI / 180.0;

        /// <summary>The currently-instantiated encounter catalog, or null before world load / if
        /// none were defined. Read-only to consumers; the runtime + adoption hook are the writers.</summary>
        public static EncounterRegistry Active { get; private set; }

        /// <summary>One adopted encounter: its identity, parent, representative size, the baked
        /// Keplerian ephemeris giving its motion in the parent's inertial frame, and the MUTABLE
        /// lifecycle/materialization state an asteroid never carries.</summary>
        public sealed class Entry
        {
            public readonly string Id;
            public readonly string ParentBodyName;
            public readonly double RepresentativeRadiusMeters;
            public readonly bool Loud;
            /// <summary>§4 roll result: false = floats in the belt, true = on a real intercept course.</summary>
            public readonly bool IsIntercept;
            private readonly KeplerianEphemeris _ephemeris;
            private readonly KeplerianElements _epochElements;

            // ── MUTABLE state (the reason this registry is separate from the immutable asteroid one) ──
            /// <summary>The materialization lifecycle (Latent | Materialized | Consumed). Mutated by
            /// the eyes-on materialize / consume hooks (H3/H4/H5).</summary>
            public EncounterStatus Status;
            /// <summary>Opaque handle the eyes-on hook maps to the captured grid snapshot blob (H2).
            /// Game-free placeholder; the offline core never reads the blob.</summary>
            public string SnapshotKey;

            public Entry(string id, string parentBodyName, double radius, bool loud,
                bool isIntercept, EncounterStatus status, string snapshotKey,
                KeplerianElements epochElements)
            {
                Id = id;
                ParentBodyName = parentBodyName;
                RepresentativeRadiusMeters = radius;
                Loud = loud;
                IsIntercept = isIntercept;
                Status = status;
                SnapshotKey = snapshotKey;
                _epochElements = epochElements;
                _ephemeris = new KeplerianEphemeris(epochElements);
            }

            /// <summary>This encounter's position RELATIVE to its parent body's centre, in the
            /// parent's inertial (root-aligned, non-rotating) frame, at absolute time t. Identical
            /// shape to <see cref="AsteroidRegistry.Entry.OffsetAt"/>.</summary>
            public Vector3D OffsetAt(double t)
            {
                return _ephemeris.PositionAt(t);
            }

            /// <summary>The baked element set (epoch elements) — exposed for persistence (§5,
            /// re-bake on load) and for offline proofs (peri/apoapsis band + period). Read-only
            /// snapshot; the element struct is copied by value.</summary>
            public KeplerianElements EpochElements { get { return _epochElements; } }
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private double _epoch;
        private SystemRegistry _sys;

        private EncounterRegistry() { }

        /// <summary>Every adopted encounter (insertion order). Do not mutate the list.</summary>
        public IReadOnlyList<Entry> Entries { get { return _entries; } }

        /// <summary>Number of adopted encounters.</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>Look up an entry by its raw id (no "ENC:" prefix); null if absent.</summary>
        public Entry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Entry e;
            return _byId.TryGetValue(id, out e) ? e : null;
        }

        /// <summary>
        /// Publish an EMPTY encounter catalog bound to the live body system (for parent μ + epoch).
        /// Encounters are adopted at runtime, so the catalog starts empty and GROWS via <see cref="Add"/>.
        /// Returns the published registry (also set as <see cref="Active"/>); never null. Mirrors
        /// <see cref="AsteroidRegistry.Build"/> but with no seed definitions.
        /// </summary>
        public static EncounterRegistry CreateEmpty(SystemRegistry sys)
        {
            EncounterRegistry reg = new EncounterRegistry();
            reg._sys = sys;
            reg._epoch = (sys != null && sys.Definition != null) ? sys.Definition.EpochSeconds : 0.0;
            Active = reg;
            return reg;
        }

        /// <summary>
        /// Build + publish a catalog from a set of definitions (used for OFFLINE PROOFS and, later,
        /// any config-authored encounters). Definitions whose parent is unknown or that fail
        /// validation are SKIPPED (reported via <paramref name="skipped"/>) rather than aborting the
        /// whole catalog — one bad encounter never blacks out the rest. Mirrors
        /// <see cref="AsteroidRegistry.Build"/>. Returns the published registry; never null.
        /// </summary>
        public static EncounterRegistry Build(IEnumerable<EncounterDefinition> defs, SystemRegistry sys,
            out List<string> skipped)
        {
            skipped = new List<string>();
            EncounterRegistry reg = CreateEmpty(sys);
            if (defs != null && sys != null)
            {
                foreach (EncounterDefinition d in defs)
                {
                    if (d == null) continue;
                    string err;
                    if (!reg.TryAdd(d, out err)) skipped.Add(err);
                }
            }
            return reg;
        }

        /// <summary>
        /// Adopt ONE encounter definition into the live catalog at runtime (the adoption hook's entry
        /// point — H1). Validates the definition + resolves the parent against the live system, bakes
        /// the ephemeris with the parent μ, and registers it under the stable id. Returns false (with
        /// <paramref name="error"/> set) on a bad/unknown-parent definition or a duplicate id, leaving
        /// the catalog unchanged. Idempotent-safe: a duplicate id is rejected, never double-added (so
        /// the OnEntityAdd debounce re-firing can't churn the catalog — the asteroid roid-dedup
        /// discipline, applied at adoption time).
        /// </summary>
        public bool TryAdd(EncounterDefinition d, out string error)
        {
            error = null;
            if (d == null) { error = "null encounter definition."; return false; }
            if (!d.TryValidate(out error)) return false;
            if (_byId.ContainsKey(d.Id))
            {
                error = "Encounter '" + d.Id + "' already adopted (duplicate id rejected).";
                return false;
            }
            if (_sys == null) { error = "Encounter registry not bound to a system."; return false; }
            GravityBody parent = _sys.Find(d.ParentBodyName);
            if (parent == null)
            {
                error = "Encounter '" + d.Id + "' references unknown parent '" + d.ParentBodyName + "'.";
                return false;
            }

            // Tilt onto the SAME plane the bodies were built on (tilt audit 2026-06-17): the
            // assigned/authored elements are canonical (+Z plane), but OffsetAt is consumed
            // root-aligned against the tilted parent — so without this the encounter sits off
            // the tilted system plane (the AsteroidRegistry root cause, inherited byte-for-byte).
            // Bake at the epoch M0 was validated against (OrbitAssigner stamps d.EpochSeconds = the
            // assignment `now`). An authored/offline def — or an old save — leaves it NaN → fall back to
            // the system epoch (the original behaviour). Without this the runtime-assigned encounter's M0,
            // chosen as "the anomaly at now", would be baked as "the anomaly at the system epoch", drifting
            // the charted shadow off the captured grid by n·(now − systemEpoch).
            double bakeEpoch = double.IsNaN(d.EpochSeconds) ? _epoch : d.EpochSeconds;
            KeplerianElements el = SystemBuilder.TiltElements(
                ElementsFor(d, parent.Mu, bakeEpoch),
                SystemBuilder.TiltFor(_sys.PlaneNormal), parent.Mu, bakeEpoch);
            Entry e = new Entry(d.Id, d.ParentBodyName, d.RepresentativeRadiusMeters, d.Loud,
                d.IsIntercept, d.Status, d.SnapshotKey, el);
            _entries.Add(e);
            _byId[d.Id] = e;
            return true;
        }

        /// <summary>Convenience overload of <see cref="TryAdd(EncounterDefinition,out string)"/> that
        /// returns the added entry (or null) — for the adoption hook that wants the live entry back to
        /// stash the snapshot blob against (H2).</summary>
        public Entry Add(EncounterDefinition d)
        {
            string err;
            return TryAdd(d, out err) ? _byId[d.Id] : null;
        }

        /// <summary>Clear the active catalog (session unload). Mirrors <see cref="AsteroidRegistry.Clear"/>.</summary>
        public static void Clear()
        {
            Active = null;
        }

        // Build a Keplerian element set from an encounter's real elements + the PARENT's μ at the
        // system epoch — byte-identical discipline to AsteroidRegistry.ElementsFor: degrees → radians,
        // mean anomaly → true anomaly via the core Newton solver, μ is the parent's so period / mean
        // motion are Kepler-3-consistent for free.
        private static KeplerianElements ElementsFor(EncounterDefinition d, double muParent, double epoch)
        {
            double m0 = d.MeanAnomalyAtEpochDeg * DegToRad;
            double nu = OrbitalMath.MeanToTrueAnomaly(m0, d.Eccentricity);

            KeplerianElements el = new KeplerianElements();
            el.SemiMajorAxis = d.SemiMajorAxisMeters;
            el.Eccentricity = d.Eccentricity;
            el.Inclination = d.InclinationDeg * DegToRad;
            el.Raan = d.RaanDeg * DegToRad;
            el.ArgPeriapsis = d.ArgPeriapsisDeg * DegToRad;
            el.TrueAnomaly = nu;
            el.Mu = muParent;
            el.Epoch = epoch;
            return el;
        }
    }
}
