using Keen.VRage.Physics.Data;
using SEAerospace.Frames;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// FrameHost (partial): the seated player - whose orbit (PlayerOrbit.Seated), the seat found and kept, and the seat
// link's gaps across a far jump.
public static partial class FrameHost
{
    /// <summary>Why the seated player's frame is what it is (harness diagnostics).</summary>
    public static string SeatedWhy = "-";
    static readonly List<PlayerOrbit.GridState> _seatGrids = new List<PlayerOrbit.GridState>();
    static Entity _seatChar, _lastChar, _lastSeatEntity;
    static long _lastSeatSid; static double _lastSeatAt;
    static readonly List<PlayerOrbit.GridState> _joinGrids = new List<PlayerOrbit.GridState>();
    /// <summary>The client's copy of the grid the player's seat is on (null: not seated, or none found), and the seat block.</summary>
    public static Entity SeatGrid, SeatBlock;
    /// <summary>The server's id of that grid (0: not matched) - for server-side code acting on the ship you fly.</summary>
    public static long SeatGridServerId;
    static Vector3D _seatVel; static bool _seatVelOk;

    /// <summary>How far a point is from the frame's anchor: its bounding box (a station you are parked in: 0), an asteroid's
    /// surface; +inf when unknown. On foot and seated alike.</summary>
    private static double AnchorDistance(ProximityFrame f, Vector3D pos)
    {
        try
        {
            if (f.AnchorEntityId == ServerFrames.AsteroidAnchorId) return AsteroidBridge.DistanceToAsteroid(pos);
            if (GridMembers.IsGridId(f.AnchorEntityId) && ServerFrames.View(f.AnchorEntityId, out var av))
                return av.HasBox ? AsteroidBridge.BoxDistance(av.Box, pos) : (av.Position - pos).Length();   // (the server's snapshot, not its entity)
        }
        catch { }
        return double.PositiveInfinity;
    }

    /// <summary>The seated player's orbit (PlayerOrbit.Seated): the client's copy of the grid they sit in (the piloted grid
    /// nearest them, else the nearest grid) for its position and velocity, the server's grid positions for frames.</summary>
    private static PlayerOrbit.Result SeatedOrbit(Keen.VRage.Core.Game.Systems.Session session, Vector3D pos)
    {
        // the grid of the seat you are in (its pilot is you) - not the nearest grid: a big ship's origin is far from its
        // cockpit, a docked fighter's is near it
        // (the seat found last tick first - still alive, you still its pilot: the scan below finds that same one. The scan, an
        //  engine query over every seat of the world each frame, was most of SeatedOrbit's 12 ms/s - a profile, 2026-10-15)
        Entity seat = null;
        var last = _seatFound;
        if (SeatAlive(last) && (last.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == _seatChar && _seatChar != null)
        {
            seat = last.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;
            SeatBlock = last;
        }
        else
        {
            _seatFound = null;
            foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>())
            {
                var s = e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat;
                if (s?.Pilot != _seatChar) continue;
                seat = e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;
                SeatBlock = e;
                _seatFound = e;
                break;
            }
        }
        _seatVelOk = false;
        SeatGrid = seat; SeatGridServerId = 0;
        if (seat == null) return new PlayerOrbit.Result { Why = "seated, no grid near" };
        Vector3D sp = seat.Data.GetWorldTransform().Position;
        bool hasRb = seat.Data.TryGet<RigidBodyData>(out var rb);
        Vector3D sv = hasRb ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
        _seatVel = sv; _seatVelOk = hasRb && IsFinite(sv);
        long sid = 0; double sd = 30;
        _seatGrids.Clear();
        lock (ServerFrames.GridPositions)
            foreach (var kv in ServerFrames.GridPositions)
            {
                _seatGrids.Add(new PlayerOrbit.GridState(kv.Key, kv.Value, Vector3D.Zero));
                double d = (kv.Value - sp).Length();
                if (d < sd) { sd = d; sid = kv.Key; }
            }
        // The server's own answer first: the grid its seat block is on (the seat whose pilot is your server character) - by
        // entity, not by the nearest origin (a big ship's sub-grids have origins within metres of each other).
        long ssid = System.Threading.Interlocked.Read(ref ServerFrames.PlayerSeatGrid);
        if (ssid != 0 && ServerFrames.View(ssid, out var ssv)) { sid = ssid; sp = ssv.Position; }
        // (no server grid within 30 m: the same seat grid as a moment ago, just moved - its client copy follows a big move
        //  (an arrival, a merge, a re-orbit) a moment late, and the seated player read as in no frame for it: warp dropped
        //  to x1, the HUD flickered. Its last id kept for 3 s.)
        double wallS = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (sid != 0) { _lastSeatSid = sid; _lastSeatEntity = seat; _lastSeatAt = wallS; }
        else if (ReferenceEquals(seat, _lastSeatEntity) && wallS - _lastSeatAt < 3 && ServerFrames.View(_lastSeatSid, out var sv0))
        { sid = _lastSeatSid; sp = sv0.Position; }   // (and where the server has it: the client copy is still at the old place)
        SeatGridServerId = sid;
        lock (ServerFrames.FramesLock)
            return PlayerOrbit.Seated(new PlayerOrbit.GridState(sid, sp, sv), _seatGrids, id => SystemHost.Frames?.FindByMember(id), SystemHost.Timescale);
    }

    /// <summary>The seat SeatedOrbit found last (client thread), and whether an entity is still in its scene (no throw: an
    /// exception costs ~200 ms in SE2).</summary>
    static Entity _seatFound;
    static bool SeatAlive(Entity e)
    {
        if (e == null) return false;
        var d = e.Data; var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }

    /// <summary>True when the local character sits in a seat.</summary>
    public static bool Seated => _pub.Seated;

    /// <summary>
    /// Seated: the character parented to a seat. The link can be missing for a tick while the game replicates a far jump of
    /// the seat's grid (a ship moved 25 km with you in its cockpit): read as on foot then, you were split off your own ship.
    /// So, seated a moment ago and the link gone, the seats are asked: is any seat's pilot this character.
    /// </summary>
    private static bool IsSeated(Entity ch)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (ch.Data.TryGet<Keen.VRage.Core.Game.Components.EntityParentData>(out _)) { _seatedAt = now; return true; }
        if (now - _seatedAt > 1.0) return false;
        try
        {
            var session = ch.GetSession();
            if (session != null)
                foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>())
                    if ((e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == ch)
                    { _seatedAt = now; return true; }
        }
        catch { }
        return false;
    }
    private static double _seatedAt = -10;
}
