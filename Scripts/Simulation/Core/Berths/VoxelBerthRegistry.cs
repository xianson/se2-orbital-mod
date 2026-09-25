using System.Collections.Generic;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

namespace SEAerospace
{
    /// <summary>
    /// The planet-cell grid: "the server knows about a grid of planets near the origin." Every
    /// voxel-bearing body has a FIXED, deterministic cell — its own spaced world region — derived
    /// purely from the system definition, NOT from occupancy order. The body's real voxel
    /// materializes at that cell when the body is occupied and dematerializes when it is vacated;
    /// the cell itself never moves and is the same on every observer and across reloads.
    ///
    /// This is the voxel analogue of the grid-berth model (<see cref="BerthAllocator"/>): just as a
    /// Conjunction's grids pack into a spaced slot near origin so berths never interfere, each
    /// PLANET owns a spaced slot for its real voxel. Spacing exceeds the body's gravity/streaming/
    /// isolation extent (<see cref="PlanetBerths.CellIsolationRadius"/> — NOT the slimmer handoff
    /// shell), so two materialized planets near origin don't pull on or stream into each other. Players are NEVER in shared interplanetary space in real physics — between bodies they
    /// coast on rails in a ProximityFrame, at a body they are in that body's planet cell — so there
    /// is no global physical space to keep consistent across observers. N players at N bodies = N
    /// occupied planet cells; each client draws its own true-scale, frame-relative proxy sky.
    ///
    /// The mapping is body -> stable slot id = the body's ordinal among voxel-bearing bodies in
    /// <see cref="SystemRegistry"/> order. Slot 0 (the first voxel body — the "home" body) lands on
    /// <see cref="PlanetBerths.CurrentBerth"/> (origin region), so single-body play is byte-for-byte
    /// the old layout (the player still spawns inside the home body's shell); subsequent bodies get
    /// spaced lattice slots. Because the mapping is occupancy-independent it is server-authoritative
    /// truth every client can derive locally.
    ///
    /// ONE SHARED LATTICE — berths never conflict. Planets and Conjunctions use the SAME
    /// <see cref="BerthAllocator"/> (<see cref="SharedAllocator"/>): planets RESERVE the deterministic
    /// slots 0..N-1, Conjunctions <c>Allocate()</c> the rest. The allocator's invariant ("slots never
    /// interact" — spacing &gt; 2× the largest body's isolation envelope) then makes EVERY berth conflict
    /// impossible by construction — planet/planet, planet/conjunction, conjunction/conjunction alike.
    ///
    /// Game-free (VRageMath + the orbital/system core), so it is exercised offline.
    /// </summary>
    public static class VoxelBerthRegistry
    {
        // Extra gap (m) between cell surfaces on top of each body's interaction extent — proxy /
        // visibility margin so two materialized planets near origin never stream into each other.
        private const double SlotClearance = 100.0e3;
        // Floor for the per-cell half-extent so a tiny test body still gets a sane slot size.
        private const double MinHalfExtent = 150.0e3;

        // Deterministic body -> slot id (ordinal among voxel bodies in registry order). Built once
        // from the active system; occupancy-INDEPENDENT, so it never changes as bodies come and go.
        private static readonly Dictionary<string, int> _slotOf = new Dictionary<string, int>();
        // The bodies that currently have a materialized voxel (a plain set — NOT an allocator).
        private static readonly HashSet<string> _occupied = new HashSet<string>();
        private static BerthAllocator _alloc;   // THE shared lattice (planets + conjunctions)
        private static bool _mapped;            // _slotOf has been built + planet slots reserved

        /// <summary>Number of bodies currently materialized (occupied cells).</summary>
        public static int Count { get { return _occupied.Count; } }

        /// <summary>Whether ANY body is currently materialized (the warp-lock signal: a real voxel is up).</summary>
        public static bool AnyOccupied { get { return _occupied.Count > 0; } }

        /// <summary>The fixed world cell center of <paramref name="body"/> (occupancy-independent).
        /// Returns false for an unknown / non-voxel body.</summary>
        public static bool TryGetCell(string body, SystemRegistry reg, out Vector3D center)
        {
            center = Vector3D.Zero;
            if (string.IsNullOrEmpty(body) || reg == null) return false;
            EnsureMapping(reg);
            int slot;
            if (!_slotOf.TryGetValue(body, out slot)) return false;
            center = _alloc.SlotCenter(slot);   // allocator is centered on CurrentBerth (slot 0 = home)
            return true;
        }

        /// <summary>
        /// THE shared berth lattice (planets + Conjunctions on one allocator, so no berth ever
        /// conflicts with another). Building it reserves the planet slots 0..N-1; the FrameManager
        /// hands this to its FrameRegistry so Conjunction <c>Allocate()</c> only ever returns the
        /// remaining (non-planet) slots. Returns null only if there is no active system to size it.
        /// </summary>
        public static BerthAllocator SharedAllocator(SystemRegistry reg)
        {
            if (reg == null) return _alloc;
            EnsureMapping(reg);
            return _alloc;
        }

        /// <summary>The fixed world cell center of <paramref name="body"/>, or
        /// <see cref="PlanetBerths.CurrentBerth"/> if it has no cell (degenerate fallback).</summary>
        public static Vector3D CellOf(string body, SystemRegistry reg)
        {
            Vector3D c;
            return TryGetCell(body, reg, out c) ? c : PlanetBerths.CurrentBerth;
        }

