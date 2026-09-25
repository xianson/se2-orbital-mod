using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// The live, session-wide star system: the built <see cref="GravityBody"/> SOI tree
    /// plus the <see cref="SystemDefinition"/> it came from, held as the single source of
    /// truth every consumer shares — the orbit renderer/controller (which body am I
    /// orbiting, what is its mu), the future Resolve layer (walk the tree to place a
    /// celestial in the observer's sky), the proxy feed (radius/color per body), and the
    /// frame runtime (which well a clump orbits). Before this existed, OrbitRenderer
    /// re-derived mu per MyPlanet ad hoc; this replaces that with one authored tree.
    ///
    /// Game-free by design (references only the orbital core + the POCO model), so it is
    /// offline-testable. A game-side session component (SystemRuntime) does the actual
    /// load/build and publishes the result into <see cref="Active"/>.
    /// </summary>
    public sealed class SystemRegistry
    {
        /// <summary>The currently-instantiated system for this session, or null before
        /// world load completes / if instantiation failed. Read-only to consumers; the
        /// runtime component is the only writer (via <see cref="Publish"/>).</summary>
        public static SystemRegistry Active { get; private set; }

        /// <summary>The source definition (authored config) this tree was built from.</summary>
        public SystemDefinition Definition { get; private set; }

        /// <summary>Root of the SOI tree (the star / barycenter).</summary>
        public GravityBody Root { get; private set; }

        /// <summary>The plane normal this system was built TILTED onto (the engine sun-axis plane;
        /// <see cref="Vector3D.Zero"/> for a canonical/identity build). The
        /// latent-conjunction catalogs (AsteroidRegistry / EncounterRegistry) and the encounter
        /// OrbitAssigner read THIS to tilt their orbits onto the SAME plane as the bodies —
        /// otherwise their canonical-plane orbits are mis-planed by the full tilt angle (the
        /// 2026-06-17 tilt audit's root cause).</summary>
        public Vector3D PlaneNormal { get; private set; }

        private readonly Dictionary<string, GravityBody> _byName =
            new Dictionary<string, GravityBody>(System.StringComparer.Ordinal);

        // Bodies in build order (root first, then BFS). Stable for enumeration/iteration.
        private readonly List<GravityBody> _bodies = new List<GravityBody>();

        private SystemRegistry() { }

        /// <summary>Every body in the system, root first (build/BFS order). Do not mutate.</summary>
        public IReadOnlyList<GravityBody> Bodies { get { return _bodies; } }

        /// <summary>Number of bodies in the system.</summary>
        public int Count { get { return _bodies.Count; } }

        /// <summary>Look up a body node by its (case-sensitive) name.</summary>
        public GravityBody Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            GravityBody body;
            return _byName.TryGetValue(name, out body) ? body : null;
        }

        /// <summary>The authored body definition for a named body (radius/color/atmosphere
        /// hints the runtime tree does not carry), or null if unknown.</summary>
        public BodyDefinition FindDefinition(string name)
        {
            if (Definition == null || Definition.Bodies == null || string.IsNullOrEmpty(name))
                return null;
            for (int i = 0; i < Definition.Bodies.Count; i++)
                if (Definition.Bodies[i].Name == name) return Definition.Bodies[i];
            return null;
        }

        /// <summary>
        /// Build a registry from a definition and (on success) publish it as the active
        /// system. Returns the underlying <see cref="SystemBuildResult"/> so the caller can
        /// surface the exact validation error on failure. On failure <see cref="Active"/>
        /// is left unchanged. ALWAYS the canonical (+Z reference plane) build — the 2026-06-12
        /// RSS-literal decision: the live system's layout never depends on world/sun/render
        /// state, so there is exactly one layout per definition (the tilted
        /// <see cref="SystemBuilder.Build(SystemDefinition, Vector3D)"/> overload is
        /// offline-only pure math with no production caller).
        /// </summary>
        public static SystemBuildResult Build(SystemDefinition def)
        {
            return Build(def, null);
        }

        /// <summary>Build + publish with a μ resolver (see
        /// <see cref="SystemBuilder.Build(SystemDefinition, System.Func{BodyDefinition, double})"/>):
        /// the in-game caller passes one that calibrates voxel-backed bodies to their stock
        /// generator gravity so rails match the materialized voxel.</summary>
        public static SystemBuildResult Build(SystemDefinition def, System.Func<BodyDefinition, double> muOf)
        {
            return PublishFrom(SystemBuilder.Build(def, muOf), def, Vector3D.Zero);
        }

        /// <summary>Build + publish TILTED onto a plane (the engine sun plane — rob 2026-06-17,
        /// "tilt our whole sky") with a μ resolver: the whole system is rotated so its canonical
        /// +Z reference normal aligns with <paramref name="planeNormal"/>, so the drawn star,
        /// rings, proxies and the rotating chart all share the engine's sweep plane and the sun
        /// is one sun in 3D (see <see cref="SystemRuntime"/>). planeNormal is derived each load
        /// from the world's base sun direction (deterministic, server+client identical).</summary>
        public static SystemBuildResult Build(SystemDefinition def, Vector3D planeNormal,
            System.Func<BodyDefinition, double> muOf)
        {
            return PublishFrom(SystemBuilder.Build(def, planeNormal, muOf), def, planeNormal);
        }

        // Shared publish: stamp the built tree onto a fresh registry (stable root-first BFS
        // enumeration order) and make it Active. On a failed build, Active is left unchanged.
        // planeNormal records the tilt the catalogs must match (Zero = canonical build).
        private static SystemBuildResult PublishFrom(SystemBuildResult result, SystemDefinition def,
            Vector3D planeNormal)
        {
            if (!result.Ok) return result;

            SystemRegistry reg = new SystemRegistry();
            reg.Definition = def;
            reg.Root = result.Root;
            reg.PlaneNormal = planeNormal;
            foreach (KeyValuePair<string, GravityBody> kv in result.ByName)
                reg._byName[kv.Key] = kv.Value;

            Queue<GravityBody> q = new Queue<GravityBody>();
            q.Enqueue(result.Root);
            while (q.Count > 0)
            {
                GravityBody b = q.Dequeue();
                reg._bodies.Add(b);
                for (int i = 0; i < b.Children.Count; i++)
                    q.Enqueue(b.Children[i]);
            }

            Active = reg;
            return result;
        }

        /// <summary>Replace the active registry directly (e.g. a pre-built tree). Mainly for
        /// tests / advanced callers; the normal path is <see cref="Build"/>.</summary>
        public static void Publish(SystemRegistry registry)
        {
            Active = registry;
        }

        /// <summary>Clear the active system (session unload).</summary>
        public static void Clear()
        {
            Active = null;
        }
    }
}
