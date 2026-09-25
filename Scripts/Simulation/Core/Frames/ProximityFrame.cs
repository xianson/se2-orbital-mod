using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.Frames
{
    /// <summary>
    /// One ProximityFrame: the bridge object that lives in BOTH realms at once
    /// (docs/architecture-proximity-frames.md). Celestial-side it is a point on rails in
    /// some body's inertial frame — a virtual orbit (<see cref="Elements"/>) about
    /// <see cref="ParentBodyName"/>. Real-side it owns a <see cref="BerthSlotId"/> /
    /// <see cref="BerthCenter"/> sphere of world space near the origin, inside which its
    /// member grids — the CONJUNCTION (rob, 06-10) — float at real vanilla-capped physics.
    ///
    /// Orbital velocity lives in the frame's elements, never in a Havok body. The anchor's
    /// per-tick Δv is drained into <see cref="VirtualVelocity"/> (the orbit) and zeroed; the
    /// other members get the apparent + Clohessy-Wiltshire forces. This type is the pure
    /// STATE of a frame — the per-tick dynamics live in the game-side FrameManager. Members
    /// are entity ids (long), not game handles, so the model stays game-free and testable.
    /// </summary>
    public sealed class ProximityFrame
    {
        /// <summary>Stable per-session frame id (registry-assigned, persisted).</summary>
        public long Id;

        /// <summary>Name of the <see cref="GravityBody"/> this frame orbits (its SOI primary).</summary>
        public string ParentBodyName;

        /// <summary>The frame's virtual orbit about the parent body (mu = parent's mu).
        /// Advanced analytically each tick; this IS the orbital motion.</summary>
        public KeplerianElements Elements;

        /// <summary>Working velocity accumulator in the parent frame — the anchor's drained
        /// Δv folds in here, then re-osculates <see cref="Elements"/>. The orbit's true
        /// velocity at the current epoch.</summary>
        public Vector3D VirtualVelocity;

        /// <summary>The frame origin entity (asteroid &gt; largest grid). 0 = none yet.
        /// Its world velocity is the per-tick force sensor; zeroed each tick (no teleport).</summary>
        public long AnchorEntityId;

        /// <summary>The Conjunction: every member grid's entity id (including the anchor).
        /// Real vanilla-physics bodies floating in the berth, sharing this one orbit.</summary>
        public readonly List<long> Members = new List<long>();

        /// <summary>Allocated berth slot (-1 = none) and its world-space center.</summary>
        public int BerthSlotId = -1;
        public Vector3D BerthCenter;

        /// <summary>True if this is an ENCOUNTER conjunction booted out of a player's berth into
        /// its own slot (rob 2026-06-16) — a non-player grid that spawned inside a player berth and
        /// was relocated here so it doesn't clutter the player's. Marks the frame EXEMPT from the
        /// all-NPC swarm-cleanup sweep (which would otherwise dissolve it as a non-player frame) and
        /// merge-INERT (ScreenMerges skips it, so a booted encounter isn't merged back into the player
        /// it came from). Persisted via FrameSnapshot, so it survives a save/reload.</summary>
        public bool IsEncounter;

        /// <summary>Set each tick by FrameManager: true when the frame is anchored by a live VOXEL
        /// (a charted asteroid) rather than a grid. The voxel-anchored update path does NOT yet
        /// warp-step its members, so the clock must hold those members at x1 (else their relative orbit
        /// about the rock advances at ~1/warp the correct rate). Runtime-only; not persisted.</summary>
        public bool VoxelAnchored;

        /// <summary>Universe-time (seconds) at which an encounter berth self-dissolves AND deletes its
        /// grids/voxels (rob 2026-06-16: "dissolve and delete themselves after a timer"). 0 = unset;
        /// FrameManager seeds it on first sight (boot, or re-seeds after a reload — NOT persisted, so a
        /// reload restarts the countdown). Only meaningful when <see cref="IsEncounter"/>.</summary>
        public double EncounterExpiry;

        /// <summary>Drained anchor delta-v awaiting fold into the orbit (the FrameManager's
        /// fold==feed accumulator, 2026-06-11): per-tick drains below the micro-impact fold
        /// threshold are ACCUMULATED here instead of discarded (so a sustained weak burn —
        /// e.g. ions under ~1.8 m/s^2 — still couples into the rails), and a hard anchor
        /// event's excess above the per-tick apparent-accel clamp drains out through here over
        /// the following ticks. PERSISTED (2026-06-12): a save mid-drain can hold tens of m/s
        /// of real delta-v here (the clamp processes only ~1.67 m/s per tick), so the snapshot
        /// carries it verbatim and restore puts it back — losing it would silently rob a
        /// burn/ram of part of its effect across a reload.</summary>
        public Vector3D PendingDrainDv;

        public ProximityFrame() { }

        public ProximityFrame(long id, string parentBodyName, KeplerianElements elements, long anchorEntityId)
        {
            Id = id;
            ParentBodyName = parentBodyName;
            Elements = elements;
            AnchorEntityId = anchorEntityId;
            VirtualVelocity = OrbitalMath.ToState(elements).Velocity;
            if (anchorEntityId != 0) Members.Add(anchorEntityId);
        }

        /// <summary>Mean motion n = 2*pi/T of the virtual orbit (the LVLH rotation rate).</summary>
        public double MeanMotion { get { return Elements.MeanMotion; } }

        /// <summary>Member count (the size of the Conjunction).</summary>
        public int MemberCount { get { return Members.Count; } }

        public bool HasMember(long entityId)
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i] == entityId) return true;
            return false;
        }

        public bool AddMember(long entityId)
        {
            if (entityId == 0 || HasMember(entityId)) return false;
            Members.Add(entityId);
            return true;
        }

        public bool RemoveMember(long entityId)
        {
            return Members.Remove(entityId);
        }

        /// <summary>The frame's celestial position+velocity in the PARENT body's inertial
        /// frame at absolute time t (analytic Kepler propagation of the virtual orbit).</summary>
        public StateVector StateAt(double t)
        {
            return OrbitPropagation.StateAt(Elements, t);
        }

        /// <summary>The frame's celestial position in the parent frame at time t.</summary>
        public Vector3D PositionAt(double t)
        {
            return OrbitPropagation.StateAt(Elements, t).Position;
        }
    }
}
