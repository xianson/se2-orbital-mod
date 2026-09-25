using System.Collections.Generic;

namespace SEAerospace.Persistence
{
    /// <summary>
    /// The serializable STATE of one live <see cref="SEAerospace.Frames.ProximityFrame"/>,
    /// flattened to plain XML-friendly primitives so a frame survives a world save/reload.
    ///
    /// VRageMath / SE types are deliberately NOT used here: the orbit is six element doubles
    /// (mirroring <see cref="SEAerospace.Orbital.KeplerianElements"/>) and the working
    /// velocity is three doubles, so the document is whitelist-clean and offline round-trips
    /// with a plain XmlSerializer. The mapper (<see cref="FramePersistence"/>) is the only
    /// thing that bridges this POCO and the live frame, keeping this type game-free.
    ///
    /// Members are entity ids (long), exactly as the frame stores them; nothing here resolves
    /// a live game handle.
    /// </summary>
    public class FrameSnapshot
    {
        /// <summary>Stable per-session frame id (registry-assigned) — must be restored EXACTLY
        /// so persisted <see cref="SEAerospace.Frames.CelestialAddress"/> LiveFrame ids keep
        /// pointing at the same frame after reload.</summary>
        public long Id = 0;

        /// <summary>Name of the GravityBody this frame orbits (its SOI primary).</summary>
        public string ParentBodyName = "";

        // ---- the virtual orbit, as KeplerianElements' eight serializable doubles ----

        public double SemiMajorAxis = 0.0;   // a, meters
        public double Eccentricity = 0.0;    // e, dimensionless
        public double Inclination = 0.0;     // i, radians
        public double Raan = 0.0;            // Omega, radians
        public double ArgPeriapsis = 0.0;    // omega, radians
        public double TrueAnomaly = 0.0;     // nu, radians
        public double Mu = 0.0;              // gravitational parameter, m^3/s^2
        public double Epoch = 0.0;           // seconds; time at which TrueAnomaly holds

        // ---- working velocity accumulator (parent frame), as three doubles ----

        public double VirtualVelocityX = 0.0;
        public double VirtualVelocityY = 0.0;
        public double VirtualVelocityZ = 0.0;

        // ---- pending (un-folded) drained anchor delta-v, as three doubles ----
        //
        // The FrameManager's fold==feed drain accumulator (ProximityFrame.PendingDrainDv): a save
        // taken while a hard anchor event is still DRAINING OUT through the MaxApparentAccel
        // clamp (~1.67 m/s per tick) can hold tens of m/s of real delta-v here — losing it on
        // reload would silently rob the burn/ram of part of its effect. Schema-additive
        // (Version 1 stays): an older save simply deserializes these as 0 (Vector3D.Zero), which
        // was the old behavior.

        public double PendingDrainDvX = 0.0;
        public double PendingDrainDvY = 0.0;
        public double PendingDrainDvZ = 0.0;

        /// <summary>The frame origin entity (0 = none yet).</summary>
        public long AnchorEntityId = 0;

        /// <summary>The Conjunction: every member grid's entity id (including the anchor).</summary>
        public List<long> MemberIds = new List<long>();

        /// <summary>Allocated berth slot (-1 = none). Stable id -> lattice cell, so the world
        /// transform recomputes from the allocator on load.</summary>
        public int BerthSlotId = -1;

        // ---- berth world-space center, as three doubles ----

        public double BerthCenterX = 0.0;
        public double BerthCenterY = 0.0;
        public double BerthCenterZ = 0.0;

        /// <summary>Encounter conjunction (booted out of a player berth) — dissolve-exempt and
        /// merge-inert. Schema-additive: an older save deserializes this as false (the old behavior).</summary>
        public bool IsEncounter = false;

        public FrameSnapshot() { }
    }
}
