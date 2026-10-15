using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// FrameHost (partial): out of the rails - the player's own arrival in a planet cell, the requests the server half
// sends (shift, merge, a grid frame's arrival), and what is left of the player's HighSpeed.
public static partial class FrameHost
{
    // ───────────────────────────── arrival (conjunction -> planet cell) ─────────────────────────────

    private static void TryMaterialize(Keen.VRage.Core.Game.Systems.Session session, ProximityFrame f, double t)
    {
        if (_tpPending) return;
        var reg = SystemHost.Registry;
        GravityBody node = reg.Find(f.ParentBodyName);
        BodyDefinition def = reg.FindDefinition(f.ParentBodyName);
        if (node == null || def == null || string.IsNullOrEmpty(def.ParkSubtype)) return; // e.g. orbiting the star
        double shell = PlanetBerths.ShellRadius(def);
        if (shell <= 0) return;

        StateVector cel = OrbitPropagation.StateAt(f.Elements, t);
        if (!IsFinite(cel.Position)) return;
        bool inside = cel.Position.Length() < shell;
        bool act = inside;
        if (!act && OrbitPropagation.TryTimeToRadius(f.Elements, shell, out double tOutRel, out double tInRel))
        {
            double tCross = NextInboundCrossing(f.Elements, tInRel, t);
            // Materialize at (or just before) the crossing; under warp one frame can jump past it.
            if (IsFinite(tCross) && tCross >= t && tCross - t <= Math.Max(0.1, SystemHost.Timescale * 0.05))
            {
                act = true;
                var entry = OrbitPropagation.StateAt(f.Elements, tCross);
                if (IsFinite(entry.Position) && IsFinite(entry.Velocity)) cel = entry;
            }
        }
        if (!act) return;
        if (!VoxelBerthRegistry.TryGetCell(node.Name, reg, out Vector3D cellCenter)) return;

        Chart chart = Chart.Of(node.Name, t);
        Vector3D worldPos = cellCenter + chart.FromInertial(cel.Position);   // the cell is the planet's rotating chart
        Vector3D worldVel = chart.VelFromInertial(cel.Position, cel.Velocity);
        double speed = worldVel.Length();
        // Inside the planet's frame the world's cap applies: reentry (EntryHost, server) brings you to it, through
        // the braking band or with an airless border's clamp. Over it here, wait a few ticks for that clamp (its
        // jolt counts); then arrive capped regardless: no rails inside the planet's frame.
        double alt = cel.Position.Length() - def.RadiusMeters;
        if (speed > SpeedCap * 1.001 && EntryHost.Enabled && ++_overCapWait <= 10) return;
        _overCapWait = 0;
        bool capped = speed > SpeedCap * 1.001;
        Vector3D applied = capped ? worldVel * (SpeedCap / speed) : worldVel;

        // (a frame with grids in it arrives on the server - grids and the player together, RequestArrival)
        foreach (long mid in f.Members) if (GridMembers.IsGridId(mid)) return;
        long fid = f.Id;
        SystemHost.Frames.Dissolve(fid);
        _cur.Frame = null;
        _wasInKeep = true;
        StartTeleport(session, worldPos, applied, t, chart.FromInertialRotation());
        Event($"ARRIVE frame #{fid} -> {node.Name} cell: r={cel.Position.Length() / 1000:F1} km (shell {shell / 1000:F1}) " +
              $"|v|={speed:F0} m/s{(capped ? $" -> capped to {SpeedCap:F0} ({alt / 1000:F1} km up)" : "")}");
    }

    // ───────────────────────────── HighSpeed (planet cell, above the cap) ─────────────────────────────

    public const double HighSpeedExitFraction = 0.9;  // drop back to physics below cap × this
    private static int _overCapWait;
    public const double SurfaceGuard = 2000.0;        // m above the body radius: never step closer

