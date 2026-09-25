using System;
using System.Collections.Generic;
using SEAerospace.Frames;

namespace SEAerospace.Persistence
{
    /// <summary>
    /// The serializable STATE of the whole live <see cref="SEAerospace.Frames.FrameRegistry"/>
    /// for a session: every frame, the registry's next-id counter, the berth allocator config +
    /// the set of occupied slots, an optional <see cref="CelestialAddress"/> side-table, and a
    /// documented clock-state seam. ONE of these is the unit a world save persists; reloading
    /// it (via <see cref="FramePersistence"/>) rebuilds the registry identically.
    ///
    /// SERIALIZATION SEAM (why the hooks, mirroring SystemDefinition): in-game the SE mod
    /// whitelist PROHIBITS <c>System.Xml.Serialization.XmlSerializer</c> and
    /// <c>StringReader/StringWriter</c>; only <c>MyAPIGateway.Utilities.SerializeToXML/FromXML</c>
    /// is allowed, and that is game-only. To keep this model class whitelist-clean AND offline
    /// unit-testable, (de)serialization is delegated to two pluggable hooks:
    ///   - In-game: a game-side FramePersistenceStorage.Install wires them to MyAPIGateway.
    ///   - Offline: the test installs a plain XmlSerializer backend (legal outside the game).
    /// The XML shape is identical either way (same [XmlRoot]/[XmlArray] attributes).
    /// </summary>
    public class FrameRegistrySnapshot
    {
        /// <summary>The current schema version stamped onto every NEW snapshot (Capture + the
        /// fresh-default FromXml paths). Bump when the schema changes in a way that needs migration.</summary>
        public const int CurrentVersion = 2;

        /// <summary>Schema version, for forward migration of saved worlds.
        /// Version 2 (2026-06-12, the no-auto-warp law): ClockTimescale is honored as a
        /// player-chosen value only from Version >= 2 snapshots. Version 1 saves predate the
        /// law — their persisted x100 came from the old auto-seed, NOT a /warp command, so
        /// restoring it would silently perpetuate auto-warp forever (found by the DS smoke).
        /// Consumers: FrameManager.LoadData clamps a Version &lt; 2 timescale to 1.
        /// THE DEFAULT IS 1, NOT CurrentVersion: a genuine pre-law save predates this field, so its
        /// XML has NO &lt;Version&gt; element and the XmlSerializer leaves the ctor default. That default
        /// MUST be the legacy version (1) so such a save migrates correctly; if it were 2 the migration
        /// would be a dead no-op (round-7 #5). New snapshots are stamped CurrentVersion explicitly by
        /// Capture / the FromXml fresh-default paths, so only element-less LEGACY saves read as 1.</summary>
        public int Version = 1;

        /// <summary>The registry's next frame id to hand out (the private _nextFrameId). Persisted
        /// so freshly created frames after reload never collide with restored ids.</summary>
        public long NextFrameId = 1;

        // ---- berth allocator config (ctor inputs) + live occupancy ----

        /// <summary>Berth sphere radius (m) — the allocator's configured slot size.</summary>
        public double BerthSlotRadius = 0.0;

        /// <summary>Extra gap between slot surfaces (m). Derived back out of the allocator's
        /// spacing (clearance = spacing - 2*radius) so the rebuilt allocator's lattice matches.</summary>
        public double BerthClearance = 0.0;

        /// <summary>Every currently-allocated slot id. On load these are Reserve()'d so the
        /// allocator hands out fresh slots without colliding with restored frames.</summary>
        public List<int> OccupiedSlotIds = new List<int>();

        // ---- the frames ----

        public List<FrameSnapshot> Frames = new List<FrameSnapshot>();

        // ---- celestial address side-table (GPS / config addresses that ride along) ----

        public List<CelestialAddress> Addresses = new List<CelestialAddress>();

