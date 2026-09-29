using System.Collections.Generic;

namespace SEAerospace.Frames
{
    /// <summary>
    /// Packs ProximityFrame berths into an origin-centered region of SE world space — the
    /// deliberate replacement for RSS's randomized far-flung spawn shell. Everything
    /// physical lives in the precision sweet spot near the origin; all "bigness" lives in
    /// the celestial realm's numbers, never in world coordinates (see
    /// docs/architecture-proximity-frames.md "Berth packing").
    ///
    /// Slot model (rob, 06-10): a SINGLE configurable berth size. Every slot is a sphere of
    /// the same <see cref="SlotRadius"/>; centers sit on a cubic lattice whose spacing
    /// (<see cref="Spacing"/> = 2*radius + clearance) guarantees slots never interact —
    /// outside each other's streaming (~15 km), sensor, weapon, and proxy-visibility ranges.
    /// Slots are enumerated in shells of increasing distance from the origin and packed
    /// nearest-first, so a whole system's physical footprint stays within a few thousand km
    /// of origin even with many frames.
    ///
    /// Slot ids are STABLE: id -> lattice cell is a fixed mapping (the cell list only ever
    /// grows, append-only), so a persisted (frame id -> slot id -> transform) survives
    /// reload and recycles on dissolve. Game-free (VRageMath only) and offline-testable.
    /// </summary>
    public sealed class BerthAllocator
    {
        private readonly double _slotRadius;
        private readonly double _spacing;
        private readonly Vector3D _origin;                 // world base the lattice is centered on

        // Append-only, distance-ordered lattice cells. Index = slot id; _cells[id] is the
        // integer cell, world center = origin + (Vector3D)cell * spacing. Never reordered, so
        // ids are stable for the session and across persistence.
        private readonly List<Vector3I> _cells = new List<Vector3I>();
        private int _shellRadius;                          // next Chebyshev shell to generate
        private readonly HashSet<int> _occupied = new HashSet<int>();

        /// <param name="slotRadius">Berth sphere radius, meters (the configurable berth size).</param>
        /// <param name="clearance">Extra gap between slot SURFACES, meters — must exceed the
        /// largest cross-slot interaction range (streaming/sensor/weapon/visibility). Slot
        /// CENTER spacing = 2*slotRadius + clearance.</param>
        public BerthAllocator(double slotRadius, double clearance)
            : this(slotRadius, clearance, Vector3D.Zero) { }

        /// <param name="origin">World base the lattice is centered on (slot 0 lands here). Lets the
        /// conjunction lattice sit in a region DISJOINT from the planet-cell grid so a coasting
        /// Conjunction is never buried inside a materialized planet's shell.</param>
        public BerthAllocator(double slotRadius, double clearance, Vector3D origin)
        {
            _slotRadius = slotRadius > 0.0 ? slotRadius : 1.0;
            double sp = 2.0 * _slotRadius + (clearance > 0.0 ? clearance : 0.0);
            _spacing = sp > 0.0 ? sp : 1.0;
            _origin = origin;
            _shellRadius = 0;
        }

        /// <summary>Berth sphere radius (m) — the single configured berth size.</summary>
        public double SlotRadius { get { return _slotRadius; } }

        /// <summary>Center-to-center lattice spacing (m) = 2*radius + clearance.</summary>
        public double Spacing { get { return _spacing; } }

        /// <summary>Number of slots currently allocated.</summary>
        public int OccupiedCount { get { return _occupied.Count; } }

        /// <summary>World-space center of a slot id (origin-centered lattice). Generates the
        /// cell on demand; stable for the life of the session.</summary>
        public Vector3D SlotCenter(int slotId)
        {
            EnsureCells(slotId + 1);
            Vector3I c = _cells[slotId];
            return _origin + new Vector3D(c.X * _spacing, c.Y * _spacing, c.Z * _spacing);
        }

