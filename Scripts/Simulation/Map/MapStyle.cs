using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The map's line language, one rule per style, so a line's look says what it is:
///  - solid: a path something travels (orbits, your orbit, your Lagrange orbit);
///  - dashed (long): a plan or a prediction (after a node, after a sector exit, the would-be orbit);
///  - dotted (short): a boundary, not a path (sectors, L1-L5 zones, a planet's space, belts, the calm core).
/// Width is importance: thick yours, medium the selected / targeted, thin everything else.
/// </summary>
public static class MapStyle
{
    public static float Thick(float u) => 2.0f * u;
    public static float Medium(float u) => 1.3f * u;
    public static float Thin(float u) => 1.0f * u;

    /// <summary>A boundary's own colour (dim, neutral): zones are not paths, and gold means you or selected.</summary>
    public static readonly ColorSRGB Zone = new ColorSRGB(0.75f, 0.80f, 0.88f, 0.55f);

    /// <summary>A boundary: short dots.</summary>
    public static void Boundary(IList<Vector2> pts, bool closed, ColorSRGB c, float width, float u)
        => MapPipeline.ScreenDotted(pts, closed, c, width, 2.5f * u, 4.5f * u);

    /// <summary>A round boundary on screen (centre, radius px).</summary>
    public static void BoundaryCircle(Vector2 centre, float r, ColorSRGB c, float width, float u)
    {
        int n = Math.Clamp((int)(r / (3f * u)), 24, 128);
        var pts = _circle; pts.Clear();   // (one list: drawn at once)
        for (int i = 0; i < n; i++) { double a = 2 * Math.PI * i / n; pts.Add(centre + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r)); }
        Boundary(pts, true, c, width, u);
    }
    static readonly List<Vector2> _circle = new List<Vector2>(128);

    /// <summary>A plan or prediction: long dashes.</summary>
    public static void Plan(IList<Vector2> pts, bool closed, ColorSRGB c, float width, float u)
        => MapPipeline.ScreenDotted(pts, closed, c, width, 12f * u, 7f * u);
}
