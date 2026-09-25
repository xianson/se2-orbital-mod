using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

namespace SystemDefTest
{
    /// <summary>
    /// Offline assertion harness for the element-based star-system definition + builder +
    /// serialization layer. No game, no test framework. Exit 0 = all pass.
    ///
    /// Proves the RSS fixes hold in built code:
    ///  - real elements drive the tree (not Euler angles),
    ///  - one mu per body and Kepler-3 (period = 2 pi sqrt(a^3 / mu_parent)) for every child,
    ///  - Laplace SOI sane (planet SOI << its orbit radius),
    ///  - definition -> XML -> definition -> tree round-trips identically (no hijacked fields).
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static readonly XmlSerializer Xs = new XmlSerializer(typeof(SystemDefinition));

        private static int Main()
        {
            Console.WriteLine("SystemDef offline tests");
            Console.WriteLine("=======================");

            // Install a game-free XML backend (the game wires MyAPIGateway instead). Legal
            // outside SE; mirrors the same [XmlRoot]/[XmlArray] shape.
            SystemDefinition.XmlSerializeHook = SerializeOffline;
            SystemDefinition.XmlDeserializeHook = DeserializeOffline;

            try
            {
                TestBuildAndStructure();
                TestMuConsistency();
                TestKepler3();
                TestLaplaceSoiSane();
                TestValidationErrors();
                TestXmlRoundTrip();
                TestRoundTripTreeIdentical();
                TestSpinPropagation();
                TestRegistry();
                TestSunEarthMoon();
                TestFiniteValidationPins();   // anti-jank #18: NaN/Inf elements rejected
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

        private static string SerializeOffline(SystemDefinition def)
        {
            using (var sw = new StringWriter())
            {
                Xs.Serialize(sw, def);
                return sw.ToString();
            }
        }

        private static SystemDefinition DeserializeOffline(string xml)
        {
            using (var sr = new StringReader(xml))
            {
                return (SystemDefinition)Xs.Deserialize(sr);
            }
        }

        // ---- assertion helpers ------------------------------------------------

        private static void Ok(bool cond, string msg)
        {
            if (cond) { _passed++; Console.WriteLine("  PASS  " + msg); }
            else { _failed++; Console.WriteLine("  FAIL  " + msg); }
        }

        private static void Approx(double actual, double expected, double relTol, string msg)
        {
            double denom = Math.Abs(expected) > 1e-9 ? Math.Abs(expected) : 1.0;
            double rel = Math.Abs(actual - expected) / denom;
            Ok(rel <= relTol, msg + " (actual=" + actual.ToString("E6") +
                " expected=" + expected.ToString("E6") + " rel=" + rel.ToString("E2") + ")");
        }

        // ---- tests ------------------------------------------------------------

        private static void TestBuildAndStructure()
        {
            Console.WriteLine("[structure]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemBuilder.Build(def);

            Ok(r.Ok, "Sol builds without error" + (r.Ok ? "" : ": " + r.Error));
            Ok(r.Root != null && r.Root.Name == "Sun", "root is Sun");
            Ok(r.Root != null && r.Root.IsRoot && r.Root.SoiRadius == double.PositiveInfinity,
                "root has infinite SOI");
            Ok(r.ByName.Count == 5, "5 nodes built (Sun + Mercury + Earth + Luna + Jupiter)");

            // parent links
            Ok(r.ByName["Mercury"].Parent == r.ByName["Sun"], "Mercury parent is Sun");
            Ok(r.ByName["Earth"].Parent == r.ByName["Sun"], "Earth parent is Sun");
            Ok(r.ByName["Luna"].Parent == r.ByName["Earth"], "Luna parent is Earth (nested moon)");
            Ok(r.ByName["Jupiter"].Parent == r.ByName["Sun"], "Jupiter parent is Sun");
            Ok(r.ByName["Sun"].Children.Count == 3, "Sun has 3 direct children");
            Ok(r.ByName["Earth"].Children.Count == 1, "Earth has 1 child (Luna)");

            // ephemeris types: root static, orbiters keplerian
            Ok(r.ByName["Sun"].ParentRelative == null, "root has no parent-relative ephemeris");
            Ok(r.ByName["Earth"].ParentRelative is KeplerianEphemeris, "Earth on Keplerian rails");
        }

        private static void TestMuConsistency()
        {
            Console.WriteLine("[mu consistency — fixes RSS#2]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemBuilder.Build(def);

            // mu = g * R^2 for each body; one value, used everywhere.
            BodyDefinition earthDef = Find(def, "Earth");
            double expectedEarthMu = earthDef.SurfaceGravityMps2 * earthDef.RadiusMeters * earthDef.RadiusMeters;
            Approx(r.ByName["Earth"].Mu, expectedEarthMu, 1e-12, "Earth node mu == g*R^2");

            // the CHILD's ephemeris element mu equals the PARENT's mu (not a back-derived,
            // per-body implied central mass like RSS).
            KeplerianEphemeris eph = (KeplerianEphemeris)r.ByName["Earth"].ParentRelative;
            KeplerianElements el = eph.ElementsAt(def.EpochSeconds);
            Approx(el.Mu, r.ByName["Sun"].Mu, 1e-12, "Earth's orbit mu == Sun's mu (parent)");

            KeplerianEphemeris lunaEph = (KeplerianEphemeris)r.ByName["Luna"].ParentRelative;
            Approx(lunaEph.ElementsAt(def.EpochSeconds).Mu, r.ByName["Earth"].Mu, 1e-12,
                "Luna's orbit mu == Earth's mu (parent)");
        }

        private static void TestKepler3()
        {
            Console.WriteLine("[Kepler-3 derived period — fixes RSS#2]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemBuilder.Build(def);

            string[] orbiters = { "Mercury", "Earth", "Luna", "Jupiter" };
            for (int i = 0; i < orbiters.Length; i++)
            {
                BodyDefinition bd = Find(def, orbiters[i]);
                double muParent = r.ByName[bd.Parent].Mu;
                double a = bd.SemiMajorAxisMeters;
                double expectedPeriod = 2.0 * Math.PI * Math.Sqrt(a * a * a / muParent);

                KeplerianEphemeris eph = (KeplerianEphemeris)r.ByName[orbiters[i]].ParentRelative;
                double period = eph.ElementsAt(def.EpochSeconds).Period;
                Approx(period, expectedPeriod, 1e-9,
                    orbiters[i] + " period == 2*pi*sqrt(a^3/mu_parent)");
            }
        }

        private static void TestLaplaceSoiSane()
        {
            Console.WriteLine("[Laplace SOI sanity]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemBuilder.Build(def);

            BodyDefinition earthDef = Find(def, "Earth");
            double earthMu = r.ByName["Earth"].Mu;
            double sunMu = r.ByName["Sun"].Mu;
            double expectedSoi = Gravity.LaplaceSoiRadius(earthDef.SemiMajorAxisMeters, earthMu, sunMu);
            Approx(r.ByName["Earth"].SoiRadius, expectedSoi, 1e-12, "Earth SOI == Laplace formula");

            // a planet's SOI must be well below its orbit radius (patched-conic dominance).
            Ok(r.ByName["Earth"].SoiRadius < earthDef.SemiMajorAxisMeters * 0.2,
                "Earth SOI << its orbit radius");
            Ok(r.ByName["Earth"].SoiRadius > 0.0, "Earth SOI positive");

            // Luna's SOI must fit inside Earth's SOI and be below Luna's orbit radius.
            BodyDefinition lunaDef = Find(def, "Luna");
            Ok(r.ByName["Luna"].SoiRadius < lunaDef.SemiMajorAxisMeters,
                "Luna SOI < its orbit radius around Earth");
        }

        private static void TestValidationErrors()
        {
            Console.WriteLine("[validation errors]");

            // unknown parent
            SystemDefinition d1 = SampleSystems.Sol();
            Find(d1, "Earth").Parent = "Nope";
            SystemBuildResult r1 = SystemBuilder.Build(d1);
            Ok(!r1.Ok && r1.Error != null && r1.Error.Contains("unknown parent"),
                "unknown parent rejected");

            // two roots
            SystemDefinition d2 = SampleSystems.Sol();
            Find(d2, "Mercury").Parent = "";
            SystemBuildResult r2 = SystemBuilder.Build(d2);
            Ok(!r2.Ok && r2.Error != null && r2.Error.Contains("Multiple root"),
                "multiple roots rejected");

            // cycle: Earth -> Luna -> Earth
            SystemDefinition d3 = SampleSystems.Sol();
            Find(d3, "Earth").Parent = "Luna";
            SystemBuildResult r3 = SystemBuilder.Build(d3);
            Ok(!r3.Ok && r3.Error != null && r3.Error.Contains("cycle"), "parent cycle rejected");
        }

        // REGRESSION PIN (anti-jank #18): Encounter/Asteroid TryValidate must reject NON-FINITE orbital
        // elements. Every range check is a comparison, and NaN passes all of them (NaN<0, NaN>=1 both
        // false) — so without an explicit finiteness gate a corrupt/partial persisted XML would validate
        // and bake a NaN ephemeris into detection (a NaN BodyInertialPoint poisoning the sweep).
        private static void TestFiniteValidationPins()
        {
            Console.WriteLine();
            Console.WriteLine("[finite-validation pins (#18)]");

            // A valid baseline that passes (so we know the guard isn't rejecting good values).
            var aOk = new AsteroidDefinition { Id = "ok", ParentBodyName = "Sun", SemiMajorAxisMeters = 1.5e10,
                Eccentricity = 0.1, InclinationDeg = 5.0, RepresentativeRadiusMeters = 200.0 };
            string e;
            Ok(aOk.TryValidate(out e), "valid asteroid passes TryValidate");

            // NaN / Inf in each element must be rejected.
            var aNan = new AsteroidDefinition { Id = "nan", ParentBodyName = "Sun",
                SemiMajorAxisMeters = double.NaN, Eccentricity = 0.1, InclinationDeg = 5.0, RepresentativeRadiusMeters = 200.0 };
            Ok(!aNan.TryValidate(out e) && e != null && e.Contains("non-finite"), "NaN SemiMajorAxis rejected");
            var aInf = new AsteroidDefinition { Id = "inf", ParentBodyName = "Sun",
                SemiMajorAxisMeters = 1.5e10, Eccentricity = double.PositiveInfinity, InclinationDeg = 5.0, RepresentativeRadiusMeters = 200.0 };
            Ok(!aInf.TryValidate(out e) && e != null && e.Contains("non-finite"), "Inf eccentricity rejected");
            var aNanI = new AsteroidDefinition { Id = "nani", ParentBodyName = "Sun",
                SemiMajorAxisMeters = 1.5e10, Eccentricity = 0.1, InclinationDeg = double.NaN, RepresentativeRadiusMeters = 200.0 };
            Ok(!aNanI.TryValidate(out e) && e != null && e.Contains("non-finite"), "NaN inclination rejected");

            var enOk = new EncounterDefinition { Id = "eok", ParentBodyName = "Sun", SemiMajorAxisMeters = 1.5e10,
                Eccentricity = 0.1, InclinationDeg = 5.0, RepresentativeRadiusMeters = 200.0 };
            Ok(enOk.TryValidate(out e), "valid encounter passes TryValidate");
            var enNan = new EncounterDefinition { Id = "enan", ParentBodyName = "Sun", SemiMajorAxisMeters = 1.5e10,
                Eccentricity = 0.1, InclinationDeg = 5.0, RaanDeg = double.NaN, RepresentativeRadiusMeters = 200.0 };
            Ok(!enNan.TryValidate(out e) && e != null && e.Contains("non-finite"), "NaN RAAN encounter rejected");
        }

        private static void TestXmlRoundTrip()
        {
            Console.WriteLine("[XML round-trip — game-free]");
            SystemDefinition def = SampleSystems.Sol();
            string xml = def.ToXml();
            Ok(xml.Contains("<SystemDefinition") && xml.Contains("Sol"), "ToXml emits a SystemDefinition doc");
            Ok(!xml.Contains("BodyInstanceName"), "no hijacked identity field (fixes RSS#5)");
            Ok(xml.Contains("MeanAnomalyAtEpochDeg"), "phase stored as mean anomaly (real element)");
            Ok(!xml.Contains("OrbitalPeriod"), "no stored period (Kepler-3 derived)");

            SystemDefinition back = SystemDefinition.FromXml(xml);
            Ok(back.Name == def.Name, "round-trip name");
            Ok(back.Bodies.Count == def.Bodies.Count, "round-trip body count");

            BodyDefinition e0 = Find(def, "Earth");
            BodyDefinition e1 = Find(back, "Earth");
            Ok(e1 != null && e1.SemiMajorAxisMeters == e0.SemiMajorAxisMeters &&
               e1.Eccentricity == e0.Eccentricity && e1.InclinationDeg == e0.InclinationDeg &&
               e1.RaanDeg == e0.RaanDeg && e1.ArgPeriapsisDeg == e0.ArgPeriapsisDeg &&
               e1.MeanAnomalyAtEpochDeg == e0.MeanAnomalyAtEpochDeg,
               "round-trip Earth elements identical");
            Ok(e1.ParkSubtype == e0.ParkSubtype && e1.HasAtmosphere == e0.HasAtmosphere,
               "round-trip Earth placement/visual fields identical");
            Ok(e1.CloudTexture == e0.CloudTexture && e1.CloudSizeMult == e0.CloudSizeMult &&
               e1.CloudRotationPeriodSeconds == e0.CloudRotationPeriodSeconds &&
               e1.ShowCloudInOrbit == e0.ShowCloudInOrbit &&
               e1.AtmoColorRgb == e0.AtmoColorRgb && e1.AtmoColorMult == e0.AtmoColorMult &&
               e1.AtmoThicknessMult == e0.AtmoThicknessMult && e1.AtmoInZoneMult == e0.AtmoInZoneMult &&
               e1.ProxyScale == e0.ProxyScale && e1.ProxyScaleFadeMin == e0.ProxyScaleFadeMin &&
               e1.ProxyFadeoutHeightMult == e0.ProxyFadeoutHeightMult,
               "round-trip Earth proxy-visual config (cloud + atmosphere) identical");

            // Non-vacuous visual-field round-trip: the fixture leaves most of the 14 proxy-visual
            // fields at their defaults, so equality above can't distinguish "serialized" from
            // "default re-applied". Author a DISTINCT non-default value into every field, then
            // prove each one survives the XML (a dropped/typo'd element would come back default).
            SystemDefinition mdef = SampleSystems.Sol();
            BodyDefinition m0 = Find(mdef, "Luna");
            m0.ProxyTexture = "RTSkin";
            m0.ProxyScale = 1.25;
            m0.ProxyScaleFadeMin = 0.45;
            m0.ProxyFadeoutHeightMult = 0.6;
            m0.CloudTexture = "Textures\\Clouds\\RT.dds";
            m0.CloudUpX = 0.11; m0.CloudUpY = -0.22; m0.CloudUpZ = 0.33;
            m0.CloudSizeMult = 2.5;
            m0.CloudRotationPeriodSeconds = 1234.0;
            m0.ShowCloudInOrbit = true;
            m0.AtmoColorRgb = 0x123456;
            m0.AtmoColorMult = 9.9;
            m0.AtmoThicknessMult = 4.4;
            m0.AtmoInZoneMult = 0.77;
            BodyDefinition m1 = Find(SystemDefinition.FromXml(mdef.ToXml()), "Luna");
            Ok(m1.ProxyTexture == "RTSkin" && m1.ProxyScale == 1.25 &&
               m1.ProxyScaleFadeMin == 0.45 && m1.ProxyFadeoutHeightMult == 0.6,
               "non-default round-trip: proxy skin/scale/fade fields");
            Ok(m1.CloudTexture == "Textures\\Clouds\\RT.dds" &&
               m1.CloudUpX == 0.11 && m1.CloudUpY == -0.22 && m1.CloudUpZ == 0.33 &&
               m1.CloudSizeMult == 2.5 && m1.CloudRotationPeriodSeconds == 1234.0 &&
               m1.ShowCloudInOrbit == true,
               "non-default round-trip: cloud fields (incl. CloudUpX/Y/Z)");
            Ok(m1.AtmoColorRgb == 0x123456 && m1.AtmoColorMult == 9.9 &&
               m1.AtmoThicknessMult == 4.4 && m1.AtmoInZoneMult == 0.77,
               "non-default round-trip: atmosphere fields");
        }

        private static void TestRoundTripTreeIdentical()
        {
            Console.WriteLine("[definition -> XML -> definition -> tree identical]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult before = SystemBuilder.Build(def);

            SystemDefinition reparsed = SystemDefinition.FromXml(def.ToXml());
            SystemBuildResult after = SystemBuilder.Build(reparsed);

            Ok(after.Ok, "reparsed system builds");
            Ok(before.ByName.Count == after.ByName.Count, "same node count after round-trip");

            foreach (KeyValuePair<string, GravityBody> kv in before.ByName)
            {
                GravityBody b = kv.Value;
                GravityBody a;
                if (!after.ByName.TryGetValue(kv.Key, out a)) { Ok(false, "missing node " + kv.Key); continue; }
                bool muSame = Math.Abs(a.Mu - b.Mu) <= 1e-9 * Math.Max(1.0, Math.Abs(b.Mu));
                bool soiSame = a.SoiRadius == b.SoiRadius ||
                    Math.Abs(a.SoiRadius - b.SoiRadius) <= 1e-9 * Math.Max(1.0, Math.Abs(b.SoiRadius));
                bool parentSame = (a.Parent == null) == (b.Parent == null) &&
                    (a.Parent == null || a.Parent.Name == b.Parent.Name);
                Ok(muSame && soiSame && parentSame, "node " + kv.Key + " identical (mu/soi/parent)");
            }

            // and a propagated position matches across the round-trip (full-pipeline check).
            double t = 12345.0;
            var bEarth = before.ByName["Earth"].ParentRelative.PositionAt(t);
            var aEarth = after.ByName["Earth"].ParentRelative.PositionAt(t);
            Ok((bEarth - aEarth).Length() < 1e-3, "Earth propagated position identical after round-trip");
        }

        private static void TestSpinPropagation()
        {
            Console.WriteLine("[spin / planet-day plumbed config -> runtime]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemBuilder.Build(def);

            // Authored rotation periods reach the runtime node unchanged.
            BodyDefinition earthDef = Find(def, "Earth");
            Approx(r.ByName["Earth"].RotationPeriodSeconds, earthDef.RotationPeriodSeconds, 1e-12,
                "Earth rotation period carried to runtime node");
            Ok(r.ByName["Earth"].RotationPeriodSeconds == 86400.0, "Earth day == 86400 s");

            // Default spin axis is +Z (unit) when the config leaves it at the default.
            Ok(Math.Abs(r.ByName["Earth"].SpinAxis.Length() - 1.0) < 1e-12, "spin axis normalized");
            Ok(r.ByName["Earth"].SpinAxis.Z > 0.999, "default spin axis is +Z");

            // Rotation phase advances linearly and wraps at one sidereal day.
            GravityBody earth = r.ByName["Earth"];
            Ok(Math.Abs(earth.RotationAngleAt(0.0)) < 1e-12, "phase 0 at epoch");
            Approx(earth.RotationAngleAt(86400.0 * 0.25), Math.PI * 0.5, 1e-9, "quarter day == 90 deg");
            Ok(Math.Abs(earth.RotationAngleAt(86400.0)) < 1e-9, "full day wraps to 0");
            Ok(Math.Abs(earth.RotationAngleAt(86400.0 * 1.5) - Math.PI) < 1e-9, "1.5 days == 180 deg");

            // A custom spin axis is normalized and honored.
            BodyDefinition tilted = new BodyDefinition();
            tilted.Name = "Tilted"; tilted.Parent = "Sun"; tilted.HasOrbit = true;
            tilted.SemiMajorAxisMeters = 2.0e8; tilted.Eccentricity = 0.0;
            tilted.SurfaceGravityMps2 = 9.0; tilted.RadiusMeters = 50.0e3;
            tilted.RotationPeriodSeconds = 1000.0;
            tilted.SpinAxisX = 0.0; tilted.SpinAxisY = 3.0; tilted.SpinAxisZ = 0.0; // non-unit +Y
            def.Bodies.Add(tilted);
            SystemBuildResult r2 = SystemBuilder.Build(def);
            Ok(Math.Abs(r2.ByName["Tilted"].SpinAxis.Length() - 1.0) < 1e-12, "custom axis normalized");
            Ok(r2.ByName["Tilted"].SpinAxis.Y > 0.999, "custom axis points +Y");

            // No spin period -> identity orientation, zero phase.
            GravityBody sun = r.ByName["Sun"];
            Ok(r.ByName["Mercury"].RotationPeriodSeconds == 5.0e6, "Mercury day carried");
            BodyDefinition noSpin = new BodyDefinition();
            noSpin.Name = "Frozen"; noSpin.Parent = "Sun"; noSpin.HasOrbit = true;
            noSpin.SemiMajorAxisMeters = 3.0e8; noSpin.Eccentricity = 0.0;
            noSpin.SurfaceGravityMps2 = 9.0; noSpin.RadiusMeters = 50.0e3;
            noSpin.RotationPeriodSeconds = 0.0;
            def.Bodies.Add(noSpin);
            SystemBuildResult r3 = SystemBuilder.Build(def);
            Ok(r3.ByName["Frozen"].RotationAngleAt(99999.0) == 0.0, "no-spin body has zero phase");
        }

        private static void TestRegistry()
        {
            Console.WriteLine("[system registry — single source of truth]");
            SystemDefinition def = SampleSystems.Sol();
            SystemBuildResult r = SystemRegistry.Build(def);

            Ok(r.Ok, "registry build ok");
            SystemRegistry reg = SystemRegistry.Active;
            Ok(reg != null, "Active published after build");
            Ok(reg != null && reg.Count == 5, "registry holds 5 bodies");
            Ok(reg != null && reg.Root != null && reg.Root.Name == "Sun", "registry root is Sun");

            // root-first BFS order: Sun first, every child after its parent.
            Ok(reg.Bodies[0].Name == "Sun", "BFS order: root first");
            int idxEarth = IndexOf(reg, "Earth");
            int idxLuna = IndexOf(reg, "Luna");
            Ok(idxEarth >= 0 && idxLuna > idxEarth, "BFS order: Earth before its moon Luna");

            // name + definition lookups.
            Ok(reg.Find("Jupiter") != null && reg.Find("Jupiter").Name == "Jupiter", "Find by name");
            Ok(reg.Find("Nope") == null, "Find unknown returns null");
            BodyDefinition earthDef = reg.FindDefinition("Earth");
            Ok(earthDef != null && earthDef.RadiusMeters == 60.0e3, "FindDefinition carries authored radius");

            // THE CANONICAL-BUILD CONTRACT (2026-06-12 RSS-literal): SystemRegistry.Build has
            // no plane input — the live registry is ALWAYS the canonical (+Z reference plane)
            // SystemBuilder build, byte-identical. One layout per definition, independent of
            // any world/sun/render state by construction (nothing to persist or migrate).
            SystemBuildResult canon = SystemBuilder.Build(SampleSystems.Sol());
            Ok((reg.Find("Earth").OriginInRoot(12345.0).Position
                - canon.ByName["Earth"].OriginInRoot(12345.0).Position).Length() == 0.0,
                "registry build == canonical SystemBuilder build (exact)");
            Ok(reg.Find("Earth").SpinAxis.Z > 0.999, "registry default spin axis stays canonical +Z");

            // failed build leaves Active unchanged.
            SystemRegistry prev = SystemRegistry.Active;
            SystemDefinition bad = SampleSystems.Sol();
            Find(bad, "Earth").Parent = "Nope";
            SystemBuildResult rbad = SystemRegistry.Build(bad);
            Ok(!rbad.Ok, "bad build reports failure");
            Ok(SystemRegistry.Active == prev, "Active unchanged after failed build");

            SystemRegistry.Clear();
            Ok(SystemRegistry.Active == null, "Clear empties Active");
        }

        private static void TestSunEarthMoon()
        {
            Console.WriteLine("[SunEarthMoon test-world config]");
            SystemDefinition def = SampleSystems.SunEarthMoon();
            SystemBuildResult r = SystemBuilder.Build(def);

            Ok(r.Ok, "SunEarthMoon builds" + (r.Ok ? "" : ": " + r.Error));
            Ok(r.ByName.Count == 4, "4 bodies (Sun/Earth/Mars/Moon)");
            Ok(r.ByName["Sun"].IsRoot && Find(def, "Sun").ParkSubtype == "", "root is Sun, no voxel subtype");
            Ok(Find(def, "Earth").ParkSubtype == "SEAeroEarth", "Earth is SEAeroEarth voxel (custom generator)");
            Ok(Find(def, "Mars").ParkSubtype == "Mars", "Mars is Mars voxel");
            Ok(Find(def, "Moon").ParkSubtype == "Moon", "Moon is Moon voxel");
            Ok(r.ByName["Moon"].Parent == r.ByName["Earth"], "Moon orbits Earth");

            // dominance: Earth's SOI sits below its own orbit AND above the Moon's orbit, so the
            // Moon is genuinely inside Earth's SOI (a sane patched-conic tree to fly).
            double earthSoi = r.ByName["Earth"].SoiRadius;
            Ok(earthSoi < Find(def, "Earth").SemiMajorAxisMeters, "Earth SOI < Earth orbit");
            Ok(Find(def, "Moon").SemiMajorAxisMeters < earthSoi, "Moon orbit < Earth SOI (Moon inside Earth SOI)");
            Ok(r.ByName["Moon"].SoiRadius > Find(def, "Moon").RadiusMeters, "Moon SOI > Moon radius");
        }

        private static int IndexOf(SystemRegistry reg, string name)
        {
            for (int i = 0; i < reg.Bodies.Count; i++)
                if (reg.Bodies[i].Name == name) return i;
            return -1;
        }

        // ---- util -------------------------------------------------------------

        private static BodyDefinition Find(SystemDefinition def, string name)
        {
            for (int i = 0; i < def.Bodies.Count; i++)
                if (def.Bodies[i].Name == name) return def.Bodies[i];
            return null;
        }
    }
}
