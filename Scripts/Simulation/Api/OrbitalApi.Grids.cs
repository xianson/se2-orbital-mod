using Keen.VRage.Physics.Data;
using SEAerospace.Api;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// (API version 2) A GRID'S OWN ORBIT AND STATUS. A grid's state is read from the grid you pass (its world transform and its
/// rigid body's velocity - the copy you hold, server or client; in single player and on a host they are the same place) and
/// placed in the solar system as TryWorldToBody does: on rails, its frame's orbit plus its place and motion in the berth (in time
/// warp: the true relative velocity the mod's steering keeps, not the physics' N x one); in a planet's cell, through the planet's
/// rotating chart. So a grid drifting in its berth has an orbit a little off its frame's, as it truly does.
/// Pass a live grid (a closed or removed one may throw from the engine's own reads). Threads: any, best the one that updates the
/// grid (another may read a transform mid-step). No frame lock (the Peek snapshot), no allocation.
/// </summary>
public static partial class OrbitalApi
{
    /// <summary>(v2) A grid's state now about the body it orbits (m, m/s, inertial axes), the body, and its frame (-1: in a
    /// planet's cell, not on rails). False: not built, or the grid is in neither a cell nor a berth.</summary>
    public static bool TryGetGridState(Entity grid, out string body, out long frameId, out Vector3D position, out Vector3D velocity)
    {
        body = null; frameId = -1; position = velocity = default;
        if (grid == null || !SystemHost.Built) return false;
        Vector3D at = grid.Data.GetWorldTransform().Position;
        Vector3D vel = grid.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
        long id = GetMemberId(grid);
        long fid = id >= 0 ? Table().FrameOf(id) : -1;
        if (fid >= 0 && Table().TryGet(fid, out var r) && r.Body != null)
        {
            // (warp: the physics carries a member at N x its true relative velocity - the views keep the true one)
            if (SystemHost.Timescale > 1.0 && ServerFrames.View(id, out var v) && v.HasTrueRel) vel = v.TrueRel;
            if (!OrbitQueries.Propagate(r.Elements, SystemHost.Now, out var fp, out var fv)) return false;
            body = r.Body; frameId = fid;
            position = fp + (at - r.Berth); velocity = fv + vel;
            return OrbitQueries.Finite(position) && OrbitQueries.Finite(velocity);
        }
        if (!TryWorldToBody(at, vel, out body, out frameId, out position, out velocity)) return false;
        return true;
    }

    /// <summary>(v2) A grid's own orbit now (osculating; its epoch is now): the body and the eight numbers of TryGetFrameOrbit.
    /// On the ground, or on a hop that never leaves the air, this is still the conic its state gives (periapsis inside the
    /// planet): check TryGetGridOrbitSummary's periapsis altitude.</summary>
    public static bool TryGetGridOrbit(Entity grid, out string body, out double semiMajorAxis, out double eccentricity, out double inclination,
        out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)
    {
        semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = epoch = mu = 0;
        if (!GridElements(grid, out body, out var el)) return false;
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly; epoch = el.Epoch; mu = el.Mu;
        return true;
    }

    /// <summary>(v2) A grid's orbit in plain numbers: the body, periapsis and apoapsis ALTITUDE over its surface (m; apoapsis
    /// +infinity when hyperbolic), period (s; +infinity when hyperbolic), and the time to the next periapsis and apoapsis (s from
    /// now; hyperbolic: the periapsis negative once past, the apoapsis NaN).</summary>
    public static bool TryGetGridOrbitSummary(Entity grid, out string body, out double periapsisAltitude, out double apoapsisAltitude,
        out double period, out double timeToPeriapsis, out double timeToApoapsis)
    {
        periapsisAltitude = apoapsisAltitude = period = timeToPeriapsis = timeToApoapsis = 0;
        if (!GridElements(grid, out body, out var el)) return false;
        if (!OrbitQueries.Events(el, out double pe, out double ap, out period, out timeToPeriapsis, out timeToApoapsis)) return false;
        double r = SystemHost.Registry?.FindDefinition(body)?.RadiusMeters ?? 0;
        periapsisAltitude = pe - r; apoapsisAltitude = ap - r;
        return true;
    }

    static bool GridElements(Entity grid, out string body, out KeplerianElements el)
    {
        el = default;
        if (!TryGetGridState(grid, out body, out _, out var p, out var v)) return false;
        var b = SystemHost.Registry?.Find(body);
        return b != null && OrbitQueries.FromState(p, v, b.Mu, SystemHost.Now, out el);
    }

    /// <summary>(v2) What orbital knows of a grid, from its snapshots (no frame lock): its member id (-1: not one of orbital's),
    /// its frame (-1: none), whether it is its frame's anchor (the body the frame's orbit is measured on), whether it holds
    /// station (dampeners on and thrust to cancel the pull: no relative force on it), whether it is joined to its frame's anchor
    /// (docked or a sub-grid: it moves as one with it), and whether it is an encounter's (NPC-owned or spawned by one). False:
    /// not a grid orbital tracks (its views refresh every 5 server ticks: a new grid shows within them).</summary>
    public static bool TryGetGridStatus(Entity grid, out long memberId, out long frameId, out bool isFrameAnchor, out bool holdingStation,
        out bool joinedToAnchor, out bool isEncounter)
    {
        frameId = -1; isFrameAnchor = holdingStation = joinedToAnchor = isEncounter = false;
        memberId = GetMemberId(grid);
        if (memberId < 0 || !ServerFrames.View(memberId, out var v)) return false;
        holdingStation = v.Holding; joinedToAnchor = v.AnchorJoined; isEncounter = v.Encounter;
        frameId = Table().FrameOf(memberId);
        if (frameId >= 0 && Table().TryGet(frameId, out var r)) isFrameAnchor = r.Anchor == memberId;
        return true;
    }
}
