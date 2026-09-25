#pragma warning disable
namespace OrbitalMod;

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
