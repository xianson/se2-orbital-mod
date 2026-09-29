using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.Volume;
using Keen.VRage.Library.Memory;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The game never spawns asteroids here: encounters place theirs, and our own system places the
/// rest. The procedural generator's asteroid path (its entity sectors) spawns from volume
/// definitions: the default space (InfiniteArea), its random ellipsoid fields, manual volumes (the
/// rings round planets) and the colonization sectors' volumes. Each of those definitions gets a
/// negative density while the session runs, so no sample ever passes; encounters come from a
/// separate path (encounter sectors) and are untouched. Definitions are saved by name only, so
/// nothing of this reaches the world file; the densities are put back on unload.
/// Asteroids the generator has already spawned are deleted through the generator itself (not the
/// player's delete, which would exclude them in the save); player-edited ones (marked manual) stay.
/// </summary>
public static class AsteroidBridge
{
    public static bool Block = true;
    public static string Status = "-";

    /// <summary>The game's rings (manual volumes) as found: type, transform, bounds (for the map and the sectors).</summary>
    public static readonly List<string> RingInfo = new List<string>();
    /// <summary>The game's planetary rings (tori): centre (world), inner / outer radius and half-thickness (m).</summary>
    public static readonly List<(Vector3D C, double In, double Out, double Half)> Rings = new List<(Vector3D, double, double, double)>();

    private static System.Reflection.MethodInfo _voxelQuery;

    /// <summary>Generator-spawned bodies (an encounter's rock) to delete near a point, on the next tick.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Vector3D at, double r)> _deleteNear = new System.Collections.Concurrent.ConcurrentQueue<(Vector3D, double)>();
    public static void RequestDeleteNear(Vector3D at, double r) => _deleteNear.Enqueue((at, r));

    /// <summary>The asteroids placed on purpose (encounters, players): world positions, refreshed every couple of seconds.</summary>
    public static readonly List<Vector3D> Asteroids = new List<Vector3D>();
    /// <summary>Their world bounding boxes (same order as found; for 'am I at it').</summary>
    public static readonly List<BoundingBoxD> AsteroidBoxes = new List<BoundingBoxD>();

    /// <summary>Distance from a world point to the nearest asteroid's bounding box (0 inside), or +inf.</summary>
    public static double DistanceToAsteroid(Vector3D p)
    {
        double best = double.PositiveInfinity;
        lock (Asteroids) foreach (var b in AsteroidBoxes) best = Math.Min(best, BoxDistance(b, p));
        return best;
    }

