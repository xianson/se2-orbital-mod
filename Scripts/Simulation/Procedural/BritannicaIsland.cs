using Keen.VRage.Library.Mathematics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// One Britannica sky island as a procedural voxel shape (2026-10-26; see <see cref="BritannicaWorld"/>).
///
/// In the island's frame (up = away from the planet's centre at its anchor; x, y across the plan):
///  - top: cut from the one terrain (<see cref="BritannicaTerrain"/>) at the island's SOURCE place (<see cref="SourceDir"/>
///    - usually where it floats; shifted for an island stacked over another), relative to the terrain at the source's
///    centre, so the anchor's radius is the island's own vertical offset; the coast drops into a cliff lip;
///  - outline: a noisy blob of <see cref="Radius"/> - or, for a cracked island, the domain-warped Voronoi plates whose
///    centres lie inside it (the coast follows the cracks), each plate its own small step;
///  - underside: every plate (cells exist on every island; only cracked ones open them) its own hanging lobe - domed
///    massif to spiky fang - deeper toward the middle (<see cref="Depth"/>), strata ledges, ridged gullies;
///  - materials: the island's biome (<see cref="Palette"/>): top soil (patches of a second) on gentle slopes, cliff rock
///    on steep ground and the first tens of metres down, the underside's rock below.
/// Every term varies across the plan only: four plan maps (top, underside, side/crack, slope) built lazily in cached tiles
/// (<see cref="PlanTiles"/>) at the resolution each request needs. A voxel is max(h - top, underside - h, side).
/// </summary>
public sealed class BritannicaIsland : ProcShape
{
    public Vector3 Anchor;            // planet-local centre of the island's top plane (metres)
    public Vector3 SourceDir;         // where on the terrain its top is cut from (unit; default: the anchor's direction)
    public float Radius = 2500, Depth = 2400, RimThickness = 60, PlateSize = 1400, CrackWidth = 0, PlateStep = 40, CoastDrop = 70;
    /// <summary>An authored island (BritannicaSea, 2026-10-26): its outline - <see cref="BritannicaSea.Bearings"/> radii (m)
    /// by bearing from -pi in the e1 / e2 frame - and its real-terrain top, GridN x GridN heights (m above its low ground)
    /// <see cref="BritannicaSea.GridStep"/> m apart, centred. Null: the procedural blob and terrain.</summary>
    public float[] Rim, Grid, Sdf;
    public int GridN;
    /// <summary>The grid's spacing (m): 60 for most, 30 for the brush-authored heroes (BritannicaSea.Step).</summary>
    public float GridStep = BritannicaSea.GridStep;
    float _gridMax;
    public int Seed = 1;
    public BritannicaBiomes.Biome Biome;
    /// <summary>The biome's materials as the planet's storage indices: top, top patches, cliffs, underside (ProcShapeBridge
    /// resolves them when the shape is added).</summary>
    public byte[] Palette = { 255, 255, 255, 255 };
    Vector3 _up, _e1, _e2, _s1, _s2;
    float _reach, _topMax, _bottomMin, _topRef;
    int _id;
    static int _nextId;

