using Keen.VRage.Library.Mathematics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// Where Britannica's islands go (2026-10-26, the user's brief: "very dense clumps in some places, others just sparse
/// occasional islands", islands sometimes over one another, nothing alive below the clouds). Over the WHOLE planet:
///  - a low-frequency density field on the sphere: where it is high, islands crowd into clumps (tight channels, mostly
///    smaller ones); where it is low they are rare and far apart - open sky;
///  - sizes on a long-tailed power law (650 m to ~9 km radius, giants rare); spacing that scales with the neighbours' sizes;
///  - a living island's top floats 0.3-7 km above the cloud deck (its own vertical offset: many levels); ~12 % are burnt
///    and hang below the clouds;
///  - ~6 % are stacked: shifted sideways over a lower island, clear of its highest terrain, their tops cut from their
///    own place on the terrain;
///  - big islands are cracked into plates now and then (the SE1 look), most are whole;
///  - satellites: tiny rocks (20-180 m) just off most islands' coasts.
/// Deterministic: the same seed gives the same islands on the server and every client.
/// </summary>
public static class BritannicaLayout
{
    public struct Island
    {
        public Vector3 Dir, SourceDir;
        public float Top, Radius, Depth, Plate, Crack, Step;
        public int Seed;
        public BritannicaBiomes.Biome Biome;
        public bool Stacked, Satellite;
    }

    static Vector3 RandomDir(Random rnd)
    {
        float z = (float)(rnd.NextDouble() * 2 - 1), a = (float)(rnd.NextDouble() * Math.PI * 2), s = MathF.Sqrt(1 - z * z);
        return new Vector3(s * MathF.Cos(a), s * MathF.Sin(a), z);
    }