    public static double BoxDistance(BoundingBoxD b, Vector3D p)
    {
        double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
        double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
        double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The nearest placed asteroid within reach of a world point.</summary>
    public static bool NearestAsteroid(Vector3D at, double reach, out Vector3D pos)
    {
        pos = default; double best = reach;
        lock (Asteroids) foreach (var a in Asteroids) { double d = (a - at).Length(); if (d <= best) { best = d; pos = a; } }
        return best < reach;
    }

    /// <summary>The ring round a world point (a planet's centre), if the game has one there.</summary>
    public static bool RingAround(Vector3D centre, out double inner, out double outer)
    {
        inner = outer = 0;
        lock (Rings)
            foreach (var r in Rings)
                if ((r.C - centre).Length() < Math.Max(5000.0, 0.05 * r.Out)) { inner = r.In; outer = r.Out; return inner > 0 && outer > inner; }
        return false;
    }

    private static readonly List<(VolumeDefinition v, float density)> _orig = new List<(VolumeDefinition, float)>();
    private static readonly HashSet<VolumeDefinition> _seen = new HashSet<VolumeDefinition>();
    private static FieldInfo _density;
    private static double _next;
    private static int _deleted;
    private static bool _loggedRings;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!Block || session == null) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _next) return;
        _next = now + 2.0;
        try
        {
            var gen = Generator(session);
            if (gen == null) { Status = "no generator"; return; }

            var def = gen.GenerationData?.Definition;
            if (def != null)
            {
                Kill(def.InfiniteArea);
                foreach (var f in def.EllipsoidFields) Kill(f.Definition);
            }
            // Manual volumes (the rings round planets): their compositions, and what they are.
            using (var buf = new Buffer<IProceduralVolume>(Allocator.Pool, "OrbitalRings"))
            {
                var all = new BoundingBoxD(new Vector3D(-1e13, -1e13, -1e13), new Vector3D(1e13, 1e13, 1e13));
                gen.QueryManualVolumes(in all, buf);
                var en = ((BufferReference<IProceduralVolume>)buf).GetEnumerator();
                var info = new List<string>();
                var rings = new List<(Vector3D, double, double, double)>();
                while (en.MoveNext())
                {
                    var vol = en.Current;
                    Kill(vol.Composition);
                    var box = vol.GetOrientedBoundingBox().GetAABB();
                    var wt = vol.Transform;
                    string radii = "";
                    if (vol is ProceduralRing pr)
                    {
                        var tv = pr.Torus;
                        rings.Add((tv.WorldTransform.Position, tv.InnerRadius, tv.OuterRadius, tv.MinorRadii.Y));
                        radii = $" inner {tv.InnerRadius / 1000:F1} km outer {tv.OuterRadius / 1000:F1} km half-height {tv.MinorRadii.Y / 1000:F2} km";
                    }
                    info.Add($"{vol.GetType().Name} at {Km(wt.Position)} up {wt.Orientation.GetUp()} box {Km(box.Min)}..{Km(box.Max)}{radii}");
                }
                en.Dispose();
                lock (RingInfo) { RingInfo.Clear(); RingInfo.AddRange(info); }
                lock (Rings) { Rings.Clear(); Rings.AddRange(rings); }
                if (!_loggedRings && info.Count > 0)
                {
                    _loggedRings = true;
                    foreach (var s in info) Log.Default?.Info("[ORBIT-ROIDS] manual volume: " + s);
                }
            }
            var col = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.ProceduralGeneration.ColonizationSectorProceduralGenerationDataSessionComponent>();
            if (col?.Definition != null) foreach (var kv in col.Definition.SectorVolumes) Kill(kv.Value);

            // Encounters moved off a planet's border leave their rock behind (voxels cannot move): gone too.
            while (_deleteNear.TryDequeue(out var dn))
            {
                var gone = new List<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.SpaceEntityId>();
                foreach (var kv in gen.Entities)
                {
                    try { if ((kv.Value.Data.GetWorldTransform().Position - dn.at).Length() <= dn.r) gone.Add(kv.Key); } catch { }
                }
                foreach (var id in gone) { try { gen.DeleteEntity(id); _deleted++; } catch { } }
                if (gone.Count > 0) Log.Default?.Info($"[ORBIT-ROIDS] deleted {gone.Count} generator bod(ies) left by an encounter moved off a planet's border");
            }
            // What the generator already spawned (not player-edited ones).
            var ids = new List<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.SpaceEntityId>();
            var kept = new List<Vector3D>();
            var boxes = new List<BoundingBoxD>();
            foreach (var kv in gen.Entities)
            {
                bool manual = false;
                try { manual = IsManual(kv.Value); } catch { }
                if (!manual) ids.Add(kv.Key);
                else try { kept.Add(kv.Value.Data.GetWorldTransform().Position); boxes.Add(Keen.VRage.Core.Game.Data.BoundingBoxData.GetWorldAABB(kv.Value)); } catch { }   // an asteroid placed on purpose: a frame anchor
            }
            // Every other voxel body that is not a planet (an encounter's rock, one spawned with a prefab,
            // not through the generator): planets are the voxel bodies at a planet's centre.
            try
            {
                // (Its base class is in VRage.Voxels, which scripts cannot reference: the query by reflection.)
                if (_voxelQuery == null)
                {
                    Type vt = PlanetRenderBridge.FindType("Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Voxels.VoxelOperationsComponent");
                    var gm = session.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetEntitiesOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    if (vt != null && gm != null) _voxelQuery = gm.MakeGenericMethod(vt);
                }
                var found = _voxelQuery?.Invoke(session, null) as System.Collections.IEnumerable;
                if (found != null)
                foreach (var o in found)
                {
                    if (!(o is Entity ve)) continue;
                    Vector3D vp = ve.Data.GetWorldTransform().Position;
                    bool planet = false;
                    foreach (var b in SystemHost.BeaconOf.Values) if ((b.Center - vp).Length() < 5000) { planet = true; break; }
                    if (!planet && !kept.Exists(k => (k - vp).Length() < 1.0))
                    {
                        kept.Add(vp);
                        try { boxes.Add(Keen.VRage.Core.Game.Data.BoundingBoxData.GetWorldAABB(ve)); } catch { boxes.Add(new BoundingBoxD(vp, vp)); }
                    }
                }
            }
            catch { }
            lock (Asteroids) { Asteroids.Clear(); Asteroids.AddRange(kept); AsteroidBoxes.Clear(); AsteroidBoxes.AddRange(boxes); }
            foreach (var id in ids) { try { gen.DeleteEntity(id); _deleted++; } catch { } }
            Status = $"asteroids blocked: {_orig.Count} volume definition(s) at no density, {_deleted} deleted, {RingInfo.Count} ring(s), {Asteroids.Count} asteroid(s) as anchors";
        }
        catch (Exception e) { Status = "asteroid block failed: " + (e.InnerException ?? e).Message; }
    }

    // The server generator and the voxel component live in assemblies mods cannot reference: by name.
    private static MethodInfo _entityTryGet;
    static ProceduralGeneratorSessionComponent Generator(Keen.VRage.Core.Game.Systems.Session session)
    {
        // The session's components: the server's generator (the one that spawns).
        ProceduralGeneratorSessionComponent server = null, any = null;
        session.SessionComponents?.ForEach(delegate (Keen.VRage.Core.Game.Components.SessionComponent sc)
        {
            if (sc is ProceduralGeneratorSessionComponent g) { any ??= g; if (g.GetType().Name.Contains("Server")) server = g; }
        });
        return server ?? any;
    }

    static bool IsManual(Entity e)
    {
        if (e == null) return false;
        if (_entityTryGet == null)
        {
            Type t = PlanetRenderBridge.FindType("VRage.Voxels", "Keen.VRage.Voxels.Components.ProceduralVoxelEntityComponent");
            var m = typeof(Entity).GetMethods().FirstOrDefault(x => x.Name == "TryGet" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
            if (t == null || m == null) return true;   // unsure: leave it be
            _entityTryGet = m.MakeGenericMethod(t);
        }
        object c = _entityTryGet.Invoke(e, null);
        return c != null && PlanetRenderBridge.GetMember(c, "IsManual") is bool b && b;
    }

    static void Kill(VolumeDefinition v)
    {
        if (v == null || !_seen.Add(v)) return;
        _density ??= typeof(VolumeDefinition).GetField("<Density>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_density == null) return;
        _orig.Add((v, v.Density));
        _density.SetValue(v, -1f);   // (0 still passes when the draw is exactly 0)
    }

    /// <summary>The session ends: the definitions (shared by the whole process) get their densities back.</summary>
    public static void Restore()
    {
        if (_density != null) foreach (var (v, d) in _orig) { try { _density.SetValue(v, d); } catch { } }
        _orig.Clear(); _seen.Clear(); _loggedRings = false;
    }

    static string Km(Vector3D v) => $"({v.X / 1000:F1}, {v.Y / 1000:F1}, {v.Z / 1000:F1}) km";
}
