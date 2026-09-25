namespace SEAerospace.SystemDef
{
    /// <summary>
    /// Programmatic sample systems — a Sol-like star with a few planets and a moon, at
    /// SE-scaled numbers (planets ~tens of km radius, orbits ~thousands of km, like the
    /// vanilla/RSS scale rather than true astronomical units). Used as an authoring
    /// example, to serialize a reference config, and as the offline test fixture.
    ///
    /// Every orbit is specified by REAL elements (a, e, i, Omega, omega, M0); NO periods
    /// are stored. Each body's gravity is one coherent set (surfaceGravity + radius). The
    /// builder derives mu, period (Kepler-3 from the parent), and Laplace SOI.
    /// </summary>
    public static class SampleSystems
    {
        /// <summary>
        /// "Sol" (SE-scaled): a star, three planets (one with an atmosphere and a moon),
        /// and a gas giant. Numbers are chosen so a planet's SOI sits well below its orbit
        /// radius (sane patched-conic dominance), and the inner-to-outer ordering is
        /// monotonic in a.
        /// </summary>
        public static SystemDefinition Sol()
        {
            var sys = new SystemDefinition();
            sys.Name = "Sol";
            sys.EpochSeconds = 0.0;

            // --- The star (root). Big mu via high surface gravity * large radius. -------
            var sun = new BodyDefinition();
            sun.Name = "Sun";
            sun.Parent = "";                 // root
            sun.HasOrbit = false;
            sun.SurfaceGravityMps2 = 28.0;   // strong central well
            sun.RadiusMeters = 6.0e6;        // 6000 km star "surface"
            sun.RotationPeriodSeconds = 2.0e6;
            sun.ProxyColorRgb = 0xFFE08A;
            sys.Bodies.Add(sun);

            // --- Inner rocky planet ----------------------------------------------------
            var mercury = new BodyDefinition();
            mercury.Name = "Mercury";
            mercury.Parent = "Sun";
            mercury.HasOrbit = true;
            mercury.SemiMajorAxisMeters = 5.0e7;   // 50,000 km
            mercury.Eccentricity = 0.10;
            mercury.InclinationDeg = 7.0;
            mercury.RaanDeg = 48.0;
            mercury.ArgPeriapsisDeg = 29.0;
            mercury.MeanAnomalyAtEpochDeg = 174.0;
            mercury.SurfaceGravityMps2 = 3.7;
            mercury.RadiusMeters = 24.0e3;         // 24 km
            mercury.RotationPeriodSeconds = 5.0e6;
            mercury.ProxyColorRgb = 0x9A8C82;
            sys.Bodies.Add(mercury);

            // --- Earth-like planet, with atmosphere ------------------------------------
            var earth = new BodyDefinition();
            earth.Name = "Earth";
            earth.Parent = "Sun";
            earth.HasOrbit = true;
            earth.SemiMajorAxisMeters = 1.0e8;     // 100,000 km
            earth.Eccentricity = 0.0167;
            earth.InclinationDeg = 0.0;
            earth.RaanDeg = 0.0;
            earth.ArgPeriapsisDeg = 102.9;
            earth.MeanAnomalyAtEpochDeg = 100.5;
            earth.SurfaceGravityMps2 = 9.81;
            earth.RadiusMeters = 60.0e3;           // 60 km (vanilla-ish)
            earth.HasAtmosphere = true;
            earth.AtmosphereHeightMeters = 8.0e3;
            earth.RotationPeriodSeconds = 86400.0;
            earth.ParkSubtype = "EarthLike";
            earth.ProxyColorRgb = 0x4A7FB5;
            // Proxy visuals (RSS EarthLike): drifting cloud shell + the default blue atmo arc.
            earth.CloudTexture = @"Textures\Clouds\CloudProxy_EarthLike.dds";
            earth.CloudSizeMult = 1.9;
            sys.Bodies.Add(earth);

            // --- Earth's moon ----------------------------------------------------------
            var luna = new BodyDefinition();
            luna.Name = "Luna";
            luna.Parent = "Earth";
            luna.HasOrbit = true;
            luna.SemiMajorAxisMeters = 1.2e6;      // 1200 km from Earth
            luna.Eccentricity = 0.0549;
            luna.InclinationDeg = 5.14;
            luna.RaanDeg = 125.0;
            luna.ArgPeriapsisDeg = 318.0;
            luna.MeanAnomalyAtEpochDeg = 135.0;
            luna.SurfaceGravityMps2 = 1.62;
            luna.RadiusMeters = 16.0e3;            // 16 km
            luna.RotationPeriodSeconds = 2.36e6;
            luna.ProxyColorRgb = 0xBBBBBB;
            // Shipped Moon skin (cm/ng pair) — ProxyTexture skins the sky proxy without
            // making the body parkable (no ParkSubtype).
            luna.ProxyTexture = "Moon";
            sys.Bodies.Add(luna);

            // --- Outer gas giant -------------------------------------------------------
            var jupiter = new BodyDefinition();
            jupiter.Name = "Jupiter";
            jupiter.Parent = "Sun";
            jupiter.HasOrbit = true;
            jupiter.SemiMajorAxisMeters = 5.2e8;   // 520,000 km
            jupiter.Eccentricity = 0.0489;
            jupiter.InclinationDeg = 1.3;
            jupiter.RaanDeg = 100.5;
            jupiter.ArgPeriapsisDeg = 273.9;
            jupiter.MeanAnomalyAtEpochDeg = 20.0;
            jupiter.SurfaceGravityMps2 = 24.8;
            jupiter.RadiusMeters = 200.0e3;        // 200 km gas giant
            jupiter.HasAtmosphere = true;
            jupiter.AtmosphereHeightMeters = 50.0e3;
            jupiter.RotationPeriodSeconds = 35700.0;
            jupiter.ProxyColorRgb = 0xC9A06A;
            // Without overrides the atmosphere arc defaults to the RSS EarthLike preset —
            // a bright Earth-blue limb on a tan gas giant. Author a tan, subtler arc instead.
            jupiter.AtmoColorRgb = 0xC9A06A;
            jupiter.AtmoColorMult = 1.2;
            sys.Bodies.Add(jupiter);

            return sys;
        }

        /// <summary>
        /// **Sun + Earth + Mars + Moon**, sized at TRUE SOLAR-SYSTEM PROPORTIONS (rob 2026-06-16).
        /// Anchor: Earth radius = 240 km; the scale factor k = 240 / 6371 (real Earth radius) is
        /// applied to every other radius and EVERY orbit from real solar-system data, so the layout
        /// is a faithful miniature (Earth orbits at ~5.6 M km, Mars ~8.6 M km, the belt ~13–16 M km).
        /// Earth/Moon/Mars radii use rob's round values (240/60/120 km — within ~6 % of exact
        /// proportion). Surface gravities stay PLAYABLE (real ~g, not scaled), so periods/velocities
        /// are NOT astronomically realistic (deliberate — sizing is realistic, the clock warps the
        /// coast). Rotation periods and atmosphere heights are scaled with each body's radius so
        /// surface co-rotation speed and the atmo/shell proportion stay sane.
        ///
        /// The Sun is a LOGICAL central body (gravity + orbit-draw origin + skybox light), no voxel;
        /// Earth/Mars/Moon are REAL voxel planets. NOTE: travel between bodies now needs warp, and
        /// the per-body r_geo / day and detection ranges may want a retune at this scale.
        /// </summary>
        public static SystemDefinition SunEarthMoon()
        {
            var sys = new SystemDefinition();
            sys.Name = "SunEarthMoon";
            sys.EpochSeconds = 0.0;

            // --- Sun: logical central well (no ParkSubtype => no voxel spawned). 696,340 km × k. ---
            var sun = new BodyDefinition();
            sun.Name = "Sun";
            sun.Parent = "";
            sun.HasOrbit = false;
            sun.SurfaceGravityMps2 = 28.0;        // playable central well (not real solar g)
            sun.RadiusMeters = 2.623e7;           // 26,230 km (real 696,340 km × k)
            sun.RotationPeriodSeconds = 2400.0;
            sun.ProxyColorRgb = 0xFFE08A;
            sys.Bodies.Add(sun);

            // --- Earth: real EarthLike voxel. a = 1 AU × k ≈ 5.635 M km. ---
            var earth = new BodyDefinition();
            earth.Name = "Earth";
            earth.Parent = "Sun";
            earth.HasOrbit = true;
            earth.SemiMajorAxisMeters = 5.635e9;   // 1 AU × k (5,635,000 km). MUST share the radius k
                                                   // (240/6371) so the proxy subtends Earth's TRUE sky
                                                   // angle and matches the voxel it materializes into.
            earth.Eccentricity = 0.0167;           // real Earth e
            earth.InclinationDeg = 0.0;
            earth.RaanDeg = 0.0;
            earth.ArgPeriapsisDeg = 0.0;
            earth.MeanAnomalyAtEpochDeg = 0.0;
            earth.SurfaceGravityMps2 = 9.81;
            earth.RadiusMeters = 240.0e3;          // 240 km (anchor)
            earth.HasAtmosphere = true;
            earth.AtmosphereHeightMeters = 48.0e3; // scaled with radius (×8 from the old 30 km/6 km)
            earth.RotationPeriodSeconds = 28800.0; // 8 h — scaled with radius so surface co-rotation stays ~52 m/s
            earth.ParkSubtype = "SEAeroEarth";      // real voxel planet (custom generator)
            earth.ProxyColorRgb = 0x4A7FB5;
            earth.CloudTexture = @"Textures\Clouds\CloudProxy_EarthLike.dds";
            earth.CloudSizeMult = 1.9;
            earth.CloudRotationPeriodSeconds = 4800.0;
            sys.Bodies.Add(earth);

            // --- Mars: real Mars voxel. a = 1.524 AU × k ≈ 8.586 M km (belt sits outside it). ---
            var mars = new BodyDefinition();
            mars.Name = "Mars";
            mars.Parent = "Sun";
            mars.HasOrbit = true;
            mars.SemiMajorAxisMeters = 8.586e9;    // 1.524 AU × k (8,586,000 km) — same radius k for true angular size
            mars.Eccentricity = 0.093;            // real Mars e
            mars.InclinationDeg = 1.85;
            mars.RaanDeg = 49.0;
            mars.ArgPeriapsisDeg = 286.0;
            mars.MeanAnomalyAtEpochDeg = 19.0;
            mars.SurfaceGravityMps2 = 3.71;        // ~0.38 g
            mars.RadiusMeters = 127.69e3;          // exact proportion: 240 km x (3389.5 / 6371)
            mars.HasAtmosphere = true;
            mars.AtmosphereHeightMeters = 22.0e3;  // scaled with radius (thin Mars × the ×7.5 radius bump)
            mars.RotationPeriodSeconds = 27750.0;  // scaled with radius (~7.7 h)
            mars.ParkSubtype = "Mars";             // real voxel planet (vanilla Mars subtype)
            mars.ProxyColorRgb = 0xB5562F;         // rusty
            sys.Bodies.Add(mars);

            // --- Moon: real Moon voxel. a (geocentric) = 384,400 km × k ≈ 14,479 km from Earth. ---
            var moon = new BodyDefinition();
            moon.Name = "Moon";
            moon.Parent = "Earth";
            moon.HasOrbit = true;
            moon.SemiMajorAxisMeters = 1.4479e7;   // 384,400 km × k (14,479 km from Earth) — same radius k
            moon.Eccentricity = 0.0549;           // real Moon e
            moon.InclinationDeg = 5.14;
            moon.RaanDeg = 30.0;
            moon.ArgPeriapsisDeg = 0.0;
            moon.MeanAnomalyAtEpochDeg = 90.0;
            moon.SurfaceGravityMps2 = 1.62;
            moon.RadiusMeters = 65.45e3;           // exact proportion: 240 km x (1737.4 / 6371)
            moon.RotationPeriodSeconds = 34500.0;  // scaled with radius (~9.6 h)
            moon.ParkSubtype = "Moon";              // real voxel planet
            moon.ProxyColorRgb = 0xBBBBBB;
            sys.Bodies.Add(moon);

            return sys;
        }
    }
}
