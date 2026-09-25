using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// The live set of charted asteroids for this session — the LATENT-CONJUNCTION catalog
    /// (docs/charted-asteroids-encounters.md). Each entry is a real orbit about a parent body,
    /// detectable + warping with the universe clock, but with NO gravity, NO SOI, NO berth and
    /// NO voxel until a player rendezvouses. This is the asteroid counterpart of
    /// <see cref="SystemRegistry"/> for bodies, kept DELIBERATELY SEPARATE so asteroids never
    /// enter the <see cref="GravityBody"/> SOI tree (thousands of negligible wells would wreck
    /// patched-conics).
    ///
    /// Game-free by design (references only the orbital core + the POCO model + VRageMath), so
    /// it is offline-testable. A session component (<c>SystemRuntime</c>) builds + publishes it
    /// right after the body system, using each parent body's μ from <see cref="SystemRegistry"/>.
    ///
    /// Each entry precomputes a <see cref="KeplerianEphemeris"/> (elements baked with the
    /// PARENT's μ, so the period is Kepler-3-consistent), and exposes the parent-RELATIVE
    /// position at any time. Detection emits each as a <c>BodyInertialPoint(parent, offset)</c>
    /// recomputed every sweep, so the asteroid ORBITS with zero changes to bodies or frames.
    /// </summary>
    public sealed class AsteroidRegistry
    {
        private const double DegToRad = Math.PI / 180.0;

        /// <summary>The currently-instantiated asteroid catalog, or null before world load /
        /// if none were defined. Read-only to consumers; the runtime is the only writer.</summary>
        public static AsteroidRegistry Active { get; private set; }

        /// <summary>One charted asteroid: its identity, parent, representative size, and the
        /// baked Keplerian ephemeris giving its motion in the parent's inertial frame.</summary>
        public sealed class Entry
        {
            public readonly string Id;
            public readonly string ParentBodyName;
            public readonly double RepresentativeRadiusMeters;
            public readonly int VoxelSeed;
            public readonly string Composition;
            /// <summary>True for a procedural belt member (range-gated in detection), false for a
            /// discrete named roid (always emitted). Mirrors <see cref="AsteroidDefinition.BeltMember"/>.</summary>
            public readonly bool IsBeltMember;
            private readonly KeplerianEphemeris _ephemeris;

            public Entry(string id, string parentBodyName, double radius, int voxelSeed,
                string composition, bool isBeltMember, KeplerianEphemeris ephemeris)
            {
                Id = id;
                ParentBodyName = parentBodyName;
                RepresentativeRadiusMeters = radius;
                VoxelSeed = voxelSeed;
                Composition = composition;
                IsBeltMember = isBeltMember;
                _ephemeris = ephemeris;
            }

            /// <summary>This asteroid's position RELATIVE to its parent body's centre, in the
            /// parent's inertial (root-aligned, non-rotating) frame, at absolute time t.</summary>
            public Vector3D OffsetAt(double t)
            {
                return _ephemeris.PositionAt(t);
            }

            /// <summary>The roid's Keplerian orbit at its baked epoch (parent-relative, parent's μ).
            /// A rendezvous'd roid's ProximityFrame seeds its <see cref="KeplerianElements"/> with
            /// this so the frame RIDES the exact same orbit the catalog/detection track — i.e.
            /// <c>frame.PositionAt(t)</c> equals <see cref="OffsetAt"/> at every t (both propagate
            /// the same epoch elements from epoch). Returns a copy (KeplerianElements is a struct).</summary>
            public KeplerianElements Elements
            {
                get { return _ephemeris.EpochElements; }
            }
        }

        private readonly List<Entry> _entries = new List<Entry>();

        private AsteroidRegistry() { }

        /// <summary>Every charted asteroid (stable order). Do not mutate.</summary>
        public IReadOnlyList<Entry> Entries { get { return _entries; } }

        /// <summary>Number of charted asteroids.</summary>
        public int Count { get { return _entries.Count; } }

        // Lazy id→entry index for O(1) lookup (the contact HUD resolves a "ROID:&lt;id&gt;" track to its
        // entry every refresh). Built on first use; the entry list is immutable after Build, so the
        // index never goes stale within a registry's life.
        private Dictionary<string, Entry> _byId;

        /// <summary>Find a charted asteroid by its stable <see cref="Entry.Id"/>. Returns false if no
        /// such id exists (or the catalog is empty). O(1) after a one-time index build.</summary>
        public bool TryGet(string id, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (_byId == null)
            {
                _byId = new Dictionary<string, Entry>(_entries.Count);
                for (int i = 0; i < _entries.Count; i++) _byId[_entries[i].Id] = _entries[i];
            }
            return _byId.TryGetValue(id, out entry);
        }

        /// <summary>
        /// Build + publish the asteroid catalog from a set of definitions against the live body
        /// system (for parent μ + epoch). Definitions whose parent is unknown or that fail
        /// validation are SKIPPED (reported via <paramref name="skipped"/>) rather than aborting
        /// the whole catalog — one bad roid never blacks out the rest. Returns the published
        /// registry (also set as <see cref="Active"/>); never null.
        /// </summary>
        public static AsteroidRegistry Build(IEnumerable<AsteroidDefinition> defs, SystemRegistry sys,
            out List<string> skipped)
        {
            skipped = new List<string>();
            AsteroidRegistry reg = new AsteroidRegistry();
            double epoch = (sys != null && sys.Definition != null) ? sys.Definition.EpochSeconds : 0.0;
            // Tilt onto the SAME plane the bodies were built on (tilt audit 2026-06-17): the
            // authored elements are canonical (+Z plane); without this the belt orbits the
            // un-tilted ecliptic while the bodies orbit the tilted plane, off by the full tilt
            // angle (OffsetAt is consumed root-aligned against the tilted parent origin).
            SystemBuilder.Tilt tilt = SystemBuilder.TiltFor(sys != null ? sys.PlaneNormal : Vector3D.Zero);

            if (defs != null && sys != null)
            {
                foreach (AsteroidDefinition d in defs)
                {
                    if (d == null) continue;
                    string err;
                    if (!d.TryValidate(out err)) { skipped.Add(err); continue; }
                    GravityBody parent = sys.Find(d.ParentBodyName);
                    if (parent == null)
                    {
                        skipped.Add("Asteroid '" + d.Id + "' references unknown parent '" + d.ParentBodyName + "'.");
                        continue;
                    }
                    KeplerianElements el = SystemBuilder.TiltElements(
                        ElementsFor(d, parent.Mu, epoch), tilt, parent.Mu, epoch);
                    reg._entries.Add(new Entry(d.Id, d.ParentBodyName, d.RepresentativeRadiusMeters,
                        d.VoxelSeed, d.Composition, d.BeltMember, new KeplerianEphemeris(el)));
                }
            }

            Active = reg;
            return reg;
        }

        /// <summary>Clear the active catalog (session unload).</summary>
        public static void Clear()
        {
            Active = null;
        }

        // Build a Keplerian element set from an asteroid's real elements + the PARENT's μ at the
        // system epoch (mirrors SystemBuilder.ElementsFor for bodies). Degrees -> radians; mean
        // anomaly -> true anomaly via the core Newton solver; μ is the parent's so period/mean
        // motion are Kepler-3-consistent.
        private static KeplerianElements ElementsFor(AsteroidDefinition d, double muParent, double epoch)
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
