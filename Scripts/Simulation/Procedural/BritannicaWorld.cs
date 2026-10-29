using Keen.VRage.Library.Mathematics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// Britannica, rebuilt (2026-10-26, the user's brief): NO ground - only sky islands, over the whole planet.
///  - Britannica is its own planet (definitions in Assets/Procedural/Planets/BritannicaFloor, radius <see cref="GameRadius"/>);
///    its surface sinks to an unreachable 40 km core (HillParams Min -0.2, heights 0);
///  - every island's top is cut from ONE terrain height map over the sphere (<see cref="BritannicaTerrain"/>) and lifted
///    or lowered by its own offset, so islands sit on many levels; now and then one is shifted sideways over another;
///  - one biome per island (<see cref="BritannicaBiomes"/>); nothing alive below the cloud deck - the few islands down
///    there are burnt;
///  - dense clumps in places, otherwise sparse, occasional islands (BritannicaLayout).
/// The heights are radii from the planet's centre (metres). The voxel storage is a cube reaching 65.5 km from the centre
/// along each axis (2R(1 + Max 0.07) rounds up to 131072 at R 50 km), so the whole band stays under ~64.5 km and fits in
/// every direction. The cloud deck is Britannica's own cloud definition (its shell starts at 1.0189 R = 50.9 km: the
/// reason R is 50 km); 1 g and air reach through the band (its prefab's gravity and atmosphere generators).
/// </summary>
public static class BritannicaWorld
{
    /// <summary>The island geometry's reference radius (arc lengths, the terrain's noise scale) - not the game radius.</summary>
    public const float PlanetRadius = 60000f;
    /// <summary>The planet's game radius (its configurator Radius: gravity, atmosphere and cloud shell scale from it).</summary>
    public const int GameRadius = 50000;
    /// <summary>The cloud sea's solid top (2026-10-26, the user: "the islands should be submerged in the clouds, but barely" -
    /// at about the islands' median top; planeteditor's cloud shell). Burnt islands hang well below it.</summary>
    public const float CloudTop = 59200f;
    /// <summary>Living islands' tops: from just above the clouds to just under the storage's reach.</summary>
    public const float AliveTopMin = CloudTop + 300f, AliveTopMax = 63600f;
    /// <summary>The burnt islands' tops: under the cloud sea's base (50.9 km), in the gloom.</summary>
    public const float BurntTopMin = 46000f, BurntTopMax = 50300f;
    /// <summary>The storage's reach from the centre along any axis (Kemik: 131072 / 2).</summary>
    public const float StorageHalf = 65536f;
}

/// <summary>The one terrain the island tops are cut from: a height (metres, about +-1.3 km) for every direction on the
/// sphere - ranges of ridged mountains over rolling uplands, valleys, small hills. Continuous over the whole sphere (3D
/// noise on the direction), so neighbouring islands cut from neighbouring places share their landforms. Octaves
/// finer than the sampling cell are skipped.</summary>
public static class BritannicaTerrain
{
    public static float Height(Vector3 dir, float cell)
    {
        const float R = BritannicaWorld.PlanetRadius;
        int oct(float wavelength) => Math.Clamp((int)Math.Ceiling(Math.Log2(wavelength / Math.Max(cell, 1f))), 1, 7);
        Vector3 p = dir * (R / 1000f);   // (km)
        float ranges = Noise.Fbm3(p * 0.09f, 101, 3);                               // where the mountain ranges run
        float mask = Math.Clamp(ranges * 1.8f + 0.25f, 0f, 1f);
        float mountains = Noise.Ridged3(p * 0.35f, 202, oct(2800f));                 // ridges ~3 km apart
        float uplands = Noise.Fbm3(p * 0.6f, 303, oct(1600f));                       // rolling uplands
        float knolls = Noise.Fbm3(p * 1.3f, 606, oct(770f));                        // knolls and hollows ~800 m (island scale)
        float hills = Noise.Fbm3(p * 4.5f, 404, oct(220f));                          // hills ~200 m
        return 1300f * mask * (mountains - 0.35f) + 300f * uplands + 450f * knolls + 120f * hills;   // (fbm sums sit around +-0.25: real relief a quarter of these)
    }
}

/// <summary>One biome per island: its materials (Kemik's own voxel materials, by definition Guid) - the top soil (and a
/// second one in patches), the cliffs (plate walls, steep slopes, the first tens of metres down) and the underside.</summary>
public static class BritannicaBiomes
{
    public enum Biome { Savanna, Desert, Highland, Badlands, Burnt }
    public const int AliveCount = 4;

    // Kemik's voxel materials (VS2_0 Kemik MaterialMaps placement rules)
    public static readonly Guid SoilSavanna = new("ce12d6a7-c915-46c0-a35b-1c8ba161f537"), SoilSavannaRocky = new("449cf6b7-a85d-4760-97fb-f1938a434702"),
        RocksSavanna = new("002442dd-b3cb-4d99-8ed2-e83767060b69"), SandSavanna = new("4e8f7725-3ae5-433e-bd50-246db5963c48"),
        Cliffs = new("18497b8b-cd09-46e7-b409-6799ca4361e8"), DesertCliffs = new("8a45dc1d-a026-4f50-b47c-fd92826da9be"),
        DesertCliffs02 = new("6af1451b-386f-4345-93d3-6f39d236d8ad"), AsteroidRocks = new("e0f41449-6873-471a-a492-41de84f1288c"),
        VolcanicAshes = new("01dfa3e0-edfa-49a0-96ff-93dacbcbf32d"), ColdMagma = new("27005d01-386d-4f9a-bbd9-f80f9d280d68"),
        VolcanicRocks = new("be265265-42f4-4609-80c0-7df3eb969a8d");

    /// <summary>top, top patches, cliffs, underside - per biome</summary>
    public static Guid[] Palette(Biome b) => b switch
    {
        Biome.Savanna => new[] { SoilSavanna, SoilSavannaRocky, Cliffs, AsteroidRocks },
        Biome.Desert => new[] { SandSavanna, SandSavanna, DesertCliffs, DesertCliffs02 },
        Biome.Highland => new[] { RocksSavanna, SoilSavannaRocky, Cliffs, AsteroidRocks },
        Biome.Badlands => new[] { SoilSavannaRocky, RocksSavanna, DesertCliffs02, Cliffs },
        _ => new[] { VolcanicAshes, ColdMagma, VolcanicRocks, VolcanicRocks },
    };

    /// <summary>The biome of a living island: regional (neighbours tend to share one - a clump has a theme), with a fifth
    /// of the islands breaking the pattern.</summary>
    public static Biome Alive(Vector3 dir, Random rnd)
    {
        if (rnd.NextDouble() < 0.2) return (Biome)rnd.Next(AliveCount);
        float v = Noise.Fbm3(dir * 3.2f, 505, 3) * 0.5f + 0.5f;
        return (Biome)Math.Clamp((int)(v * AliveCount * 1.15f - 0.3f), 0, AliveCount - 1);
    }
}
