using SEAerospace;
using SEAerospace.Api;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// (API version 2) THE BODIES AND THE GAME WORLD. Two kinds of place in the game world hold what orbital simulates:
///  - a planet's CELL: the real voxel planet where the world put it (its centre: TryGetBodyCell). A spinning planet's cell
///    turns with it: cell axes are the planet's rotating chart, carried to the solar system's inertial axes by its rotation
///    (TryGetBodyRotation) - a point at rest on the ground moves (w x r) in inertial terms.
///  - a frame's BERTH: a sphere of world space (TryPeekFrameBerth) where a frame's members float; its axes are the inertial
///    ones, its centre rides the frame's orbit.
/// TryWorldToBody and TryBodyToWorld carry a point (and a velocity) between the world and "about a body, inertial axes" - the
/// frame every orbit, body state and TryGetTargetState answer in. Elsewhere (the spawn space, legacy space): no answer.
/// Threads: any. No frame lock (the berths come from the Peek snapshot), no allocation.
/// </summary>
public static partial class OrbitalApi
{
    // ── bodies ──

    /// <summary>(v2) A body's state relative to the STAR (m, m/s, solar system inertial axes) at a universe time (NaN: now) - the
    /// sum of its parents' states; the star itself is the origin (zero). TryGetBodyState gives it relative to its parent.</summary>
    public static bool TryGetBodySystemState(string name, double time, out Vector3D position, out Vector3D velocity)
    {
        position = velocity = default;
        var b = name != null ? SystemHost.Registry?.Find(name) : null;
        if (b == null) return false;
        RootOf(b, double.IsNaN(time) ? SystemHost.Now : time, out position, out velocity);
        return OrbitQueries.Finite(position) && OrbitQueries.Finite(velocity);
    }

    /// <summary>A body's origin about the star: its chain walked here (OriginInRoot's memo belongs to the first thread to ask -
    /// the client's map - and an API caller asking first would take it).</summary>
    static void RootOf(GravityBody b, double t, out Vector3D position, out Vector3D velocity)
    {
        position = velocity = default;
        for (var w = b; w.Parent != null; w = w.Parent)
        {
            var s = w.StateInParentAt(t);
            position += s.Position; velocity += s.Velocity;
        }
    }

    /// <summary>(v2) A body's orbit about its parent, osculating at a universe time (NaN: now; that time is the epoch): the eight
    /// numbers of TryGetFrameOrbit (mu: the PARENT's). False: no such body, or the star.</summary>
    public static bool TryGetBodyOrbit(string name, double time, out double semiMajorAxis, out double eccentricity, out double inclination,
        out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)
    {
        semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = epoch = mu = 0;
        var b = name != null ? SystemHost.Registry?.Find(name) : null;
        if (b == null || b.Parent == null) return false;
        double t = double.IsNaN(time) ? SystemHost.Now : time;
        var s = b.StateInParentAt(t);
        if (!OrbitQueries.FromState(s.Position, s.Velocity, b.Parent.Mu, t, out var el)) return false;
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly; epoch = t; mu = el.Mu;
        return true;
    }

    /// <summary>(v2) A body's spin at a universe time (NaN: now): its axis (unit, inertial: the north pole), its rotation angle
    /// (rad, [0, 2 pi)) and rate (rad/s). The planets spin on the game's own clock (shared by every player, not sped up by warp),
    /// so the angle moves at the rate per GAME second. False: no such body, or it does not spin.</summary>
    public static bool TryGetBodyRotation(string name, double time, out Vector3D axis, out double angle, out double angularVelocity)
    {
        axis = default; angle = angularVelocity = 0;
        var b = name != null ? SystemHost.Registry?.Find(name) : null;
        if (b == null || !PlanetBerths.TryBodySpin(b, out axis, out angularVelocity)) return false;
        angle = b.RotationAngleAt(double.IsNaN(time) ? SystemHost.Now : time);
        return true;
    }

