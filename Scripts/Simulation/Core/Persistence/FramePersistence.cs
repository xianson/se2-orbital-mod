using System.Collections.Generic;
using SEAerospace.Frames;
using SEAerospace.Orbital;

namespace SEAerospace.Persistence
{
    /// <summary>
    /// The mapper between the live <see cref="FrameRegistry"/> + <see cref="BerthAllocator"/>
    /// and the serializable <see cref="FrameRegistrySnapshot"/>. CAPTURE reads as far as the
    /// existing public API allows; RESTORE rebuilds as far as the public API allows and targets
    /// a small documented FrameRegistry addition for the parts the public API cannot reach
    /// exactly. Game-free (VRageMath + the SEAerospace.Frames/Orbital model only) so the whole
    /// capture/restore path is offline-testable; the game-side host only installs the XML hooks
    /// and does world-storage file I/O.
    /// </summary>
    public static class FramePersistence
    {
        // =====================================================================================
        // CAPTURE — live registry -> snapshot (reads public state only)
        // =====================================================================================

        /// <summary>
        /// Snapshot a live registry. Reads each frame's public state, the allocator's public
        /// config + occupancy (reconstructed from the frames' slot ids, since the live occupied
        /// set is the union of the frames' slots), and the EXACT next-frame-id.
        ///
        /// NEXT-FRAME-ID: delegates to the explicit overload with <c>registry.NextFrameId</c>, the
        /// live <c>_nextFrameId</c> counter, so the persisted value is exact even when frames were
        /// created and then ALL dissolved (counter ahead of max id). The max(frame id)+1 heuristic
        /// survives ONLY as a fallback for a null registry guard below.
        /// </summary>
        public static FrameRegistrySnapshot Capture(FrameRegistry registry)
        {
            if (registry == null) return Capture(registry, 1L);
            return Capture(registry, registry.NextFrameId);
        }

        /// <summary>
        /// Snapshot a live registry with an EXPLICIT next-frame-id (use once
        /// <c>FrameRegistry.NextFrameId</c> exists, to capture the exact counter).
        /// </summary>
        public static FrameRegistrySnapshot Capture(FrameRegistry registry, long nextFrameId)
        {
            FrameRegistrySnapshot snap = new FrameRegistrySnapshot();
            // Stamp the CURRENT schema version on every new save. The field default is 1 (so an
            // element-less LEGACY save deserializes as pre-law and migrates), so new saves MUST set
            // it explicitly or they would all read back as legacy and clamp the player's /warp choice
            // (round-7 #5). The null-registry fresh snapshot below is also current-schema.
            snap.Version = FrameRegistrySnapshot.CurrentVersion;
            if (registry == null) return snap;

            snap.NextFrameId = nextFrameId;

            BerthAllocator alloc = registry.Allocator;
            if (alloc != null)
            {
                snap.BerthSlotRadius = alloc.SlotRadius;
                // clearance = spacing - 2*radius (the allocator derives spacing from these two).
                double clearance = alloc.Spacing - 2.0 * alloc.SlotRadius;
                snap.BerthClearance = clearance > 0.0 ? clearance : 0.0;
            }

            foreach (ProximityFrame f in registry.Frames)
            {
                FrameSnapshot fs = CaptureFrame(f);
                snap.Frames.Add(fs);
                if (fs.BerthSlotId >= 0 && !snap.OccupiedSlotIds.Contains(fs.BerthSlotId))
                    snap.OccupiedSlotIds.Add(fs.BerthSlotId);
            }

            snap.OccupiedSlotIds.Sort();
            return snap;
        }

        /// <summary>Snapshot a single frame's public state to a <see cref="FrameSnapshot"/>.</summary>
        public static FrameSnapshot CaptureFrame(ProximityFrame f)
        {
            FrameSnapshot fs = new FrameSnapshot();
            if (f == null) return fs;

            fs.Id = f.Id;
            fs.ParentBodyName = f.ParentBodyName;

            KeplerianElements el = f.Elements;
            fs.SemiMajorAxis = el.SemiMajorAxis;
            fs.Eccentricity = el.Eccentricity;
            fs.Inclination = el.Inclination;
            fs.Raan = el.Raan;
            fs.ArgPeriapsis = el.ArgPeriapsis;
            fs.TrueAnomaly = el.TrueAnomaly;
            fs.Mu = el.Mu;
            fs.Epoch = el.Epoch;

            fs.VirtualVelocityX = f.VirtualVelocity.X;
            fs.VirtualVelocityY = f.VirtualVelocity.Y;
            fs.VirtualVelocityZ = f.VirtualVelocity.Z;

            fs.PendingDrainDvX = f.PendingDrainDv.X;
            fs.PendingDrainDvY = f.PendingDrainDv.Y;
            fs.PendingDrainDvZ = f.PendingDrainDv.Z;

            fs.AnchorEntityId = f.AnchorEntityId;

            fs.MemberIds = new List<long>(f.Members.Count);
            for (int i = 0; i < f.Members.Count; i++)
                fs.MemberIds.Add(f.Members[i]);

            fs.BerthSlotId = f.BerthSlotId;
            fs.BerthCenterX = f.BerthCenter.X;
            fs.BerthCenterY = f.BerthCenter.Y;
            fs.BerthCenterZ = f.BerthCenter.Z;
            fs.IsEncounter = f.IsEncounter;
            return fs;
        }

