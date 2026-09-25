using System;
using System.Collections.Generic;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

namespace SEAerospace
{
    /// <summary>
    /// An observer's current rendering frame: the world cell their sky is centered on, the celestial
    /// position that cell corresponds to, and — as a FRAME DISCRIMINATOR only — the body the frame
    /// belongs to. <see cref="SpinAnchor"/> is set for a body frame (resolved from a materialized
    /// voxel anchor OR from cell containment during a transition) and null for a conjunction / home
    /// frame. NOTHING rotates the sky by it anymore (day/night comes from the snapshot's
    /// GameDateTime sun bake, not a sky wheel); it exists so consumers can tell WHICH body's frame
    /// they are in (e.g. the sun drive picks its day/night source from it). Pure data — game-free.
    ///
    /// <see cref="FrameCelVel"/> is d/dt of <see cref="FrameCel"/> — the celestial velocity the
    /// 1:1 inertial window RIDES at (a planet/cell frame rides its body's root-frame velocity; a
    /// conjunction frame rides parent-body velocity + its virtual orbit's velocity). It is what
    /// turns a world-measured velocity into a celestial one (<see cref="PlanetBerths.BodyRelativeState"/>):
    /// positions map 1:1 through the cell, but velocities pick up the window's own motion. Every
    /// resolve site fills it ANALYTICALLY from the ephemeris chain (StateVector carries velocity
    /// up the parent chain) — never finite-differenced. The legacy 3-arg constructor leaves it
    /// zero, which is correct only for placement-only consumers (proxy/sun/sky positioning never
    /// reads it).
    ///
    /// THE ROTATING SURFACE CHART (2026-06-12, docs/rotating-anchored-frames.md as-built):
    /// inside the SURFACE ZONE — r &lt; R_c = <see cref="PlanetBerths.ShellRadius"/> of the
    /// cell body — a planet cell's window is the BODY-FIXED ROTATING chart of its body:
    ///
    ///   cel(p)   = FrameCel + R(θ)·(p − c)                      c = CellCenter, R about SpinAxis
    ///   v_cel(p) = FrameCelVel + R·v_world + ω × (R·(p − c))    θ = RotationAngleAt(t), ω = 2π/T_sid
    ///
    /// Outside R_c the window stays the 1:1 inertial chart exactly as before (R = I). The four
    /// spin fields below carry the chart: <see cref="SpinActive"/> is the per-SUBJECT flag (the
    /// camera/grid the frame was resolved FOR is inside the zone — filled by the resolves from
    /// the stateless radius test, overridden by the owner's hysteresis latch via
    /// <see cref="PlanetBerths.SetWindowSpinActive"/>); axis/theta/omega are filled CELL-WIDE
    /// whenever the window is a planet-cell window with a spinning body, so a latch owner in the
    /// hysteresis band can flip the flag without re-resolving. When inactive every consumer is
    /// byte-identical to the pre-chart behavior (R = I in all the law helpers).
    /// </summary>
    public struct ObserverFrame
    {
        public Vector3D CellCenter;     // myCellWorldPos — the world cell the observer sits in
        public Vector3D FrameCel;       // myFrame.celestial — the cell's celestial (root) position
        public Vector3D FrameCelVel;    // d/dt FrameCel — the celestial velocity the window rides at
        public GravityBody SpinAnchor;  // the frame's body (discriminator only); null = conjunction/home

        // ---- the rotating surface chart (identity when SpinActive is false) ----
        public bool SpinActive;         // the resolve subject is inside the cell body's surface zone
        public Vector3D SpinAxisDir;    // unit spin axis (world axes == celestial axes); zero when unfilled
        public double SpinTheta;        // R's rotation angle at the resolve time t, radians
        public double SpinOmega;        // scalar spin rate 2π/T_sid (rad/s); vector ω = SpinAxisDir·SpinOmega

        /// <summary>Placement-only frame (FrameCelVel zero). Use the 4-arg overload wherever a
        /// consumer may convert velocities through the frame.</summary>
        public ObserverFrame(Vector3D cellCenter, Vector3D frameCel, GravityBody spinAnchor)
        {
            CellCenter = cellCenter;
            FrameCel = frameCel;
            FrameCelVel = Vector3D.Zero;
            SpinAnchor = spinAnchor;
            SpinActive = false;
            SpinAxisDir = Vector3D.Zero;
            SpinTheta = 0.0;
            SpinOmega = 0.0;
        }

        public ObserverFrame(Vector3D cellCenter, Vector3D frameCel, Vector3D frameCelVel,
            GravityBody spinAnchor)
        {
            CellCenter = cellCenter;
            FrameCel = frameCel;
            FrameCelVel = frameCelVel;
            SpinAnchor = spinAnchor;
            SpinActive = false;
            SpinAxisDir = Vector3D.Zero;
            SpinTheta = 0.0;
            SpinOmega = 0.0;
        }
    }

    /// <summary>Where (and how big) to draw a sky proxy this frame: a world render position and a
    /// render radius (meters), plus the body's TRUE distance for fade/LOD decisions. Pure data.</summary>
    public struct ProxyPlacement
    {
        public Vector3D RenderPos;     // world position to draw the proxy sphere at (camera-relative)
        public double RenderRadius;    // proxy sphere radius (m) — subtends the body's true angular size
        public double TrueDistance;    // the body's real celestial distance from the observer (m)

        public ProxyPlacement(Vector3D renderPos, double renderRadius, double trueDistance)
        {
            RenderPos = renderPos;
            RenderRadius = renderRadius;
            TrueDistance = trueDistance;
        }
    }

    /// <summary>
    /// The fixed 1:1 world layout of the celestial system, packed near the origin. Every body —
    /// whether materialized as a real voxel or drawn as a proxy — lives at a STABLE world
    /// position here, so there are no teleports: you fly between bodies through this layout, and
    /// a body materializes when you enter its shell / dematerializes (back to a proxy) when you
    /// leave. The layout is centered on the first voxel body so it sits at the berth near origin
    /// (keeps Havok precision good for the test scale).
    ///
    /// (Real astronomical scale would blow past Havok precision; that needs per-frame
    /// re-anchoring. This fixed layout is correct for the compact test system.)
    /// </summary>
    public static class PlanetBerths
    {
        /// <summary>World center the first voxel body sits at (and the lattice is built around).
        /// Close enough to the origin that the player spawns INSIDE the first body's shell, and the
        /// shared berth lattice (planets + Conjunctions) is centered here so slot 0 = the home body.</summary>
        public static readonly Vector3D CurrentBerth = new Vector3D(0.0, 0.0, -40.0e3);

        /// <summary>Atmosphere clearance multiplier for the handoff shell: an atmospheric body's
        /// shell sits at RadiusMeters + this × AtmosphereHeightMeters, i.e. SLIGHTLY ABOVE the
        /// atmosphere — you fly toward the planet seeing its full-quality proxy almost all the
        /// way in, and the real voxel materializes only when you're about to enter the
        /// atmosphere (in-game retune, 2026-06-11; the old shell was the whole planet envelope).</summary>
        public const double ShellAtmosphereMult = 2.0;

        /// <summary>Airless-body shell clearance as a fraction of the radius — a STAND-IN for
        /// ~2× a typical voxel hill height. <see cref="BodyDefinition"/> carries no hill data
        /// (the generator's HillParams are game-side only), and stock generators crest at roughly
        /// 3–12% of the radius, so 0.12 R clears about double a typical crest. Documented
        /// approximation; revisit if a definition ever grows real hill data.</summary>
        public const double ShellAirlessClearanceFraction = 0.12;

        /// <summary>Absolute floor (m) on the shell clearance. The materializer's /goto drops the
        /// player at RadiusMeters + 3 km, so the floor must keep that arrival point inside the
        /// STRICT shell (not merely inside the ×1.5 leave-hysteresis) — 4 km covers it with
        /// margin and gives tiny test bodies a sane minimum approach envelope.</summary>
        public const double ShellMinClearanceMeters = 4.0e3;

