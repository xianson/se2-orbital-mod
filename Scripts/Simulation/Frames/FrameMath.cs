#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// A planet's gravity law exactly as SE2 evaluates it (GravityGeneratorComponent,
/// CalculateGravitationalAccelerationMagnitude): g(d) = G0 · (R0 / d)^Falloff inside Reach, zero
/// outside; a negative Falloff means the linear ramp from R0 to Reach.
/// </summary>
public struct GravityLaw
{
    public double G0;       // m/s² at R0
    public double R0;       // AccelerationDistance, m
    public double Falloff;  // FallOffPower
    public double Reach;    // AffectDistance, m (hard cutoff)

    public bool Valid => G0 > 0 && R0 > 0 && Reach > 0;

    /// <summary>True Keplerian gravity: the displayed conic is exact, not an approximation.</summary>
    public bool IsInverseSquare => Math.Abs(Falloff - 2.0) < 1e-3;

    public double At(double d)
    {
        if (!Valid || d > Reach) return 0;
        if (Falloff < 0)
        {
            double ramp = 1.0 - (d - R0) / Math.Max(1e-9, Reach - R0);
            return G0 * Math.Clamp(ramp, 0.0, 1.0);
        }
        return d <= 0 ? G0 : G0 * Math.Pow(R0 / d, Falloff);
    }

    /// <summary>
    /// Gravitational parameter for a Keplerian fit. Exact (G0·R0²) for inverse-square planets;
    /// otherwise the LOCAL equivalent g(d)·d², which matches the current pull but not how it changes
    /// with altitude, so the drawn conic is only a short-horizon approximation.
    /// </summary>
    public double MuAt(double d) => IsInverseSquare ? G0 * R0 * R0 : At(d) * d * d;
}

/// <summary>
/// Game-free frame and proxy math. No engine calls, so it can be exercised offline.
///
/// A planet is "in frame" when the camera is inside its frame sphere. In frame, the real
/// voxel planet is drawn. Out of frame, the real planet is hidden and a proxy globe is drawn
/// in its place: same direction, same angular size, render distance optionally clamped.
/// This is the SE-Aerospace model (docs/proxy-rendering-frames.md) without berths yet.
/// </summary>
public static class FrameMath
{
    /// <summary>
    /// Frame membership with hysteresis: enter below <paramref name="enterRadius"/>,
    /// leave above <paramref name="exitRadius"/>. Stops flicker at the boundary.
    /// </summary>
    public static bool UpdateInFrame(bool wasInFrame, double distance, double enterRadius, double exitRadius)
    {
        return wasInFrame ? distance < exitRadius : distance < enterRadius;
    }

    /// <summary>
    /// Where to draw a proxy of a body of <paramref name="radius"/> at <paramref name="bodyCenter"/>
    /// seen from <paramref name="camera"/>. With <paramref name="clampDistance"/> &lt;= 0 the proxy
    /// sits at the true position and true size. Otherwise, beyond the clamp it is pulled in along
    /// the view ray and shrunk by the same factor, which keeps its angular size exact:
    /// asin(r/d) == asin((r*k)/(d*k)).
    /// </summary>
    public static void ProjectProxy(
        Vector3D camera, Vector3D bodyCenter, double radius, double clampDistance,
        out Vector3D renderCenter, out double renderRadius)
    {
        Vector3D delta = bodyCenter - camera;
        double distance = delta.Length();

        if (clampDistance <= 0 || distance <= clampDistance || distance < 1e-6)
        {
            renderCenter = bodyCenter;
            renderRadius = radius;
            return;
        }

        double k = clampDistance / distance;
        renderCenter = camera + delta * k;
        renderRadius = radius * k;
    }
}
