using System;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// A plain serializable description of ONE body in a star system: its physical
    /// parameters, its orbit (as REAL classical elements, absent for the root), and
    /// optional visual/placement hints. This is the clean replacement for Real Solar
    /// Systems' <c>OrbitInfoConfig</c> — every documented RSS flaw is fixed here:
    ///
    ///  - RSS#1 (Euler pitch/roll/yaw + free-floating period): orbit orientation is the
    ///    proper element triple i / RAAN(Omega) / argPeriapsis(omega); phase is the
    ///    physically-meaningful mean anomaly at epoch M0. There is NO stored period.
    ///  - RSS#2 (mu back-derived per body from 4 pi^2 a^3 / T^2): mu is NOT stored or
    ///    derived from a period. A body's gravity is one coherent set —
    ///    surfaceGravity + radius -> mu = g * R^2 (the same convention OrbitController /
    ///    OrbitRenderer use). The CHILD's period follows from the PARENT's mu and the
    ///    child's a by Kepler-3, computed in <see cref="SystemBuilder"/>; it is never an
    ///    independent input, so Kepler-3 holds across the whole system by construction.
    ///  - RSS#5 (planet stowage hijacking the BodyInstanceName string): placement is an
    ///    explicit, typed field (<see cref="ParkSubtype"/> / <see cref="ParkPositionXyz"/>),
    ///    not smuggled through an unrelated identity field.
    ///
    /// POCO rules for SE serialization: public fields, parameterless ctor, no game types.
    /// Units are SI: meters, seconds, m/s^2, m^3/s^2. Angles are DEGREES in the config
    /// (human-authorable) and converted to radians by the builder.
    /// </summary>
    public class BodyDefinition
    {
        // ---- identity / hierarchy ---------------------------------------------

        /// <summary>Unique body name (the SOI-tree node name). Required.</summary>
        public string Name = "";

        /// <summary>Name of the parent body this one orbits. Empty/null = this is the
        /// root (the star / system barycenter). Exactly one body must be a root.</summary>
        public string Parent = "";

        // ---- physical parameters (one coherent gravity set; fixes RSS#2) ------

        /// <summary>Surface gravity in m/s^2 (e.g. Earth ~9.81). With <see cref="RadiusMeters"/>
        /// this defines mu = SurfaceGravityMps2 * RadiusMeters^2 — the SAME no-cutoff
        /// inverse-square convention the renderer/controller already use. This is the
        /// single source of truth for the body's gravity.</summary>
        public double SurfaceGravityMps2 = 0.0;

        /// <summary>Sea-level / mean radius in meters. Sets surface-gravity reference and
        /// the body's physical size.</summary>
        public double RadiusMeters = 0.0;

        // ---- orbital elements (REAL elements; fixes RSS#1) --------------------
        // Absent (HasOrbit == false) for the root. For an orbiting body, these six plus
        // the parent's mu fully determine the orbit; the period is DERIVED, never stored.

        /// <summary>Whether this body orbits its parent. False only for the root (or a
        /// deliberately parked/static body). When false the orbital fields are ignored
        /// and the body is placed via a <see cref="FixedEphemeris"/> at the origin (root)
        /// or at <see cref="ParkPositionXyz"/>.</summary>
        public bool HasOrbit = true;

        /// <summary>Semi-major axis a, meters. With the parent's mu this fixes the period
        /// by Kepler-3 (T = 2 pi sqrt(a^3 / mu_parent)). No independent period exists.</summary>
        public double SemiMajorAxisMeters = 0.0;

        /// <summary>Eccentricity e (0 = circular, [0,1) elliptic).</summary>
        public double Eccentricity = 0.0;

        /// <summary>Inclination i, DEGREES [0,180]. Proper element — NOT a "pitch".</summary>
        public double InclinationDeg = 0.0;

        /// <summary>Right ascension of the ascending node Omega, DEGREES. Orients the node
        /// line. Proper element — NOT a "yaw".</summary>
        public double RaanDeg = 0.0;

        /// <summary>Argument of periapsis omega, DEGREES. Orients periapsis within the
        /// orbital plane. Proper element — NOT a "roll".</summary>
        public double ArgPeriapsisDeg = 0.0;

        /// <summary>Mean anomaly at epoch M0, DEGREES. The phase along the orbit at the
        /// system epoch — physically meaningful, replacing RSS's decorative spiral angle /
        /// period-offset fraction.</summary>
        public double MeanAnomalyAtEpochDeg = 0.0;

        // ---- logical spin (rotation) ------------------------------------------

        /// <summary>Sidereal rotation period in seconds (logical spin). 0 = unspecified.</summary>
        public double RotationPeriodSeconds = 0.0;

        /// <summary>Spin-axis direction (unit-ish), the body's north pole. Defaults to +Z
        /// (the orbital reference axis) if left zero.</summary>
        public double SpinAxisX = 0.0;
        public double SpinAxisY = 0.0;
        public double SpinAxisZ = 1.0;

        // ---- atmosphere (optional) --------------------------------------------

        public bool HasAtmosphere = false;
        public double AtmosphereHeightMeters = 0.0;

        /// <summary>How much sunlight its face reflects (Bond albedo, 0..1): planet-shine and the glare of its
        /// day side in the sensor model. Earth ~0.3, the Moon ~0.12.</summary>
        public double Albedo = 0.3;

        // ---- placement / stowage (explicit; fixes RSS#5) ----------------------

        /// <summary>Optional SE planet subtype to instantiate as the parked voxel body for
        /// this celestial (world-placement hint for the materializer). Empty = proxy only,
        /// no voxel planet.</summary>
        public string ParkSubtype = "";

        /// <summary>Explicit parked "true position" of the voxel planet, as three doubles.
        /// Used only when <see cref="HasParkPosition"/> is true. This is a deliberate,
        /// typed field — NOT a hijacked identity string (RSS#5).</summary>
        public bool HasParkPosition = false;
        public double ParkPositionX = 0.0;
        public double ParkPositionY = 0.0;
        public double ParkPositionZ = 0.0;

        // ---- visual hints (kept minimal; optional) ----------------------------

        /// <summary>Proxy color as 0xRRGGBB packed int (-1 = unset).</summary>
        public int ProxyColorRgb = -1;

        /// <summary>Optional proxy texture / material name hint. When set it overrides
        /// <see cref="ParkSubtype"/> as the skin base name (the proxy renderer applies
        /// Textures\Planets\PlanetProxy_&lt;name&gt;_cm/ng.dds), so a body with no voxel —
        /// or a voxel of a different subtype — can still carry a specific proxy skin.</summary>
        public string ProxyTexture = "";

        // ---- proxy visual config (RSS PlanetProxyInfo equivalents; optional) ---
        // Per-body sky-proxy visuals consumed by ProxyRenderer. Defaults match the RSS
        // class/sbc defaults so an existing system definition gets sane visuals (Earth-like
        // blue atmosphere when HasAtmosphere, no clouds) without any edits.

        /// <summary>Authoring scale multiplier on the proxy sphere (RSS "Scale"; the model
        /// is additionally oversized x1.025 at render time to skin over the voxel seam).</summary>
        public double ProxyScale = 1.0;

        /// <summary>Bottom of the anchor body's surface-fade scale ramp (RSS "scaleFadeMin"),
        /// (0, 1]. The renderer draws scale = pow(mapRange(fadeFrac, 0, 1, value, 1), 0.2), so
        /// the DRAWN scale at the bottom of the band is value^0.2 — NOT the value itself: the
        /// default 0.7 gives a bottom scale of ~0.93, ramping to 1 at the top of the band.
        /// PORTING CAVEAT (RSS configs): RSS silently pow-5's AUTHORED config values before its
        /// own ^0.2 ramp (authored 0.7 => drawn bottom 0.70), while its field DEFAULT 0.7
        /// bypasses that re-shaping (drawn bottom ~0.93 — identical to our default). So
        /// default-vs-default we match RSS exactly, but a value COPIED from an RSS config will
        /// fade shallower here; to reproduce an RSS-authored value v, author v^5.</summary>
        public double ProxyScaleFadeMin = 0.7;

        /// <summary>Where the anchor body's proxy hides/fades over its materialized voxel,
        /// [0, 1] (RSS "PlanetProxyFadeoutHeightMult"): 0 maps the fadeout threshold to
        /// RadiusMeters * 1.05 (proxy visible from just above the surface), 1 maps it to
        /// max(RadiusMeters * 1.05 + 100, ShellRadius / 1.3) (proxy only well up the
        /// interaction shell). The fade band is [threshold, min(threshold * 1.3, ShellRadius)] —
        /// the renderer clamps both ends to the handoff shell so the fade always completes
        /// while the voxel still exists (it persists to ShellRadius x the keep-hysteresis).</summary>
        public double ProxyFadeoutHeightMult = 0.0;

        // ---- cloud layer (optional; empty CloudTexture = no cloud entity) -----

        /// <summary>Mod-relative cloud texture path (e.g. Textures\Clouds\CloudProxy_EarthLike.dds)
        /// applied to the PlanetProxy_Cloud.mwm shell. Empty = this body has no cloud layer.</summary>
        public string CloudTexture = "";

        /// <summary>Cloud-drift rotation axis (unit-ish, body-local on top of the spin
        /// orientation). RSS default (-0.2, 1, 0.2) gives a slightly tilted drift.</summary>
        public double CloudUpX = -0.2;
        public double CloudUpY = 1.0;
        public double CloudUpZ = 0.2;

        /// <summary>Cloud shell oversize knob: shell radius = proxy radius * (1 + 0.06 * this).</summary>
        public double CloudSizeMult = 1.0;

        /// <summary>Cloud layer's own rotation period, seconds — a slow drift independent
        /// of the body's spin (RSS default 7200).</summary>
        public double CloudRotationPeriodSeconds = 7200.0;

        /// <summary>Whether the ANCHOR body's cloud shell stays visible over its materialized
        /// voxel (following the proxy's surface fade). Non-anchor proxies always show clouds.</summary>
        public bool ShowCloudInOrbit = false;

        // ---- atmosphere arc (drawn when HasAtmosphere; RSS atmo* equivalents) --

        /// <summary>Atmosphere arc tint as 0xRRGGBB packed int. Default 0x3380FF is the RSS
        /// Earth-like blue (0.2, 0.5, 1.0).</summary>
        public int AtmoColorRgb = 0x3380FF;

        /// <summary>Atmosphere arc intensity multiplier (RSS EarthLike default 2.2).</summary>
        public double AtmoColorMult = 2.2;

        /// <summary>Atmosphere arc thickness multiplier (RSS EarthLike default 1.03).</summary>
        public double AtmoThicknessMult = 1.03;

        /// <summary>Extra intensity multiplier while the observer is AT this body (the
        /// anchor) — dims the arc seen from inside (RSS default 0.5).</summary>
        public double AtmoInZoneMult = 0.5;

        public BodyDefinition() { }

        // ---- derived helpers (no game dependency) -----------------------------

        public bool IsRoot
        {
            get { return string.IsNullOrEmpty(Parent); }
        }

        /// <summary>Gravitational parameter mu = g_surface * R^2, m^3/s^2. The single,
        /// consistent value for rails, wells, and HUD — fixes RSS#2.</summary>
        public double ComputeMu()
        {
            return SurfaceGravityMps2 * RadiusMeters * RadiusMeters;
        }

        /// <summary>Basic structural validity of this body in isolation (parent linkage
        /// and cycle checks are done by the builder across the whole set).</summary>
        public bool TryValidate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(Name)) { error = "Body has empty Name."; return false; }
            if (RadiusMeters <= 0.0) { error = "Body '" + Name + "' has non-positive RadiusMeters."; return false; }
            if (SurfaceGravityMps2 <= 0.0) { error = "Body '" + Name + "' has non-positive SurfaceGravityMps2."; return false; }
            if (HasOrbit && !IsRoot)
            {
                if (SemiMajorAxisMeters <= 0.0) { error = "Orbiting body '" + Name + "' has non-positive SemiMajorAxisMeters."; return false; }
                if (Eccentricity < 0.0 || Eccentricity >= 1.0) { error = "Orbiting body '" + Name + "' has eccentricity outside [0,1)."; return false; }
                if (InclinationDeg < 0.0 || InclinationDeg > 180.0) { error = "Orbiting body '" + Name + "' has inclination outside [0,180]."; return false; }
            }
            return true;
        }
    }
}