        // =====================================================================================
        //  THE CONVERSION RADIUS — the ONE per-body radius every frame-conversion band derives
        //  from (consolidation, 2026-06-12; the bands had sprawled across FrameManager /
        //  PlanetTransition / CelestialScene with private multipliers — this block is now the
        //  single source of truth and the only place a band multiplier may be defined).
        //
        //    R_c = ShellRadius(def)                        — the conversion radius itself
        //
        //    band                      radius          const                   consumers
        //    ------------------------- --------------- ----------------------- -------------------
        //    surface-zone boundary     enter R_c,      SurfaceZoneDropPad      the body-fixed
        //    (rotating chart)          leave R_c x1.01                          rotating chart's
        //                                                                       membership latch
        //                                                                       (FrameManager zone
        //                                                                       seam; the snapshot's
        //                                                                       observer latch)
        //    arrival release           R_c             (none — R_c itself)     FrameManager
        //                                                                       AutoMaterializeFrame
        //                                                                       (the shell-entry /
        //                                                                       true-crossing release)
        //    outbound stow floor       R_c x 1.05      StowShellMargin         FrameManager
        //                                                                       AutoStowGrid
        //    keep / anchor / demat     R_c x 1.5       ShellKeepHysteresis     PlanetTransition keep,
        //                              (KeepRadius)                             CelestialScene anchor,
        //                                                                       SpinAnchor discriminator,
        //                                                                       FrameManager keep tests
        //    anti-flap drop edge       keep x 1.02     KeepDropPad             the LATCHED keep
        //                              (KeepDropRadius)                         consumers only (voxel
        //                                                                       demat + render anchor):
        //                                                                       DEMAND < keep,
        //                                                                       DROP > keep x 1.02
        //
        //  Worked example (test fixture): Earth R 30 km, atmo 6 km -> R_c = 42 km, stow floor
        //  44.1 km, keep 63 km, drop 64.26 km. Moon R 9.4 km (airless, 4 km floor) -> R_c =
        //  13.4 km, stow floor 14.07 km, keep 20.1 km, drop 20.502 km.
        //
        //  There is deliberately NO other multiplier: the old PlanetTransition/FrameSim extra
        //  x1.1 demat pad (Earth 69.3 km) is GONE — its anti-flap job is the single small
        //  KeepDropPad above (demat moved 69.3 -> 64.26 km).
        //
        //  NOT conversion radii (VISIBILITY/lattice quantities — they size where things are
        //  drawn/spaced, never when a grid changes realm): the lattice slot radius (150 km test
        //  fixture) and cell spacing derive from CellIsolationRadius below (view range +
        //  worst-case terrain); the ProxyRenderer fade band is [1.05 R, min(1.365 R, R_c)] —
        //  fractions of the body radius clamped BY R_c, defined in BodyDefinition/ProxyRenderer.
        // =====================================================================================

        /// <summary>Leave/keep hysteresis on the conversion radius: keep = R_c × this — the ONE
        /// keep/anchor/demat band (see the conversion-radius table above). SHARED by the
        /// materializer's keep + pre-warm tests (<see cref="PlanetTransition"/>), the
        /// render-anchor test (<see cref="CelestialScene.CurrentBody"/>) and the SpinAnchor
        /// discriminator below, so they can never disagree: entry is at <see cref="ShellRadius"/>,
        /// and the voxel (and the observer's anchor on it) persists out to the keep.</summary>
        public const double ShellKeepHysteresis = 1.5;

        /// <summary>The single anti-flap pad on the keep boundary: a LATCHED keep consumer
        /// (the materialized voxel and the render anchor riding it) DEMANDS below
        /// <see cref="KeepRadius"/> and DROPS only above <see cref="KeepDropRadius"/> =
        /// keep × this, so a grid hovering exactly at the keep cannot flap materialize/demat.
        /// Stateless (re-evaluated-fresh) keep tests use the plain keep. This pad REPLACES the
        /// old ×1.1 demat band (2026-06-12 consolidation; Earth demat 69.3 → 64.26 km).</summary>
        public const double KeepDropPad = 1.02;

        /// <summary>Outbound stow floor: a departing grid may stow inside the keep but never
        /// below R_c × this — below it the VoxelFrame owns descent/landing/surface play
        /// (<c>FrameManager.AutoStowGrid</c>'s departure-imminent gate). The margin also keeps
        /// the stow swap ABOVE the proxy surface-fade band (fade top ≤ min(1.365 R, R_c) &lt;
        /// 1.05 × R_c), so the proxy the player sees at the swap is already full-scale.</summary>
        public const double StowShellMargin = 1.05;

        /// <summary>SURFACE-ZONE hysteresis pad (the rotating-chart membership latch): a grid /
        /// observer ENTERS the body-fixed rotating chart when its distance to the cell center
        /// drops below R_c, and LEAVES it (the outbound conversion fires) only above
        /// R_c × this — so a grid hovering exactly at the boundary cannot flap convert. ~1%:
        /// well inside the stow floor (×1.05), so a latched-inside grid can never reach a stow
        /// capture (the stow seam always sees the inertial window). The band [R_c, 1.01×R_c]
        /// is bistable BY DESIGN; whichever chart the latch says the coordinates are in IS the
        /// correct interpretation (the conversion is exact at any radius — R_c is the trigger,
        /// not a validity limit). See <see cref="ZoneLatch"/>.</summary>
        public const double SurfaceZoneDropPad = 1.01;

        /// <summary>The keep/anchor/demat radius: R_c × <see cref="ShellKeepHysteresis"/>
        /// (Earth 63 km, Moon 20.1 km in the test fixture). 0 for a degenerate def.</summary>
        public static double KeepRadius(BodyDefinition def)
        {
            return ShellRadius(def) * ShellKeepHysteresis;
        }

        /// <summary>The latched keep consumers' drop edge: keep × <see cref="KeepDropPad"/>
        /// (Earth 64.26 km, Moon 20.502 km). See KeepDropPad for the demand/drop contract.</summary>
        public static double KeepDropRadius(BodyDefinition def)
        {
            return KeepRadius(def) * KeepDropPad;
        }

        /// <summary>
        /// THE CONVERSION RADIUS R_c for a body (historically "R_shell") — the VOXEL HANDOFF
        /// radius BOTH the VoxelFrame owner (<see cref="PlanetTransition"/>,
        /// materialize/dematerialize) and the ProximityFrame owner (<c>FrameManager</c>,
        /// auto-stow / auto-materialize) test against, so they can never disagree about which
        /// realm a grid belongs in; EVERY conversion band is a documented multiple of it (see
        /// the table above). Sits SLIGHTLY ABOVE THE ATMOSPHERE (in-game decision 2026-06-11 —
        /// the old whole-planet-envelope shell materialized far too early; the full-quality
        /// proxy is the view almost all the way in):
        ///   R_c = RadiusMeters + clearance,
        ///   clearance = max( ShellAtmosphereMult × AtmosphereHeightMeters  [atmospheric bodies],
        ///                    ShellAirlessClearanceFraction × RadiusMeters  [~2× hill stand-in],
        ///                    ShellMinClearanceMeters ).
        /// Below R_c terrain/atmosphere can be touched → must be a materialized voxel; above
        /// it neither is possible → rails are always safe. NOTE: this is the CONVERSION quantity
        /// only — lattice/berth spacing keys off <see cref="CellIsolationRadius"/>, which
        /// deliberately did NOT shrink with the shell (see there). Returns 0 for a
        /// null/degenerate def (no shell → never inside).
        /// </summary>
        public static double ShellRadius(BodyDefinition def)
        {
            if (def == null || def.RadiusMeters <= 0.0) return 0.0;
            double clearance = ShellAirlessClearanceFraction * def.RadiusMeters;
            if (def.HasAtmosphere && ShellAtmosphereMult * def.AtmosphereHeightMeters > clearance)
                clearance = ShellAtmosphereMult * def.AtmosphereHeightMeters;
            if (clearance < ShellMinClearanceMeters) clearance = ShellMinClearanceMeters;
            return def.RadiusMeters + clearance;
        }

        /// <summary>How many body radii out the cell ISOLATION envelope extends — the worst-case
        /// terrain/streaming margin used to SIZE lattice cells (<see cref="CellIsolationRadius"/>),
        /// NOT the handoff shell.</summary>
        public const double CellIsolationMult = 2.0;

