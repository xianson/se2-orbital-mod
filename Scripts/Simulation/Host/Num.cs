#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The small helpers every server / host / entry / encounter / asteroid / dev / HUD / map class had its own copy of (the same one-liner,
/// a dozen times). Reached unqualified through <c>using static OrbitalMod.Num;</c>; a class with a member of the same name
/// keeps its own (members hide the import). The engine-free core (Core/, namespace SEAerospace) keeps its own: the offline
/// tests compile it without this mod.
/// </summary>
internal static class Num
{
    /// <summary>Wall-clock seconds (Stopwatch): rate limits, timeouts and diagnostics - never game time.</summary>
    public static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The entity is alive in its scene - asked without a throw (an exception costs ~200 ms in SE2): the guard before
    /// reading a transform or session of an entity that may have been deleted.</summary>
    public static bool Alive(Entity e)
    {
        if (e == null) return false;
        var d = e.Data; var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }

    public static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
