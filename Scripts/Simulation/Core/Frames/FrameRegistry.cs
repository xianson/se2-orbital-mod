using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.Frames
{
    /// <summary>
    /// The live set of ProximityFrames for the session: create/dissolve, member indexing,
    /// berth-slot management (via <see cref="BerthAllocator"/>), and the "only when isolated"
    /// spawn query (rob, 06-10) — a new grid joins the nearest frame within capture range and
    /// only spawns its OWN frame when none is near. Per-tick rails + dynamics + the
    /// rel-velocity merge/split gate live in the game-side FrameManager; this is the pure
    /// bookkeeping, kept game-free (entity ids as long) so it is offline-testable.
    /// </summary>
    public sealed class FrameRegistry
    {
        /// <summary>The active registry for this session (the FrameManager publishes it).</summary>
        public static FrameRegistry Active { get; private set; }

        private readonly BerthAllocator _allocator;
        private readonly Dictionary<long, ProximityFrame> _byId = new Dictionary<long, ProximityFrame>();
        private readonly Dictionary<long, ProximityFrame> _byMember = new Dictionary<long, ProximityFrame>();
        private long _nextFrameId = 1;

        public FrameRegistry(BerthAllocator allocator)
        {
            _allocator = allocator;
        }

        public BerthAllocator Allocator { get { return _allocator; } }

        /// <summary>The next frame id this registry will hand out (the live <c>_nextFrameId</c>
        /// counter). Persisted on capture so reload resumes the counter EXACTLY, never reusing a
        /// dissolved frame's id (which would silently re-point a persisted LiveFrame address).</summary>
        public long NextFrameId { get { return _nextFrameId; } }

        /// <summary>Restore the next-frame-id counter to a persisted value (load path). Only moves
        /// the counter FORWARD: never below the next free id, so newly created frames after reload
        /// cannot collide with restored ids even on a stale/edited save.</summary>
        public void SetNextFrameId(long next)
        {
            if (next > _nextFrameId) _nextFrameId = next;
        }

        /// <summary>All live frames (do not mutate the collection during iteration).</summary>
        public IEnumerable<ProximityFrame> Frames { get { return _byId.Values; } }

        /// <summary>Number of live frames.</summary>
        public int Count { get { return _byId.Count; } }

        public static void Publish(FrameRegistry registry) { Active = registry; }
        public static void Clear() { Active = null; }

        public ProximityFrame Get(long frameId)
        {
            ProximityFrame f;
            return _byId.TryGetValue(frameId, out f) ? f : null;
        }

        /// <summary>The frame a given member grid belongs to, or null if it is in none.</summary>
        public ProximityFrame FindByMember(long entityId)
        {
            ProximityFrame f;
            return _byMember.TryGetValue(entityId, out f) ? f : null;
        }

        /// <summary>
        /// Create a new frame orbiting <paramref name="parentBodyName"/> with the given virtual
        /// orbit and anchor, allocating a fresh berth slot. The anchor is registered as the
        /// first member.
        /// </summary>
        public ProximityFrame CreateFrame(string parentBodyName, KeplerianElements elements, long anchorEntityId)
        {
            long id = _nextFrameId++;
            ProximityFrame frame = new ProximityFrame(id, parentBodyName, elements, anchorEntityId);

            Vector3D center;
            frame.BerthSlotId = _allocator.Allocate(out center);
            frame.BerthCenter = center;

            _byId[id] = frame;
            if (anchorEntityId != 0) _byMember[anchorEntityId] = frame;
            return frame;
        }

        /// <summary>
        /// Create a LATENT frame with NO lattice berth slot (BerthSlotId = -1), its berth fixed at
        /// <paramref name="berthCenter"/> — the world point its (static) anchor was materialized at.
        /// This is the charted-asteroid rendezvous case (docs/charted-asteroids-encounters.md): a
        /// roid's frame must live AT THE ROCK, not at a lattice cell, so the player floats relative
        /// to the rock via the voxel-anchored update path (FrameManager.UpdateFrameVoxelAnchored —
        /// no drain, no hard-pin). Mirrors <see cref="CreateFrame"/> but skips
        /// <c>_allocator.Allocate</c>, so it consumes no lattice slot (and <see cref="Dissolve"/>'s
        /// <c>BerthSlotId &gt;= 0</c> guard correctly frees nothing). The anchor is the first member.
        /// </summary>
        public ProximityFrame CreateLatentFrame(string parentBodyName, KeplerianElements elements,
            long anchorEntityId, Vector3D berthCenter)
        {
            long id = _nextFrameId++;
            ProximityFrame frame = new ProximityFrame(id, parentBodyName, elements, anchorEntityId);

            frame.BerthSlotId = -1;            // no lattice slot — the berth lives where the rock is
            frame.BerthCenter = berthCenter;

            _byId[id] = frame;
            if (anchorEntityId != 0) _byMember[anchorEntityId] = frame;
            return frame;
        }

        /// <summary>
        /// LOAD PATH — recreate one frame with its EXACT persisted identity (no <c>_nextFrameId++</c>,
        /// no velocity re-derivation), the documented restore surface (see
        /// <see cref="SEAerospace.Persistence.FramePersistence.IRestoreTarget"/>). Builds the
        /// <see cref="ProximityFrame"/> directly from the saved state: exact id, elements, the saved
        /// working <paramref name="virtualVelocity"/> accumulator (NOT re-osculated from elements,
        /// so a frame mid-drain keeps its Δv), anchor, and every member id re-indexed in
        /// <c>_byId</c>/<c>_byMember</c>.
        ///
        /// BERTH SLOT (H3 duplicate-slot contract): if <paramref name="berthSlotId"/> &gt;= 0 it is
        /// reserved on the allocator and used as-is. If it is -1 (the frame legitimately had no slot,
        /// a duplicate re-pointed to -1, OR the game-side restore target re-pointed a planet-slot
        /// collision to -1), a FRESH slot is allocated so two berths never overlap — and
        /// <paramref name="berthCenter"/> is replaced by the fresh slot's center. Returns the restored
        /// frame (already indexed), or null on a degenerate id.
        /// </summary>
        public ProximityFrame RestoreFrame(long id, string parentBodyName, KeplerianElements elements,
            Vector3D virtualVelocity, Vector3D pendingDrainDv, long anchorEntityId,
            IList<long> memberIds, int berthSlotId, Vector3D berthCenter, bool isEncounter)
        {
            if (id == 0) return null;

            ProximityFrame frame = new ProximityFrame();
            frame.Id = id;
            frame.ParentBodyName = parentBodyName;
            frame.Elements = elements;
            frame.VirtualVelocity = virtualVelocity;   // saved accumulator, NOT re-derived
            frame.PendingDrainDv = pendingDrainDv;     // saved mid-drain delta-v, NOT discarded
            frame.AnchorEntityId = anchorEntityId;
            frame.IsEncounter = isEncounter;           // dissolve-exempt / merge-inert encounter berth

            // Members: re-index every saved member id (anchor included). Enforce the live registry's
            // single-ownership contract — an id already owned by an earlier-restored frame must NOT be
            // re-indexed (a corrupt/hand-edited save with one id under two frames would otherwise let a
            // later Dissolve remove the index entry pointing at the OTHER frame, orphaning that grid).
            if (memberIds != null)
            {
                for (int i = 0; i < memberIds.Count; i++)
                {
                    long mid = memberIds[i];
                    if (mid == 0 || _byMember.ContainsKey(mid)) continue;
                    if (frame.AddMember(mid)) _byMember[mid] = frame;
                }
            }
            // If the anchor wasn't in the member list, index it too (CreateFrame keeps it a member) —
            // same single-ownership guard.
            if (anchorEntityId != 0 && !frame.HasMember(anchorEntityId) && !_byMember.ContainsKey(anchorEntityId))
            {
                frame.Members.Add(anchorEntityId);
                _byMember[anchorEntityId] = frame;
            }

            // Berth slot: reserve the exact slot, or allocate fresh on -1 (no-slot / duplicate, OR a
            // planet-slot collision the game-side restore target re-pointed to -1). NOTE (#16, deferred):
            // a cross-version MIGRATION save whose OccupiedSlotIds desync from the frames' BerthSlotIds
            // can let a planet-slot re-point Allocate a slot a later frame also claims. A bare IsOccupied
            // guard here is NOT the fix — RebuildAllocator pre-seeds every saved slot, so every legit
            // slot already reads occupied; the allocator would need per-frame slot OWNERSHIP (or the
            // Restore `claimed` set to absorb the re-point's fresh allocation) to tell "mine" from
            // "another frame's". Left as-is to avoid regressing the common pre-seeded restore.
            if (berthSlotId >= 0 && _allocator.Reserve(berthSlotId))
            {
                frame.BerthSlotId = berthSlotId;
                // NOTE (round-6, deferred): the SAVED absolute center is authoritative BY DESIGN — a
                // static-anchor conjunction deliberately persists BerthCenter = the static grid's COM,
                // intentionally OFF the lattice slot (FrameManager: "the berth lives where the static grid
                // is"). RelocateIntoBerth is event-driven, not re-run on plain load, so this saved value is
                // the only post-reload source. It CAN go stale only if the shared lattice origin/spacing
                // changes between save and load (a CurrentBerth/largest-body/SlotRadius retune) — then a
                // DYNAMIC berth's saved center no longer equals SlotCenter(slot). Do NOT "fix" this by
                // blindly overwriting with _allocator.SlotCenter(berthSlotId): that would yank every
                // static-anchored berth back to its lattice cell, breaking join/merge placement, the
                // celestial reference, and ghost-relay spawn. A real fix needs a persisted lattice origin
                // + a static-anchor flag to tell the two cases apart; narrow/config-driven, left as-is.
                frame.BerthCenter = berthCenter;
            }
            else
            {
                // No slot, OR a crafted/corrupt slot id the allocator refused (beyond its sane ceiling):
                // fall through to a fresh, bounded slot instead of trusting the saved value.
                Vector3D fresh;
                frame.BerthSlotId = _allocator.Allocate(out fresh);
                frame.BerthCenter = fresh;
            }

            _byId[id] = frame;
            // The counter must never hand out a restored id again.
            if (id >= _nextFrameId) _nextFrameId = id + 1;
            return frame;
        }

        /// <summary>Add a member grid to a frame and index it. Returns false if already a member
        /// (of this or another frame — caller should remove it from its old frame first).</summary>
        public bool AddMember(ProximityFrame frame, long entityId)
        {
            if (frame == null || entityId == 0) return false;
            if (_byMember.ContainsKey(entityId)) return false;
            if (!frame.AddMember(entityId)) return false;
            _byMember[entityId] = frame;
            return true;
        }

        /// <summary>Remove a member grid from its frame and drop the index entry.</summary>
        public bool RemoveMember(long entityId)
        {
            ProximityFrame frame;
            if (!_byMember.TryGetValue(entityId, out frame)) return false;
            frame.RemoveMember(entityId);
            _byMember.Remove(entityId);
            return true;
        }

        /// <summary>
        /// Dissolve a frame: drop all its member indices and free its berth slot. Used on
        /// merge (the incomer dissolves into the host) and when a frame empties.
        /// </summary>
        public void Dissolve(long frameId)
        {
            ProximityFrame frame;
            if (!_byId.TryGetValue(frameId, out frame)) return;
            for (int i = 0; i < frame.Members.Count; i++)
                _byMember.Remove(frame.Members[i]);
            if (frame.BerthSlotId >= 0) _allocator.Free(frame.BerthSlotId);
            _byId.Remove(frameId);
        }

        /// <summary>
        /// MERGE bookkeeping: fold <paramref name="incomer"/> into <paramref name="host"/> —
        /// transfer every incomer member to the host's Conjunction (re-indexing each), then
        /// retire the incomer (free its berth slot, drop it from the registry) WITHOUT
        /// touching the now-transferred members' index entries. The caller is responsible for
        /// the physical reposition of the incomer grids into the host berth at their relative
        /// state; this is the pure registry side. Returns false on a degenerate request.
        /// </summary>
        public bool MergeInto(ProximityFrame host, ProximityFrame incomer)
        {
            if (host == null || incomer == null || host == incomer) return false;
            if (!_byId.ContainsKey(host.Id) || !_byId.ContainsKey(incomer.Id)) return false;

            for (int i = 0; i < incomer.Members.Count; i++)
            {
                long id = incomer.Members[i];
                if (host.AddMember(id)) _byMember[id] = host;   // reindex transferred members
            }
            incomer.Members.Clear();
            if (incomer.BerthSlotId >= 0) _allocator.Free(incomer.BerthSlotId);
            _byId.Remove(incomer.Id);
            return true;
        }

        /// <summary>
        /// SPLIT: fork <paramref name="memberId"/> out of <paramref name="frame"/> into its OWN
        /// new frame with the supplied virtual orbit (derived by the caller from the member's
        /// berth-relative state). Allocates a fresh berth slot; the member becomes the new
        /// frame's anchor. The anchor itself cannot be split off (it IS the frame). Returns the
        /// new frame, or null on a degenerate request.
        /// </summary>
        public ProximityFrame SplitOff(ProximityFrame frame, long memberId, string parentBodyName,
            KeplerianElements newElements)
        {
            if (frame == null || memberId == 0) return null;
            if (!frame.HasMember(memberId)) return null;
            if (memberId == frame.AnchorEntityId) return null;   // never split the anchor

            RemoveMember(memberId);                              // drop from old frame + index
            return CreateFrame(parentBodyName, newElements, memberId);
        }

        /// <summary>
        /// The "only when isolated" query: the nearest OTHER frame sharing the same parent
        /// body whose celestial position at time t is within <paramref name="withinMeters"/>
        /// of <paramref name="celestialPos"/> (a position in that parent's inertial frame).
        /// Returns null if the grid/frame is isolated (→ it should spawn its own frame).
        /// The full merge decision (rel-velocity gate) is the FrameManager's per-tick job;
        /// this is only the spawn-vs-join screen.
        /// </summary>
        public ProximityFrame FindNearestFrame(string parentBodyName, Vector3D celestialPos,
            double t, double withinMeters, long excludeFrameId)
        {
            ProximityFrame best = null;
            double bestSq = withinMeters * withinMeters;
            foreach (ProximityFrame f in _byId.Values)
            {
                if (f.Id == excludeFrameId) continue;
                if (f.ParentBodyName != parentBodyName) continue;
                double dSq = (f.PositionAt(t) - celestialPos).LengthSquared();
                if (dSq < bestSq) { bestSq = dSq; best = f; }
            }
            return best;
        }
    }
}