        /// <summary>Visibility pad (meters) on the isolation envelope: a NEIGHBOR cell's voxel
        /// must sit beyond grid sync/visibility range (~tens of km), so cell spacing keeps 50 km
        /// of margin on top of the geometric envelope (docs §"The interaction shell").</summary>
        public const double CellIsolationVisibilityPad = 50.0e3;

        /// <summary>
        /// The per-body ISOLATION radius the shared berth lattice is SIZED from
        /// (<see cref="VoxelBerthRegistry"/>: cell half-extent = this + one more radius of gravity
        /// margin). A VISIBILITY quantity, explicitly NOT a conversion radius (see the
        /// conversion-radius table above): it — and the lattice slot radius / cell spacing
        /// derived from it (150 km / 400 km in the test fixture) — sizes where things are SPACED
        /// AND DRAWN, never when a grid changes realm. This is the OLD interaction-shell formula,
        /// kept INDEPENDENT of <see cref="ShellRadius"/> on purpose: when the handoff shell
        /// shrank to "slightly above the atmosphere" (2026-06-11) the spacing guarantee — a
        /// neighbor cell's voxel is beyond view/sync range and two materialized planets never
        /// pull on or stream into each other — did not get any weaker, because it depends on
        /// view range and worst-case terrain extent, not on WHERE the materialize handoff
        /// happens. Spacing and handoff are independent quantities; sizing cells from the slim
        /// shell would silently shrink the lattice.
        ///   isolation = max( RadiusMeters * CellIsolationMult [max-hill/terrain margin],
        ///                    RadiusMeters + AtmosphereHeightMeters [atmosphere top] )
        ///                    + CellIsolationVisibilityPad.
        /// Always ≥ ShellRadius for any sane definition, so the handoff shell and its
        /// <see cref="ShellKeepHysteresis"/> envelope always fit inside the cell. Returns 0 for a
        /// null/degenerate def. The atmosphere term only applies when the body HasAtmosphere.
        /// </summary>
        public static double CellIsolationRadius(BodyDefinition def)
        {
            if (def == null || def.RadiusMeters <= 0.0) return 0.0;
            double terrain = def.RadiusMeters * CellIsolationMult;
            double atmoTop = def.HasAtmosphere
                ? def.RadiusMeters + def.AtmosphereHeightMeters
                : 0.0;
            double geom = terrain > atmoTop ? terrain : atmoTop;
            return geom + CellIsolationVisibilityPad;
        }

        // =====================================================================================
        //  THE ROTATING SURFACE CHART (2026-06-12, docs/rotating-anchored-frames.md as-built).
        //  Inside the surface zone (r < R_c of a planet cell's body) the window is the
        //  BODY-FIXED rotating chart; outside it stays the 1:1 inertial window. All the chart
        //  math lives in the pure helpers below (game-free, shared by the mod seams, FrameSim
        //  and Orbital.Tests):
        //
        //    forward law  (window -> celestial):
        //       cel    = F + R(θ)·(p − c)                       ObserverCelestial
        //       celVel = Fv + R·v_w + ω×(R·(p − c))             ObserverCelestialVelocity
        //    inverse law  (celestial -> window):
        //       p   = c + R⁻¹·(cel − F)                         WorldFromCelestial
        //       v_w = R⁻¹·(celVel − Fv − ω×(cel − F))           WorldVelFromCelestial
        //    zone-boundary conversion (between the two charts of the SAME cell — a seam
        //    teleport; celestial state continuous by construction):
        //       INBOUND  (inertial -> rotating): p' = c + R⁻¹·(p − c)
        //                                        v' = R⁻¹·(v − ω×(p − c))      Q' = R⁻¹·Q
        //       OUTBOUND (rotating -> inertial): p  = c + R·(p' − c)
        //                                        v  = R·v' + ω×(p − c)         Q  = R·Q'
        //    (ω ∥ axis commutes with R about the axis: ω×(R·x) = R·(ω×x), so both forms of the
        //    Coriolis-free transport term are identical; outbound∘inbound = identity exactly.)
        //
        //  Membership is a per-subject hysteresis LATCH (enter < R_c, leave > R_c ×
        //  SurfaceZoneDropPad — ZoneLatch): the conversion fires when the latch flips, and the
        //  latch owner reconciles the resolve's stateless SpinActive via SetWindowSpinActive so
        //  interpretation always matches the coordinates, including in the bistable band.
        // =====================================================================================

        /// <summary>The surface-zone membership hysteresis (pure): given the previous latched
        /// state, the subject's distance to the cell center, and the body's R_c, the new latched
        /// state. Enter strictly below R_c; leave only above R_c × <see cref="SurfaceZoneDropPad"/>.
        /// Initialization (no history): pass wasInside = false — a subject first seen in the
        /// band counts as outside (documented corner: a save made in the ~1% band while latched
        /// inside re-loads as outside, a one-time relabel; see the design doc).</summary>
        public static bool ZoneLatch(bool wasInside, double distance, double shellRadius)
        {
            if (shellRadius <= 0.0 || double.IsNaN(distance)) return false;
            return wasInside ? distance <= shellRadius * SurfaceZoneDropPad : distance < shellRadius;
        }

        // ---- SEAM-COHERENCE HARDENING (steal audit 2026-06-12 S4 — RSS ZoneManager.cs).
        // Pure predicates for the FrameManager zone seam, kept here so the offline twins
        // (FrameSim / Orbital.Tests) exercise the LITERAL shipped rules.

        /// <summary>TELEPORT-DETECTOR threshold (RSS ZoneManager.cs:466 — their constant is the
        /// squared form 25 000 000 m² = (5 km)²): a per-tick move this far beyond what the
        /// entity's TRUE velocity explains is an unexplained jump (jump drive, admin teleport),
        /// so the zone latch is stale and must be re-derived statelessly.</summary>
        public const double ZoneTeleportThreshold = 5000.0;

        /// <summary>COMPANION-SWEEP range (RSS ZoneManager.cs:513 — their 2000 f sphere): when a
        /// tracked entity's latch flips, every other zone-tracked entity within this range of
        /// the crossing converts in the SAME tick with the SAME θ sample, so a formation never
        /// scrambles because its members crossed R_c ticks apart.</summary>
        public const double ZoneCompanionSweepRange = 2000.0;

        /// <summary>The unexplained-jump test (RSS ZoneManager.cs:466): TRUE per-check
        /// displacement minus the velocity-explained displacement exceeds
        /// <see cref="ZoneTeleportThreshold"/>. <paramref name="trueVel"/> must be the TRUE
        /// velocity (HighSpeedFlight's virtual velocity while engaged — a stepped grid moves
        /// v·dt per tick by OUR OWN teleports, an explained move), which is how rails/HighSpeed
        /// stepping is exempted; the seam conversions exempt themselves by re-seeding lastPos
        /// after every conversion (the GridTeleport external-jump-guard pattern). Non-finite
        /// input never trips (a NaN comparison is false) — the caller's finite guards own it.</summary>
        public static bool UnexplainedJump(Vector3D pos, Vector3D lastPos, Vector3D trueVel, double dt)
        {
            return (pos - lastPos - trueVel * dt).LengthSquared()
                > ZoneTeleportThreshold * ZoneTeleportThreshold;
        }

        /// <summary>The detector's stateless RE-LATCH with RSS's AMBIGUITY-BAND skip
        /// (ZoneManager.cs:682-714 — their isAmbiguousZone gate, applied to the re-latch path
        /// ONLY, exactly as the audit scoped it): a re-derive landing inside the bistable band
        /// [R_c, R_c × <see cref="SurfaceZoneDropPad"/>] KEEPS the stale latch — both charts
        /// are valid there (the band is bistable by design), and re-deriving "outside" for a
        /// latched-inside subject at R_c + ε would convert on a coin-flip radius. Outside the
        /// band the stateless radius test wins. The continuous-crossing path keeps the plain
        /// <see cref="ZoneLatch"/> hysteresis — it cannot flap by construction and needs no
        /// band skip.</summary>
        public static bool ReLatchAfterJump(bool wasInside, double distance, double shellRadius)
        {
            if (shellRadius <= 0.0 || double.IsNaN(distance)) return false;
            if (distance >= shellRadius && distance <= shellRadius * SurfaceZoneDropPad)
                return wasInside;   // ambiguous band: trust the latch, never convert here
            return distance < shellRadius;
        }