    /// <summary>A direction at a random heading and arc distance (m) from `dir`.</summary>
    static Vector3 Around(Vector3 dir, float arc, Random rnd)
    {
        var e1 = Vector3.Normalize(Vector3.Cross(dir, Math.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var e2 = Vector3.Cross(dir, e1);
        float a = (float)(rnd.NextDouble() * Math.PI * 2), th = arc / BritannicaWorld.PlanetRadius;
        return Vector3.Normalize(dir * MathF.Cos(th) + (e1 * MathF.Cos(a) + e2 * MathF.Sin(a)) * MathF.Sin(th));
    }

    public static List<Island> Generate(int seed, int count)
    {
        const float R = BritannicaWorld.PlanetRadius;
        var rnd = new Random(seed);
        var result = new List<Island>();
        // the clumps: centres, sizes (radius of the crowd, m) and a theme biome each
        const int Clumps = 10;
        var clumps = new List<(Vector3 dir, float size, BritannicaBiomes.Biome theme)>();
        for (int c = 0; c < Clumps; c++)
            clumps.Add((RandomDir(rnd), 6000f + 16000f * (float)rnd.NextDouble(), (BritannicaBiomes.Biome)(c % BritannicaBiomes.AliveCount)));
        int inClumps = (int)(count * 0.65f);
        for (int attempt = 0; attempt < count * 300 && result.Count < count; attempt++)
        {
            bool clumped = result.Count < inClumps;
            int ci = rnd.Next(Clumps);
            var cl = clumps[ci];
            // (in a clump: a gaussian crowd round its centre; elsewhere: anywhere)
            float g = MathF.Abs((float)(rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) / 1.5f);
            var dir = clumped ? Around(cl.dir, g * cl.size, rnd) : RandomDir(rnd);
            float u = (float)rnd.NextDouble();
            // (a long tail: most 0.5-2 km, now and then a giant up to ~9 km - the user, 2026-10-26: "much larger islands")
            float radius = (650f + 8400f * MathF.Pow(u, 3.4f)) * (clumped ? 0.8f : 1f);
            bool ok = true;
            foreach (var o in result)
            {
                if (o.Stacked) continue;
                float arc = R * MathF.Acos(Math.Clamp(Vector3.Dot(dir, o.Dir), -1f, 1f));
                float gap = clumped ? 120f + 0.2f * (radius + o.Radius)      // a crowd: narrow channels
                                    : 4000f + 1.5f * (radius + o.Radius);    // the open: far apart
                if (arc < radius + o.Radius + gap) { ok = false; break; }
            }
            if (!ok) continue;
            bool burnt = rnd.NextDouble() < 0.12;
            float depth = Math.Min(radius * (0.75f + 0.4f * (float)rnd.NextDouble()), 5500f);
            float top = burnt
                ? BritannicaWorld.BurntTopMin + (BritannicaWorld.BurntTopMax - BritannicaWorld.BurntTopMin) * (float)rnd.NextDouble()
                : BritannicaWorld.AliveTopMin + 600f + (BritannicaWorld.AliveTopMax - BritannicaWorld.AliveTopMin - 600f) * MathF.Pow((float)rnd.NextDouble(), 0.8f);
            // (a giant's flat plane rises off the sphere toward its rim: keep its edges inside the storage)
            if (radius > 4000f) top = Math.Min(top, BritannicaWorld.AliveTopMax - radius * radius / (2f * R) - 800f);
            bool cracked = radius > 1500f && rnd.NextDouble() < 0.3;
            var biome = burnt ? BritannicaBiomes.Biome.Burnt
                : clumped && rnd.NextDouble() > 0.2 ? cl.theme
                : (BritannicaBiomes.Biome)rnd.Next(BritannicaBiomes.AliveCount);
            result.Add(new Island
            {
                Dir = dir, SourceDir = dir, Top = top, Radius = radius, Depth = depth,
                Plate = Math.Clamp(radius * (0.45f + 0.25f * (float)rnd.NextDouble()), 350f, 1900f),
                Crack = cracked ? 60f + 80f * (float)rnd.NextDouble() : 0f,
                Step = cracked ? 120f + 140f * (float)rnd.NextDouble() : 30f,
                Seed = rnd.Next(1, 1 << 20), Biome = biome,
            });
        }
        // stacks: a smaller island over a lower, larger one, shifted sideways, clear of its highest terrain (+-1.3 km)
        int stacks = (int)(result.Count * 0.06f);
        for (int k = 0, tries = 0; k < stacks && tries < stacks * 40; tries++)
        {
            var a = result[rnd.Next(result.Count)];
            if (a.Stacked || a.Biome == BritannicaBiomes.Biome.Burnt || a.Radius < 1200f || a.Top > 60500f) continue;
            float radius = a.Radius * (0.3f + 0.25f * (float)rnd.NextDouble());
            float depth = radius * (0.75f + 0.4f * (float)rnd.NextDouble());
            float top = a.Top + 1300f + depth * 1.6f + 200f + 900f * (float)rnd.NextDouble();
            if (top > BritannicaWorld.AliveTopMax) continue;
            // (the sideways shift: 20-80 % of the lower island's radius, any heading)
            var e1 = Vector3.Normalize(Vector3.Cross(a.Dir, Math.Abs(a.Dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var e2 = Vector3.Cross(a.Dir, e1);
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), off = a.Radius * (0.2f + 0.6f * (float)rnd.NextDouble());
            var dir = Vector3.Normalize(a.Dir * R + (e1 * MathF.Cos(ang) + e2 * MathF.Sin(ang)) * off);
            result.Add(new Island
            {
                Dir = dir, SourceDir = RandomDir(rnd), Top = top, Radius = radius, Depth = depth,
                Plate = Math.Clamp(radius * 0.6f, 350f, 1900f), Crack = 0f, Step = 30f,
                Seed = rnd.Next(1, 1 << 20), Biome = (BritannicaBiomes.Biome)rnd.Next(BritannicaBiomes.AliveCount), Stacked = true,
            });
            k++;
        }
        // satellites (the user, 2026-10-26: "very tiny ones near by"): a few rocks of 20-180 m round most islands, just off
        // the coast, near the parent's top level, same biome
        int parents = result.Count;
        for (int p = 0; p < parents; p++)
        {
            var a = result[p];
            int n = rnd.Next(0, 3 + (int)(a.Radius / 1200f));
            var e1 = Vector3.Normalize(Vector3.Cross(a.Dir, Math.Abs(a.Dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var e2 = Vector3.Cross(a.Dir, e1);
            for (int s = 0; s < n; s++)
            {
                float radius = 20f + 160f * MathF.Pow((float)rnd.NextDouble(), 2f);
                float ang = (float)(rnd.NextDouble() * Math.PI * 2), off = a.Radius * (1.35f + 0.5f * (float)rnd.NextDouble()) + radius * 2f + 60f;
                var dir = Vector3.Normalize(a.Dir * R + (e1 * MathF.Cos(ang) + e2 * MathF.Sin(ang)) * off);
                bool clear = true;
                foreach (var o in result)
                {
                    float arc = R * MathF.Acos(Math.Clamp(Vector3.Dot(dir, o.Dir), -1f, 1f));
                    if (arc < o.Radius * 1.3f + radius + 40f && MathF.Abs(o.Top - a.Top) < o.Depth + 1500f) { clear = false; break; }
                }
                if (!clear) continue;
                float top = Math.Min(a.Top - 350f + 600f * (float)rnd.NextDouble(), BritannicaWorld.AliveTopMax);
                if (a.Biome != BritannicaBiomes.Biome.Burnt) top = Math.Max(top, BritannicaWorld.AliveTopMin);
                result.Add(new Island
                {
                    Dir = dir, SourceDir = dir, Top = top, Radius = radius, Depth = radius * (0.9f + 0.9f * (float)rnd.NextDouble()),
                    Plate = Math.Max(radius * 0.9f, 30f), Crack = 0f, Step = 6f,
                    Seed = rnd.Next(1, 1 << 20), Biome = a.Biome, Satellite = true,
                });
            }
        }
        return result;
    }
}