        /// <summary>Rebuild a <see cref="KeplerianElements"/> from a frame snapshot's doubles.</summary>
        public static KeplerianElements ElementsOf(FrameSnapshot fs)
        {
            KeplerianElements el = new KeplerianElements();
            el.SemiMajorAxis = fs.SemiMajorAxis;
            el.Eccentricity = fs.Eccentricity;
            el.Inclination = fs.Inclination;
            el.Raan = fs.Raan;
            el.ArgPeriapsis = fs.ArgPeriapsis;
            el.TrueAnomaly = fs.TrueAnomaly;
            el.Mu = fs.Mu;
            el.Epoch = fs.Epoch;
            return el;
        }

        /// <summary>
        /// True if a restored frame's Keplerian elements are sane enough to admit into a live frame.
        /// A crafted/corrupt save can carry Mu&lt;=0 / SemiMajorAxis&lt;=0 / Eccentricity&gt;=1, which
        /// bakes a NaN/Inf-producing degenerate orbit into the frame (mean motion sqrt(mu/a^3) divides
        /// by zero; ToElements with Mu=0 → NaN); the per-tick force guard then contains the NaN but the
        /// frame silently dead-ends (no station-keeping, NaN proxy/HUD position) for the whole session.
        /// This mirrors the validators the asteroid/encounter restore paths already have (finiteness
        /// FIRST — NaN passes every range comparison). The hyperbolic case is legitimate (the element
        /// model supports e&gt;1 with a&lt;0), so we don't blanket-require a&gt;0; we reject only the
        /// parabolic knife-edge (e==1, where period/mean-motion is undefined).
        /// </summary>
        public static bool IsValidElements(KeplerianElements el, out string reason)
        {
            reason = null;
            if (!IsFinite(el.Mu) || !IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity) ||
                !IsFinite(el.Inclination) || !IsFinite(el.Raan) || !IsFinite(el.ArgPeriapsis) ||
                !IsFinite(el.TrueAnomaly) || !IsFinite(el.Epoch))
            { reason = "non-finite element"; return false; }
            if (el.Mu <= 0.0) { reason = "Mu <= 0"; return false; }
            if (el.Eccentricity < 0.0) { reason = "Eccentricity < 0"; return false; }
            if (el.Eccentricity < 1.0)
            {
                if (el.SemiMajorAxis <= 0.0) { reason = "elliptic with SemiMajorAxis <= 0"; return false; }
            }
            else if (el.Eccentricity > 1.0)
            {
                if (el.SemiMajorAxis >= 0.0) { reason = "hyperbolic with SemiMajorAxis >= 0"; return false; }
            }
            else { reason = "parabolic (Eccentricity == 1)"; return false; }
            return true;
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        // =====================================================================================
        // RESTORE — snapshot -> live registry
        // =====================================================================================
        //
        // The EXISTING public FrameRegistry/BerthAllocator API is INSUFFICIENT to restore a
        // registry EXACTLY, for three reasons:
        //
        //   1. FrameRegistry.CreateFrame allocates a FRESH berth slot and AUTO-ASSIGNS the id
        //      (_nextFrameId++). It cannot reproduce a persisted (id, slotId, center) triple.
        //   2. FrameRegistry exposes no way to set _nextFrameId, so post-load created frames
        //      could collide with restored ids.
        //   3. CreateFrame recomputes VirtualVelocity from the elements; a frame whose anchor
        //      drained Δv since its last re-osculation would lose that working accumulator.
        //
        // Per the task, FrameRegistry is NOT edited. Restore therefore targets a SINGLE small
        // method the main agent must add (signature documented below) and, where that method
        // is absent, this file provides a snapshot-level rebuild used by the offline tests.
        //
        // REQUIRED FrameRegistry ADDITIONS (main agent):
        //
        //   public ProximityFrame RestoreFrame(
        //       long id, string parentBodyName, KeplerianElements elements,
        //       Vector3D virtualVelocity, Vector3D pendingDrainDv, long anchorEntityId,
        //       IList<long> memberIds, int berthSlotId, Vector3D berthCenter)
        //   {
        //       // Build the frame with the EXACT id (no _nextFrameId++), set its
        //       // VirtualVelocity from the argument (do NOT re-derive), Reserve the exact
        //       // berth slot on _allocator, set BerthSlotId/BerthCenter, then index every
        //       // member id in _byId/_byMember.
        //   }
        //
        //   public void SetNextFrameId(long next);   // restore the exact _nextFrameId counter
        //
        // The IRestoreTarget interface below is the seam these map onto; the offline tests
        // implement it to prove Restore drives the right calls in the right order.