        /// <summary>
        /// Allocate the nearest-to-origin free slot. Returns its id; <paramref name="center"/>
        /// is its world center. Slots are handed out packed-nearest-first.
        /// </summary>
        public int Allocate(out Vector3D center)
        {
            int id = 0, skipped = 0;
            while (true)
            {
                EnsureCells(id + 1);
                if (!_occupied.Contains(id))
                {
                    Vector3I c = _cells[id];
                    center = _origin + new Vector3D(c.X * _spacing, c.Y * _spacing, c.Z * _spacing);
                    // A slot is only handed out physically empty: grids left behind in a freed slot (or a rock)
                    // would sit right next to whoever lands there. (Give up checking after many: never stall.)
                    if (IsClear == null || skipped > 256 || IsClear(center))
                    {
                        _occupied.Add(id);
                        return id;
                    }
                    skipped++;
                }
                id++;
            }
        }

        /// <summary>Whether a slot's space is physically empty (no leftover grid, no rock); null = not checked.</summary>
        public System.Func<Vector3D, bool> IsClear;

        // Hard ceiling on lattice growth. EnsureCells materializes ~slotId cells, so a corrupt or crafted
        // persisted slot id (e.g. 2,000,000,000 from a hand-edited save) would OOM/hang the world load.
        // A real world never has anywhere near a million conjunction berths; beyond this we refuse to grow.
        // (~12 MB at 1<<20 Vector3I — a safe cap, not a target.)
        private const int MaxSlots = 1 << 20;

        /// <summary>Reserve a specific slot id (loading a persisted frame). Idempotent. A slot id beyond
        /// the sane <see cref="MaxSlots"/> ceiling is IGNORED (a corrupt/crafted save value) rather than
        /// driving unbounded lattice allocation — the caller's frame then falls through to a fresh slot.</summary>
        public bool Reserve(int slotId)
        {
            if (slotId < 0 || slotId >= MaxSlots) return false;
            EnsureCells(slotId + 1);
            _occupied.Add(slotId);
            return true;
        }

        /// <summary>Release a slot back to the pool (frame dissolve/merge). Idempotent.</summary>
        public void Free(int slotId)
        {
            _occupied.Remove(slotId);
        }

        /// <summary>Whether a slot id is currently allocated.</summary>
        public bool IsOccupied(int slotId)
        {
            return _occupied.Contains(slotId);
        }

        // Grow the cell list until it has at least `needed` entries, appending whole
        // Chebyshev shells (max(|x|,|y|,|z|) == r) sorted within the shell by true distance.
        // Append-only keeps every existing slot id pinned to its cell.
        private void EnsureCells(int needed)
        {
            if (needed > MaxSlots) needed = MaxSlots;   // never grow the lattice past the hard ceiling
            while (_cells.Count < needed)
                AppendShell();
        }

        private void AppendShell()
        {
            int r = _shellRadius;
            if (r == 0)
            {
                _cells.Add(new Vector3I(0, 0, 0));
                _shellRadius = 1;
                return;
            }

            // Collect the shell's surface cells (at least one coordinate == +/-r).
            List<Vector3I> shell = new List<Vector3I>();
            for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                    {
                        int ax = x < 0 ? -x : x;
                        int ay = y < 0 ? -y : y;
                        int az = z < 0 ? -z : z;
                        int cheb = ax > ay ? ax : ay;
                        if (az > cheb) cheb = az;
                        if (cheb == r) shell.Add(new Vector3I(x, y, z));
                    }

            // Order the shell by true (euclidean) distance so packing stays compact, with a
            // deterministic tiebreak (x, then y, then z) for stable ids.
            shell.Sort(CompareByDistance);
            for (int i = 0; i < shell.Count; i++) _cells.Add(shell[i]);
            _shellRadius = r + 1;
        }

        private static int CompareByDistance(Vector3I a, Vector3I b)
        {
            long da = (long)a.X * a.X + (long)a.Y * a.Y + (long)a.Z * a.Z;
            long db = (long)b.X * b.X + (long)b.Y * b.Y + (long)b.Z * b.Z;
            if (da != db) return da < db ? -1 : 1;
            if (a.X != b.X) return a.X < b.X ? -1 : 1;
            if (a.Y != b.Y) return a.Y < b.Y ? -1 : 1;
            if (a.Z != b.Z) return a.Z < b.Z ? -1 : 1;
            return 0;
        }
    }
}
