#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The small helpers the HUD, the map and the flight UI share (each class had its own copy; files take them with
/// `using static OrbitalMod.HudMath;`).
/// </summary>
internal static class HudMath
{
    /// <summary>Neither NaN nor infinite.</summary>
    public static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
    /// <summary>Wall-clock seconds (Stopwatch): UI timing, not the game's clock.</summary>
    public static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
