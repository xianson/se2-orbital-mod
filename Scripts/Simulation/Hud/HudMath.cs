#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Forwarder only: the HUD's copy of <see cref="Num"/> (byte-identical), unified 2026-10-15. Every HUD / map / flight file
/// now takes <c>using static OrbitalMod.Num;</c>; this name is kept so nothing that names it breaks. No callers.
/// </summary>
internal static class HudMath
{
    public static bool IsFinite(double x) => Num.IsFinite(x);
    public static bool IsFinite(Vector3D v) => Num.IsFinite(v);
    public static double Wall() => Num.Wall();
}