        /// <summary>
        /// The minimal restore surface FrameRegistry must expose for an EXACT reload. The game
        /// adapter forwards each call to the new FrameRegistry methods above; the offline test
        /// implements it against a stand-in registry to verify Restore's behaviour.
        /// </summary>
        public interface IRestoreTarget
        {
            /// <summary>Reset the next-frame-id counter to the persisted value.</summary>
            void SetNextFrameId(long next);

            /// <summary>Recreate one frame with EXACT id, elements, velocity, anchor, members,
            /// and berth slot/center (reserving the slot on the allocator).
            ///
            /// DUPLICATE-SLOT CONTRACT (H3): <see cref="Restore"/> guarantees it never hands the
            /// SAME <paramref name="berthSlotId"/> to two frames. If a save carried a duplicate,
            /// the colliding frame arrives with <paramref name="berthSlotId"/> = -1 (and a zero
            /// <paramref name="berthCenter"/>); the implementation MUST then allocate a FRESH slot
            /// from its allocator and use that slot's id/center instead, so two berths never
            /// overlap. A genuine -1 (frame legitimately had no slot) is handled the same way.
            ///
            /// <paramref name="pendingDrainDv"/> is the saved fold==feed drain accumulator
            /// (ProximityFrame.PendingDrainDv) — restored verbatim (NOT re-derived) so a save
            /// taken mid-drain (a hard anchor event still bleeding through the apparent-accel
            /// clamp) keeps the un-folded delta-v.</summary>
            void RestoreFrame(long id, string parentBodyName, KeplerianElements elements,
                Vector3D virtualVelocity, Vector3D pendingDrainDv, long anchorEntityId,
                IList<long> memberIds, int berthSlotId, Vector3D berthCenter, bool isEncounter);
        }

        /// <summary>
        /// Restore a snapshot into a live registry via the documented <see cref="IRestoreTarget"/>
        /// seam. Sets the next-id counter first, then recreates every frame exactly. The caller
        /// supplies a freshly constructed registry/allocator (the allocator's radius/clearance
        /// should come from <paramref name="snap"/>'s BerthSlotRadius/BerthClearance) wrapped in
        /// an IRestoreTarget adapter.
        /// </summary>
        public static void Restore(FrameRegistrySnapshot snap, IRestoreTarget target)
        {
            if (snap == null || target == null) return;

            target.SetNextFrameId(snap.NextFrameId);

            // H3: never hand the same berth slot to two frames. A duplicate slot in the save is
            // re-pointed to -1 so the target allocates a fresh, non-overlapping slot (per the
            // IRestoreTarget.RestoreFrame contract).
            HashSet<int> claimed = new HashSet<int>();

            for (int i = 0; i < snap.Frames.Count; i++)
            {
                FrameSnapshot fs = snap.Frames[i];
                KeplerianElements el = ElementsOf(fs);
                // Reject a frame whose persisted orbit is degenerate/non-finite (a hand-edited or
                // corrupt save). Skipping it orphans its member grids in world space — strictly better
                // than a NaN frame silently holding them — and never blacks out the rest of the restore
                // (same one-bad-record discipline EncounterPersistence uses). Never substitute a repaired
                // orbit (that would teleport the berth + members to a fabricated position).
                string elReason;
                if (!IsValidElements(el, out elReason))
                {
                    PersistenceLog.Warn("frame " + fs.Id + " has invalid orbit (" + elReason +
                        "); skipping restore of this frame");
                    continue;
                }
                Vector3D vel = new Vector3D(fs.VirtualVelocityX, fs.VirtualVelocityY, fs.VirtualVelocityZ);
                Vector3D pend = new Vector3D(fs.PendingDrainDvX, fs.PendingDrainDvY, fs.PendingDrainDvZ);

                int slotId = fs.BerthSlotId;
                Vector3D center = new Vector3D(fs.BerthCenterX, fs.BerthCenterY, fs.BerthCenterZ);
                if (slotId >= 0)
                {
                    if (claimed.Contains(slotId))
                    {
                        PersistenceLog.Warn("frame " + fs.Id + " carried duplicate berth slot " + slotId +
                            "; restore target will allocate a fresh slot to avoid overlap");
                        slotId = -1;                 // signal the target to allocate fresh
                        center = Vector3D.Zero;
                    }
                    else
                    {
                        claimed.Add(slotId);
                    }
                }

                target.RestoreFrame(fs.Id, fs.ParentBodyName, el, vel, pend, fs.AnchorEntityId,
                    fs.MemberIds, slotId, center, fs.IsEncounter);
            }
        }