        // ---- CLOCK-STATE SEAM ------------------------------------------------------------
        // Another agent builds SEAerospace.Time.UniverseTimeState. To avoid a cross-agent type
        // dependency, the universe clock is persisted here as plain doubles. The main agent
        // wires these two fields to/from UniverseTimeState in the game-side storage step
        // (UniverseTimeState <-> { ClockEpochSeconds, ClockTimescale }). Until then they default
        // to a unit clock and round-trip harmlessly.

        /// <summary>Universe clock epoch in seconds (absolute sim time of the save).</summary>
        public double ClockEpochSeconds = 0.0;

        /// <summary>Universe clock timescale (sim-seconds per real-second multiplier; 1 = realtime).</summary>
        public double ClockTimescale = 1.0;

        /// <summary>The timescale to RESTORE, after the no-auto-warp migration: Version >= 2
        /// snapshots carry a genuinely player-chosen value (/warp); Version 1 snapshots predate
        /// the law and their value came from the old auto-seed — restoring it would silently
        /// perpetuate auto-warp (DS smoke finding, 2026-06-12), so they clamp to 1. The epoch
        /// is always honored regardless (rails must not jump).</summary>
        public double ChosenTimescale()
        {
            return Version >= 2 ? ClockTimescale : 1.0;
        }

        public FrameRegistrySnapshot() { }

        // ---- pluggable XML backend (whitelist-clean here; installed by host) ----

        /// <summary>Serialize a snapshot to an XML string. Installed by the host
        /// (game: MyAPIGateway; tests: plain XmlSerializer).</summary>
        public static Func<FrameRegistrySnapshot, string> XmlSerializeHook;

        /// <summary>Parse a snapshot from an XML string. Installed by the host.</summary>
        public static Func<string, FrameRegistrySnapshot> XmlDeserializeHook;

        /// <summary>Serialize to XML using the installed backend.</summary>
        public string ToXml()
        {
            if (XmlSerializeHook == null)
                throw new InvalidOperationException(
                    "FrameRegistrySnapshot XML backend not installed (call FramePersistenceStorage.Install in-game, or install a test backend offline).");
            return XmlSerializeHook(this);
        }

        /// <summary>
        /// Parse from XML using the installed backend, HARDENED for the world-load path.
        ///
        /// CONTRACT: never returns null and never throws. A null/empty/whitespace input (no save
        /// yet, or an empty file) returns a fresh DEFAULT snapshot (CurrentVersion, NextFrameId 1, no
        /// frames) — a no-save world is the CURRENT schema, not a legacy save, so it is stamped
        /// CurrentVersion (the field default 1 is reserved for element-less LEGACY XML, see Version).
        /// A malformed/truncated/old-schema input that the deserializer rejects is caught,
        /// logged, and likewise yields a fresh default snapshot — so a corrupt save loads as "no
        /// frames" rather than bricking the session. Callers (Restore/RebuildFrames) dereference
        /// the result's Frames/NextFrameId/Occupied lists, so a non-null snapshot is the safe
        /// contract; those lists are always initialized on a fresh instance.
        /// </summary>
        public static FrameRegistrySnapshot FromXml(string xml)
        {
            if (XmlDeserializeHook == null)
                throw new InvalidOperationException(
                    "FrameRegistrySnapshot XML backend not installed (call FramePersistenceStorage.Install in-game, or install a test backend offline).");

            if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0)
                return FreshDefault();

            try
            {
                FrameRegistrySnapshot snap = XmlDeserializeHook(xml);
                return snap != null ? snap : FreshDefault();
            }
            catch (Exception ex)
            {
                PersistenceLog.Warn("frame snapshot load failed: " + ex);
                return FreshDefault();
            }
        }

        /// <summary>A fresh, current-schema snapshot for the no-save / corrupt-save paths. Stamped
        /// CurrentVersion (NOT the field default 1) so a brand-new world is never mistaken for an
        /// element-less legacy save by the no-auto-warp migration (round-7 #5).</summary>
        private static FrameRegistrySnapshot FreshDefault()
        {
            return new FrameRegistrySnapshot { Version = CurrentVersion };
        }
    }
}