        /// <summary>COMPANION-SWEEP stability guard (ours — a documented DEVIATION from RSS's
        /// unconditional sweep, ZoneManager.cs:513-522): only sweep a companion whose flipped
        /// latch the <see cref="ZoneLatch"/> hysteresis will KEEP next tick — inbound requires
        /// d ≤ R_c × <see cref="SurfaceZoneDropPad"/> (above it the leave edge fires
        /// immediately), outbound requires d ≥ R_c (below it the enter edge fires immediately).
        /// RSS doesn't need this: its hysteresis bands are percent-of-zone-radius wide (their
        /// rangeMod 1.01/1.02 rides PlanetOrbitZoneRadius, km-scale), so a 2 km sweep always
        /// lands inside them; our band is 1% of R_c (~420 m on the test Earth) — narrower than
        /// the sweep range — so an unconditionally swept radial straggler would flap-convert
        /// right back next tick. A guarded-out straggler converts on its own crossing exactly
        /// as before (exact at any radius; only the same-θ guarantee is lost for it).</summary>
        public static bool CompanionSweepKeeps(bool inbound, double distance, double shellRadius)
        {
            if (shellRadius <= 0.0 || double.IsNaN(distance)) return false;
            return inbound ? distance <= shellRadius * SurfaceZoneDropPad : distance >= shellRadius;
        }

        /// <summary>The body's spin data for the chart: unit axis + scalar rate ω = 2π/T_sid.
        /// False (zeroes) when the body has no specified spin — no chart, the window stays
        /// inertial everywhere.</summary>
        public static bool TryBodySpin(GravityBody body, out Vector3D axisUnit, out double omega)
        {
            axisUnit = Vector3D.Zero;
            omega = 0.0;
            if (body == null || body.RotationPeriodSeconds <= 0.0) return false;
            Vector3D a = body.SpinAxis;
            if (a.LengthSquared() < 1e-12) a = Vector3D.UnitZ; else a.Normalize();
            axisUnit = a;
            omega = 2.0 * Math.PI / body.RotationPeriodSeconds;
            return true;
        }

        /// <summary>Rodrigues rotation of <paramref name="v"/> by <paramref name="angle"/>
        /// (right-handed) about the UNIT axis — the one rotation primitive every law helper
        /// shares (kept matrix-free so the offline twins never depend on MatrixD conventions).</summary>
        public static Vector3D RotateAboutAxis(Vector3D v, Vector3D axisUnit, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + Vector3D.Cross(axisUnit, v) * s + axisUnit * (Vector3D.Dot(axisUnit, v) * (1.0 - c));
        }

        /// <summary>Fill a planet-cell window's spin fields from its cell body: axis/θ/ω
        /// cell-wide (whenever the body spins), the per-subject <see cref="ObserverFrame.SpinActive"/>
        /// flag from the STATELESS radius test |subjectPos − c| &lt; R_c. Latch owners (the
        /// FrameManager zone seam for grids, the scene snapshot for the camera) override the
        /// flag with their hysteresis state via <see cref="SetWindowSpinActive"/>.</summary>
        public static void FillWindowSpin(ref ObserverFrame frame, GravityBody body,
            BodyDefinition def, double t, Vector3D subjectPos)
        {
            Vector3D axis;
            double omega;
            if (!TryBodySpin(body, out axis, out omega)) return;
            frame.SpinAxisDir = axis;
            frame.SpinOmega = omega;
            frame.SpinTheta = body.RotationAngleAt(t);
            double rc = ShellRadius(def);
            frame.SpinActive = rc > 0.0
                && Vector3D.DistanceSquared(subjectPos, frame.CellCenter) < rc * rc;
        }

        /// <summary>Latch-owner override of the per-subject chart flag (the spin DATA stays;
        /// only the active flag flips). No-op activation on a window with no spin data.</summary>
        public static void SetWindowSpinActive(ref ObserverFrame frame, bool active)
        {
            frame.SpinActive = active && frame.SpinOmega > 0.0;
        }

        /// <summary>R(θ)·v for an active chart; identity otherwise (window -> celestial axes).</summary>
        public static Vector3D SpinToCelestial(ObserverFrame frame, Vector3D v)
        {
            return frame.SpinActive ? RotateAboutAxis(v, frame.SpinAxisDir, frame.SpinTheta) : v;
        }

        /// <summary>R(θ)⁻¹·v for an active chart; identity otherwise (celestial -> window axes).</summary>
        public static Vector3D SpinFromCelestial(ObserverFrame frame, Vector3D v)
        {
            return frame.SpinActive ? RotateAboutAxis(v, frame.SpinAxisDir, -frame.SpinTheta) : v;
        }

        /// <summary>The chart velocity law (forward): the celestial (root) velocity of a point
        /// measured at <paramref name="worldVel"/> in the window. Inertial window:
        /// v + FrameCelVel (today's law, byte-identical). Rotating chart:
        /// FrameCelVel + R·v + ω×(R·(p − c)) — a world-PARKED point co-rotates, so its captured
        /// celestial velocity carries the ω×r launch credit BY CONSTRUCTION.</summary>
        public static Vector3D ObserverCelestialVelocity(ObserverFrame frame, Vector3D worldPos,
            Vector3D worldVel)
        {
            if (!frame.SpinActive) return worldVel + frame.FrameCelVel;
            Vector3D rotOff = RotateAboutAxis(worldPos - frame.CellCenter, frame.SpinAxisDir, frame.SpinTheta);
            return frame.FrameCelVel
                 + RotateAboutAxis(worldVel, frame.SpinAxisDir, frame.SpinTheta)
                 + Vector3D.Cross(frame.SpinAxisDir * frame.SpinOmega, rotOff);
        }

        /// <summary>The inverse position law: the window/world position of a celestial point
        /// (c + R⁻¹·(cel − F); 1:1 when inertial). forward∘inverse = identity exactly.</summary>
        public static Vector3D WorldFromCelestial(ObserverFrame frame, Vector3D cel)
        {
            return frame.CellCenter + SpinFromCelestial(frame, cel - frame.FrameCel);
        }

        /// <summary>The inverse velocity law: the window/world velocity that, at celestial
        /// position <paramref name="cel"/>, has celestial velocity <paramref name="celVel"/>:
        /// R⁻¹·(celVel − Fv − ω×(cel − F)); celVel − Fv when inertial.</summary>
        public static Vector3D WorldVelFromCelestial(ObserverFrame frame, Vector3D cel, Vector3D celVel)
        {
            Vector3D rel = celVel - frame.FrameCelVel;
            if (!frame.SpinActive) return rel;
            rel -= Vector3D.Cross(frame.SpinAxisDir * frame.SpinOmega, cel - frame.FrameCel);
            return RotateAboutAxis(rel, frame.SpinAxisDir, -frame.SpinTheta);
        }

        /// <summary>The INBOUND zone-boundary conversion (inertial window -> rotating chart),
        /// applied to a state's position/velocity when its membership latch flips inside:
        /// p' = c + R⁻¹·(p − c), v' = R⁻¹·(v − ω×(p − c)). Pure; the caller owns the
        /// orientation half (Q' = R⁻¹·Q) and the latch.</summary>
        public static void ConvertIntoZone(Vector3D axisUnit, double theta, double omega,
            Vector3D center, ref Vector3D pos, ref Vector3D vel)
        {
            Vector3D off = pos - center;
            vel = RotateAboutAxis(vel - Vector3D.Cross(axisUnit * omega, off), axisUnit, -theta);
            pos = center + RotateAboutAxis(off, axisUnit, -theta);
        }

