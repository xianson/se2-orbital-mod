using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// First in-game use of the ported orbital core: the predicted orbit of the camera around the
/// planet whose gravity dominates, drawn as a line with an element readout.
///
/// State vector: position relative to the planet centre, velocity from differentiating the
/// camera position (smoothed). Elements: SEAerospace.Orbital.OrbitalMath.ToElements. Path:
/// OrbitSampler.SamplePath. Drawn with a UI3D MeshBuilder, which the culling shader treats as a
/// Default (world-view) entity, so it is visible without debug draw and needs no reflection.
///
/// SE2 planet gravity is g0·(r0/d)^p with a hard cutoff; unless p == 2 the conic is only the
/// local Keplerian fit (see <see cref="GravityLaw.MuAt"/>), and the readout says so.
/// </summary>
public static class OrbitDisplay
{
    private const int PathPoints = 160;
    private const double MinSpeed = 0.5;      // m/s; below this there is no meaningful orbit
    private const double VelocitySmoothing = 0.15;
    private const double DominanceMargin = 1.1;

    private static MeshBuilder _builder;
    private static object _dominant;
    private static double _dominantG;
    private static bool _hasPrev;
    private static Vector3D _prevCamera;
    private static long _prevTicks;
    private static Vector3D _velocity;
    private static bool _drewLastFrame;

    public static string LastReadout;

    /// <summary>
    /// Called by every client planet each frame, under PlanetRenderBridge.Lock. The planet with the
    /// strongest gravity at the camera takes over (with a margin) and draws.
    /// </summary>
    public static void Consider(object owner, Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera,
                                Vector3D center, double radius, GravityLaw law, string name)
    {
        double d = (camera.Position - center).Length();
        double g = law.At(d);

        if (_dominant == null || ReferenceEquals(owner, _dominant))
        {
            _dominant = g > 0 ? owner : null;
            _dominantG = g;
        }
        else if (g > _dominantG * DominanceMargin)
        {
            _dominant = owner;
            _dominantG = g;
            _hasPrev = false;
        }
        if (!ReferenceEquals(owner, _dominant))
        {
            // Nobody under gravity: clear once.
            if (_dominant == null && _drewLastFrame) Clear();
            return;
        }

        UpdateVelocity(camera.Position);
        if (!OrbitalConfig.ShowOrbit || g <= 0) { Clear(); return; }

        _builder ??= CreateBuilder(session);
        if (_builder == null) return;

        double mu = law.MuAt(d);
        var sv = new StateVector(camera.Position - center, _velocity);
        string fit = law.IsInverseSquare ? "exact (1/r²)" : $"local fit (falloff {law.Falloff:F1})";
        double speed = _velocity.Length();

        if (speed < MinSpeed || mu <= 0)
        {
            LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km, v {speed:F1} m/s — no orbit (at rest) [{fit}]";
            DrawText(camera, LastReadout);
            _builder.Commit();
            _drewLastFrame = true;
            return;
        }

        KeplerianElements el = OrbitalMath.ToElements(sv, mu);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity))
        {
            Clear();
            return;
        }

        OrbitPath path = OrbitSampler.SamplePath(el, PathPoints, law.Reach);
        var color = el.IsElliptic ? ColorSRGB.Yellow : ColorSRGB.Red;
        var pts = path.Points;
        if (pts != null && pts.Length > 1)
        {
            for (int i = 1; i < pts.Length; i++) _builder.AddLine(center + pts[i - 1], center + pts[i], color, 3f);
            if (path.IsClosed) _builder.AddLine(center + pts[pts.Length - 1], center + pts[0], color, 3f);
        }

        double pe = el.PeriapsisRadius - radius;
        string ap = el.IsElliptic ? $"Ap {(el.ApoapsisRadius - radius) / 1000:F1} km  T {el.Period / 60:F1} min" : "escape";
        LastReadout = $"{name}: alt {(d - radius) / 1000:F1} km  v {speed:F0} m/s\n" +
                      $"a {el.SemiMajorAxis / 1000:F1} km  e {el.Eccentricity:F3}  i {el.Inclination * 180 / Math.PI:F1}°\n" +
                      $"Pe {pe / 1000:F1} km{(pe < 0 ? " (IMPACT)" : "")}  {ap}\n{fit}";
        DrawText(camera, LastReadout);
        _builder.Commit();
        _drewLastFrame = true;
    }

    private static void UpdateVelocity(Vector3D cam)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (now - _prevTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_hasPrev && dt > 1e-4 && dt < 0.5)
        {
            Vector3D inst = (cam - _prevCamera) * (1.0 / dt);
            // A teleport shows up as an absurd jump; drop it instead of smearing it in.
            if (inst.Length() < 20000) _velocity = _velocity + (inst - _velocity) * VelocitySmoothing;
            else _velocity = Vector3D.Zero;
        }
        else if (!_hasPrev)
        {
            _velocity = Vector3D.Zero;
        }
        _prevCamera = cam;
        _prevTicks = now;
        _hasPrev = true;
    }

    private static void DrawText(WorldTransform camera, string text)
    {
        Vector3D fwd = (Vector3D)Vector3.Transform(Vector3.Forward, camera.Orientation);
        Vector3D up = (Vector3D)Vector3.Transform(Vector3.Up, camera.Orientation);
        Vector3D left = (Vector3D)Vector3.Transform(-Vector3.Right, camera.Orientation);
        _builder.AddText(camera.Position + fwd * 10.0 + up * 3.0 + left * 4.0, text, ColorSRGB.Yellow, 0.7f);
    }

    private static void Clear()
    {
        if (_builder != null && _drewLastFrame) _builder.Commit();
        _drewLastFrame = false;
        LastReadout = null;
    }

    private static MeshBuilder CreateBuilder(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            return session.Get<IDebugDraw>().MeshBuilderFactory.CreateMeshBuilder(MeshBuilder.Type.UI3D, 0L, 512, "OrbitalModOrbit");
        }
        catch (Exception e)
        {
            Log.Default?.Warning($"[ORBIT] orbit display unavailable: {e.Message}");
            return null;
        }
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