    /// <summary>Load: back onto the saved HighSpeed conic (the saved position is already on it).</summary>
    public static void RestoreHighSpeed(string body, KeplerianElements el)
    {
        // A saved state that is not a real orbit (a hand-edited or corrupt save): ignored.
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity) || !IsFinite(el.MeanMotion) || !IsFinite(el.Epoch) || el.Eccentricity < 0)
        { Event($"saved HighSpeed state for {body} ignored: not finite"); return; }
        // (HighSpeed is gone: a saved HighSpeed conic goes onto rails with the same orbit.)
        _pendingOrbit = (body, el);
        ForceStow = true;
    }

    // (the player's analytic HighSpeed - its conic, thrust fold and harness kick - went with it; what the harness reads and
    //  sets of it stays, reading "not in HighSpeed": the status line's host row, 'kick', 'hsfold')
    public static bool HighSpeedActive => false;
    public static Vector3D HighSpeedVelocity => Vector3D.Zero;
    public static string HighSpeedElements => "-";
    public static double HsResidualAvg, HsResidualMax;
    public static int HsFolds;
    public static bool HsThrustFold = true;   // INERT since the player HighSpeed removal: the harness 'hsfold' sets it, nothing reads it
    public static string HsDiag = "";
    /// <summary>DEV: give the HighSpeed player a velocity - never in HighSpeed now.</summary>
    public static string Kick(double pro, double rad, double nor) => "not in HighSpeed";

    // ───────────────────────────── requests from the server half ─────────────────────────────

    private static long _playerId;
    private static Vector3D _pendingShift;
    private sealed class ArrivalRequest
    {
        public string Body; public Vector3D Target; public Vector3D AnchorPos; public Vector3D CelPos; public Vector3D CelVel; public double Epoch;
        public long Wall;   // (when it was made: requests over 3 s old are dropped)
    }
    private static ArrivalRequest _pendingArrival;

    /// <summary>Server re-pinned a grid-anchored frame by shifting it; the player member shifts too.</summary>
    public static void RequestShift(long frameId, Vector3D shift)
    {
        var f = SystemHost.Frames?.FindByMember(_playerId);
        if (f != null && f.Id == frameId) _pendingShift += shift;
    }

    private static Vector3D _pendingMergeMove, _pendingMergeDv;
    private static bool _pendingMerge;

    /// <summary>No arrival, merge or shift waiting for the walking player (seated, a new character, a new session).</summary>
    private static void DropPending()
    {
        // (under the frames lock: the server thread writes these - RequestShift, RequestMerge, RequestArrival - under it)
        lock (ServerFrames.FramesLock) { _pendingArrival = null; _pendingMerge = false; _pendingMergeMove = _pendingMergeDv = Vector3D.Zero; _pendingShift = Vector3D.Zero; }
    }

    /// <summary>Server merged the player's frame into another: move into the host berth at the relative state.</summary>
    public static void RequestMerge(long incomerId, Vector3D translation, Vector3D dVel)
    {
        var f = SystemHost.Frames?.FindByMember(_playerId);
        if (f == null || f.Id != incomerId) return;
        _pendingMerge = true;
        _pendingMergeMove += translation;
        _pendingMergeDv += dVel;
    }

    /// <summary>Server materialized a grid-anchored frame; the player member arrives at the same offset.</summary>
    public static void RequestArrival(long frameId, string body, Vector3D target, Vector3D anchorPos,
                                      Vector3D celPos, Vector3D celVel, double epoch)
    {
        var f = SystemHost.Frames?.FindByMember(_playerId);
        if (f == null || f.Id != frameId) return;
        _pendingArrival = new ArrivalRequest { Body = body, Target = target, AnchorPos = anchorPos, CelPos = celPos, CelVel = celVel, Epoch = epoch,
                                               Wall = System.Diagnostics.Stopwatch.GetTimestamp() };
    }

    /// <summary>Returns true when this tick was consumed by a server-requested move.</summary>
    private static bool ApplyServerRequests(Keen.VRage.Core.Game.Systems.Session session, Entity ch, Vector3D pos, double t)
    {
        if (_pendingArrival != null)
        {
            var a = _pendingArrival;
            _pendingArrival = null;
            // (stale: applied only on foot, a request made while seated waited - the ship carried the player down - and fired
            //  minutes later when the cockpit was destroyed, teleporting the player to where the frame had arrived)
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - a.Wall) > 3 * System.Diagnostics.Stopwatch.Frequency)
            { Event("player arrival request dropped: stale"); return false; }
            _pendingShift = Vector3D.Zero;
            Vector3D off = pos - a.AnchorPos;              // berth offset (inertial window axes)
            Vector3D relPos = a.CelPos + off;
            Chart achart = Chart.Of(a.Body, t);
            // (your own motion in the frame too, true - not warped: the grids arrive with theirs; a rider drifting off the
            //  ship lost it, and left the ship at a different speed than they had)
            Vector3D vRel = ReadVelocity(ch) / Math.Max(1.0, SystemHost.Timescale);
            Vector3D chartVel = achart.VelFromInertial(relPos, a.CelVel + (IsFinite(vRel) ? vRel : Vector3D.Zero));
            Vector3D arriveAt = VoxelBerthRegistry.TryGetCell(a.Body, SystemHost.Registry, out Vector3D acell)
                ? acell + achart.FromInertial(relPos) : a.Target + off;
            double speed = chartVel.Length();
            // (the grids of this frame arrive capped - TryMaterializeGrids - so the player is capped with them: arriving
            //  in HighSpeed a hair over the cap (1010 vs 990) re-stowed the player inside the planet's frame and the
            //  whole frame arrived a second time)
            if (speed > SpeedCap) { chartVel *= SpeedCap / speed; speed = SpeedCap; }
            _cur.Frame = null;
            _wasInKeep = true;
            StartTeleport(session, arriveAt, chartVel, t, achart.FromInertialRotation());
            // (the grids the server turned into the chart: their cockpits' targets with them - ServerFrames.TargetTurns)
            Event($"player arrives with its grid frame at {a.Body}");
            return true;
        }
        if (_pendingMerge)
        {
            _pendingMerge = false;
            Vector3D target = (_tpPending ? _tpTarget : pos) + _pendingMergeMove;
            Vector3D v = (_tpPending ? _tpVelocity : ReadVelocity(ch)) + _pendingMergeDv;
            _pendingMergeMove = Vector3D.Zero; _pendingMergeDv = Vector3D.Zero; _pendingShift = Vector3D.Zero;
            StartTeleport(session, target, v, t);
            return true;
        }
        if (_pendingShift.LengthSquared() > 1e-6 && _tpPending)
        {
            // Still arriving: the frame moved meanwhile (a re-pin, a move off a dirty slot), so the teleport's
            // target moves with it (dropping the shift left you where the frame was: split off 800 km away).
            _tpTarget += _pendingShift;
            _pendingShift = Vector3D.Zero;
        }
        if (_pendingShift.LengthSquared() > 1e-6)
        {
            Vector3D p = pos + _pendingShift;
            _pendingShift = Vector3D.Zero;
            if (!GridMembers.Finite(p)) { GridMembers.NaNRefused++; return false; }
            var wt = ch.Data.GetWorldTransform();
            ch.Data.Set(new WorldTransform(p, wt.Orientation));
            ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Position = p };
            return true;   // this tick's position is stale now (the rider step split you off 800 km 'away')
        }
        return false;
    }

    /// <summary>The earliest inbound shell crossing of any frame in (t0, t1], or NaN. Caller holds FramesLock.</summary>
    internal static double EarliestArrival(double t0, double t1)
    {
        var reg = SystemHost.Registry;
        double best = double.NaN;
        foreach (var f in SystemHost.Frames.Frames)
        {
            BodyDefinition def = reg.FindDefinition(f.ParentBodyName);
            if (def == null || string.IsNullOrEmpty(def.ParkSubtype)) continue;
            double shell = PlanetBerths.ShellRadius(def);
            if (shell <= 0) continue;
            var cur = OrbitPropagation.StateAt(f.Elements, t0);
            if (!IsFinite(cur.Position) || cur.Position.Length() < shell) continue;
            if (!OrbitPropagation.TryTimeToRadius(f.Elements, shell, out _, out double tInRel)) continue;
            double tc = NextInboundCrossing(f.Elements, tInRel, t0);
            if (IsFinite(tc) && tc > t0 && tc <= t1 && (double.IsNaN(best) || tc < best)) best = tc;
        }
        return best;
    }

    public static double NextInboundCrossingPublic(KeplerianElements el, double tInRel, double t) => NextInboundCrossing(el, tInRel, t);
}
