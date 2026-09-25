using System;

namespace SEAerospace
{
    /// <summary>
    /// The ENGINE SUN PLANE — pure math (VRageMath only, offline-pinned): SE's baked sun-sweep
    /// model, vendored from the decompile. Since the 2026-06-12 RSS-LITERAL sun decision this
    /// is REFERENCE/FALLBACK knowledge only — the system layout is always canonical and never
    /// reads world sun state (<c>SystemRuntime.EnsureBuilt</c>); the snapshot's bake measures
    /// the engine's real sweep basis EMPIRICALLY (<c>CelestialSceneSnapshot.ProbeSunBasis</c>)
    /// and this class documents what that probe is expected to find (the deterministic basis
    /// the engine derives when nothing else interferes).
    ///
    /// THE ENGINE MODEL (decompiled Sandbox.Game / VRage.Game, game version on disk):
    ///  - <c>MySectorWeatherComponent.CalculateSunDirection()</c>:
    ///      <c>sunDir = Rot(m_sunRotationAxis, 2π·ElapsedGameTime/m_speed) · m_baseSunDirection</c>
    ///    with <c>ElapsedGameTime = GameDateTime − 2081-01-01</c> (MySession.GameDateTime's
    ///    exact definition) and <c>m_speed</c> the day length our bake configures.
    ///  - the axis is NOT authored data anywhere: <c>MySunProperties.SunRotationAxis</c> is a
    ///    GET-ONLY property computed from the base direction —
    ///      <c>axis = normalize(Up − B·(Up·B))</c> (the world-up component orthogonal to B),
    ///    or the same with LEFT when |B·Up| &gt; 0.95. <see cref="EngineSunRotationAxis"/> is
    ///    that formula, vendored verbatim.
    ///  - B (<c>BaseSunDirectionNormalized</c>) comes from the WORLD: the saved environment
    ///    settings' SunAzimuth/SunElevation when present and EnableSunRotation was on at load
    ///    (<c>MySector.InitEnvironmentSettings</c>), else the active EnvironmentDefinition.
    ///
    /// CONSEQUENCE (the impossibility proof, pinned in Orbital.Tests EngineSunAxisFormula):
    /// with B = (0,b_y,b_z) the Up-branch axis has |axis·Z| = |b_y| ≤ 0.95, and in general
    /// axis·Z = −B_z·(B·Up)/‖…‖ is bounded away from ±1 (the Left branch is ≈ −X); the axis is
    /// a DERIVED quantity that can never equal ±Z for any base direction a mod could author or
    /// write — the engine sweep plane can NEVER be our canonical system plane. The first
    /// response (same day) tilted the whole system onto the engine plane; that coupled the
    /// LAYOUT to mutable world render state (the steal audit's A2 cross-session hazard) and
    /// was deleted the same day for RSS's contract: the layout stays canonical, the drawn star
    /// is the chart-mapped celestial truth, the engine terminator is driven PHASE-ONLY through
    /// the probed basis, and its elevation error vs the drawn star is ACCEPTED (RSS ships ~45°
    /// of exactly this, unnoticed).
    /// </summary>
    public static class SunPlane
    {
        /// <summary>The vanilla default BaseSunDirectionNormalized (MySunProperties.Defaults) —
        /// the deterministic last-resort fallback when neither the world settings nor the
        /// environment definition are readable.</summary>
        public static readonly Vector3D DefaultBaseSunDirection =
            new Vector3D(0.33946735, 0.70979536, -0.61721337);

        /// <summary>
        /// SE's sun rotation axis for a given baked base sun direction — the literal decompiled
        /// <c>MySunProperties.SunRotationAxis</c> getter (Up-component orthogonal to B; Left
        /// when B is within ~18° of ±Y). Returns a unit vector; falls back to +Y for a
        /// degenerate (zero) input.
        /// </summary>
        public static Vector3D EngineSunRotationAxis(Vector3D baseSunDirection)
        {
            Vector3D b = baseSunDirection;
            if (b.LengthSquared() < 1e-12) return Vector3D.Up;
            b.Normalize();
            // Vector3.Up = (0,1,0); Vector3.Left = (-1,0,0) — VRageMath conventions.
            Vector3D refDir = Math.Abs(Vector3D.Dot(b, Vector3D.Up)) > 0.95
                ? new Vector3D(-1.0, 0.0, 0.0)
                : new Vector3D(0.0, 1.0, 0.0);
            // cross(cross(B, ref), B) == ref·(B·B) − B·(ref·B): the ref component ⊥ B.
            Vector3D axis = Vector3D.Cross(Vector3D.Cross(b, refDir), b);
            if (axis.LengthSquared() < 1e-12) return Vector3D.Up;   // unreachable with the 0.95 gate
            axis.Normalize();
            return axis;
        }
    }
}