        /// <summary>
        /// The voxel body whose fixed lattice cell (slot sphere, radius = the shared allocator's
        /// <see cref="BerthAllocator.SlotRadius"/>) contains <paramref name="worldPos"/> — or false
        /// when the position is in no planet cell. At most one cell can match (slot spheres are
        /// disjoint by the lattice spacing invariant), and the mapping is occupancy-INDEPENDENT:
        /// it answers "whose cell is this position in" even while the body's voxel does not exist
        /// yet (streaming in) or was just torn down. That is exactly the anchor-swap transition
        /// window <c>PlanetBerths.ResolveFrame</c>'s cell-containment fallback closes — see there.
        /// Deterministic from the system definition, so safe client-side with no sync.
        /// </summary>
        public static bool TryCellContaining(Vector3D worldPos, SystemRegistry reg,
            out string body, out Vector3D center)
        {
            body = null;
            center = Vector3D.Zero;
            if (reg == null) return false;
            EnsureMapping(reg);
            double r = _alloc.SlotRadius;
            foreach (KeyValuePair<string, int> kv in _slotOf)
            {
                Vector3D c = _alloc.SlotCenter(kv.Value);
                if (Vector3D.DistanceSquared(worldPos, c) <= r * r)
                {
                    body = kv.Key;
                    center = c;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Mark <paramref name="body"/> as materialized (its voxel is up in its cell).</summary>
        public static void MarkOccupied(string body)
        {
            if (!string.IsNullOrEmpty(body)) _occupied.Add(body);
        }

        /// <summary>Mark <paramref name="body"/> as vacated (its voxel was torn down).</summary>
        public static void MarkVacant(string body)
        {
            if (!string.IsNullOrEmpty(body)) _occupied.Remove(body);
        }

        /// <summary>Whether <paramref name="body"/> is currently materialized.</summary>
        public static bool IsOccupied(string body)
        {
            return !string.IsNullOrEmpty(body) && _occupied.Contains(body);
        }

        /// <summary>Whether <paramref name="slot"/> is a RESERVED PLANET slot on the shared lattice
        /// (0..N-1). The frame restore path uses this to re-point a Conjunction from an older save
        /// whose saved slot now belongs to a planet, so two berths never share a slot.</summary>
        public static bool IsPlanetSlot(int slot, SystemRegistry reg)
        {
            if (slot < 0 || reg == null) return false;
            EnsureMapping(reg);
            return slot < _slotOf.Count;   // planets own the contiguous block 0..N-1
        }

        /// <summary>The occupied bodies paired with their fixed cell centers (snapshot-safe).</summary>
        public static IEnumerable<KeyValuePair<string, Vector3D>> Occupied(SystemRegistry reg)
        {
            List<KeyValuePair<string, Vector3D>> snap = new List<KeyValuePair<string, Vector3D>>(_occupied.Count);
            foreach (string body in _occupied)
            {
                Vector3D c;
                if (TryGetCell(body, reg, out c)) snap.Add(new KeyValuePair<string, Vector3D>(body, c));
            }
            return snap;
        }

        /// <summary>Drop all state (session unload / world change — rebuild the mapping next world).</summary>
        public static void Clear()
        {
            _slotOf.Clear();
            _occupied.Clear();
            _alloc = null;
            _mapped = false;
        }

        // Build the deterministic body -> slot map once from the active system: walk reg.Bodies in
        // order, assign sequential slot ids to voxel-bearing bodies (radius>0 && has ParkSubtype).
        // The first such body gets slot 0 -> CurrentBerth (home at origin). Same definition -> same
        // map on every client and across reloads. Then RESERVE those planet slots on the shared
        // allocator so Conjunction Allocate() can never hand a planet's slot to a berth (the
        // berths-never-conflict guarantee). Also sizes the lattice from the largest body.
        private static void EnsureMapping(SystemRegistry reg)
        {
            if (_mapped) return;
            EnsureAllocator(reg);
            _slotOf.Clear();
            int next = 0;
            IReadOnlyList<GravityBody> bodies = reg.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                BodyDefinition def = reg.FindDefinition(bodies[i].Name);
                if (def == null || def.RadiusMeters <= 0.0 || string.IsNullOrEmpty(def.ParkSubtype)) continue;
                _slotOf[bodies[i].Name] = next++;
            }
            for (int slot = 0; slot < next; slot++) _alloc.Reserve(slot);   // planets own slots 0..N-1
            _mapped = true;
        }

        // Size the lattice once from the system's largest occupiable body so a cell comfortably holds
        // any body's voxel + isolation envelope + gravity reach AND no two cells' envelopes interact.
        // Half-extent = CellIsolationRadius + one extra radius (gravity margin beyond the envelope);
        // spacing = 2*halfExtent + clearance. DELIBERATELY NOT ShellRadius: the handoff shell shrank
        // to "slightly above the atmosphere" (2026-06-11), but the spacing guarantee ("a neighbor
        // cell's voxel is beyond view range") keys off view range + worst-case terrain extent, which
        // did not shrink — see PlanetBerths.CellIsolationRadius. Centered on CurrentBerth so planet
        // slot 0 = the home body at the origin region; Conjunction berths (Allocate() on the SAME
        // lattice) are spaced by the same guarantee.
        private static void EnsureAllocator(SystemRegistry reg)
        {
            if (_alloc != null) return;
            double halfExtent = MinHalfExtent;
            if (reg != null)
            {
                IReadOnlyList<GravityBody> bodies = reg.Bodies;
                for (int i = 0; i < bodies.Count; i++)
                {
                    BodyDefinition def = reg.FindDefinition(bodies[i].Name);
                    if (def == null || def.RadiusMeters <= 0.0 || string.IsNullOrEmpty(def.ParkSubtype)) continue;
                    double he = PlanetBerths.CellIsolationRadius(def) + def.RadiusMeters;   // isolation + gravity margin
                    if (he > halfExtent) halfExtent = he;
                }
            }
            _alloc = new BerthAllocator(halfExtent, SlotClearance, PlanetBerths.CurrentBerth);
        }
    }
}
