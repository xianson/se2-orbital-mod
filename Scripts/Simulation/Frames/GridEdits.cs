#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// WHAT THIS MOD WROTE INTO A GRID'S PHYSICS (2026-10-14, for OrbitalApi.TryGetGridEdits): per server grid entity, the
/// running sum (world axes, m/s) of every velocity change the mod wrote into its rigid body (new - old at each write:
/// the anchor's drain, the warp rescale and steering, the N^2 differential gravity, the spin's fictitious forces, the
/// HighSpeed exit, arrivals, deferred moves), and a stamp bumped on every teleport or turn (the AeroNoteMoved sites).
/// Another mod that balances the physics' own velocity change (the Aerodynamics Mod's contact loads: m dv less the known
/// forces) subtracts the sum's change between two of its reads, so our writes - after the physics step, between its reads
/// - never read as a force on the ship. The sum is DOUBLE: a drained anchor's sum grows by its thrust for hours, and only
/// differences are used (a float sum at 2e5 m/s would round 0.02 m/s a step - a megatonne ship's phantom 1 MN).
/// Its own lock (never FramesLock: callers hold that one, the other mod holds none of ours); entries go when the grid
/// leaves the scene (OrbitalGridComponent.OnBeforeRemovedFromScene) and with the world (Clear).
/// </summary>
public static class GridEdits
{
    sealed class Rec { public Vector3D Sum; public long Stamp; }
    static readonly Dictionary<Entity, Rec> _of = new Dictionary<Entity, Rec>();
    static readonly object _lock = new object();
    /// <summary>Stamps are drawn from one counter (under _lock): a record made anew (after Forget, Clear) never repeats a
    /// stamp a reader saw, so its restarted sum reads as a move (re-baselined), never as a velocity change.</summary>
    static long _stamps;
    /// <summary>Diagnostics: velocity writes and moves recorded.</summary>
    public static long Writes, Moves;

    /// <summary>A velocity change written into this grid's rigid body (world, m/s: new - old). Grids of ours only: a wheel
    /// or a rotor part moved with its ship is not recorded (it is no grid the other mod asks about, and would never be
    /// forgotten).</summary>
    public static void Wrote(OrbitalGridComponent g, Vector3 dv)
    {
        var e = g?.Entity;
        if (e == null || !(dv.LengthSquared() > 0f) || !float.IsFinite(dv.X + dv.Y + dv.Z)) return;
        lock (_lock)
        {
            if (!_of.TryGetValue(e, out var r)) _of[e] = r = new Rec { Stamp = ++_stamps };
            r.Sum += (Vector3D)dv;
            Writes++;
        }
    }

    /// <summary>As Wrote, for an entity of a moved set (only a grid of ours is recorded).</summary>
    public static void Wrote(Entity e, Vector3 dv)
    {
        if (e == null) return;
        OrbitalGridComponent g; try { g = e.TryGet<OrbitalGridComponent>(); } catch { return; }
        if (g != null) Wrote(g, dv);
    }

    /// <summary>The grid was teleported or turned in place (SetWorldTransform): its stamp changed (a new one from the counter).</summary>
    public static void Moved(Entity e)
    {
        if (e == null) return;
        OrbitalGridComponent g; try { g = e.TryGet<OrbitalGridComponent>(); } catch { return; }
        if (g == null) return;
        lock (_lock)
        {
            if (!_of.TryGetValue(e, out var r)) _of[e] = r = new Rec();
            r.Stamp = ++_stamps;
            Moves++;
        }
    }

    /// <summary>The record (OrbitalApi.TryGetGridEdits). False: nothing ever written or moved (the sum 0, the stamp 0).</summary>
    public static bool TryGet(Entity e, out Vector3D velocityWrittenSum, out long moveStamp)
    {
        velocityWrittenSum = default; moveStamp = 0;
        if (e == null) return false;
        lock (_lock)
        {
            if (!_of.TryGetValue(e, out var r)) return false;
            velocityWrittenSum = r.Sum; moveStamp = r.Stamp;
            return true;
        }
    }

    /// <summary>The grid left the scene: its record goes.</summary>
    public static void Forget(Entity e) { if (e != null) lock (_lock) _of.Remove(e); }

    /// <summary>A new world: every record goes.</summary>
    public static void Clear() { lock (_lock) _of.Clear(); }

    /// <summary>Diagnostics: grids with a record.</summary>
    public static int Count { get { lock (_lock) return _of.Count; } }
}