        /// <summary>The OUTBOUND zone-boundary conversion (rotating chart -> inertial window):
        /// p = c + R·(p' − c), v = R·v' + ω×(p − c). Exact inverse of
        /// <see cref="ConvertIntoZone"/> (round-trip = identity).</summary>
        public static void ConvertOutOfZone(Vector3D axisUnit, double theta, double omega,
            Vector3D center, ref Vector3D pos, ref Vector3D vel)
        {
            Vector3D off = RotateAboutAxis(pos - center, axisUnit, theta);
            pos = center + off;
            vel = RotateAboutAxis(vel, axisUnit, theta) + Vector3D.Cross(axisUnit * omega, off);
        }

        // =====================================================================================
        //  THE ONE MATRIX-SPACE CONVERSION PRIMITIVE (steal audit 2026-06-12 S1 — RSS's
        //  ConvertMatrixSpace/ConvertProxyMatToReal, reference ZoneManager.cs:1209-1235).
        //  RSS routes EVERY placed world matrix (proxies, clouds, particles, GPS, the entity
        //  seam itself) through ONE mat conversion: translate −center, multiply by the chart
        //  rotation — a matrix times a pure rotation rotates its ORIENTATION rows and its
        //  RELATIVE TRANSLATION row together — then translate +newCenter. Ours is the same
        //  shape with scale 1 (no proxy-space miniature, audit S6): the chart rotation is
        //  R(−θ) about the window's spin axis — SpinFromCelestial as a matrix — and identity
        //  when the chart is inactive, so the translation half agrees with the existing
        //  WorldFromCelestial / ObserverCelestial laws (pinned offline; MatrixD vs Rodrigues
        //  is the ChartOrientationAlgebra convention pin). Consumers stop hand-composing
        //  CreateFromAxisAngle wheels onto orientations: with every matrix routed through
        //  here, the bug class "one consumer missed the wheel" is unrepresentable.
        // =====================================================================================

        /// <summary>Celestial-space matrix -> window/world matrix through the frame's chart
        /// (RSS ConvertProxyMatToReal, ZoneManager.cs:1219-1222): translate −FrameCel, rotate
        /// orientation AND relative translation by R(−θ) (identity when the chart is
        /// inactive), translate +CellCenter. The translation row equals
        /// <see cref="WorldFromCelestial"/> of the input translation.</summary>
        public static MatrixD CelestialMatToWorld(ref ObserverFrame frame, MatrixD celMat)
        {
            celMat.Translation = celMat.Translation - frame.FrameCel;
            if (frame.SpinActive)
                celMat = celMat * MatrixD.CreateFromAxisAngle(frame.SpinAxisDir, -frame.SpinTheta);
            celMat.Translation = celMat.Translation + frame.CellCenter;
            return celMat;
        }

        /// <summary>Window/world matrix -> celestial-space matrix — the exact inverse of
        /// <see cref="CelestialMatToWorld"/> (RSS ConvertRealMatToProxy, ZoneManager.cs:1256-1259):
        /// translate −CellCenter, rotate by R(+θ), translate +FrameCel. The translation row
        /// equals <see cref="ObserverCelestial"/> of the input translation.</summary>
        public static MatrixD WorldMatToCelestial(ref ObserverFrame frame, MatrixD worldMat)
        {
            worldMat.Translation = worldMat.Translation - frame.CellCenter;
            if (frame.SpinActive)
                worldMat = worldMat * MatrixD.CreateFromAxisAngle(frame.SpinAxisDir, frame.SpinTheta);
            worldMat.Translation = worldMat.Translation + frame.FrameCel;
            return worldMat;
        }

        // =====================================================================================
        //  RSS PROXY ORIENTATION (steal audit 2026-06-12 S2 — reference
        //  PlanetProxyManager.cs:25,110-132; fixes root-cause A1 / render-audit M4). The
        //  vendored PlanetProxy.mwm and every skin were authored against RSS's composition:
        //      baseMat        = CreateWorld(0, Forward, Down)         // model-up -> world DOWN
        //      rotationMatrix = CreateFromAxisAngle(Up, −rotation)
        //      proxyRotMat    = baseMat * Invert(rotationMatrix)      // spin +θ about world Up
        //  i.e. the assets are authored UPSIDE-DOWN relative to a naive model-up -> spin-axis
        //  pivot: mapping model-up to +axis drew every skin N/S-mirrored with retrograde
        //  apparent spin. RSS only ever spins about world Up; our bodies carry TILTED axes, so
        //  the canonical composition is carried by ONE basis change B — the minimal rotation
        //  taking Up onto the body's spin axis — applied on the RIGHT of the whole product
        //  (row-vector algebra: R(Up,θ)·B = B·R(Up·B,θ), hence baseMat·R(Up,θ)·B =
        //  (baseMat·B)·R(axis,θ): the carried base spins about the carried axis — the
        //  canonical picture tilted whole). With axis == Up, B == Identity and the helpers
        //  reproduce RSS byte-for-byte (pinned offline, alongside the in-zone pin and the
        //  seam-continuity identities).
        // =====================================================================================

        /// <summary>RSS's authored proxy base (PlanetProxyManager.cs:25): model-up -> world
        /// DOWN, model-forward -> world Forward. Every vendored skin/MWM assumes it.</summary>
        public static readonly MatrixD ProxyBaseMatrix =
            MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Forward, Vector3D.Down);

        /// <summary>B: the minimal rotation carrying world Up onto <paramref name="spinAxisUnit"/>
        /// (about Up×axis by the angle between them). Exact Identity when axis == Up; a π flip
        /// about a deterministic perpendicular (Forward) when axis == Down.</summary>
        public static MatrixD ProxyAxisChange(Vector3D spinAxisUnit)
        {
            Vector3D cross = Vector3D.Cross(Vector3D.Up, spinAxisUnit);
            double s = cross.Length();
            double c = Vector3D.Dot(Vector3D.Up, spinAxisUnit);
            if (s < 1e-12)
                return c >= 0.0 ? MatrixD.Identity
                                : MatrixD.CreateFromAxisAngle(Vector3D.Forward, Math.PI);
            return MatrixD.CreateFromAxisAngle(cross / s, Math.Atan2(s, c));
        }

        /// <summary>The PINNED base orientation for a body: baseMat·B — what the in-zone
        /// hard-pin draws (RSS's SURFACE case, PlanetProxyManager.cs:126) and what
        /// <see cref="ProxySpinOrientation"/> reduces to at θ = 0. The fully-composed in-zone
        /// drawn orientation (live spin through the chart's R(−θ)) equals exactly this — the
        /// skin sits still on the static voxel; the explicit pin just stops trusting two θ
        /// samples to cancel.</summary>
        public static MatrixD ProxyBaseOrientation(Vector3D spinAxisUnit)
        {
            return ProxyBaseMatrix * ProxyAxisChange(spinAxisUnit);
        }

        /// <summary>The LIVE spinning skin orientation in CELESTIAL axes: RSS's
        /// proxyRotMat = baseMat·Invert(CreateFromAxisAngle(Up,−θ)) carried by B
        /// (== <see cref="ProxyBaseOrientation"/>·R(axis,+θ) — surface features advance by
        /// R(+θ) about the axis, the same R(θ) the rotating chart is built from). Consumers
        /// convert it into the window through <see cref="CelestialMatToWorld"/>; PLACEMENT
        /// stays the angular-size-preserving clamp (<see cref="ProjectProxy"/>), never RSS's
        /// miniature scale (audit S6).</summary>
        public static MatrixD ProxySpinOrientation(Vector3D spinAxisUnit, double theta)
        {
            MatrixD rotationMatrix = MatrixD.CreateFromAxisAngle(Vector3D.Up, -theta);
            return ProxyBaseMatrix * MatrixD.Invert(rotationMatrix) * ProxyAxisChange(spinAxisUnit);
        }

        /// <summary>
        /// The LOCAL client's current CONJUNCTION frame, published every frame by
        /// <c>ObserverFramePublisher</c> (null when the local observer is not coasting in a
        /// Conjunction). Client-local and per-observer — NOT a shared server anchor; each client
        /// publishes its own. When no planet voxel is in frame, the proxy sky centers on this
        /// conjunction cell with the virtual-orbit celestial state (the treadmill).
        /// </summary>
        public static ObserverFrame? LocalConjunctionFrame;