        /// <summary>
        /// Snapshot-level rebuild of the live FRAME objects WITHOUT touching FrameRegistry: builds
        /// each <see cref="ProximityFrame"/> from its snapshot and reserves its berth slot on the
        /// supplied allocator. This restores frame STATE (id, elements, members, slot, center)
        /// faithfully but does NOT re-index them in a FrameRegistry's private dictionaries — that
        /// is exactly the gap the documented <c>RestoreFrame</c> closes. Returned for offline
        /// verification and for any host path that holds frames outside the registry.
        /// </summary>
        public static List<ProximityFrame> RebuildFrames(FrameRegistrySnapshot snap, BerthAllocator allocator)
        {
            List<ProximityFrame> frames = new List<ProximityFrame>();
            if (snap == null) return frames;

            // Track slots already claimed by an earlier restored frame. A save with two frames
            // carrying the SAME BerthSlotId (hand-edited, or a serialization bug) would otherwise
            // overlap two berths in world space. The colliding frame gets a FRESH slot instead.
            HashSet<int> claimed = new HashSet<int>();

            for (int i = 0; i < snap.Frames.Count; i++)
            {
                FrameSnapshot fs = snap.Frames[i];
                ProximityFrame f = new ProximityFrame();
                f.Id = fs.Id;
                f.ParentBodyName = fs.ParentBodyName;
                f.Elements = ElementsOf(fs);
                f.VirtualVelocity = new Vector3D(fs.VirtualVelocityX, fs.VirtualVelocityY, fs.VirtualVelocityZ);
                f.PendingDrainDv = new Vector3D(fs.PendingDrainDvX, fs.PendingDrainDvY, fs.PendingDrainDvZ);
                f.AnchorEntityId = fs.AnchorEntityId;
                f.IsEncounter = fs.IsEncounter;
                for (int m = 0; m < fs.MemberIds.Count; m++)
                    if (!f.HasMember(fs.MemberIds[m])) f.Members.Add(fs.MemberIds[m]);

                int slotId = fs.BerthSlotId;
                Vector3D center = new Vector3D(fs.BerthCenterX, fs.BerthCenterY, fs.BerthCenterZ);

                if (allocator != null && slotId >= 0)
                {
                    if (claimed.Contains(slotId))
                    {
                        // Duplicate slot — give this frame a fresh, non-overlapping slot.
                        int original = slotId;
                        Vector3D freshCenter;
                        slotId = allocator.Allocate(out freshCenter);
                        center = freshCenter;
                        PersistenceLog.Warn("frame " + fs.Id + " carried duplicate berth slot " + original +
                            "; reallocated to fresh slot " + slotId + " to avoid overlap");
                    }
                    else
                    {
                        allocator.Reserve(slotId);
                    }
                    claimed.Add(slotId);
                }

                f.BerthSlotId = slotId;
                f.BerthCenter = center;
                frames.Add(f);
            }
            return frames;
        }

        /// <summary>
        /// Build a fresh <see cref="BerthAllocator"/> from a snapshot's persisted config and
        /// reserve every occupied slot, so it hands out new slots without colliding with restored
        /// frames. The convenience the game-side load path uses to reconstruct the allocator
        /// before <see cref="RebuildFrames"/> / <see cref="Restore"/>.
        /// </summary>
        public static BerthAllocator RebuildAllocator(FrameRegistrySnapshot snap)
        {
            if (snap == null) return new BerthAllocator(1.0, 0.0);
            BerthAllocator alloc = new BerthAllocator(snap.BerthSlotRadius, snap.BerthClearance);
            for (int i = 0; i < snap.OccupiedSlotIds.Count; i++)
                alloc.Reserve(snap.OccupiedSlotIds[i]);
            return alloc;
        }

        // ---- XML convenience (delegates to the snapshot's pluggable hooks) ----

        /// <summary>Capture + serialize in one call (the host save path).</summary>
        public static string ToXml(FrameRegistry registry)
        {
            return Capture(registry).ToXml();
        }

        /// <summary>Parse a snapshot from XML (the host load path); then Restore it. Delegates to
        /// <see cref="FrameRegistrySnapshot.FromXml"/>, inheriting its hardened contract: never
        /// returns null and never throws — a missing/empty/corrupt save yields a fresh default
        /// (empty) snapshot so world load continues with no frames instead of bricking.</summary>
        public static FrameRegistrySnapshot FromXml(string xml)
        {
            return FrameRegistrySnapshot.FromXml(xml);
        }
    }
}