    public BritannicaIsland Init(Vector3 planetCenterStorage, Vector3 anchorLocal)
    {
        Center = planetCenterStorage; Anchor = anchorLocal;
        _up = Vector3.Normalize(anchorLocal);
        _e1 = Vector3.Normalize(Vector3.Cross(_up, Math.Abs(_up.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        _e2 = Vector3.Cross(_up, _e1);
        if (SourceDir == Vector3.Zero) SourceDir = _up;
        SourceDir = Vector3.Normalize(SourceDir);
        _s1 = Vector3.Normalize(Vector3.Cross(SourceDir, Math.Abs(SourceDir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        _s2 = Vector3.Cross(SourceDir, _s1);
        _topRef = BritannicaTerrain.Height(SourceDir, 64f);
        if (Grid != null) { _gridMax = 0; for (int k = 0; k < Grid.Length; k++) if (Sdf == null || Sdf[k] > -60f) _gridMax = Math.Max(_gridMax, Grid[k]); }
        if (Grid != null)
        {
            // (the box the island really fills - 2026-10-26: a fixed 1.5 km allowance from the old procedural terrain gave
            //  even a 30 m rock a 3.4 km box; in a dense clump every voxel chunk overlapped hundreds of shapes, each filling
            //  all its voxels: the first view among the islands froze the game 7 min)
            _topMax = _gridMax + 20f + PlateStep;
            _bottomMin = -(CoastDrop + RimThickness + Depth * 1.65f + StalactiteMax + 40f);
            _reach = (GridN - 1) * 0.5f * GridStep + Radius * 0.1f + 60f;          // (lateral: the grid, the walls' jut)
        }
        else
        {
            _topMax = 1400f + PlateStep + 100; _bottomMin = -1400f - Depth * 1.6f - 100 - StalactiteMax;
            _reach = Math.Max(Radius * 1.55f, Depth * 1.6f + 1500f + StalactiteMax);
        }
        // (the oriented box - lateral +-_reach along e1 / e2, _bottomMin.._topMax along up - as an axis-aligned one)
        float hv = (_topMax - _bottomMin) * 0.5f;
        Vector3 c = Center + Anchor + _up * ((_topMax + _bottomMin) * 0.5f);
        Vector3 ext = Abs(_up) * hv + (Abs(_e1) + Abs(_e2)) * _reach;
        Box = new BoundingBox(c - ext, c + ext);
        // (never pruned: the composition drops a shape whose LodSize is below the request's lod + CompositionLodPruning, so
        //  from far away the islands vanished and only the planet's core was drawn - 2026-10-26. A coarse lod has few voxels
        //  per island: keeping them costs little)
        LodSize = 30;
        _id = System.Threading.Interlocked.Increment(ref _nextId);
        return this;
    }

    void Frame(Vector3 local, out float h, out float x, out float y)
    {
        Vector3 d = local - Anchor;
        h = Vector3.Dot(d, _up); x = Vector3.Dot(d, _e1); y = Vector3.Dot(d, _e2);
    }

    public override float Distance(Vector3 local, float voxel)
    {
        Frame(local, out float h, out float x, out float y);
        float r2 = x * x + y * y, rmax = Grid != null ? _reach : Radius * 1.5f;
        if (r2 > rmax * rmax) return MathF.Sqrt(r2) - rmax;   // (far outside: a cheap bound, no maps)
        if (h > _topMax) return h - _topMax;
        if (h < _bottomMin) return _bottomMin - h;
        SamplePlan(x, y, voxel, out float top, out float bottom, out float side, out _);
        float d0 = Math.Max(h - top, bottom - h);
        // (the wall's 3D noise only where it can change the answer: the density is clamped at +-1 voxel, and the wall only
        //  pushes the coast inward - clearly outside by the top / underside, or deeper inside than it reaches, it cannot.
        //  2026-10-26: with the deep undersides most of each box is sky under the island, and paid for the wall anyway)
        if (d0 >= voxel || side <= -WallReach - voxel) return Math.Max(d0, side);
        side = WallSide(x, y, h, top - h, voxel, side);
        return Math.Max(d0, side);
    }

    /// <summary>The plan maps at (x, y) for `voxel`: from the cached tiles near the island; at a coarse lod (voxels over a
    /// quarter of the island) straight from <see cref="Plan"/> at the point - a far clipmap needs a handful of samples per
    /// island, and building tiles for every level of the whole planet at load thrashed the cache and hung the game
    /// (2026-10-26, planeteditor's stacks).</summary>
    void SamplePlan(float x, float y, float voxel, out float top, out float bottom, out float side, out float slope)
    {
        if (voxel * 4f >= Radius) { Plan(x, y, voxel, out top, out bottom, out side); slope = 0f; return; }
        PlanTiles.Sample(this, x, y, voxel, out top, out bottom, out side, out slope);
    }

    /// <summary>How far inside the coast <see cref="Wall"/> can reach (its largest inset).</summary>
    float WallReach => Math.Min(Depth, 1600f) * 0.9f + 250f;

    /// <summary>The side wall (2026-10-26, the user: the smooth band round every island had to go; then "a weird cone with
    /// dimples, need more character"). Big forms, not texture - added to the plan's side term (positive = outside),
    /// going down from the top (`below`, m):
    ///  - the silhouette: how fast the wall steps in varies round the island - sheer cliffs on one side, steep slopes on
    ///    another;
    ///  - rock blocks: the wall is broken into big faceted blocks - Voronoi cells across the plan times strata bands in the
    ///    height - each set in or jutting out (overhangs, shelves) by its own amount;
    ///  - buttresses: massive ridged ribs running down the faces;
    ///  - gullies: a few big vertical ones.
    /// Height-dependent, so it runs per voxel, but only within <see cref="WallReach"/> of the coast.</summary>
    float WallSide(float x, float y, float h, float below, float voxel, float side)
    {
        if (below <= 0f) return side;
        float fade = Math.Clamp(below / 30f, 0f, 1f);   // (the coast's lip itself stays put)
        // the silhouette: sheer (0.08) to sloped (1.0) by sector
        float sector = Math.Clamp(Noise.Fbm(x / (Radius * 0.7f), y / (Radius * 0.7f), Seed + 70, 2) * 1.6f + 0.5f, 0f, 1f);
        float taper = 0.08f + 0.92f * sector * sector;
        float cellS = Math.Clamp(Radius * 0.13f, 50f, 420f), bandH = Math.Clamp(Radius * 0.07f, 30f, 200f);
        // (the detail's bounds - blocks -0.455..0.845 bandH, ribs down to -0.99 x their size, gullies 0..70 m: the costly
        //  3D part only where it can decide the voxel - within that much of the wall's surface. 2026-10-26: the wall was
        //  evaluated across its whole 1.6 km reach inside every deep island, 19 s of a server stall - planeteditor's trace)
        float ribC = Math.Clamp(Radius * 0.05f, 25f, 260f), slope = below * taper;
        float lo = side + Math.Min(slope - 0.455f * bandH - 0.99f * ribC, WallReach) * fade;
        float hi = side + Math.Min(slope + 0.845f * bandH + 70f, WallReach) * fade;
        if (hi <= -voxel) return hi;   // (solid whatever the detail)
        if (lo >= voxel) return lo;    // (sky whatever the detail)
        return side + Wall(x, y, h, below, voxel, fade, taper, cellS, bandH);
    }

    float Wall(float x, float y, float h, float below, float voxel, float fade, float taper, float cellS, float bandH)
    {
        // rock blocks: plan cells x strata bands (the bands' boundaries wander a little)
        Noise.VoronoiCell(x / cellS, y / cellS, Seed + 71, out int bx, out int by, out _, out _, out float bedge);
        float bq = (below + bandH * 0.45f * Noise.Perlin(x / (cellS * 2.5f), y / (cellS * 2.5f), Seed + 72)) / bandH;
        int band = (int)MathF.Floor(bq);
        float hb = Noise.Hash01(bx * 7919 + band, by * 104729 - band, Seed + 73);
        float block = (hb - 0.35f) * bandH * 1.3f;   // (a third jut out: overhangs and shelves)
        // buttresses: ribs in the plan, so they run straight down
        float rib = -Math.Clamp(Radius * 0.05f, 25f, 260f) * MathF.Max(0f, Noise.Ridged(x / (Radius * 0.22f), y / (Radius * 0.22f), Seed + 74, 2) - 0.55f) * 2.2f;
        // gullies: few, big, vertical
        float gully = voxel < 60f ? 70f * MathF.Max(0f, Noise.Ridged3(new Vector3(x / 140f, y / 140f, h / 900f), Seed + 75, voxel < 10f ? 2 : 1) - 0.6f) * 2.5f : 0f;
        float inset = below * taper + block + rib + gully;
        return Math.Min(inset, WallReach) * fade;
    }

    public override byte Material(Vector3 local, float voxel)
    {
        Frame(local, out float h, out float x, out float y);
        // (Distance's early-outs: outside the island the material is never used - the density decides - and sampling
        //  there built plan tiles over empty sky round every island; 2026-10-26 the load hung)
        float r2 = x * x + y * y, rmax = Grid != null ? _reach : Radius * 1.5f;
        if (r2 > rmax * rmax || h > _topMax || h < _bottomMin) return Palette[3];
        SamplePlan(x, y, voxel, out float top, out _, out _, out float slope);
        float below = top - h;
        if (below < Math.Max(3f, voxel * 1.5f) && slope < 0.75f)
            return Noise.Fbm(x / 180f, y / 180f, Seed + 21, 2) > 0.18f ? Palette[1] : Palette[0];   // top soil, patches
        if (below < 45f + voxel) return Palette[2];   // cliff rock: steep ground, plate walls, the coast's lip
        return Palette[3];   // the underside
    }

    /// <summary>The plan maps at one point for map cells of `cell` metres: the top's and the underside's heights (along up,
    /// from the anchor's top plane) and the side term (positive off the island or in a crack, metres).</summary>
    internal void Plan(float x, float y, float cell, out float topOut, out float bottomOut, out float sideOut)
    {
        int oct(float scale) => Math.Clamp((int)Math.Ceiling(Math.Log2(scale / Math.Max(cell, 1f))), 1, 6);
        float ps = PlateSize;
        float wx = x + ps * 0.28f * Noise.Fbm(x / ps * 0.8f, y / ps * 0.8f, Seed + 5, oct(ps * 0.6f));
        float wy = y + ps * 0.28f * Noise.Fbm(x / ps * 0.8f + 9.1f, y / ps * 0.8f - 4.3f, Seed + 6, oct(ps * 0.6f));
        Noise.VoronoiCell(wx / ps, wy / ps, Seed + 3, out int cx, out int cy, out float sx, out float sy, out float edge);
        sx *= ps; sy *= ps; edge *= ps;
        float h1 = Noise.Hash01(cx, cy, Seed + 11), h2 = Noise.Hash01(cx, cy, Seed + 12), h3 = Noise.Hash01(cx, cy, Seed + 13);
        float r = MathF.Sqrt(x * x + y * y);
        float rimAt(float px, float py) => Radius * (1f + 0.28f * Noise.Fbm(px / Radius * 1.4f, py / Radius * 1.4f, Seed, oct(Radius / 1.4f)));

        // the outline: a cracked island's coast follows its plates; an uncracked one is a noisy blob
        float side, coast;
        if (CrackWidth > 0)
        {
            float sr = MathF.Sqrt(sx * sx + sy * sy);
            float crackHalf = CrackWidth * (0.35f + 0.3f * Noise.Fbm(wx / ps * 6f, wy / ps * 6f, Seed + 4, oct(ps / 6f)));
            if (sr > rimAt(sx, sy)) { topOut = 0; bottomOut = 0; sideOut = edge + crackHalf; return; }
            side = crackHalf - edge;
            coast = edge;   // (distance to the plate's own border: every plate has its lip)
        }
        else
        {
            if (Sdf != null)
            {
                // (the authored coast: a contour of the island's real terrain, as a signed distance - + inside - its edge
                //  broken a few metres finer than the grid)
                int o2 = Math.Clamp((int)Math.Ceiling(Math.Log2(90f / Math.Max(cell, 1f))), 1, 3);
                float d = GridSample(Sdf, x, y, -600f) + 10f * Noise.Fbm(x / 90f, y / 90f, Seed + 83, o2);
                side = -d;
                coast = d;
            }
            else
            {
                float rim = Rim != null ? RimAt(x, y, cell) : rimAt(x, y);
                side = r - rim;
                coast = rim - r;
            }
        }

        // the top: the terrain at the source place, relative to the source's centre; a small step per plate; the lip
        Vector3 src = Vector3.Normalize(SourceDir * BritannicaWorld.PlanetRadius + _s1 * x + _s2 * y);
        float step = (h1 - 0.5f) * PlateStep;
        float top = (Grid != null ? GridAt(x, y) + MicroRelief(x, y, cell) : BritannicaTerrain.Height(src, cell) - _topRef) + step;
        float t = Math.Clamp(coast / 260f, 0f, 1f);
        top -= CoastDrop * (1f - t * t * (3f - 2f * t));

        // the underside: per-plate hanging lobes, deeper toward the middle, ledged, gullied - hung from the island's
        // top plane (not each point's terrain: the top's hills don't show through underneath)
        float tI = Math.Clamp(1f - r / Math.Max(Radius, 1f), 0f, 1f);
        float dx = wx - sx, dy = wy - sy;
        float ds = MathF.Sqrt(dx * dx + dy * dy) / (ps * 0.85f);
        float lobe = MathF.Pow(Math.Clamp(1f - ds * ds, 0f, 1f), 0.55f + 1.6f * h3 * h3);
        const float Ledges = 6f;
        float lk = lobe * Ledges, lf = lk - MathF.Floor(lk);
        lobe = (MathF.Floor(lk) + lf * lf * lf * (lf * (lf * 6 - 15) + 10)) / Ledges * 0.7f + lobe * 0.3f;
        float plateDepth = Depth * (0.25f + 0.75f * MathF.Pow(tI, 0.7f)) * (0.55f + 0.7f * h2);
        float ridge = Noise.Ridged(x / ps * 2.4f - 3.7f, y / ps * 2.4f + 5.2f, Seed + 2, oct(ps / 2.4f));
        float bottom = step - CoastDrop - RimThickness - plateDepth * lobe * (0.55f + 0.75f * ridge);

        // stalactites (2026-10-26, the user: "thick stalactites, rocky, sharp, chunky"): faceted pyramids - the height grows
        // with the distance to the Voronoi cell's border, so the faces are flat and the tip is sharp - in two sizes, most
        // cells hung, the longest toward the middle, none at the coast; then fine ridged rock over all of it
        float fade = Math.Clamp(coast / 120f, 0f, 1f) * (0.45f + 0.55f * tI);
        float spikes = 0f;
        for (int layer = 0; layer < 2; layer++)
        {
            float s = layer == 0 ? StalS0 : StalS1;
            if (s < cell * 2f) continue;   // (finer than this request's cells: invisible)
            float gx = x + s * 0.35f * Noise.Perlin(x / s * 0.7f, y / s * 0.7f, Seed + 31 + layer);
            float gy = y + s * 0.35f * Noise.Perlin(x / s * 0.7f + 5.3f, y / s * 0.7f - 2.1f, Seed + 33 + layer);
            Noise.VoronoiCell(gx / s, gy / s, Seed + 41 + layer, out int kx, out int ky, out _, out _, out float e);
            float hk = Noise.Hash01(kx, ky, Seed + 43 + layer);
            if (hk < 0.25f) continue;   // (a quarter bare)
            float len = s * (layer == 0 ? 1.6f + 2.6f * hk * hk : 1.2f + 1.6f * hk);
            float k = Math.Clamp(e * 2.4f, 0f, 1f);   // (0 at the border, 1 near the middle)
            spikes += len * MathF.Pow(k, 1.35f);
        }
        float rock = 30f * Noise.Ridged(x / 70f + 1.3f, y / 70f - 7.7f, Seed + 51, oct(70f)) + 14f * Noise.Fbm(x / 25f, y / 25f, Seed + 52, oct(25f));
        bottom -= (spikes + rock) * fade;
        topOut = top;
        bottomOut = Math.Min(bottom, top - RimThickness);
        sideOut = side;
    }

    internal int Id => _id;

    /// <summary>The authored outline at (x, y): the profile between its two nearest bearings, its edge broken a little
    /// (a few % of the radius, ~40 m and finer) so straight polygon sides read as rock.</summary>
    float RimAt(float x, float y, float cell)
    {
        int n = Rim.Length;
        float t = (MathF.Atan2(y, x) + MathF.PI) / (2 * MathF.PI) * n - 0.5f;
        int i0 = (int)MathF.Floor(t); float f = t - i0;
        float r0 = Rim[((i0 % n) + n) % n], r1 = Rim[(((i0 + 1) % n) + n) % n];
        float r = r0 + (r1 - r0) * f;
        int oct = Math.Clamp((int)Math.Ceiling(Math.Log2(160f / Math.Max(cell, 1f))), 1, 4);
        return r + Math.Min(r * 0.06f, 45f) * Noise.Fbm(x / 160f, y / 160f, Seed + 81, oct);
    }

    /// <summary>The real-terrain top at (x, y): the grid's bilinear sample (clamped at its border).</summary>
    float GridAt(float x, float y) => GridSample(Grid, x, y, float.NaN);

    /// <summary>A GridN x GridN map (GridStep apart, centred) sampled bilinearly at (x, y); outside it: clamped at its
    /// border, or - when `outside` is a number - that value less the distance beyond the border (a signed distance).</summary>
    float GridSample(float[] map, float x, float y, float outside)
    {
        float c = (GridN - 1) * 0.5f;
        float fxr = x / GridStep + c, fyr = y / GridStep + c;
        float gx = Math.Clamp(fxr, 0, GridN - 1.001f), gy = Math.Clamp(fyr, 0, GridN - 1.001f);
        if (!float.IsNaN(outside) && (gx != fxr || gy != fyr))
            return outside - MathF.Sqrt((gx - fxr) * (gx - fxr) + (gy - fyr) * (gy - fyr)) * GridStep;
        int ix = (int)gx, iy = (int)gy; float fx = gx - ix, fy = gy - iy;
        int k = iy * GridN + ix;
        float a = map[k] + (map[k + 1] - map[k]) * fx, b = map[k + GridN] + (map[k + GridN + 1] - map[k + GridN]) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>Relief finer than the 60 m grid (a few metres, only where this request's cells resolve it): the
    /// planet-editing rule "never 100 % flat" for the real terrain's smoothed samples.</summary>
    static float MicroRelief(float x, float y, float cell) =>
        cell >= 40f ? 0f : 4f * Noise.Fbm(x / 35f, y / 35f, 977, cell < 8f ? 3 : 2);
    /// <summary>The stalactites' and rock's longest reach below the lobes (both layers at full length, plus the rock).</summary>
    float StalactiteMax => StalS0 * 4.2f + StalS1 * 2.8f + Math.Min(50f, Radius * 0.3f);
    /// <summary>The stalactites' cell sizes (big, small): with the island - a 30 m rock got 190 m spikes at the old 45 m floor.</summary>
    float StalS0 => Math.Clamp(Radius * 0.11f, 6f, 380f);
    float StalS1 => Math.Clamp(Radius * 0.035f, 3f, 110f);
    static Vector3 Abs(Vector3 v) => new(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
}

/// <summary>The islands' plan maps, built lazily in tiles and cached (bounded). A tile is 64 x 64 cells (+1 border for
/// bilinear sampling) of 4 floats - top, underside, side, slope - at level L (cells of BaseCell x 2^L). A request picks the
/// level whose cells are no bigger than its voxels. Thread-safe: tiles are immutable once built; a race builds one twice.
/// Bounded at <see cref="MaxTiles"/> (~68 KB a tile): past it the oldest quarter goes (2026-10-26: clearing it ALL at
/// 3000 thrashed with 275 islands - 52k tiles built, 234 kept, 300 ns a voxel, islands streaming in for minutes).</summary>
public static class PlanTiles
{
    const int T = 64, C = 4;
    const float BaseCell = 4f;   // (2 m tiles cost 16 s of plan building in a stall; the tops are 30-60 m grids: 4 m loses nothing)
    const int MaxLevel = 5;   // (64 m cells)
    public static int MaxTiles = 6000;
    static readonly System.Collections.Concurrent.ConcurrentQueue<long> _order = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, float[]> _tiles = new();
    [ThreadStatic] static long _lastKey;
    [ThreadStatic] static float[] _last;
    public static long Built;

    public static void Sample(BritannicaIsland isl, float x, float y, float voxel, out float top, out float bottom, out float side, out float slope)
    {
        int level = Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(voxel, BaseCell) / BaseCell)), 0, MaxLevel);
        float cell = BaseCell * (1 << level);
        float gx = x / cell, gy = y / cell;
        int ix = (int)MathF.Floor(gx), iy = (int)MathF.Floor(gy);
        int tx = ix >> 6, ty = iy >> 6;   // (T = 64; arithmetic shift floors negatives)
        long key = ((long)isl.Id << 44) | ((long)level << 40) | ((long)(tx & 0xFFFFF) << 20) | (long)(ty & 0xFFFFF);
        float[] t = _last;
        if (t == null || _lastKey != key)
        {
            if (!_tiles.TryGetValue(key, out t))
            {
                t = Build(isl, tx, ty, cell);
                if (_tiles.TryAdd(key, t)) _order.Enqueue(key);
                if (_tiles.Count > MaxTiles)
                    while (_tiles.Count > MaxTiles * 3 / 4 && _order.TryDequeue(out long old)) _tiles.TryRemove(old, out _);
                System.Threading.Interlocked.Increment(ref Built);
            }
            _last = t; _lastKey = key;
        }
        float fx = gx - ix, fy = gy - iy;
        int lx = ix - (tx << 6), ly = iy - (ty << 6);
        int i00 = (ly * (T + 1) + lx) * C, i10 = i00 + C, i01 = i00 + (T + 1) * C, i11 = i01 + C;
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        top = t[i00] * w00 + t[i10] * w10 + t[i01] * w01 + t[i11] * w11;
        bottom = t[i00 + 1] * w00 + t[i10 + 1] * w10 + t[i01 + 1] * w01 + t[i11 + 1] * w11;
        side = t[i00 + 2] * w00 + t[i10 + 2] * w10 + t[i01 + 2] * w01 + t[i11 + 2] * w11;
        slope = t[i00 + 3] * w00 + t[i10 + 3] * w10 + t[i01 + 3] * w01 + t[i11 + 3] * w11;
    }

    static float[] Build(BritannicaIsland isl, int tx, int ty, float cell)
    {
        // (one cell of margin all round, for the slope's finite differences)
        int n = T + 3;
        var tops = new float[n * n];
        var a = new float[(T + 1) * (T + 1) * C];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                isl.Plan(((tx << 6) + i - 1) * cell, ((ty << 6) + j - 1) * cell, cell, out float top, out float bottom, out float side);
                tops[j * n + i] = top;
                if (i >= 1 && i <= T + 1 && j >= 1 && j <= T + 1)
                {
                    int k = ((j - 1) * (T + 1) + (i - 1)) * C; a[k] = top; a[k + 1] = bottom; a[k + 2] = side;
                }
            }
        for (int j = 1; j <= T + 1; j++)
            for (int i = 1; i <= T + 1; i++)
            {
                float gx = (tops[j * n + i + 1] - tops[j * n + i - 1]) / (2 * cell), gy = (tops[(j + 1) * n + i] - tops[(j - 1) * n + i]) / (2 * cell);
                a[((j - 1) * (T + 1) + (i - 1)) * C + 3] = MathF.Sqrt(gx * gx + gy * gy);
            }
        return a;
    }

    public static string Stats => $"plan tiles {_tiles.Count} cached, {Built} built";
}

/// <summary>Seeded gradient noise for the procedural shapes: pure functions over a fixed table (thread-safe).</summary>
public static class Noise
{
    static readonly int[] P = BuildPerm();
    static int[] BuildPerm()
    {
        var p = new int[512]; var rnd = new Random(1234567);
        var q = Enumerable.Range(0, 256).ToArray();
        for (int i = 255; i > 0; i--) { int j = rnd.Next(i + 1); (q[i], q[j]) = (q[j], q[i]); }
        for (int i = 0; i < 512; i++) p[i] = q[i & 255];
        return p;
    }

    static float Grad(int h, float x, float y)
    {
        switch (h & 7)
        {
            case 0: return x + y; case 1: return -x + y; case 2: return x - y; case 3: return -x - y;
            case 4: return x; case 5: return -x; case 6: return y; default: return -y;
        }
    }

    static float Grad(int h, float x, float y, float z)
    {
        int k = h & 15;
        float u = k < 8 ? x : y, v = k < 4 ? y : (k == 12 || k == 14 ? x : z);
        return ((k & 1) == 0 ? u : -u) + ((k & 2) == 0 ? v : -v);
    }

    static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>2D Perlin noise, about -1..1.</summary>
    public static float Perlin(float x, float y, int seed)
    {
        x += seed * 17.137f; y += seed * 31.71f;
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        int X = xi & 255, Y = yi & 255;
        float uu = Fade(xf), vv = Fade(yf);
        int aa = P[P[X] + Y], ab = P[P[X] + Y + 1], ba = P[P[X + 1] + Y], bb = P[P[X + 1] + Y + 1];
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), uu);
        float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), uu);
        return Lerp(x1, x2, vv) * 0.7f;
    }

    /// <summary>3D Perlin noise, about -1..1 (for fields on the sphere: the direction scaled).</summary>
    public static float Perlin3(Vector3 p, int seed)
    {
        float x = p.X + seed * 17.137f, y = p.Y + seed * 31.71f, z = p.Z + seed * 7.913f;
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
        float xf = x - xi, yf = y - yi, zf = z - zi;
        int X = xi & 255, Y = yi & 255, Z = zi & 255;
        float u = Fade(xf), v = Fade(yf), w = Fade(zf);
        int A = P[X] + Y, AA = P[A & 255] + Z, AB = P[(A + 1) & 255] + Z, B = P[X + 1] + Y, BA = P[B & 255] + Z, BB = P[(B + 1) & 255] + Z;
        float r = Lerp(Lerp(Lerp(Grad(P[AA & 511], xf, yf, zf), Grad(P[BA & 511], xf - 1, yf, zf), u),
                            Lerp(Grad(P[AB & 511], xf, yf - 1, zf), Grad(P[BB & 511], xf - 1, yf - 1, zf), u), v),
                       Lerp(Lerp(Grad(P[(AA + 1) & 511], xf, yf, zf - 1), Grad(P[(BA + 1) & 511], xf - 1, yf, zf - 1), u),
                            Lerp(Grad(P[(AB + 1) & 511], xf, yf - 1, zf - 1), Grad(P[(BB + 1) & 511], xf - 1, yf - 1, zf - 1), u), v), w);
        return r * 0.9f;
    }

    public static float Fbm(float x, float y, int seed, int octaves)
    {
        float s = 0, a = 1, n = 0;
        for (int i = 0; i < octaves; i++) { s += a * Perlin(x, y, seed + i * 101); n += a; a *= 0.5f; x *= 2.03f; y *= 2.03f; }
        return s / n;
    }

    public static float Fbm3(Vector3 p, int seed, int octaves)
    {
        float s = 0, a = 1, n = 0;
        for (int i = 0; i < octaves; i++) { s += a * Perlin3(p, seed + i * 101); n += a; a *= 0.5f; p *= 2.03f; }
        return s / n;
    }

    /// <summary>Ridged multifractal, 0..1: sharp crests (the underside's hanging spurs).</summary>
    public static float Ridged(float x, float y, int seed, int octaves)
    {
        float s = 0, a = 1, n = 0;
        for (int i = 0; i < octaves; i++) { float r = 1f - MathF.Abs(Perlin(x, y, seed + i * 131)); s += a * r * r; n += a; a *= 0.5f; x *= 2.07f; y *= 2.07f; }
        return s / n;
    }

    /// <summary>Ridged multifractal in 3D, 0..1 (mountain ranges on the sphere).</summary>
    public static float Ridged3(Vector3 p, int seed, int octaves)
    {
        float s = 0, a = 1, n = 0;
        for (int i = 0; i < octaves; i++) { float r = 1f - MathF.Abs(Perlin3(p, seed + i * 131)); s += a * r * r; n += a; a *= 0.5f; p *= 2.07f; }
        return s / n;
    }

    public static float Hash01(int x, int y, int seed) => Hash(x, y, seed);

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16)) / 4294967296f;
        }
    }

    /// <summary>The Voronoi cell the point falls in: its integer id, its site (cell units) and the distance (cell units)
    /// to the nearest border with another cell.</summary>
    public static void VoronoiCell(float x, float y, int seed, out int cellX, out int cellY, out float siteX, out float siteY, out float edge)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float d1 = float.MaxValue, d2 = float.MaxValue; float p1x = 0, p1y = 0, p2x = 0, p2y = 0; int c1x = 0, c1y = 0;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int cx = xi + i, cy = yi + j;
                float px = cx + 0.15f + 0.7f * Hash(cx, cy, seed), py = cy + 0.15f + 0.7f * Hash(cx, cy, seed + 7);
                float dx = px - x, dy = py - y, d = dx * dx + dy * dy;
                if (d < d1) { d2 = d1; p2x = p1x; p2y = p1y; d1 = d; p1x = px; p1y = py; c1x = cx; c1y = cy; }
                else if (d < d2) { d2 = d; p2x = px; p2y = py; }
            }
        float mx = (p1x + p2x) * 0.5f, my = (p1y + p2y) * 0.5f, nx = p2x - p1x, ny = p2y - p1y;
        float len = MathF.Sqrt(nx * nx + ny * ny);
        cellX = c1x; cellY = c1y; siteX = p1x; siteY = p1y;
        edge = len < 1e-6f ? 0 : MathF.Abs((x - mx) * nx + (y - my) * ny) / len;
    }
}