        /// <summary>The world position at which to draw <paramref name="node"/> in the sky of an
        /// observer whose current frame is <paramref name="anchor"/> (the body whose FIXED cell the
        /// observer is physically in). This is the per-observer, frame-relative proxy formula:
        ///
        ///   proxyPos = CellOf(anchor) + ( node.celestial − anchor.celestial )    [wheeled by spin]
        ///
        /// The anchor body's own offset is zero, so its real voxel stays pinned at its cell while
        /// every other body is drawn at its true celestial offset from the anchor. Because the
        /// anchor's cell is its STABLE <see cref="VoxelBerthRegistry"/> cell — not always the origin
        /// berth — two observers at two different bodies each get a correct sky centered on their own
        /// cell (N players at N bodies). With <paramref name="anchor"/> null the observer is NOT at a
        /// planet: if a conjunction frame is published the sky centers on it (the treadmill); else it
        /// falls back to the home body at <see cref="CurrentBerth"/>.</summary>
        public static Vector3D BodyWorldPos(SystemRegistry reg, GravityBody node, double t, GravityBody anchor)
        {
            if (reg == null || node == null) return CurrentBerth;
            return BodyWorldPosInFrame(node, t, ResolveFrame(reg, t, anchor));
        }

        /// <summary>The observer's rendering frame for a given planet anchor: a planet frame when
        /// <paramref name="anchor"/> is a materialized voxel the observer is in; else the LOCAL
        /// client's published conjunction frame; else the home compact layout. The single source of
        /// truth both <see cref="BodyWorldPos"/> and the proxy projection resolve through.
        /// Camera-free overload — prefer the camera overload where an observer position exists,
        /// so transitions resolve by CELL CONTAINMENT instead of falling to home. The rotating
        /// chart's spin fields are NOT filled here (there is no subject position to test against
        /// R_c): this overload always hands out the inertial window.</summary>
        public static ObserverFrame ResolveFrame(SystemRegistry reg, double t, GravityBody anchor)
        {
            if (anchor != null)
            {
                StateVector o = anchor.OriginInRoot(t);
                return new ObserverFrame(VoxelBerthRegistry.CellOf(anchor.Name, reg), o.Position, o.Velocity, anchor);
            }
            if (LocalConjunctionFrame.HasValue)
                return LocalConjunctionFrame.Value;
            return HomeFrame(reg, t);
        }

        /// <summary>
        /// THE observer-frame resolution, with the CELL-CONTAINMENT transition fallback. Priority:
        ///
        ///  1. <paramref name="anchor"/> (a materialized voxel the observer is inside) — its body frame.
        ///  2. The published <see cref="LocalConjunctionFrame"/> (coasting — the treadmill).
        ///  3. CELL CONTAINMENT: the body whose fixed lattice cell contains the camera
        ///     (<see cref="VoxelBerthRegistry.TryCellContaining"/>) — resolved exactly like a planet
        ///     frame for THAT body. This is the anchor-swap gap closer: on ARRIVAL the grid is
        ///     teleported into the target body's cell before its voxel exists/streams (~0.2–1.2 s),
        ///     and on DEPARTURE the camera is still inside the old body's cell for the ≥1-frame
        ///     window before the conjunction frame publishes (AfterSimulation). In both windows the
        ///     camera IS inside the right body's cell, so containment resolves the correct frame and
        ///     the whole sky (proxy directions, sun, orbit lines) stays pointed the right way instead
        ///     of snapping to the HOME layout ≥380 km away. The lattice mapping is deterministic
        ///     from the system definition, so this works client-side with no voxel and no sync.
        ///     The POSITIONAL frame is cell-wide, but the BODY discriminator (<see cref="ObserverFrame.SpinAnchor"/>)
        ///     is handed out only within the keep envelope (<see cref="ShellRadius"/> ×
        ///     <see cref="ShellKeepHysteresis"/>) — beyond it the observer is coasting in the cell:
        ///     same placement, SpinAnchor null (the slot radius is a LATTICE quantity and must not
        ///     gate the gameplay "in this body's frame" boundary; 2026-06-11 retune).
        ///  4. The home compact layout (genuinely nowhere: no anchor, no conjunction, no containing
        ///     cell — e.g. a fresh spawn outside every cell).
        /// </summary>
        public static ObserverFrame ResolveFrame(SystemRegistry reg, double t, GravityBody anchor,
            Vector3D camWorldPos)
        {
            if (anchor != null)
            {
                StateVector o = anchor.OriginInRoot(t);
                ObserverFrame f = new ObserverFrame(VoxelBerthRegistry.CellOf(anchor.Name, reg),
                    o.Position, o.Velocity, anchor);
                FillWindowSpin(ref f, anchor, reg != null ? reg.FindDefinition(anchor.Name) : null,
                    t, camWorldPos);
                return f;
            }
            if (LocalConjunctionFrame.HasValue)
                return LocalConjunctionFrame.Value;

            string cellBody;
            Vector3D cellCenter;
            if (VoxelBerthRegistry.TryCellContaining(camWorldPos, reg, out cellBody, out cellCenter))
            {
                GravityBody node = reg.Find(cellBody);
                if (node != null)
                {
                    // The cell's POSITIONAL mapping (cell center <-> body celestial) is correct
                    // ANYWHERE in the cell (the cell is a 1:1 inertial window) and must stay
                    // cell-wide, or a mid-cell camera would snap to the HOME layout hundreds of
                    // km away. The BODY-FRAME DISCRIMINATOR (SpinAnchor — "you are in this
                    // body's frame", so day/night keys to its spin) is a GAMEPLAY boundary and
                    // ends at the SHARED keep envelope, KeepRadius (= R_c x ShellKeepHysteresis;
                    // stateless test, so the plain keep — see the conversion-radius table) —
                    // the band where the voxel dematerializes (PlanetTransition) and the render
                    // anchor drops (CelestialScene.CurrentBody). Before this clamp the
                    // discriminator ran out to the whole LATTICE slot radius (150 km test
                    // Earth vs the 63 km keep envelope — a lattice quantity gating a gameplay
                    // boundary), which is what made the planet frame "go too far out"
                    // (2026-06-11 retune: gameplay keys off the slim shell; only lattice /
                    // visibility quantities keep the big envelope). Beyond the envelope the
                    // observer is coasting INSIDE the cell: identical sky placement, no body
                    // frame (the sun drive falls to the orbital-azimuth phase, eased).
                    BodyDefinition cellDef = reg.FindDefinition(cellBody);
                    double keepR = KeepRadius(cellDef);
                    bool inKeep = keepR > 0.0
                        && Vector3D.DistanceSquared(camWorldPos, cellCenter) <= keepR * keepR;
                    StateVector o = node.OriginInRoot(t);
                    ObserverFrame f = new ObserverFrame(cellCenter, o.Position, o.Velocity,
                        inKeep ? node : null);
                    FillWindowSpin(ref f, node, cellDef, t, camWorldPos);
                    return f;
                }
            }
            return HomeFrame(reg, t);
        }

