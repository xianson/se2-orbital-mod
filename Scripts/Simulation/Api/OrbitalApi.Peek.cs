using SEAerospace;
using SEAerospace.Api;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// (API version 2) THE PEEK READS: the frames as of the last server tick, read WITHOUT the frame lock - they never block and
/// never allocate, so they suit a per-frame HUD, a render path or a per-tick controller. The lock-taking version 1 members
/// (GetFrames, TryGetFrameOrbit, GetFrameOf ...) stay as they were: an exact answer that can wait out a server step.
///
/// The snapshot (Core/FrameTable) is refreshed by the first Peek after the universe clock moved (or a quarter second
/// passed), and only when the frame lock is free at that moment (Monitor.TryEnter, no wait): while a server step holds it,
/// readers get the last copy. So a Peek answer is at most a tick or two old. Frame states are still propagated to the time
/// asked (NaN: now), from the elements as copied - exact on rails between burns.
/// </summary>
public static partial class OrbitalApi
{
    static readonly FrameTable _table = new FrameTable();
    static double _tableClock = double.NaN;
    static long _tableWall;
    static object _tableReg;

    /// <summary>The snapshot, refreshed when stale and the frame lock is free (never waits for it).</summary>
    static FrameTable Table()
    {
        var reg = SystemHost.Frames;
        double now = SystemHost.Now;
        long wall = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(reg, System.Threading.Volatile.Read(ref _tableReg)) || now != System.Threading.Volatile.Read(ref _tableClock)
            || wall - System.Threading.Volatile.Read(ref _tableWall) > System.Diagnostics.Stopwatch.Frequency / 4)
        {
            if (System.Threading.Monitor.TryEnter(ServerFrames.FramesLock))
            {
                try
                {
                    reg = SystemHost.Frames; now = SystemHost.Now;
                    _table.Refresh(reg, now);
                    System.Threading.Volatile.Write(ref _tableReg, reg); System.Threading.Volatile.Write(ref _tableClock, now); System.Threading.Volatile.Write(ref _tableWall, wall);
                }
                finally { System.Threading.Monitor.Exit(ServerFrames.FramesLock); }
            }
        }
        return _table;
    }

    /// <summary>(v2) Every frame's id into the caller's buffer (as many as fit). Returns the number of frames: more than
    /// ids.Length - grow it and ask again. Snapshot; never blocks, never allocates.</summary>
    public static int PeekFrames(long[] ids) => Table().CopyIds(ids);

    /// <summary>(v2) Bumped whenever a frame was made or dissolved or any frame's members changed (as the snapshot saw it):
    /// cache what you built from the frames until it moves.</summary>
    public static int PeekFrameSetVersion() => Table().SetVersion;

    /// <summary>(v2) A frame, from the snapshot: its body, member count, anchor (a member id, 0: none yet), whether it is an
    /// encounter site's (booted from a player's berth) and whether a voxel (a charted asteroid) anchors it.</summary>
    public static bool TryPeekFrame(long frameId, out string body, out int memberCount, out long anchorId, out bool isEncounter)
    {
        body = null; memberCount = 0; anchorId = 0; isEncounter = false;
        if (!Table().TryGet(frameId, out var r)) return false;
        body = r.Body; memberCount = r.MemberCount; anchorId = r.Anchor; isEncounter = r.IsEncounter;
        return true;
    }

    /// <summary>(v2) A frame's members into the caller's buffer (as many as fit): grid member ids (from 1 000 000 000) and
    /// player ids. Returns its member count, or -1: no such frame.</summary>
    public static int PeekFrameMembers(long frameId, long[] memberIds) => Table().CopyMembers(frameId, memberIds);

    /// <summary>(v2) A frame's orbit as TryGetFrameOrbit gives it (a, e, i, raan, argp, true anomaly at the epoch, epoch, mu),
    /// from the snapshot.</summary>
    public static bool TryPeekFrameOrbit(long frameId, out double semiMajorAxis, out double eccentricity, out double inclination,
        out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)
    {
        semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = epoch = mu = 0;
        if (!Table().TryGet(frameId, out var r)) return false;
        var el = r.Elements;
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly; epoch = el.Epoch; mu = el.Mu;
        return true;
    }

    /// <summary>(v2) A frame's state relative to its body (m, m/s, inertial) at a universe time (NaN: now), propagated on rails
    /// from the snapshot's elements.</summary>
    public static bool TryPeekFrameState(long frameId, double time, out Vector3D position, out Vector3D velocity)
    {
        position = velocity = default;
        return Table().TryGet(frameId, out var r) && OrbitQueries.Propagate(r.Elements, double.IsNaN(time) ? SystemHost.Now : time, out position, out velocity);
    }

    /// <summary>(v2) Where a frame sits in the game world: its berth's centre (world, m) and radius (m) - the sphere its members
    /// float in. A point p in the berth is at (frame's position + (p - centre)) about the frame's body: the berth's axes are
    /// the solar system's inertial ones (no rotation).</summary>
    public static bool TryPeekFrameBerth(long frameId, out Vector3D worldCenter, out double radius)
    {
        worldCenter = default; radius = 0;
        if (!Table().TryGet(frameId, out var r)) return false;
        worldCenter = r.Berth; radius = ServerFrames.SlotRadius;
        return true;
    }

    /// <summary>(v2) The frame a member (a grid member id from GetMemberId, or a player id) is in, or -1. Snapshot.</summary>
    public static long PeekFrameOfMember(long memberId) => memberId < 0 ? -1 : Table().FrameOf(memberId);

    /// <summary>(v2) The frame a grid is in, or -1: GetFrameOf without the frame lock (snapshot).</summary>
    public static long PeekFrameOf(Entity grid)
    {
        long id = GetMemberId(grid);
        return id < 0 ? -1 : Table().FrameOf(id);
    }
}
