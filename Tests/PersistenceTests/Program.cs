using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Persistence;
using SEAerospace.SystemDef;

namespace PersistenceTest
{
    /// <summary>
    /// Offline assertion harness for the save/load persistence layer. No game, no test
    /// framework. Exit 0 = all pass. Mirrors tools\SystemDefTest.
    ///
    /// Proves:
    ///  - Capture(FrameRegistry) reads the live frames + allocator config + occupied slots,
    ///  - the snapshot fields match the live frame state field-for-field,
    ///  - snapshot -> XML -> snapshot round-trips identically (frames, members, slots,
    ///    element doubles, nextFrameId, allocator config, clock seam),
    ///  - the CelestialAddress side-table round-trips,
    ///  - Restore drives the documented IRestoreTarget seam with exact values, and the
    ///    snapshot-level RebuildFrames/RebuildAllocator reconstruct the live state.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static readonly XmlSerializer Xs = new XmlSerializer(typeof(FrameRegistrySnapshot));

        private static int Main()
        {
            Console.WriteLine("Persistence offline tests");
            Console.WriteLine("=========================");

            // Install a game-free XML backend (the game wires MyAPIGateway instead). Legal
            // outside SE; mirrors the same [XmlRoot]/[XmlArray] shape.
            FrameRegistrySnapshot.XmlSerializeHook = SerializeOffline;
            FrameRegistrySnapshot.XmlDeserializeHook = DeserializeOffline;

            try
            {
                TestCaptureMatchesLive();
                TestXmlRoundTrip();
                TestAddressSideTable();
                TestClockSeam();
                TestRestoreSeam();
                TestRebuildFromSnapshot();
                TestNextFrameIdExactOverload();
                TestNextFrameIdExactAfterDissolve();
                TestFromXmlHardening();
                TestDuplicateBerthSlotOnRestore();
                TestRestoreIntoLiveRegistry();
                TestPendingDrainDvRoundTrip();
                TestMidPatchEpochFidelity();
                TestAbsentSaveRestoresEmpty();
                TestRestoreRejectsDegenerateOrbit();
                TestCreateLatentFrameNoSlot();
            }
            catch (Exception ex)
            {
                Console.WriteLine("UNHANDLED: " + ex);
                _failed++;
            }

            Console.WriteLine();
            Console.WriteLine("passed=" + _passed + " failed=" + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // ---- offline XML backend ----------------------------------------------

        private static string SerializeOffline(FrameRegistrySnapshot snap)
        {
            using (var sw = new StringWriter())
            {
                Xs.Serialize(sw, snap);
                return sw.ToString();
            }
        }

        private static FrameRegistrySnapshot DeserializeOffline(string xml)
        {
            using (var sr = new StringReader(xml))
            {
                return (FrameRegistrySnapshot)Xs.Deserialize(sr);
            }
        }

        // ---- fixtures ---------------------------------------------------------

        // A registry with three frames: two around Earth (one with extra members), one around
        // Luna. Distinct elements + anchors so field mismatches are caught.
        private static FrameRegistry BuildLiveRegistry()
        {
            BerthAllocator alloc = new BerthAllocator(2500.0, 18000.0);
            FrameRegistry reg = new FrameRegistry(alloc);

            ProximityFrame a = reg.CreateFrame("Earth", Elem(7.0e6, 0.01, 0.5, 1.0, 2.0, 3.0, 3.986e14, 100.0), 1001L);
            reg.AddMember(a, 1002L);
            reg.AddMember(a, 1003L);
            // mutate the working velocity so it differs from the freshly-derived one (proves we
            // persist VirtualVelocity verbatim, not re-derive it).
            a.VirtualVelocity = new Vector3D(123.5, -44.25, 9.75);
            // mid-drain accumulator (a hard anchor event still bleeding through the apparent-accel
            // clamp at save time) — must round-trip verbatim, never be discarded or re-derived.
            a.PendingDrainDv = new Vector3D(18.25, -3.5, 0.0625);

            ProximityFrame b = reg.CreateFrame("Earth", Elem(8.2e6, 0.2, 0.9, 0.3, 1.1, 0.0, 3.986e14, 100.0), 2001L);

            ProximityFrame c = reg.CreateFrame("Luna", Elem(3.0e6, 0.05, 0.1, 0.0, 0.7, 2.4, 4.9e12, 100.0), 3001L);
            reg.AddMember(c, 3002L);

            return reg;
        }

        private static KeplerianElements Elem(double a, double e, double i, double raan,
            double argp, double nu, double mu, double epoch)
        {
            KeplerianElements el = new KeplerianElements();
            el.SemiMajorAxis = a; el.Eccentricity = e; el.Inclination = i; el.Raan = raan;
            el.ArgPeriapsis = argp; el.TrueAnomaly = nu; el.Mu = mu; el.Epoch = epoch;
            return el;
        }

        // ---- tests ------------------------------------------------------------

        private static void TestCaptureMatchesLive()
        {
            Console.WriteLine("[capture matches live state]");
            FrameRegistry reg = BuildLiveRegistry();
            FrameRegistrySnapshot snap = FramePersistence.Capture(reg);

            Ok(snap.Frames.Count == reg.Count, "frame count matches (" + reg.Count + ")");
            Ok(snap.Frames.Count == 3, "three frames captured");

            // allocator config round-tripped from the live allocator.
            Ok(snap.BerthSlotRadius == 2500.0, "allocator radius captured");
            Ok(Math.Abs(snap.BerthClearance - 18000.0) < 1e-6, "allocator clearance derived from spacing");

            // nextFrameId best-effort = max id + 1 (3 frames created -> ids 1,2,3 -> next 4).
            Ok(snap.NextFrameId == 4L, "nextFrameId best-effort = maxId+1 (=4)");

            // occupied slots == the frames' slots, deduped + sorted.
            HashSet<int> liveSlots = new HashSet<int>();
            foreach (ProximityFrame f in reg.Frames) liveSlots.Add(f.BerthSlotId);
            Ok(snap.OccupiedSlotIds.Count == liveSlots.Count, "occupied-slot count matches live");
            bool allOccupied = true;
            for (int i = 0; i < snap.OccupiedSlotIds.Count; i++)
                if (!reg.Allocator.IsOccupied(snap.OccupiedSlotIds[i])) allOccupied = false;
            Ok(allOccupied, "every captured slot is live-occupied");

            // field-for-field check of one fully-populated frame (the Earth frame, id 1).
            ProximityFrame live = reg.Get(1L);
            FrameSnapshot fs = FindFrame(snap, 1L);
            Ok(fs != null, "frame id 1 present in snapshot");
            Ok(fs.ParentBodyName == live.ParentBodyName, "parent name captured");
            Ok(fs.SemiMajorAxis == live.Elements.SemiMajorAxis, "a captured");
            Ok(fs.Eccentricity == live.Elements.Eccentricity, "e captured");
            Ok(fs.Inclination == live.Elements.Inclination, "i captured");
            Ok(fs.Raan == live.Elements.Raan, "raan captured");
            Ok(fs.ArgPeriapsis == live.Elements.ArgPeriapsis, "argp captured");
            Ok(fs.TrueAnomaly == live.Elements.TrueAnomaly, "nu captured");
            Ok(fs.Mu == live.Elements.Mu, "mu captured");
            Ok(fs.Epoch == live.Elements.Epoch, "epoch captured");
            Ok(fs.VirtualVelocityX == live.VirtualVelocity.X &&
               fs.VirtualVelocityY == live.VirtualVelocity.Y &&
               fs.VirtualVelocityZ == live.VirtualVelocity.Z, "virtual velocity captured verbatim");
            Ok(fs.PendingDrainDvX == live.PendingDrainDv.X &&
               fs.PendingDrainDvY == live.PendingDrainDv.Y &&
               fs.PendingDrainDvZ == live.PendingDrainDv.Z, "pending drain dv captured verbatim");
            Ok(fs.AnchorEntityId == live.AnchorEntityId, "anchor captured");
            Ok(fs.BerthSlotId == live.BerthSlotId, "berth slot captured");
            Ok(fs.BerthCenterX == live.BerthCenter.X &&
               fs.BerthCenterY == live.BerthCenter.Y &&
               fs.BerthCenterZ == live.BerthCenter.Z, "berth center captured");
            Ok(fs.MemberIds.Count == live.Members.Count && fs.MemberIds.Count == 3,
               "member count captured (anchor + 2)");
            Ok(fs.MemberIds[0] == 1001L && fs.MemberIds[1] == 1002L && fs.MemberIds[2] == 1003L,
               "member ids captured in order");
        }

        private static void TestXmlRoundTrip()
        {
            Console.WriteLine("[snapshot -> XML -> snapshot identical]");
            FrameRegistry reg = BuildLiveRegistry();
            FrameRegistrySnapshot snap = FramePersistence.Capture(reg);

            string xml = snap.ToXml();
            Ok(xml.Contains("<FrameRegistrySnapshot"), "ToXml emits a FrameRegistrySnapshot doc");
            // SE2 port: the [XmlType("Frame")] attribute was stripped (System.Xml is not a script reference), so the
            // offline XmlSerializer names the element after the class. The in-game format is not XML in SE2.
            Ok(xml.Contains("<Frame>") || xml.Contains("<Frame ") || xml.Contains("<FrameSnapshot"), "doc contains Frame elements");

            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(xml);

            Ok(back.NextFrameId == snap.NextFrameId, "nextFrameId round-trips");
            Ok(back.BerthSlotRadius == snap.BerthSlotRadius, "allocator radius round-trips");
            Ok(back.BerthClearance == snap.BerthClearance, "allocator clearance round-trips");
            Ok(back.Version == snap.Version, "version round-trips");
            Ok(SlotsEqual(back.OccupiedSlotIds, snap.OccupiedSlotIds), "occupied slots round-trip");
            Ok(back.Frames.Count == snap.Frames.Count, "frame count round-trips");

            bool allFramesEqual = true;
            for (int i = 0; i < snap.Frames.Count; i++)
            {
                FrameSnapshot o = snap.Frames[i];
                FrameSnapshot r = FindFrame(back, o.Id);
                if (r == null) { allFramesEqual = false; continue; }
                if (!FrameSnapshotsEqual(o, r)) allFramesEqual = false;
            }
            Ok(allFramesEqual, "every frame round-trips identically (ids/elements/members/slots/center/velocity)");
        }

        private static void TestAddressSideTable()
        {
            Console.WriteLine("[celestial address side-table round-trips]");
            FrameRegistrySnapshot snap = new FrameRegistrySnapshot();
            snap.Addresses.Add(CelestialAddress.SurfaceFixed("Earth", 51.5, -0.12, 80.0));
            snap.Addresses.Add(CelestialAddress.BodyInertialPoint("Luna", 1.0, 2.0, 3.0));
            snap.Addresses.Add(CelestialAddress.LiveFrame(42L));
            snap.Addresses.Add(CelestialAddress.DeepSpaceFixed(-5.0, 6.0, -7.0));
            snap.Addresses.Add(CelestialAddress.GridRelative(9001L, 0.5, 0.25, -0.75));

            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(snap.ToXml());
            Ok(back.Addresses.Count == 5, "all five addresses round-trip");

            CelestialAddress s = back.Addresses[0];
            Ok(s.Kind == CelestialAddressKind.SurfaceFixed && s.BodyName == "Earth" &&
               s.LatDeg == 51.5 && s.LonDeg == -0.12 && s.AltMeters == 80.0, "surface-fixed fields round-trip");

            CelestialAddress lf = back.Addresses[2];
            Ok(lf.Kind == CelestialAddressKind.LiveFrame && lf.FrameId == 42L, "live-frame id round-trips");

            CelestialAddress gr = back.Addresses[4];
            Ok(gr.Kind == CelestialAddressKind.GridRelative && gr.EntityId == 9001L &&
               gr.X == 0.5 && gr.Y == 0.25 && gr.Z == -0.75, "grid-relative fields round-trip");
        }

        private static void TestClockSeam()
        {
            Console.WriteLine("[clock-state seam round-trips]");
            FrameRegistrySnapshot snap = new FrameRegistrySnapshot();
            snap.Version = FrameRegistrySnapshot.CurrentVersion;   // a real save is stamped current (Capture does this)
            snap.ClockEpochSeconds = 1234567.875;
            snap.ClockTimescale = 60.0;
            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(snap.ToXml());
            Ok(back.ClockEpochSeconds == 1234567.875, "clock epoch round-trips");
            Ok(back.ClockTimescale == 60.0, "clock timescale round-trips");

            FrameRegistrySnapshot fresh = new FrameRegistrySnapshot();
            Ok(fresh.ClockTimescale == 1.0, "clock timescale defaults to 1 (realtime) when unset");

            // NO-AUTO-WARP MIGRATION (DS smoke finding, 2026-06-12; default-version fix round-7 #5):
            // pre-law saves carry the old auto-seeded x100 — ChosenTimescale clamps them to 1; the
            // player's genuine /warp choice on Version >= 2 saves restores verbatim. The FIELD DEFAULT
            // is the LEGACY version (1) ON PURPOSE: a genuine pre-law save predates the <Version> element,
            // so the XmlSerializer leaves the ctor default for the absent element — that MUST read as
            // legacy, or the migration is a dead no-op. New saves are stamped CurrentVersion by Capture
            // and the FromXml fresh-default paths.
            Ok(FrameRegistrySnapshot.CurrentVersion == 2, "current schema version is 2 (post no-auto-warp law)");
            Ok(fresh.Version == 1, "bare-ctor default is the LEGACY version (so element-less old saves migrate)");
            Ok(back.Version == 2 && back.ChosenTimescale() == 60.0,
                "Version 2 (stamped): persisted timescale IS the player's choice, restores verbatim");
            FrameRegistrySnapshot preLaw = FrameRegistrySnapshot.FromXml(
                "<FrameRegistrySnapshot><Version>1</Version><ClockEpochSeconds>500</ClockEpochSeconds>" +
                "<ClockTimescale>100</ClockTimescale></FrameRegistrySnapshot>");
            Ok(preLaw.Version == 1 && preLaw.ClockTimescale == 100.0,
                "pre-law snapshot deserializes with its original fields intact");
            Ok(preLaw.ChosenTimescale() == 1.0,
                "pre-law x100 is the old auto-seed, NOT a choice: migrated to x1");
            Ok(preLaw.ClockEpochSeconds == 500.0, "pre-law epoch still honored (rails must not jump)");

            // REGRESSION PIN (round-7 #5): a GENUINE pre-law save predates the <Version> element ENTIRELY.
            // The XmlSerializer leaves the field default for an absent element — that default MUST be 1, or
            // the legacy x100 auto-seed is honored as a player choice and auto-warp is perpetuated forever
            // (the bug: the field defaulted to 2, so no input could ever deserialize as legacy).
            FrameRegistrySnapshot noVer = FrameRegistrySnapshot.FromXml(
                "<FrameRegistrySnapshot><ClockEpochSeconds>500</ClockEpochSeconds>" +
                "<ClockTimescale>100</ClockTimescale></FrameRegistrySnapshot>");
            Ok(noVer.Version == 1, "legacy save with NO <Version> element deserializes as Version 1 (the fix)");
            Ok(noVer.ChosenTimescale() == 1.0, "element-less legacy x100 migrated to x1 (was perpetuating auto-warp)");
        }

        private static void TestRestoreSeam()
        {
            Console.WriteLine("[Restore drives the IRestoreTarget seam exactly]");
            FrameRegistry reg = BuildLiveRegistry();
            FrameRegistrySnapshot snap = FrameRegistrySnapshot.FromXml(FramePersistence.Capture(reg).ToXml());

            RecordingTarget target = new RecordingTarget();
            FramePersistence.Restore(snap, target);

            Ok(target.NextFrameIdSet == snap.NextFrameId, "SetNextFrameId called with persisted value first");
            Ok(target.NextIdCallOrder == 0, "SetNextFrameId is the first call");
            Ok(target.Restored.Count == snap.Frames.Count, "RestoreFrame called once per frame");

            // verify the fully-populated frame (id 1) reached the target with exact args.
            RestoredFrame rf = target.FindById(1L);
            FrameSnapshot fs = FindFrame(snap, 1L);
            Ok(rf != null, "frame id 1 restored");
            Ok(rf.ParentBodyName == fs.ParentBodyName, "restored parent name");
            Ok(rf.Elements.SemiMajorAxis == fs.SemiMajorAxis && rf.Elements.Mu == fs.Mu &&
               rf.Elements.TrueAnomaly == fs.TrueAnomaly, "restored elements exact");
            Ok(rf.VirtualVelocity.X == fs.VirtualVelocityX &&
               rf.VirtualVelocity.Y == fs.VirtualVelocityY &&
               rf.VirtualVelocity.Z == fs.VirtualVelocityZ, "restored velocity exact (not re-derived)");
            Ok(rf.PendingDrainDv.X == fs.PendingDrainDvX &&
               rf.PendingDrainDv.Y == fs.PendingDrainDvY &&
               rf.PendingDrainDv.Z == fs.PendingDrainDvZ, "restored pending drain dv exact");
            Ok(rf.AnchorEntityId == fs.AnchorEntityId, "restored anchor exact");
            Ok(rf.BerthSlotId == fs.BerthSlotId, "restored berth slot exact");
            Ok(rf.MemberIds.Count == 3 && rf.MemberIds[0] == 1001L && rf.MemberIds[2] == 1003L,
               "restored members exact");
        }

        private static void TestRebuildFromSnapshot()
        {
            Console.WriteLine("[snapshot-level RebuildFrames/RebuildAllocator]");
            FrameRegistry reg = BuildLiveRegistry();
            FrameRegistrySnapshot snap = FrameRegistrySnapshot.FromXml(FramePersistence.Capture(reg).ToXml());

            BerthAllocator alloc = FramePersistence.RebuildAllocator(snap);
            Ok(alloc.SlotRadius == 2500.0, "rebuilt allocator radius from snapshot");
            Ok(Math.Abs(alloc.Spacing - (2.0 * 2500.0 + 18000.0)) < 1e-6, "rebuilt allocator spacing");
            bool allReserved = true;
            for (int i = 0; i < snap.OccupiedSlotIds.Count; i++)
                if (!alloc.IsOccupied(snap.OccupiedSlotIds[i])) allReserved = false;
            Ok(allReserved, "rebuilt allocator reserved every occupied slot");

            // a freshly allocated slot must not collide with a restored one.
            Vector3D center;
            int fresh = alloc.Allocate(out center);
            Ok(!snap.OccupiedSlotIds.Contains(fresh), "fresh Allocate avoids restored slots");

            List<ProximityFrame> frames = FramePersistence.RebuildFrames(snap, alloc);
            Ok(frames.Count == 3, "rebuilt three frames");

            ProximityFrame f1 = null;
            for (int i = 0; i < frames.Count; i++) if (frames[i].Id == 1L) f1 = frames[i];
            Ok(f1 != null, "rebuilt frame id 1");
            Ok(f1.ParentBodyName == "Earth", "rebuilt parent name");
            Ok(f1.Elements.SemiMajorAxis == 7.0e6, "rebuilt element a");
            Ok(f1.VirtualVelocity.X == 123.5 && f1.VirtualVelocity.Y == -44.25 && f1.VirtualVelocity.Z == 9.75,
               "rebuilt velocity verbatim");
            Ok(f1.PendingDrainDv.X == 18.25 && f1.PendingDrainDv.Y == -3.5 && f1.PendingDrainDv.Z == 0.0625,
               "rebuilt pending drain dv verbatim");
            Ok(f1.Members.Count == 3 && f1.HasMember(1002L), "rebuilt members");
            Ok(f1.BerthSlotId == reg.Get(1L).BerthSlotId, "rebuilt berth slot id matches live");
        }

        private static void TestNextFrameIdExactOverload()
        {
            Console.WriteLine("[Capture(reg, nextFrameId) exact overload]");
            FrameRegistry reg = BuildLiveRegistry();
            // Simulate the future FrameRegistry.NextFrameId getter returning a counter ahead of
            // max id (e.g. frames were created and dissolved). The explicit overload must honor it.
            FrameRegistrySnapshot snap = FramePersistence.Capture(reg, 99L);
            Ok(snap.NextFrameId == 99L, "explicit nextFrameId honored over the max+1 heuristic");
            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(snap.ToXml());
            Ok(back.NextFrameId == 99L, "explicit nextFrameId round-trips");
        }

        // FIX H2: after frames are created then ALL dissolved, the live _nextFrameId counter is
        // ahead of max(frame id) (in fact there are no frames at all). Capture must persist the
        // EXACT counter, not the old max+1 heuristic, so a reloaded world never reuses a dissolved
        // frame's id (which would silently re-point a persisted LiveFrame(5) address).
        private static void TestNextFrameIdExactAfterDissolve()
        {
            Console.WriteLine("[H2: exact next-frame-id survives create-then-dissolve-all]");
            BerthAllocator alloc = new BerthAllocator(2500.0, 18000.0);
            FrameRegistry reg = new FrameRegistry(alloc);

            // Create frames 1..5, then dissolve all of them.
            ProximityFrame[] created = new ProximityFrame[5];
            for (int k = 0; k < 5; k++)
                created[k] = reg.CreateFrame("Earth", Elem(7.0e6, 0.01, 0.5, 1.0, 2.0, 3.0, 3.986e14, 100.0), 1000L + k);
            for (int k = 0; k < 5; k++)
                reg.Dissolve(created[k].Id);

            Ok(reg.Count == 0, "all five frames dissolved (count 0)");
            Ok(reg.NextFrameId == 6L, "live NextFrameId counter is 6 after create-then-dissolve-all");

            FrameRegistrySnapshot snap = FramePersistence.Capture(reg);
            // The OLD heuristic (max id + 1) would have produced 1 here (no frames -> max undefined).
            Ok(snap.NextFrameId == 6L, "Capture persists the exact next id (6), not the max+1 heuristic (1)");

            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(snap.ToXml());
            Ok(back.NextFrameId == 6L, "exact next id round-trips through XML");

            // Restore into a FRESH registry: SetNextFrameId resumes the counter, so the next
            // CreateFrame must yield id 6 — no reuse of a dissolved id (1..5).
            FrameRegistry fresh = new FrameRegistry(new BerthAllocator(2500.0, 18000.0));
            fresh.SetNextFrameId(back.NextFrameId);
            ProximityFrame next = fresh.CreateFrame("Earth", Elem(7.0e6, 0.01, 0.5, 1.0, 2.0, 3.0, 3.986e14, 100.0), 5001L);
            Ok(next.Id == 6L, "next CreateFrame after restore yields id 6 (no dissolved-id reuse)");

            // SetNextFrameId only moves forward: a stale/lower value must not rewind the counter.
            long before = fresh.NextFrameId;
            fresh.SetNextFrameId(2L);
            Ok(fresh.NextFrameId == before, "SetNextFrameId never rewinds the counter below the next free id");
        }

        // FIX H1: FromXml must never throw into world load and never return null (callers
        // dereference Frames/NextFrameId). Null/empty/whitespace/garbage all yield a usable
        // EMPTY/default snapshot so load continues with no frames.
        private static void TestFromXmlHardening()
        {
            Console.WriteLine("[H1: FromXml null/empty/garbage -> usable empty snapshot, never throws]");

            FrameRegistrySnapshot s1 = SafeFromXml(null);
            Ok(s1 != null, "FromXml(null) returns non-null");
            Ok(s1 != null && s1.Frames != null && s1.Frames.Count == 0, "FromXml(null) -> empty frames");
            Ok(s1 != null && s1.NextFrameId == 1L && s1.Version == 2, "FromXml(null) -> default counter/version (2 post no-auto-warp)");

            FrameRegistrySnapshot s2 = SafeFromXml("");
            Ok(s2 != null && s2.Frames != null && s2.Frames.Count == 0, "FromXml(\"\") -> empty snapshot");

            FrameRegistrySnapshot s3 = SafeFromXml("   \r\n\t  ");
            Ok(s3 != null && s3.Frames.Count == 0, "FromXml(whitespace) -> empty snapshot");

            FrameRegistrySnapshot s4 = SafeFromXml("<garbage");
            Ok(s4 != null, "FromXml(\"<garbage\") returns non-null (did not throw)");
            Ok(s4 != null && s4.Frames != null && s4.Frames.Count == 0, "FromXml(\"<garbage\") -> empty snapshot");

            FrameRegistrySnapshot s5 = SafeFromXml("<FrameRegistrySnapshot><Frames><Frame><Id>3</Id>");
            Ok(s5 != null && s5.Frames.Count == 0, "FromXml(truncated valid-root) -> empty snapshot (no throw)");

            // Occupied/Addresses lists are also initialized so downstream rebuild never NREs.
            Ok(s1.OccupiedSlotIds != null && s1.Addresses != null, "empty snapshot has initialized side-table lists");
        }

        // Wrap FromXml so a thrown exception is recorded as a test failure rather than aborting.
        private static FrameRegistrySnapshot SafeFromXml(string xml)
        {
            try
            {
                return FrameRegistrySnapshot.FromXml(xml);
            }
            catch (Exception ex)
            {
                Ok(false, "FromXml threw (must not): " + ex.GetType().Name);
                return null;
            }
        }

        // FIX H3: a snapshot with two frames sharing a BerthSlotId must restore into two frames
        // with DISTINCT slot ids and centers — the duplicate is reallocated a fresh slot so two
        // berths never overlap in world space.
        private static void TestDuplicateBerthSlotOnRestore()
        {
            Console.WriteLine("[H3: duplicate berth slot on restore -> distinct non-overlapping slots]");

            FrameRegistrySnapshot snap = new FrameRegistrySnapshot();
            snap.NextFrameId = 3L;
            snap.BerthSlotRadius = 2500.0;
            snap.BerthClearance = 18000.0;

            // Two frames both claiming slot id 0 (a corrupt/edited save). Give them VALID orbits so the
            // test exercises pure slot-dedup — the Restore path now rejects a degenerate (Mu<=0) orbit
            // separately, which is its own pin (TestRestoreRejectsDegenerateOrbit below).
            FrameSnapshot a = new FrameSnapshot();
            a.Id = 1L; a.ParentBodyName = "Earth"; a.BerthSlotId = 0;
            a.SemiMajorAxis = 1.0e7; a.Eccentricity = 0.1; a.Mu = 1.0e14;
            FrameSnapshot b = new FrameSnapshot();
            b.Id = 2L; b.ParentBodyName = "Earth"; b.BerthSlotId = 0;
            b.SemiMajorAxis = 1.2e7; b.Eccentricity = 0.2; b.Mu = 1.0e14;
            snap.Frames.Add(a);
            snap.Frames.Add(b);
            snap.OccupiedSlotIds.Add(0);

            BerthAllocator alloc = FramePersistence.RebuildAllocator(snap);
            List<ProximityFrame> frames = FramePersistence.RebuildFrames(snap, alloc);

            Ok(frames.Count == 2, "both frames restored");
            ProximityFrame f1 = frames[0];
            ProximityFrame f2 = frames[1];
            Ok(f1.BerthSlotId != f2.BerthSlotId, "restored frames have DISTINCT berth slot ids");
            Ok(f1.BerthSlotId >= 0 && f2.BerthSlotId >= 0, "both slot ids valid");
            Ok(f1.BerthCenter != f2.BerthCenter, "restored frames have DISTINCT berth centers (no overlap)");
            Ok(alloc.IsOccupied(f1.BerthSlotId) && alloc.IsOccupied(f2.BerthSlotId),
               "both restored slots reserved on the allocator");

            // The first frame keeps the original slot; the second got reallocated.
            Ok(f1.BerthSlotId == 0, "first frame keeps its original slot 0");
            Ok(f2.BerthSlotId != 0, "second (colliding) frame got a fresh slot");

            // A subsequent fresh allocation must avoid BOTH restored slots.
            Vector3D c;
            int freshId = alloc.Allocate(out c);
            Ok(freshId != f1.BerthSlotId && freshId != f2.BerthSlotId,
               "fresh Allocate avoids both restored slots");

            // The Restore (IRestoreTarget) seam must ALSO not hand the same slot to two frames:
            // the colliding frame arrives with slot -1 so the target allocates fresh.
            RecordingTarget rt = new RecordingTarget();
            FramePersistence.Restore(snap, rt);
            Ok(rt.Restored.Count == 2, "Restore drove both frames through the seam");
            RestoredFrame seamA = rt.FindById(1L);
            RestoredFrame seamB = rt.FindById(2L);
            Ok(seamA != null && seamA.BerthSlotId == 0, "Restore: first frame keeps slot 0");
            Ok(seamB != null && seamB.BerthSlotId == -1,
               "Restore: colliding frame re-pointed to slot -1 (target allocates fresh)");
        }

        // END-TO-END: the game-side restore path. Capture a live registry -> XML -> FromXml ->
        // Restore into a FRESH FrameRegistry through an adapter that forwards to the new
        // FrameRegistry.RestoreFrame / SetNextFrameId (exactly what FramePersistenceStorage does
        // in-game). Proves frames come back with exact identity, members re-indexed (FindByMember),
        // berth slots reserved, the working VirtualVelocity verbatim, and the id counter resumed.
        private static void TestRestoreIntoLiveRegistry()
        {
            Console.WriteLine("[end-to-end: Restore rebuilds a live FrameRegistry exactly]");

            FrameRegistry live = BuildLiveRegistry();
            long liveNext = live.NextFrameId;
            FrameRegistrySnapshot snap = FrameRegistrySnapshot.FromXml(FramePersistence.Capture(live).ToXml());

            // Fresh registry with the SAME berth config, restored via the live adapter.
            BerthAllocator alloc = FramePersistence.RebuildAllocator(snap);
            FrameRegistry restored = new FrameRegistry(alloc);
            FramePersistence.Restore(snap, new LiveRegistryTarget(restored));

            Ok(restored.Count == live.Count, "restored registry has the same frame count (" + live.Count + ")");
            Ok(restored.NextFrameId == liveNext, "next-frame-id counter resumed exactly (" + liveNext + ")");

            foreach (ProximityFrame orig in live.Frames)
            {
                ProximityFrame r = restored.Get(orig.Id);
                Ok(r != null, "frame " + orig.Id + " restored by exact id");
                if (r == null) continue;

                Ok(r.ParentBodyName == orig.ParentBodyName, "frame " + orig.Id + " parent body matches");
                Ok(r.AnchorEntityId == orig.AnchorEntityId, "frame " + orig.Id + " anchor matches");
                Ok(Math.Abs(r.Elements.SemiMajorAxis - orig.Elements.SemiMajorAxis) < 1e-6,
                   "frame " + orig.Id + " semi-major axis matches");
                Ok(r.VirtualVelocity == orig.VirtualVelocity,
                   "frame " + orig.Id + " VirtualVelocity restored verbatim (not re-derived)");
                Ok(r.PendingDrainDv == orig.PendingDrainDv,
                   "frame " + orig.Id + " PendingDrainDv restored verbatim");
                Ok(r.BerthSlotId == orig.BerthSlotId, "frame " + orig.Id + " berth slot id matches");
                Ok(r.MemberCount == orig.MemberCount, "frame " + orig.Id + " member count matches");

                // Every member must be indexed so FindByMember routes back to THIS frame.
                for (int i = 0; i < orig.Members.Count; i++)
                {
                    long mid = orig.Members[i];
                    ProximityFrame byMem = restored.FindByMember(mid);
                    Ok(byMem != null && byMem.Id == orig.Id,
                       "member " + mid + " re-indexed to frame " + orig.Id);
                }

                Ok(alloc.IsOccupied(r.BerthSlotId), "frame " + orig.Id + " berth slot reserved on allocator");
            }

            // A fresh CreateFrame after restore must not collide with any restored id.
            ProximityFrame fresh = restored.CreateFrame("Earth",
                Elem(9.0e6, 0.0, 0.0, 0.0, 0.0, 0.0, 3.986e14, 100.0), 9001L);
            Ok(fresh.Id == liveNext, "post-restore CreateFrame yields the resumed id (" + liveNext + ")");
            Ok(restored.FindByMember(9001L) != null, "post-restore new frame's anchor is indexed");
        }

        // MID-DRAIN SAVE: a frame whose anchor took a hard event (ram/explosion/hard burn) right
        // before the save still has un-folded delta-v in PendingDrainDv (the fold==feed clamp
        // processes only ~1.67 m/s per tick). The whole accumulator must survive the save/load
        // boundary verbatim — losing it would silently rob the event of part of its effect.
        private static void TestPendingDrainDvRoundTrip()
        {
            Console.WriteLine("[mid-drain save: PendingDrainDv survives capture -> XML -> live restore]");

            FrameRegistry live = new FrameRegistry(new BerthAllocator(2500.0, 18000.0));
            ProximityFrame f = live.CreateFrame("Earth",
                Elem(7.0e6, 0.01, 0.5, 1.0, 2.0, 3.0, 3.986e14, 100.0), 7001L);
            // ~31 m/s of un-drained ram delta-v (dyadic doubles -> bit-exact comparison is fair).
            Vector3D pend = new Vector3D(-27.5, 14.125, -1.0078125);
            f.PendingDrainDv = pend;

            string xml = FramePersistence.Capture(live).ToXml();
            Ok(xml.Contains("PendingDrainDv"), "XML document carries the PendingDrainDv fields");

            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(xml);
            FrameRegistry restored = new FrameRegistry(FramePersistence.RebuildAllocator(back));
            FramePersistence.Restore(back, new LiveRegistryTarget(restored));

            ProximityFrame r = restored.Get(f.Id);
            Ok(r != null, "mid-drain frame restored");
            Ok(r != null && r.PendingDrainDv == pend,
               "PendingDrainDv bit-exact through capture -> XML -> restore (" + pend + ")");

            // SCHEMA COMPATIBILITY: a pre-PendingDrainDv save (no such elements) must load with a
            // zero accumulator — the old behavior, no throw.
            string oldXml = xml.Replace("<PendingDrainDvX>-27.5</PendingDrainDvX>", "")
                               .Replace("<PendingDrainDvY>14.125</PendingDrainDvY>", "")
                               .Replace("<PendingDrainDvZ>-1.0078125</PendingDrainDvZ>", "");
            Ok(!oldXml.Contains("PendingDrainDv"), "legacy-save fixture really has no PendingDrainDv elements");
            FrameRegistrySnapshot old = SafeFromXml(oldXml);
            Ok(old != null && old.Frames.Count == 1, "legacy save (no PendingDrainDv) still loads");
            Ok(old != null && old.Frames.Count == 1 &&
               old.Frames[0].PendingDrainDvX == 0.0 && old.Frames[0].PendingDrainDvY == 0.0 &&
               old.Frames[0].PendingDrainDvZ == 0.0, "legacy save defaults PendingDrainDv to zero");
        }

        // MID-SOI-PATCH SAVE: the wave-2 look-ahead SOI patch COMMITS the encounter immediately —
        // ParentBodyName is already the child and the elements' EPOCH is the FUTURE crossing time
        // tX (SoiReparent.Step's pre-epoch guard then blocks new patch decisions until the rails
        // reach tX). That whole state lives in (ParentBodyName, Elements incl. Epoch), so a save
        // taken mid-patch needs no extra patch record — but the epoch must survive EXACTLY and the
        // restored clock must resume below it, or the guard (and the back-propagated hyperbola)
        // breaks. This pins: epoch fidelity, the pre-epoch guard on the RESTORED frame, and
        // bit-equal propagation across the boundary.
        private static void TestMidPatchEpochFidelity()
        {
            Console.WriteLine("[mid-SOI-patch save: future-epoch elements + pre-epoch guard survive reload]");

            SystemRegistry.Build(SampleSystems.SunEarthMoon());
            SystemRegistry sys = SystemRegistry.Active;
            GravityBody moon = sys.Find("Moon");
            Ok(moon != null, "fixture has a Moon node");

            double now = 1000.0;          // universe time of the save
            double tX = now + 120.0;      // the committed future crossing (elements epoch)

            // A Moon-parented encounter orbit re-bound AT tX (epoch in the future of the save).
            KeplerianElements el = Elem(20.0e3, 0.4, 0.0, 0.0, 0.5, -1.0, moon.Mu, tX);

            FrameRegistry live = new FrameRegistry(new BerthAllocator(2500.0, 18000.0));
            ProximityFrame f = live.CreateFrame("Moon", el, 8001L);

            FrameRegistrySnapshot snap = FramePersistence.Capture(live);
            snap.ClockEpochSeconds = now;            // the universe clock at save time (< epoch)
            snap.ClockTimescale = 100.0;
            FrameRegistrySnapshot back = FrameRegistrySnapshot.FromXml(snap.ToXml());

            Ok(back.ClockEpochSeconds == now, "saved clock epoch (universe time) round-trips exactly");
            FrameRegistry restored = new FrameRegistry(FramePersistence.RebuildAllocator(back));
            FramePersistence.Restore(back, new LiveRegistryTarget(restored));
            ProximityFrame r = restored.Get(f.Id);
            Ok(r != null, "mid-patch frame restored");
            Ok(r != null && r.Elements.Epoch == tX, "future element epoch restored EXACTLY (" + tX + ")");
            Ok(r != null && r.ParentBodyName == "Moon", "patched parent (the child body) restored");
            Ok(back.ClockEpochSeconds < r.Elements.Epoch,
               "restored clock resumes BELOW the patch epoch (same universe time base)");

            // The pre-epoch guard must hold on the RESTORED frame: no new patch decisions before
            // the rails reach the committed crossing (this is what stops the patch/escape flap).
            KeplerianElements before = r.Elements;
            string parentBefore = r.ParentBodyName;
            SoiReparent.Change ch = SoiReparent.Step(r, sys, back.ClockEpochSeconds);
            Ok(ch == SoiReparent.Change.None, "pre-epoch guard: restored frame takes NO patch decision before tX");
            Ok(r.ParentBodyName == parentBefore && r.Elements.Epoch == before.Epoch &&
               r.Elements.TrueAnomaly == before.TrueAnomaly, "guard left the restored frame untouched");

            // Propagation fidelity across the boundary: the restored rails are the SAME conic.
            StateVector a1 = OrbitPropagation.StateAt(el, tX + 50.0);
            StateVector a2 = OrbitPropagation.StateAt(r.Elements, tX + 50.0);
            Ok(a1.Position == a2.Position && a1.Velocity == a2.Velocity,
               "post-epoch propagation bit-equal between saved and restored elements");
        }

        // ABSENT SAVE (a world from BEFORE frame persistence existed, or a brand-new world): the
        // load path sees no stored data. In-game FramePersistenceStorage.Load returns null (file
        // absent) and LoadData skips the restore; the offline equivalent — FromXml(null/empty) +
        // Restore — must yield an EMPTY registry that still works (no throw, fresh ids from 1).
        private static void TestAbsentSaveRestoresEmpty()
        {
            Console.WriteLine("[absent save: no stored data -> empty registry, fully functional]");

            FrameRegistrySnapshot snap = SafeFromXml(null);   // "no save yet"
            RecordingTarget rt = new RecordingTarget();
            FramePersistence.Restore(snap, rt);
            Ok(rt.Restored.Count == 0, "no frames restored from an absent save");
            Ok(rt.NextFrameIdSet == 1L, "id counter starts at 1 on an absent save");

            FrameRegistry reg = new FrameRegistry(FramePersistence.RebuildAllocator(snap));
            FramePersistence.Restore(snap, new LiveRegistryTarget(reg));
            Ok(reg.Count == 0, "live registry stays empty");
            ProximityFrame fresh = reg.CreateFrame("Earth",
                Elem(7.0e6, 0.01, 0.5, 1.0, 2.0, 3.0, 3.986e14, 100.0), 9101L);
            Ok(fresh.Id == 1L && fresh.BerthSlotId >= 0,
               "post-restore registry is fully functional (first frame id 1, slot allocated)");

            // Restore(null, target) / Restore(snap, null) are hard no-ops (never throw).
            try
            {
                FramePersistence.Restore(null, rt);
                FramePersistence.Restore(snap, null);
                Ok(true, "Restore(null, ...) / Restore(..., null) are no-ops");
            }
            catch (Exception ex)
            {
                Ok(false, "Restore null guards threw: " + ex.GetType().Name);
            }
        }

        // A crafted/corrupt save with a degenerate orbit (Mu<=0 / SemiMajorAxis<=0 / e>=1) must be
        // REJECTED on restore — not baked into a live frame where it produces a NaN celestial position
        // all session. The valid sibling frame still restores (one bad record never blacks out the rest).
        private static void TestRestoreRejectsDegenerateOrbit()
        {
            Console.WriteLine("[restore rejects a degenerate/non-finite orbit; valid frames survive]");

            // Direct validator unit checks (finiteness FIRST — NaN passes range comparisons).
            string why;
            Ok(FramePersistence.IsValidElements(Elem(1.0e7, 0.1, 0.0, 0.0, 0.0, 0.0, 1.0e14, 0.0), out why),
               "a sane elliptic orbit is valid");
            Ok(!FramePersistence.IsValidElements(Elem(1.0e7, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0), out why),
               "Mu == 0 rejected");
            Ok(!FramePersistence.IsValidElements(Elem(1.0e7, 0.1, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0), out why),
               "Mu < 0 rejected");
            Ok(!FramePersistence.IsValidElements(Elem(double.NaN, 0.1, 0.0, 0.0, 0.0, 0.0, 1.0e14, 0.0), out why),
               "NaN SemiMajorAxis rejected");
            Ok(!FramePersistence.IsValidElements(Elem(1.0e7, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0e14, 0.0), out why),
               "parabolic (e == 1) rejected");
            Ok(!FramePersistence.IsValidElements(Elem(-1.0e7, 0.1, 0.0, 0.0, 0.0, 0.0, 1.0e14, 0.0), out why),
               "elliptic with SemiMajorAxis < 0 rejected");
            // RED counterfactual: a hyperbolic orbit (e>1, a<0) is LEGITIMATE — must NOT be rejected.
            Ok(FramePersistence.IsValidElements(Elem(-1.0e7, 1.5, 0.0, 0.0, 0.0, 0.0, 1.0e14, 0.0), out why),
               "hyperbolic (e>1, a<0) is valid (not over-rejected)");

            // End-to-end through the Restore seam: one degenerate + one valid frame.
            FrameRegistrySnapshot snap = new FrameRegistrySnapshot();
            snap.NextFrameId = 3L;
            FrameSnapshot bad = new FrameSnapshot();
            bad.Id = 1L; bad.ParentBodyName = "Earth"; bad.BerthSlotId = 0;   // Mu defaults to 0 -> degenerate
            FrameSnapshot good = new FrameSnapshot();
            good.Id = 2L; good.ParentBodyName = "Earth"; good.BerthSlotId = 1;
            good.SemiMajorAxis = 1.0e7; good.Eccentricity = 0.1; good.Mu = 1.0e14;
            snap.Frames.Add(bad);
            snap.Frames.Add(good);

            RecordingTarget rt = new RecordingTarget();
            FramePersistence.Restore(snap, rt);
            Ok(rt.Restored.Count == 1, "only the valid frame restored (degenerate skipped)");
            Ok(rt.FindById(2L) != null, "the valid frame survived");
            Ok(rt.FindById(1L) == null, "the degenerate frame was NOT restored");
        }

        // Adapter mirroring FramePersistenceStorage.RegistryRestoreTarget: forwards the pure
        // IRestoreTarget seam onto a real FrameRegistry's RestoreFrame/SetNextFrameId.
        private sealed class LiveRegistryTarget : FramePersistence.IRestoreTarget
        {
            private readonly FrameRegistry _reg;
            public LiveRegistryTarget(FrameRegistry reg) { _reg = reg; }
            public void SetNextFrameId(long next) { _reg.SetNextFrameId(next); }
            public void RestoreFrame(long id, string parentBodyName, KeplerianElements elements,
                Vector3D virtualVelocity, Vector3D pendingDrainDv, long anchorEntityId,
                IList<long> memberIds, int berthSlotId, Vector3D berthCenter, bool isEncounter)
            {
                _reg.RestoreFrame(id, parentBodyName, elements, virtualVelocity, pendingDrainDv,
                    anchorEntityId, memberIds, berthSlotId, berthCenter, isEncounter);
            }
        }

        // ---- IRestoreTarget recorder ------------------------------------------

        private sealed class RestoredFrame
        {
            public long Id;
            public string ParentBodyName;
            public KeplerianElements Elements;
            public Vector3D VirtualVelocity;
            public Vector3D PendingDrainDv;
            public long AnchorEntityId;
            public List<long> MemberIds;
            public int BerthSlotId;
            public Vector3D BerthCenter;
            public bool IsEncounter;
        }

        private sealed class RecordingTarget : FramePersistence.IRestoreTarget
        {
            public long NextFrameIdSet = -1;
            public int NextIdCallOrder = -1;
            private int _calls;
            public readonly List<RestoredFrame> Restored = new List<RestoredFrame>();

            public void SetNextFrameId(long next)
            {
                NextFrameIdSet = next;
                NextIdCallOrder = _calls;
                _calls++;
            }

            public void RestoreFrame(long id, string parentBodyName, KeplerianElements elements,
                Vector3D virtualVelocity, Vector3D pendingDrainDv, long anchorEntityId,
                IList<long> memberIds, int berthSlotId, Vector3D berthCenter, bool isEncounter)
            {
                RestoredFrame rf = new RestoredFrame();
                rf.Id = id; rf.ParentBodyName = parentBodyName; rf.Elements = elements;
                rf.VirtualVelocity = virtualVelocity; rf.PendingDrainDv = pendingDrainDv;
                rf.AnchorEntityId = anchorEntityId;
                rf.MemberIds = new List<long>(memberIds);
                rf.BerthSlotId = berthSlotId; rf.BerthCenter = berthCenter;
                rf.IsEncounter = isEncounter;
                Restored.Add(rf);
                _calls++;
            }

            public RestoredFrame FindById(long id)
            {
                for (int i = 0; i < Restored.Count; i++) if (Restored[i].Id == id) return Restored[i];
                return null;
            }
        }

        // ---- assertion + util helpers -----------------------------------------

        private static FrameSnapshot FindFrame(FrameRegistrySnapshot snap, long id)
        {
            for (int i = 0; i < snap.Frames.Count; i++)
                if (snap.Frames[i].Id == id) return snap.Frames[i];
            return null;
        }

        private static bool SlotsEqual(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool FrameSnapshotsEqual(FrameSnapshot a, FrameSnapshot b)
        {
            if (a.Id != b.Id) return false;
            if (a.ParentBodyName != b.ParentBodyName) return false;
            if (a.SemiMajorAxis != b.SemiMajorAxis || a.Eccentricity != b.Eccentricity ||
                a.Inclination != b.Inclination || a.Raan != b.Raan ||
                a.ArgPeriapsis != b.ArgPeriapsis || a.TrueAnomaly != b.TrueAnomaly ||
                a.Mu != b.Mu || a.Epoch != b.Epoch) return false;
            if (a.VirtualVelocityX != b.VirtualVelocityX || a.VirtualVelocityY != b.VirtualVelocityY ||
                a.VirtualVelocityZ != b.VirtualVelocityZ) return false;
            if (a.PendingDrainDvX != b.PendingDrainDvX || a.PendingDrainDvY != b.PendingDrainDvY ||
                a.PendingDrainDvZ != b.PendingDrainDvZ) return false;
            if (a.AnchorEntityId != b.AnchorEntityId) return false;
            if (a.BerthSlotId != b.BerthSlotId) return false;
            if (a.BerthCenterX != b.BerthCenterX || a.BerthCenterY != b.BerthCenterY ||
                a.BerthCenterZ != b.BerthCenterZ) return false;
            if (a.MemberIds.Count != b.MemberIds.Count) return false;
            for (int i = 0; i < a.MemberIds.Count; i++) if (a.MemberIds[i] != b.MemberIds[i]) return false;
            return true;
        }

        // Stage-2 charted-roid rendezvous: FrameRegistry.CreateLatentFrame builds a voxel-anchored
        // frame with NO lattice berth slot (BerthSlotId = -1), its berth fixed at the rock's world
        // spawn point, riding the roid's catalog orbit. The materialize flow itself is game-coupled
        // (AsteroidMaterializer uses MyAPIGateway, not offline-reachable), but this registry primitive
        // — the verifiable CORE of the feature — is game-free, so we pin it directly.
        private static void TestCreateLatentFrameNoSlot()
        {
            Console.WriteLine("[CreateLatentFrame: latent (no-slot) voxel-anchored frame]");
            BerthAllocator alloc = new BerthAllocator(2500.0, 18000.0);
            FrameRegistry reg = new FrameRegistry(alloc);

            // The roid's catalog orbit (epoch elements) + a world spawn point (where the rock
            // materialized). Anchor id = the voxel entity id.
            KeplerianElements el = Elem(1.5e7, 0.12, 0.3, 0.6, 1.4, 2.1, 3.986e14, 250.0);
            Vector3D spawnWorld = new Vector3D(54321.0, -12000.0, 777.0);
            const long voxId = 909090L;

            ProximityFrame frame = reg.CreateLatentFrame("Earth", el, voxId, spawnWorld);

            Ok(frame != null, "CreateLatentFrame returns a frame");
            Ok(reg.Count == 1, "registry holds exactly the one latent frame");
            Ok(frame.BerthSlotId == -1, "latent frame has NO lattice slot (BerthSlotId == -1)");
            Ok(frame.BerthCenter == spawnWorld, "BerthCenter == the rock's world spawn point");
            Ok(frame.ParentBodyName == "Earth", "parent is the roid's orbit parent");
            Ok(frame.AnchorEntityId == voxId, "voxel is the anchor");
            Ok(frame.HasMember(voxId), "voxel is a MEMBER (so ElectVoxelAnchor will elect it)");
            Ok(reg.FindByMember(voxId) == frame, "voxel is indexed by member -> FindByMember resolves it");

            // It allocated NO slot: a fresh CreateFrame on the same allocator must therefore get slot 0
            // (the latent frame consumed none). Proves the latent path is slot-free.
            Vector3D otherCenter;
            int firstFreeSlot = alloc.Allocate(out otherCenter);
            Ok(firstFreeSlot == 0, "latent frame consumed no slot (next Allocate gets slot 0)");
            alloc.Free(firstFreeSlot);   // tidy

            // The frame RIDES the roid's exact orbit: frame.PositionAt(t) must equal the same elements
            // propagated from epoch (a KeplerianEphemeris of el), at an arbitrary future time — this is
            // the correctness claim that the player floats relative to the catalog-tracked rock.
            var eph = new KeplerianEphemeris(el);
            double t = 250.0 + 9000.0;   // epoch + ~2.5 hours
            Vector3D framePos = frame.PositionAt(t);
            Vector3D roidPos = eph.PositionAt(t);
            Ok((framePos - roidPos).Length() < 1e-6,
                "frame rides the roid's catalog orbit (PositionAt == ephemeris PositionAt)");

            // Dissolve a no-slot frame: drops the member index, frees NOTHING (no slot), removes it.
            reg.Dissolve(frame.Id);
            Ok(reg.Count == 0, "Dissolve removes the latent frame");
            Ok(reg.FindByMember(voxId) == null, "Dissolve drops the voxel member index");
            // The allocator was never touched by the latent frame, so slot 0 is still free post-dissolve.
            Vector3D afterCenter;
            int afterSlot = alloc.Allocate(out afterCenter);
            Ok(afterSlot == 0, "Dissolve of a no-slot frame freed no slot (still slot 0 available)");
            alloc.Free(afterSlot);
        }

        private static void Ok(bool cond, string msg)
        {
            if (cond) { _passed++; Console.WriteLine("  PASS  " + msg); }
            else { _failed++; Console.WriteLine("  FAIL  " + msg); }
        }
    }
}