        /// <summary>
        /// SERVER-SIDE observer-frame resolution for a GRID (the seam math: arrival transfer,
        /// stow capture, join placement). The camera overloads above are CLIENT semantics: their
        /// priority 2 is the client-published <see cref="LocalConjunctionFrame"/> static, which
        /// on a listen host is the HOST player's own conjunction window — resolving a REMOTE
        /// player's grid through it corrupts that grid's celestial math. This overload never
        /// reads it. Priority:
        ///  1. <paramref name="anchor"/> (a materialized voxel body the grid is at) — body frame.
        ///  2. FRAME MEMBERSHIP: the grid is a member of a ProximityFrame -> that frame's window
        ///     (parent node root state + the virtual orbit's rails state). The live seam call
        ///     sites filter framed grids out first, so this is completeness, not their hot path.
        ///  3. PLANET-CELL containment (the same deterministic lattice test the camera overload
        ///     uses; SpinAnchor handed out only inside the shared keep envelope, same clamp).
        ///  4. CONJUNCTION-BERTH containment: the grid is physically inside some conjunction's
        ///     berth slot sphere -> that frame's window from its rails (the server-side analog
        ///     of <c>ObserverFramePublisher.BerthContaining</c>): a loose grid coasting through
        ///     a foreign berth measures its world state in THAT window, not the home layout
        ///     hundreds of km away.
        ///  5. The home compact layout.
        /// Game-free (registry + lattice + rails state only), so it is exercised offline.
        /// </summary>
        public static ObserverFrame ResolveFrameForGrid(SystemRegistry reg, double t,
            GravityBody anchor, long gridEntityId, Vector3D gridWorldPos,
            SEAerospace.Frames.FrameRegistry frames)
        {
            if (anchor != null)
            {
                StateVector o = anchor.OriginInRoot(t);
                ObserverFrame fa = new ObserverFrame(VoxelBerthRegistry.CellOf(anchor.Name, reg),
                    o.Position, o.Velocity, anchor);
                FillWindowSpin(ref fa, anchor, reg != null ? reg.FindDefinition(anchor.Name) : null,
                    t, gridWorldPos);
                return fa;
            }

            // 2. membership window (the grid rides its own frame's rails).
            if (frames != null && gridEntityId != 0)
            {
                SEAerospace.Frames.ProximityFrame member = frames.FindByMember(gridEntityId);
                ObserverFrame memberWin;
                if (member != null && TryFrameWindow(reg, member, t, out memberWin))
                    return memberWin;
            }

            // 3. planet-cell containment (deterministic from the system definition).
            string cellBody;
            Vector3D cellCenter;
            if (reg != null && VoxelBerthRegistry.TryCellContaining(gridWorldPos, reg, out cellBody, out cellCenter))
            {
                GravityBody node = reg.Find(cellBody);
                if (node != null)
                {
                    BodyDefinition cellDef = reg.FindDefinition(cellBody);
                    double keepR = KeepRadius(cellDef);
                    bool inKeep = keepR > 0.0
                        && Vector3D.DistanceSquared(gridWorldPos, cellCenter) <= keepR * keepR;
                    StateVector o = node.OriginInRoot(t);
                    ObserverFrame fc = new ObserverFrame(cellCenter, o.Position, o.Velocity,
                        inKeep ? node : null);
                    FillWindowSpin(ref fc, node, cellDef, t, gridWorldPos);
                    return fc;
                }
            }

            // 4. conjunction-berth containment (slot spheres are disjoint — at most one match).
            if (frames != null && frames.Allocator != null)
            {
                double slotR = frames.Allocator.SlotRadius;
                foreach (SEAerospace.Frames.ProximityFrame f in frames.Frames)
                {
                    if (f == null || f.BerthSlotId < 0) continue;
                    if (Vector3D.DistanceSquared(gridWorldPos, f.BerthCenter) > slotR * slotR) continue;
                    ObserverFrame berthWin;
                    if (TryFrameWindow(reg, f, t, out berthWin)) return berthWin;
                    break;   // containing berth found but window degenerate -> fall to home
                }
            }

            return HomeFrame(reg, t);
        }

        // A ProximityFrame's 1:1 window: cell = its berth center, celestial = parent node root
        // state + the virtual orbit's rails state (position AND velocity — what the window rides
        // at). False for legacy "Planet:<id>" frames (no tree node) or a non-finite rails state.
        private static bool TryFrameWindow(SystemRegistry reg, SEAerospace.Frames.ProximityFrame frame,
            double t, out ObserverFrame win)
        {
            win = new ObserverFrame();
            if (reg == null || frame == null) return false;
            GravityBody node = reg.Find(frame.ParentBodyName);
            if (node == null) return false;
            StateVector cel = OrbitPropagation.StateAt(frame.Elements, t);
            StateVector root = node.OriginInRoot(t);
            Vector3D framePos = root.Position + cel.Position;
            Vector3D frameVel = root.Velocity + cel.Velocity;
            if (!IsFiniteVec(framePos) || !IsFiniteVec(frameVel) || !IsFiniteVec(frame.BerthCenter))
                return false;
            win = new ObserverFrame(frame.BerthCenter, framePos, frameVel, null);
            return true;
        }

        private static bool IsFiniteVec(Vector3D v)
        {
            return !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                  || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
        }

        /// <summary>Place <paramref name="node"/> in an explicit observer frame (cell center +
        /// celestial reference). The sky sits at its TRUE celestial directions relative to the
        /// cell — composed through R⁻¹ when the observer's rotating chart is active
        /// (<see cref="WorldFromCelestial"/>): for a surface observer the whole proxy sky WHEELS
        /// at the day rate ω, which is the diurnal truth, not a double-count — the sun drive's
        /// in-zone phase is the azimuth of the SAME R⁻¹-rotated star direction, so sky and
        /// terminator stay mutually exact. Outside the zone (chart inactive) this is the old
        /// unwheeled 1:1 placement byte-for-byte.</summary>
        public static Vector3D BodyWorldPosInFrame(GravityBody node, double t, ObserverFrame frame)
        {
            return WorldFromCelestial(frame, node.OriginInRoot(t).Position);
        }

        /// <summary>Layout with the default anchor (the first voxel body). Use the anchor overload
        /// when a specific body is materialized so THAT body stays pinned at the berth.</summary>
        public static Vector3D BodyWorldPos(SystemRegistry reg, GravityBody node, double t)
        {
            return BodyWorldPos(reg, node, t, null);
        }

        /// <summary>
        /// Render distance ceiling for sky proxies (m). A proxy whose TRUE distance exceeds this is
        /// drawn ON a shell at this radius (its radius scaled down to keep the exact angular size), so
        /// a true-scale system can never push a proxy past single-precision / the far plane. Bodies
        /// nearer than this render at their true distance and size — so compact systems are unchanged.
        /// This is RSS's PlanetProxyScale idea (compress the render distance, preserve angular size)
        /// adapted to the fixed-cell model. Equal to the precision envelope the offline budget guards.
        /// </summary>
        public const double SkyClampDistance = 2.0e6;   // 2000 km

        /// <summary>Sky-proxy APPARENT-SIZE exaggeration (rob 2026-06-16, "they look small ...
        /// human eye vs game fov"). A flat monitor squeezes the game's wide FOV into the small
        /// solid angle the screen actually subtends to your eye (~half), so a body drawn at its
        /// TRUE angular size reads ~half its real-sky size. Every drawn celestial disc — sky
        /// proxies (via <see cref="ProjectProxy"/>) and the Sun disc (SunRenderer) — is scaled by
        /// this so the monitor view matches what the eye expects. 2.0 = eye-matched (cancels the
        /// compression); raise for a more dramatic sky. Positions/distances are NEVER touched —
        /// only the drawn size. The factor RAMPS back to 1x as a body nears materialization
        /// (<see cref="ApparentSizeMult"/>) so the proxy -> real-voxel handoff has no size pop.</summary>
        public const double ProxyApparentSizeMult = 2.0;

        /// <summary>Outer edge of the apparent-size ramp, in body radii: beyond this the full
        /// <see cref="ProxyApparentSizeMult"/> applies; it eases to 1x by <see cref="ApparentRampInnerRadii"/>.</summary>
        private const double ApparentRampOuterRadii = 12.0;

        /// <summary>Inner edge of the apparent-size ramp, in body radii: at/inside this the proxy
        /// is drawn at TRUE size (1x). For an AIRLESS body the keep is ~1.5-1.7 R so 2.0 R sits safely
        /// outside it. For an ATMOSPHERIC body the keep is atmosphere-driven and can EXCEED 2.0 R (the ×k
        /// Earth dematerializes at ~2.14 R), so the inner edge is raised to the body's actual KeepDropRadius
        /// when that is larger — see the <paramref name="keepDropRadius"/> arg of <see cref="ApparentSizeMult"/>
        /// — so the exaggeration is provably gone (1×) before the voxel takes over: no atmo-arc size pop.</summary>
        private const double ApparentRampInnerRadii = 2.0;

