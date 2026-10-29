using Keen.VRage.Library.Mathematics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>A mod voxel shape in a planet's storage space (see <see cref="ProcShapeBridge"/>). Positions handed to it are
/// storage-space voxels; <see cref="Center"/> turns them into planet-local metres. Thread-safe by construction: the game
/// calls it from many worker threads at once - no shared mutable state but the per-thread buffer.</summary>
public abstract class ProcShape
{
    /// <summary>The planet's storage-space centre (ProcShapeBridge.PlanetCenter).</summary>
    public Vector3 Center;
    /// <summary>The shape's box in storage space.</summary>
    public BoundingBox Box;
    /// <summary>log2 of the shape's size: the composition skips shapes smaller than the request's level of detail.</summary>
    public int LodSize;

    [ThreadStatic] static byte[] _buf;

    /// <summary>The signed distance (metres, negative inside) at a planet-local position, for a voxel of this size.</summary>
    public abstract float Distance(Vector3 local, float voxelSize);

    public virtual ContainmentType Contains(BoundingBox box, BoundingSphere sphere, int lodVoxelSize)
        => Box.Intersects(box) ? ContainmentType.Intersects : ContainmentType.Disjoint;

    public float SignedDistanceStorage(Vector3 storagePos, int lodVoxelSize) => Distance(storagePos - Center, lodVoxelSize);

    /// <summary>The density of a lod voxel range, x fastest - PlanetShapeProvider's mapping: clamp(-d / voxel, -1, 1) / 2 + 1/2.</summary>
    /// <summary>Cost counters over every shape (Fill calls, voxels, time) - `aeroget OrbitalMod.ProcShape.Stats`.</summary>
    static long _calls, _voxels, _ticks, _maxTicks;
    public static string Stats
    {
        get
        {
            double ms = _ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, max = _maxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long c = Math.Max(1, _calls);
            return $"fills {_calls}, voxels {_voxels / 1e6:F1} M, total {ms:F0} ms (worker time), {ms / c:F2} ms/fill avg, {max:F1} ms max, {(_voxels > 0 ? ms * 1e6 / _voxels : 0):F0} ns/voxel";
        }
    }
    public static void ResetStats() { _calls = _voxels = _ticks = _maxTicks = 0; }

    public byte[] Fill(int lod, Vector3I min, Vector3I max, int lodVoxelSize)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var a = FillCore(lod, min, max, lodVoxelSize);
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        System.Threading.Interlocked.Increment(ref _calls);
        System.Threading.Interlocked.Add(ref _voxels, (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1));
        System.Threading.Interlocked.Add(ref _ticks, dt);
        long m;
        while (dt > (m = System.Threading.Interlocked.Read(ref _maxTicks)) && System.Threading.Interlocked.CompareExchange(ref _maxTicks, dt, m) != m) { }
        return a;
    }

    /// <summary>The material (the planet storage's index; 255 = none, keep what is there) at a planet-local position.</summary>
    public virtual byte Material(Vector3 local, float voxelSize) => 255;

    /// <summary>The materials of a lod voxel range, x fastest (ICompositeMaterialReader.ComputeMaterial; the composition
    /// keeps them only where the shape's content is inside or at its surface - CopyShapeMaterialOp).</summary>
    public byte[] FillMaterial(int lod, Vector3I min, Vector3I max, int lodVoxelSize)
    {
        int nx = max.X - min.X + 1, ny = max.Y - min.Y + 1, nz = max.Z - min.Z + 1, n = nx * ny * nz;
        var a = _buf;
        if (a == null || a.Length < n) _buf = a = new byte[Math.Max(n, 40 * 40 * 40)];
        float s = lodVoxelSize;
        Vector3 o = new Vector3(min.X, min.Y, min.Z) * s - Center;
        int i = 0;
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            {
                Vector3 p = o + new Vector3(0, y, z) * s;
                for (int x = 0; x < nx; x++, i++) { a[i] = Material(p, s); p.X += s; }
            }
        return a;
    }

    byte[] FillCore(int lod, Vector3I min, Vector3I max, int lodVoxelSize)
    {
        int nx = max.X - min.X + 1, ny = max.Y - min.Y + 1, nz = max.Z - min.Z + 1, n = nx * ny * nz;
        var a = _buf;
        if (a == null || a.Length < n) _buf = a = new byte[Math.Max(n, 40 * 40 * 40)];
        float s = lodVoxelSize;
        Vector3 o = new Vector3(min.X, min.Y, min.Z) * s - Center;
        int i = 0;
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            {
                Vector3 p = o + new Vector3(0, y, z) * s;
                for (int x = 0; x < nx; x++, i++)
                {
                    float d = Distance(p, s) / s;
                    float c = Math.Clamp(-d, -1f, 1f) * 0.5f + 0.5f;
                    a[i] = (byte)(c * 255f);
                    p.X += s;
                }
            }
        return a;
    }

    /// <summary>Line vs the shape's box (the query box swept along the line, local to the line's start): the entry and exit
    /// distances as fractions of the line, or null.</summary>
    public double[] Intersect(LineD line, BoundingBoxD box, int lod)
    {
        // (Minkowski: the query box's extent folded into ours)
        var b = new BoundingBoxD(new Vector3D(Box.Min.X, Box.Min.Y, Box.Min.Z) - box.Max, new Vector3D(Box.Max.X, Box.Max.Y, Box.Max.Z) - box.Min);
        Vector3D o = line.From, d = line.To - line.From;
        double t0 = 0, t1 = 1;
        for (int k = 0; k < 3; k++)
        {
            double ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            double lo = k == 0 ? b.Min.X : k == 1 ? b.Min.Y : b.Min.Z, hi = k == 0 ? b.Max.X : k == 1 ? b.Max.Y : b.Max.Z;
            if (Math.Abs(dk) < 1e-12) { if (ok < lo || ok > hi) return null; continue; }
            double ta = (lo - ok) / dk, tb2 = (hi - ok) / dk;
            if (ta > tb2) (ta, tb2) = (tb2, ta);
            t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb2);
            if (t0 > t1) return null;
        }
        return new[] { t0, t1 };
    }
}
