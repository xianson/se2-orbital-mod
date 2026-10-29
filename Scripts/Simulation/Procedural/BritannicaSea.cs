#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The island types of Britannica's authored sea (tools/britannica: island_sea.py, build_sea.py). Each is a
/// biome: its materials (<see cref="Palette"/>) and the real place its tops are cut from.</summary>
public static class BritannicaPaint
{
    public enum Type { Mesa, Forest, Alpine, Plateau, Burnt, Desert }

    /// <summary>top, top patches, cliffs, underside - Kemik's voxel materials (Britannica's material set) per type.</summary>
    public static Guid[] Palette(Type t) => t switch
    {
        Type.Mesa => new[] { BritannicaBiomes.SoilSavannaRocky, BritannicaBiomes.SandSavanna, BritannicaBiomes.DesertCliffs, BritannicaBiomes.DesertCliffs02 },
        Type.Forest => new[] { BritannicaBiomes.SoilSavanna, BritannicaBiomes.SoilSavannaRocky, BritannicaBiomes.Cliffs, BritannicaBiomes.AsteroidRocks },
        Type.Alpine => new[] { BritannicaBiomes.RocksSavanna, BritannicaBiomes.SoilSavannaRocky, BritannicaBiomes.Cliffs, BritannicaBiomes.AsteroidRocks },
        Type.Plateau => new[] { BritannicaBiomes.SoilSavanna, BritannicaBiomes.SandSavanna, BritannicaBiomes.DesertCliffs02, BritannicaBiomes.Cliffs },
        Type.Desert => new[] { BritannicaBiomes.SandSavanna, BritannicaBiomes.SandSavanna, BritannicaBiomes.DesertCliffs, BritannicaBiomes.DesertCliffs02 },
        _ => new[] { BritannicaBiomes.VolcanicAshes, BritannicaBiomes.ColdMagma, BritannicaBiomes.VolcanicRocks, BritannicaBiomes.VolcanicRocks },
    };
}

/// <summary>Britannica's authored island sea: the generated data (BritannicaSea.g.cs) decoded per island - its land
/// mask, its real-terrain heights (stored only on its land and a 2-cell border: the mod compiler allows no compression)
/// and its coast's signed distance, computed here from the mask.</summary>
public static partial class BritannicaSea
{
    static byte[] _blob;
    static byte[] Blob => _blob ??= Convert.FromBase64String(string.Concat(Blob64));

    /// <summary>Island i's maps, GridN[i] x GridN[i] (Step[i] apart, centred on it): heights (m above its low ground,
    /// 0 off its land) and the coast's signed distance (m, + inside).</summary>
    public static void Maps(int i, out float[] height, out float[] sdf)
    {
        int n = GridN[i], nn = n * n, o = GridOffset[i];
        var b = Blob;
        var mask = new bool[nn];
        for (int k = 0; k < nn; k++) mask[k] = (b[o + (k >> 3)] & (0x80 >> (k & 7))) != 0;
        o += (nn + 7) >> 3;
        // (the cells with heights: the land and 2 cells round it - build_sea.py's dilation, the same order)
        height = new float[nn];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (Near(mask, n, x, y, 2)) height[y * n + x] = GridLo[i] + b[o++] * GridScale[i];
        sdf = SignedDistance(mask, n, Step[i]);
    }

    static bool Near(bool[] m, int n, int x, int y, int r)
    {
        for (int j = Math.Max(0, y - r); j <= Math.Min(n - 1, y + r); j++)
            for (int i = Math.Max(0, x - r); i <= Math.Min(n - 1, x + r); i++)
                if (m[j * n + i]) return true;
        return false;
    }

    /// <summary>The mask's signed distance (+ inside, m): a two-pass chamfer (1, sqrt 2) each way - within ~8 % of
    /// the true distance, ample for a coast's walls.</summary>
    static float[] SignedDistance(bool[] m, int n, float step)
    {
        var din = Chamfer(m, n, true); var dout = Chamfer(m, n, false);
        var d = new float[n * n];
        for (int k = 0; k < d.Length; k++) d[k] = (m[k] ? din[k] - 0.5f : -(dout[k] - 0.5f)) * step;
        return d;
    }

    static float[] Chamfer(bool[] m, int n, bool inside)
    {
        const float D = 1.4142135f, Big = 1e6f;
        var d = new float[n * n];
        for (int k = 0; k < d.Length; k++) d[k] = m[k] == inside ? Big : 0f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int k = y * n + x; float v = d[k];
                if (v == 0) continue;
                if (x > 0) v = Math.Min(v, d[k - 1] + 1);
                if (y > 0) { v = Math.Min(v, d[k - n] + 1); if (x > 0) v = Math.Min(v, d[k - n - 1] + D); if (x < n - 1) v = Math.Min(v, d[k - n + 1] + D); }
                d[k] = v;
            }
        for (int y = n - 1; y >= 0; y--)
            for (int x = n - 1; x >= 0; x--)
            {
                int k = y * n + x; float v = d[k];
                if (v == 0) continue;
                if (x < n - 1) v = Math.Min(v, d[k + 1] + 1);
                if (y < n - 1) { v = Math.Min(v, d[k + n] + 1); if (x < n - 1) v = Math.Min(v, d[k + n + 1] + D); if (x > 0) v = Math.Min(v, d[k + n - 1] + D); }
                d[k] = v;
            }
        for (int k = 0; k < d.Length; k++) if (d[k] >= Big) d[k] = n;   // (no land at all / all land: far)
        return d;
    }

    /// <summary>Island i's outline (m by bearing).</summary>
    public static float[] RimOf(int i)
    {
        var r = new float[Bearings];
        Array.Copy(Rim, i * Bearings, r, 0, Bearings);
        return r;
    }

    /// <summary>How deep island i hangs below its top (m) - deep, as the user's reference: an inverted mountain at least
    /// as deep as the island is wide (2026-10-26: at 0.75-1.15 x the radius they read as thin cut-out plates). Main
    /// islands 1.2-2 x their radius, the brush-authored heroes (30 m grids) 1.7-2.4 x, satellites 1.2-2.4 x; never down
    /// into the core's reach (41 km).</summary>
    public static float DepthOf(int i)
    {
        float r = Radius[i], h = Noise.Hash01(i, 17, 1915);
        float d = Satellite[i] ? r * (1.2f + 1.2f * h) : Step[i] < 45f ? r * (1.7f + 0.7f * h) : r * (1.2f + 0.8f * h);
        return Math.Clamp(d, 20f, Math.Max(20f, Top[i] - 41000f));
    }

    public static Vector3 DirOf(int i) => new(Dir[i * 3], Dir[i * 3 + 1], Dir[i * 3 + 2]);
}