        /// <summary>The apparent-size multiplier for a body of <paramref name="bodyRadius"/> seen
        /// at <paramref name="trueDistance"/>: <see cref="ProxyApparentSizeMult"/> when far, eased
        /// linearly to 1.0 across [inner, <see cref="ApparentRampOuterRadii"/>] body radii so an
        /// approached body reaches true size before it materializes. The inner edge is
        /// max(<see cref="ApparentRampInnerRadii"/>·R, <paramref name="keepDropRadius"/>) so an atmospheric
        /// body whose voxel keep extends past 2.0 R still reaches 1× by its demat edge (keepDropRadius=0
        /// reproduces the old airless behaviour exactly). Pure.</summary>
        public static double ApparentSizeMult(double bodyRadius, double trueDistance, double keepDropRadius = 0.0)
        {
            if (bodyRadius <= 0.0 || ProxyApparentSizeMult == 1.0) return 1.0;
            double lo = Math.Max(bodyRadius * ApparentRampInnerRadii, keepDropRadius);
            double hi = bodyRadius * ApparentRampOuterRadii;
            if (hi <= lo) return 1.0;   // a very thick atmosphere could push lo past hi; no ramp room → true size
            if (trueDistance <= lo) return 1.0;
            if (trueDistance >= hi) return ProxyApparentSizeMult;
            double f = (trueDistance - lo) / (hi - lo);
            return 1.0 + f * (ProxyApparentSizeMult - 1.0);
        }

        /// <summary>The observer's celestial position given their camera world position within the
        /// frame: world offset from the cell maps 1:1 to a celestial offset (the cell is a 1:1
        /// inertial window), so distant proxies show correct (small) parallax as the observer flies.
        /// Inside an ACTIVE rotating chart the offset is carried through R(θ) — the forward
        /// position law (see the chart block above); identity when the chart is inactive.</summary>
        public static Vector3D ObserverCelestial(ObserverFrame frame, Vector3D camWorldPos)
        {
            return frame.FrameCel + SpinToCelestial(frame, camWorldPos - frame.CellCenter);
        }

        /// <summary>
        /// Camera-relative placement of a sky proxy: TRUE sky direction + TRUE angular size, but the
        /// render distance is CLAMPED to <see cref="SkyClampDistance"/>. A sphere of radius r at
        /// distance d subtends asin(r/d), so scaling the radius by the same factor the distance is
        /// compressed (renderRadius = dRender · R/dTrue) preserves the angle EXACTLY. For a body
        /// nearer than the clamp, dRender = dTrue and renderRadius = R (true distance & size).
        /// Day/night is the snapshot's GameDateTime sun bake — no sky wheel is applied here.
        ///
        /// DEGENERATE GUARD: when the observer is ON or INSIDE the body (dTrue &lt;= R) the proxy
        /// has no valid placement — the asin(r/d) identity needs r/d &lt; 1, and a planet-radius
        /// sphere centered at the camera would fill the screen. Reachable when an observer's frame
        /// resolves to a body they are celestially inside (e.g. another player's conjunction
        /// decaying through a body on rails). Returns RenderRadius 0 so the renderer's
        /// RenderRadius &lt;= 0 skip drops it (the materialized voxel, if any, is the real view).
        /// </summary>
        public static ProxyPlacement ProjectProxy(Vector3D camPos, Vector3D bodyCelestial,
            ObserverFrame frame, double bodyRadius, double t, double keepDropRadius = 0.0)
        {
            // True sky direction relative to the observer, expressed in WINDOW axes: a celestial
            // direction u draws at R⁻¹·u inside an active rotating chart (the diurnal sky wheel),
            // and at u itself outside it (the inertial window — byte-identical to the old path).
            Vector3D delta = SpinFromCelestial(frame, bodyCelestial - ObserverCelestial(frame, camPos));
            double dTrue = delta.Length();
            if (bodyRadius <= 0.0 || dTrue <= bodyRadius || dTrue < 1.0)
                return new ProxyPlacement(camPos, 0.0, dTrue);   // degenerate: skip (see guard doc)
            Vector3D dir = delta / dTrue;
            double dRender = dTrue < SkyClampDistance ? dTrue : SkyClampDistance;
            double renderRadius = dRender * bodyRadius / dTrue;   // exact angular-size match (asin r/d)
            // Apparent-size exaggeration for the flat-monitor FOV (see ProxyApparentSizeMult):
            // grows the DRAWN disc only — direction (dir) and render distance (dRender) are
            // untouched, so the placement, parallax and true-distance readout are unchanged.
            renderRadius *= ApparentSizeMult(bodyRadius, dTrue, keepDropRadius);
            return new ProxyPlacement(camPos + dir * dRender, renderRadius, dTrue);
        }

        /// <summary>
        /// Sky-shell compression for drawn line points (matches <see cref="ProjectProxy"/>; the
        /// ONE clamp implementation, shared by <c>OrbitRenderer</c> and
        /// <c>CelestialOrbitRenderer</c>): the proxies render no farther than
        /// <see cref="SkyClampDistance"/> from the camera, so at true scale an UNcompressed arc
        /// (Jupiter 5.2e8 m) would hit single precision / the far plane and terminate ~260x past
        /// the clamped proxy it belongs to. Each sampled point is compressed individually,
        /// direction-preserved, distance clamped to the shell — the map is monotonic along every
        /// camera ray, so consecutive endpoints stay adjacent and segments stay connected.
        /// Residual distortion: a SEGMENT whose chord crosses the clamp boundary draws as the
        /// straight line between its two compressed endpoints rather than bending along the
        /// shell, and all radial depth beyond the clamp flattens ONTO the shell — far arcs read
        /// as direction-true rings at shell distance, hugging the clamped proxies they wrap.
        /// Points inside the clamp (the whole compact fixture; the player's local orbit) are
        /// untouched.
        /// </summary>
        public static Vector3D CompressToSkyShell(Vector3D point, Vector3D camPos)
        {
            Vector3D delta = point - camPos;
            double d = delta.Length();
            if (d <= SkyClampDistance) return point;
            return camPos + delta * (SkyClampDistance / d);
        }

        /// <summary>
        /// A craft's celestial state RELATIVE TO <paramref name="body"/> from its world-space
        /// state observed through <paramref name="frame"/> — the celestial-native start state
        /// (no voxel involved). Position: world offset from the cell maps 1:1 to a celestial
        /// offset (<see cref="ObserverCelestial"/>), minus the body's root origin. Velocity:
        /// the world-measured velocity through the chart velocity law
        /// (<see cref="ObserverCelestialVelocity"/>: worldVel + FrameCelVel in an inertial
        /// window, + the R/ω×r terms inside an active rotating chart), minus the body's own
        /// root velocity (Galilean, all analytic from the ephemeris chain). When the frame rides
        /// the body itself (FrameCelVel == body root velocity, chart inactive) this reduces
        /// EXACTLY to the anchored voxel math (offset from center, raw world velocity).
        /// </summary>
        public static StateVector BodyRelativeState(ObserverFrame frame, Vector3D worldPos,
            Vector3D worldVel, GravityBody body, double t)
        {
            StateVector origin = body.OriginInRoot(t);
            return new StateVector(
                ObserverCelestial(frame, worldPos) - origin.Position,
                ObserverCelestialVelocity(frame, worldPos, worldVel) - origin.Velocity);
        }

        // The home-fallback frame: the compact layout centered on the first VOXEL body at the
        // origin berth, riding that body's celestial state (position AND velocity — the home
        // cell is pinned to the home body exactly like a planet cell is to its body).
        private static ObserverFrame HomeFrame(SystemRegistry reg, double t)
        {
            StateVector cel = ReferenceCelState(reg, t);
            return new ObserverFrame(CurrentBerth, cel.Position, cel.Velocity, null);
        }

        // Celestial state of the body the home layout is centered on: the first VOXEL body
        // (so the static fallback matches the old behavior). Zero state if none.
        private static StateVector ReferenceCelState(SystemRegistry reg, double t)
        {
            if (reg == null) return StateVector.Zero;   // degenerate caller (no system): origin
            IReadOnlyList<GravityBody> bodies = reg.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                BodyDefinition def = reg.FindDefinition(bodies[i].Name);
                if (def != null && !string.IsNullOrEmpty(def.ParkSubtype))
                    return bodies[i].OriginInRoot(t);
            }
            return StateVector.Zero;
        }
    }
}