    /// <summary>(v2) Where a planet or moon sits in the game world: its cell's centre (world, m: the real voxel planet's centre)
    /// and the cell's radius (m: what counts as "in its cell" - planet space, where the game's own physics flies; beyond it, the
    /// rails). False: no such body, no cell (the star, a body the world has no planet for), or not built.</summary>
    public static bool TryGetBodyCell(string name, out Vector3D worldCenter, out double radius)
    {
        worldCenter = default; radius = 0;
        var reg = SystemHost.Registry;
        if (name == null || reg == null || !SystemHost.Built || reg.Find(name) == null) return false;
        if (!VoxelBerthRegistry.TryGetCell(name, reg, out worldCenter)) return false;
        radius = VoxelBerthRegistry.SharedAllocator(reg)?.SlotRadius ?? 0;
        return true;
    }

    // ── the world <-> the bodies ──

    /// <summary>(v2) A world point (and a velocity measured in the world there) as the solar system has it NOW: the body it is
    /// about and the state relative to that body (m, m/s, inertial axes). In a planet's cell: that planet, through its rotating
    /// chart (frameId -1). In a frame's berth: the frame's body, the frame's state plus the offset from the berth's centre
    /// (frameId: that frame). False: in neither (the spawn space), or not built. In time warp a velocity measured inside a berth
    /// is the physics' (N x the true relative one, averaged over the step): pass the true one if you know it (TryGetGridState does).</summary>
    public static bool TryWorldToBody(Vector3D worldPosition, Vector3D worldVelocity, out string body, out long frameId,
        out Vector3D position, out Vector3D velocity)
    {
        body = null; frameId = -1; position = velocity = default;
        var reg = SystemHost.Registry;
        if (reg == null || !SystemHost.Built) return false;
        double now = SystemHost.Now;
        if (VoxelBerthRegistry.TryCellContaining(worldPosition, reg, out string cellBody, out Vector3D cell) && reg.Find(cellBody) != null)
        {
            var ch = Chart.Of(cellBody, now);
            Vector3D lr = worldPosition - cell;
            body = cellBody; position = ch.ToInertial(lr); velocity = ch.VelToInertial(lr, worldVelocity);
            return OrbitQueries.Finite(position) && OrbitQueries.Finite(velocity);
        }
        if (!Table().TryBerthAt(worldPosition, ServerFrames.SlotRadius, out var r)) return false;
        if (!OrbitQueries.Propagate(r.Elements, now, out var fp, out var fv)) return false;
        body = r.Body; frameId = r.Id;
        position = fp + (worldPosition - r.Berth); velocity = fv + worldVelocity;
        return OrbitQueries.Finite(position) && OrbitQueries.Finite(velocity);
    }

    /// <summary>(v2) The inverse, NOW: a state about a body (m, m/s, inertial) placed in the world. frameId -1: into that body's own
    /// cell (through its rotating chart; the velocity is then the one the game's physics would have there). frameId &gt;= 0: into
    /// that frame's berth (any body: the state is carried through the star to the frame's). False: unknown body or frame, no
    /// cell, or not built. A point far from the cell or berth is placed all the same - it is only meaningful inside.</summary>
    public static bool TryBodyToWorld(string body, long frameId, Vector3D position, Vector3D velocity, out Vector3D worldPosition, out Vector3D worldVelocity)
    {
        worldPosition = worldVelocity = default;
        var reg = SystemHost.Registry;
        var b = body != null && reg != null && SystemHost.Built ? reg.Find(body) : null;
        if (b == null) return false;
        double now = SystemHost.Now;
        if (frameId < 0)
        {
            if (!VoxelBerthRegistry.TryGetCell(body, reg, out Vector3D cell)) return false;
            var ch = Chart.Of(body, now);
            worldPosition = cell + ch.FromInertial(position); worldVelocity = ch.VelFromInertial(position, velocity);
            return OrbitQueries.Finite(worldPosition) && OrbitQueries.Finite(worldVelocity);
        }
        if (!Table().TryGet(frameId, out var r)) return false;
        var fb = r.Body != null ? reg.Find(r.Body) : null;
        if (fb == null || !OrbitQueries.Propagate(r.Elements, now, out var fp, out var fv)) return false;
        Vector3D dp = position - fp, dv = velocity - fv;
        if (!ReferenceEquals(fb, b))
        {
            // (about another body: both through the star)
            RootOf(b, now, out var bp, out var bv); RootOf(fb, now, out var fbp, out var fbv);
            dp += bp - fbp; dv += bv - fbv;
        }
        worldPosition = r.Berth + dp; worldVelocity = dv;
        return OrbitQueries.Finite(worldPosition) && OrbitQueries.Finite(worldVelocity);
    }
}
