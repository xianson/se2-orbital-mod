using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// Result of building a <see cref="SystemDefinition"/> into a live SOI tree:
    /// the root <see cref="GravityBody"/>, a name -&gt; node lookup, and either success
    /// or a clear error message. C# 6: no tuples, plain class.
    /// </summary>
    public sealed class SystemBuildResult
    {
        public bool Ok;
        public string Error;
        public GravityBody Root;
        public readonly Dictionary<string, GravityBody> ByName =
            new Dictionary<string, GravityBody>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Turns a <see cref="SystemDefinition"/> (POCO, real elements) into the orbital
    /// core's <see cref="GravityBody"/> SOI tree. This is where the RSS fixes become
    /// concrete and testable:
    ///
    ///  - One mu per body: mu = g_surface * R^2 (<see cref="BodyDefinition.ComputeMu"/>),
    ///    the same value used everywhere. (fixes RSS#2)
    ///  - Period is DERIVED, never input: each child's <see cref="KeplerianElements"/> is
    ///    built with Mu = PARENT's mu, so its period is exactly 2 pi sqrt(a^3 / mu_parent).
    ///    Kepler-3 holds across the whole system by construction. (fixes RSS#2)
    ///  - Real elements i / Omega / omega / M0 drive orientation and phase, converted
    ///    deg-&gt;rad and M0-&gt;true-anomaly via the core's Newton solver. (fixes RSS#1, RSS#3)
    ///  - SOI from Laplace (<see cref="Gravity.LaplaceSoiRadius"/>) from the mass ratio,
    ///    not a stored gravity-limit.
    ///  - Static bodies (root, or HasOrbit==false) use <see cref="FixedEphemeris"/>;
    ///    parked placement is the explicit ParkPosition field, not a hijacked string.
    ///    (fixes RSS#5)
    ///
    /// THE SYSTEM-PLANE TILT — LIVE IN PRODUCTION again since 2026-06-17 (rob "tilt our whole
    /// sky"): the authored definition is written in the orbital core's canonical X-Y reference
    /// plane (+Z normal), and the production build (<see cref="Build(SystemDefinition, Vector3D,
    /// System.Func{BodyDefinition, double})"/> via <c>SystemRuntime.EnsureBuilt</c>) rotates the
    /// WHOLE built system by the minimal rotation taking +Z onto <paramref name="planeNormal"/>
    /// (the engine sun-axis plane) — every orbit, park position and spin axis; a rotation is an
    /// isometry, so nothing inside the system can tell. The latent-conjunction catalogs
    /// (AsteroidRegistry / EncounterRegistry) and the encounter OrbitAssigner MUST tilt onto the
    /// SAME plane via <see cref="TiltElements"/> / <see cref="Tilt"/> — they bake elements consumed
    /// root-aligned against tilted parent bodies, so a canonical-plane orbit is mis-planed by the
    /// full tilt angle (the 2026-06-17 audit's one root cause). Exercised by Orbital.Tests'
    /// rotation-isometry pins.
    ///
    /// Validation returns clear errors for: empty/duplicate names, no root, multiple
    /// roots, unknown parent, and parent cycles.
    /// </summary>
    public static class SystemBuilder
    {
        private const double DegToRad = Math.PI / 180.0;

        public static SystemBuildResult Build(SystemDefinition def)
        {
            return Build(def, Vector3D.UnitZ, null);
        }

        /// <summary>Canonical build with a μ RESOLVER: <paramref name="muOf"/> returns the
        /// gravitational parameter to use for each body, overriding the authored
        /// <see cref="BodyDefinition.ComputeMu"/>. The in-game caller passes a resolver that
        /// calibrates voxel-backed bodies to their stock planet generator's actual gravity
        /// (g_surface × R_max², the inverse-square well a ship in orbit truly feels) so the
        /// analytic rails match the materialized voxel; offline callers pass null (authored μ).
        /// Threaded INTO the build (not patched after) because a child's orbit bakes the
        /// PARENT's μ at construction — see <see cref="BuildChild"/>.</summary>
        public static SystemBuildResult Build(SystemDefinition def, Func<BodyDefinition, double> muOf)
        {
            return Build(def, Vector3D.UnitZ, muOf);
        }

        public static SystemBuildResult Build(SystemDefinition def, Vector3D planeNormal)
        {
            return Build(def, planeNormal, null);
        }

        // MU resolver indirection: a body's μ is muOf(def) when a resolver is supplied, else the
        // authored g_surface·R². One chokepoint so root, children and parent-μ-for-orbits all
        // agree on the same value.
        private static double MuOf(BodyDefinition d, Func<BodyDefinition, double> muOf)
        {
            return muOf != null ? muOf(d) : d.ComputeMu();
        }

        public static SystemBuildResult Build(SystemDefinition def, Vector3D planeNormal,
            Func<BodyDefinition, double> muOf)
        {
            var result = new SystemBuildResult();
            Tilt tilt = Tilt.For(planeNormal);

            if (def == null) { result.Error = "SystemDefinition is null."; return result; }
            if (def.Bodies == null || def.Bodies.Count == 0)
            {
                result.Error = "SystemDefinition has no bodies.";
                return result;
            }

            // 1. Per-body structural validation + duplicate-name + root-count checks.
            var defByName = new Dictionary<string, BodyDefinition>(StringComparer.Ordinal);
            BodyDefinition rootDef = null;
            for (int i = 0; i < def.Bodies.Count; i++)
            {
                BodyDefinition b = def.Bodies[i];
                string bodyError;
                if (!b.TryValidate(out bodyError)) { result.Error = bodyError; return result; }
                if (defByName.ContainsKey(b.Name))
                {
                    result.Error = "Duplicate body name '" + b.Name + "'.";
                    return result;
                }
                defByName.Add(b.Name, b);
                if (b.IsRoot)
                {
                    if (rootDef != null)
                    {
                        result.Error = "Multiple root bodies ('" + rootDef.Name + "' and '" + b.Name +
                                       "'); exactly one body must have an empty Parent.";
                        return result;
                    }
                    rootDef = b;
                }
            }
            if (rootDef == null)
            {
                result.Error = "No root body (every body has a Parent). Exactly one body must have an empty Parent.";
                return result;
            }

            // 2. Parent resolution + cycle check (each non-root must reach the root).
            for (int i = 0; i < def.Bodies.Count; i++)
            {
                BodyDefinition b = def.Bodies[i];
                if (b.IsRoot) continue;
                if (!defByName.ContainsKey(b.Parent))
                {
                    result.Error = "Body '" + b.Name + "' references unknown parent '" + b.Parent + "'.";
                    return result;
                }
                string cycleError;
                if (!CheckReachesRoot(b, defByName, out cycleError)) { result.Error = cycleError; return result; }
            }

            // 3. Build the root node.
            GravityBody rootNode = GravityBody.Root(rootDef.Name, MuOf(rootDef, muOf));
            ApplySpin(rootNode, rootDef, tilt);
            result.ByName.Add(rootDef.Name, rootNode);

            // 4. Build children in parent-before-child order (BFS from the root).
            var queue = new Queue<string>();
            queue.Enqueue(rootDef.Name);
            // index children by parent name for O(1) expansion
            var childrenOf = new Dictionary<string, List<BodyDefinition>>(StringComparer.Ordinal);
            for (int i = 0; i < def.Bodies.Count; i++)
            {
                BodyDefinition b = def.Bodies[i];
                if (b.IsRoot) continue;
                List<BodyDefinition> list;
                if (!childrenOf.TryGetValue(b.Parent, out list))
                {
                    list = new List<BodyDefinition>();
                    childrenOf[b.Parent] = list;
                }
                list.Add(b);
            }

            while (queue.Count > 0)
            {
                string parentName = queue.Dequeue();
                GravityBody parentNode = result.ByName[parentName];
                List<BodyDefinition> kids;
                if (!childrenOf.TryGetValue(parentName, out kids)) continue;
                for (int i = 0; i < kids.Count; i++)
                {
                    BodyDefinition kid = kids[i];
                    GravityBody node = BuildChild(parentNode, kid, def.EpochSeconds, tilt, muOf);
                    result.ByName.Add(kid.Name, node);
                    queue.Enqueue(kid.Name);
                }
            }

            result.Root = rootNode;
            result.Ok = true;
            return result;
        }

        // Walk Parent links up to the root; detect cycles and orphan chains.
        private static bool CheckReachesRoot(BodyDefinition start,
            Dictionary<string, BodyDefinition> defByName, out string error)
        {
            error = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            BodyDefinition cur = start;
            while (cur != null && !cur.IsRoot)
            {
                if (!seen.Add(cur.Name))
                {
                    error = "Parent cycle detected involving body '" + cur.Name + "'.";
                    return false;
                }
                BodyDefinition parent;
                if (!defByName.TryGetValue(cur.Parent, out parent))
                {
                    error = "Body '" + cur.Name + "' references unknown parent '" + cur.Parent + "'.";
                    return false;
                }
                cur = parent;
            }
            return true;
        }

        // Build one child GravityBody under an already-built parent node.
        private static GravityBody BuildChild(GravityBody parentNode, BodyDefinition kid,
            double epoch, Tilt tilt, Func<BodyDefinition, double> muOf)
        {
            double childMu = MuOf(kid, muOf);

            IEphemeris ephem;
            double soiRadius;

            if (kid.HasOrbit)
            {
                // Period is DERIVED: Mu of the element set is the PARENT's mu, so the core's
                // KeplerianElements.Period == 2 pi sqrt(a^3 / mu_parent). No stored period.
                // The system-plane tilt (pinned offline in Orbital.Tests "tilted system build").
                // Shared with the latent-conjunction catalogs via TiltElements.
                KeplerianElements el = TiltElements(ElementsFor(kid, parentNode.Mu, epoch), tilt, parentNode.Mu, epoch);
                ephem = new KeplerianEphemeris(el);
                // SOI from the Laplace formula on the mass (mu) ratio at the orbit radius.
                soiRadius = Gravity.LaplaceSoiRadius(kid.SemiMajorAxisMeters, childMu, parentNode.Mu);
            }
            else
            {
                // Static / parked body: fixed position in the parent frame.
                Vector3D pos = kid.HasParkPosition
                    ? new Vector3D(kid.ParkPositionX, kid.ParkPositionY, kid.ParkPositionZ)
                    : Vector3D.Zero;
                ephem = new FixedEphemeris(tilt.Rotate(pos));
                // No orbit radius for Laplace; fall back to a radius-scaled SOI placeholder.
                soiRadius = kid.RadiusMeters * 10.0;
            }

            GravityBody node = parentNode.AddChild(kid.Name, childMu, soiRadius, ephem);
            ApplySpin(node, kid, tilt);
            return node;
        }

        // Carry the authored "planet day" (sidereal rotation period + spin axis) from the
        // config POCO onto the runtime node. Spin is a readout/orientation quantity only;
        // it never touches the gravity/orbit math. The axis defaults to +Z when unset; the
        // system-plane tilt rotates it like every other authored direction (the default
        // becomes the plane normal itself).
        private static void ApplySpin(GravityBody node, BodyDefinition def, Tilt tilt)
        {
            node.RotationPeriodSeconds = def.RotationPeriodSeconds;
            Vector3D axis = new Vector3D(def.SpinAxisX, def.SpinAxisY, def.SpinAxisZ);
            if (axis.LengthSquared() < 1e-12) axis = Vector3D.UnitZ;
            else axis.Normalize();
            node.SpinAxis = tilt.Rotate(axis);
        }

        /// <summary>
        /// The system-plane tilt: the MINIMAL rotation carrying the canonical +Z reference
        /// normal onto the requested plane normal (Rodrigues about normalize(Z × n)).
        /// Deterministic from the normal alone. Identity (Active == false) for +Z — the
        /// production-and-offline default, byte-identical to the legacy build (the tilted
        /// overload is the LIVE production path since 2026-06-17; see the class doc).
        /// Self-contained (no helper types) so every csproj that compiles SystemBuilder keeps
        /// compiling unchanged.
        /// </summary>
        /// <summary>The system-plane rotation (canonical +Z -&gt; the world's engine sun-axis
        /// plane normal). PUBLIC so the latent-conjunction catalogs (AsteroidRegistry,
        /// EncounterRegistry) and the encounter OrbitAssigner can tilt their orbits onto the SAME
        /// plane the bodies are built on — they bake elements that are consumed root-aligned
        /// against tilted parent bodies, so a canonical-plane orbit would be mis-planed by the
        /// full tilt angle (the 2026-06-17 tilt audit's one root cause).</summary>
        public struct Tilt
        {
            public bool Active;
            private Vector3D _axis;       // unit rotation axis (Z × n, normalized)
            private double _cos, _sin;    // cos/sin of the rotation angle (Z -> n)

            public static Tilt For(Vector3D planeNormal)
            {
                Tilt t = new Tilt();
                Vector3D n = planeNormal;
                if (n.LengthSquared() < 1e-12) return t;        // degenerate input -> identity
                n.Normalize();
                Vector3D ax = Vector3D.Cross(Vector3D.UnitZ, n);
                double sin = ax.Length();
                double cos = n.Z;
                if (sin < 1e-12)
                {
                    // n ∥ ±Z. +Z: identity. −Z: a flip about X (any axis ⊥ Z is minimal).
                    if (cos > 0.0) return t;
                    t.Active = true;
                    t._axis = Vector3D.UnitX;
                    t._cos = -1.0;
                    t._sin = 0.0;
                    return t;
                }
                t.Active = true;
                t._axis = ax / sin;
                t._cos = cos;
                t._sin = sin;
                return t;
            }

            /// <summary>Rodrigues rotation of <paramref name="v"/> (identity when inactive).</summary>
            public Vector3D Rotate(Vector3D v)
            {
                if (!Active) return v;
                return v * _cos + Vector3D.Cross(_axis, v) * _sin
                     + _axis * (Vector3D.Dot(_axis, v) * (1.0 - _cos));
            }

            /// <summary>The INVERSE rotation (tilted plane -&gt; canonical +Z): Rodrigues with
            /// −angle (sin negated). Used to bring a world/tilted-frame position back to the
            /// canonical frame the OrbitAssigner samples in.</summary>
            public Vector3D RotateInverse(Vector3D v)
            {
                if (!Active) return v;
                return v * _cos - Vector3D.Cross(_axis, v) * _sin
                     + _axis * (Vector3D.Dot(_axis, v) * (1.0 - _cos));
            }
        }

        /// <summary>Rotate a canonical-plane conic onto the system plane: elements -&gt; epoch
        /// state -&gt; rotate -&gt; elements (a rotated conic is the same conic about the same mu, so
        /// the re-derived set propagates identically up to the rotation for ALL t). THE one place
        /// the tilt round-trip lives; <see cref="BuildChild"/> and the latent-conjunction catalogs
        /// all call it so the four element-baking sites can never drift. Identity when inactive.</summary>
        public static KeplerianElements TiltElements(KeplerianElements el, Tilt tilt, double muParent, double epoch)
        {
            if (!tilt.Active) return el;
            StateVector s = OrbitalMath.ToState(el);
            return OrbitalMath.ToElements(
                new StateVector(tilt.Rotate(s.Position), tilt.Rotate(s.Velocity)), muParent, epoch);
        }

        /// <summary>The system tilt for a plane normal — the public factory the catalogs use
        /// (forwards to the internal <see cref="Tilt.For"/>).</summary>
        public static Tilt TiltFor(Vector3D planeNormal)
        {
            return Tilt.For(planeNormal);
        }

        /// <summary>
        /// Build a full <see cref="KeplerianElements"/> set from a body's real elements and
        /// the PARENT's mu at the system epoch. Degrees -&gt; radians; mean anomaly at epoch
        /// -&gt; true anomaly via the core Newton solver. Mu is the parent's, so period and
        /// mean motion are Kepler-3-consistent.
        /// </summary>
        public static KeplerianElements ElementsFor(BodyDefinition kid, double muParent, double epoch)
        {
            double m0 = kid.MeanAnomalyAtEpochDeg * DegToRad;
            double nu = OrbitalMath.MeanToTrueAnomaly(m0, kid.Eccentricity);

            KeplerianElements el = new KeplerianElements();
            el.SemiMajorAxis = kid.SemiMajorAxisMeters;
            el.Eccentricity = kid.Eccentricity;
            el.Inclination = kid.InclinationDeg * DegToRad;
            el.Raan = kid.RaanDeg * DegToRad;
            el.ArgPeriapsis = kid.ArgPeriapsisDeg * DegToRad;
            el.TrueAnomaly = nu;
            el.Mu = muParent;
            el.Epoch = epoch;
            return el;
        }
    }
}
