using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): a grid-anchored frame arriving in a planet cell, and grids above the cap on their conic
// (grid HighSpeed).
public static partial class ServerFrames
{
    /// <summary>Grid HighSpeed (analytic, like the player's): grid id -> (body, elements).</summary>
    private static readonly Dictionary<long, (string body, KeplerianElements el)> _gridHighSpeed =
        new Dictionary<long, (string, KeplerianElements)>();

    // ───────────────────────────── arrival (grid-anchored frame) ─────────────────────────────

    /// <summary>How far ahead (real seconds) a piloted grid's arrival is announced to its client (TryMaterializeGrids):
    /// its fade (CrossingFade.OutSeconds) is black before the move.</summary>
    public const double NoticeSeconds = SEAerospace.Frames.ArrivalNotice.NoticeSeconds;
    public static int ArrivalNotices;
    /// <summary>The frame the last arrival notice is for (the client fades only for its own: CrossingFade).</summary>
    public static long ArrivalNoticeFrame = -1;

    /// <summary>Past this from its anchor (static: a station, an asteroid) or its berth's centre (a ship) a member leaves its
    /// frame on its own orbit - the split (FrameHost.UpdateRider, the grid loop here) and the HUD's boundary alike.</summary>
    public static double LeaveRadius(bool staticAnchor) => SlotRadius;   // (one size: a static anchor's frame as a ship's)
    /// <summary>Frame id -> the real time of its last arrival notice (ArrivalNotice.Due re-arms a stale one).</summary>
    static readonly Dictionary<long, double> _arrivalNoticed = new Dictionary<long, double>();
    static object _noticeSession;

    private static void TryMaterializeGrids(ProximityFrame f, List<OrbitalGridComponent> grids, Vector3D anchorPos, double t)
    {
        var reg = SystemHost.Registry;
        GravityBody node = reg.Find(f.ParentBodyName);
        BodyDefinition def = reg.FindDefinition(f.ParentBodyName);
        if (node == null || def == null || string.IsNullOrEmpty(def.ParkSubtype)) return;
        double shell = PlanetBerths.ShellRadius(def);
        StateVector cel = OrbitPropagation.StateAt(f.Elements, t);
        if (!IsFinite(cel.Position)) return;
        bool act = cel.Position.Length() < shell;
        double epoch = t;   // the time the state `cel` holds at (the crossing, when predicted)
        if (!act && OrbitPropagation.TryTimeToRadius(f.Elements, shell, out _, out double tInRel))
        {
            double tCross = FrameHost.NextInboundCrossingPublic(f.Elements, tInRel, t);
            // A piloted grid's arrival announced NoticeSeconds (real time) ahead: its client fades the view out
            // (CrossingFade) so that the frame the turned ship arrives in - drawn before any mod code can turn the chase
            // camera - and the engine's ~1 s stall after a crossing are black. The move itself keeps its time.
            double wallNow = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (SEAerospace.Frames.ArrivalNotice.Due(tCross, t, SystemHost.Timescale, _arrivalNoticed.TryGetValue(f.Id, out double lastWall) ? lastWall : double.NaN, wallNow))
            {
                // (a piloted grid aboard - or the player riding it on foot: moved as far, through the same stall)
                string who = null;
                foreach (var g in grids)
                    if (g.Entity != null && g.Entity.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) { who = "a piloted grid aboard"; break; }
                if (who == null && FrameHost.PlayerId != 0 && f.HasMember(FrameHost.PlayerId)) who = "the player riding it";
                if (who != null)
                {
                    _arrivalNoticed[f.Id] = wallNow;
                    System.Threading.Interlocked.Exchange(ref ArrivalNoticeFrame, f.Id);
                    System.Threading.Interlocked.Increment(ref ArrivalNotices);
                    Event($"arrival of frame #{f.Id} announced {tCross - t:F2} s ahead ({who})");
                }
            }
            if (!double.IsNaN(tCross) && tCross >= t && tCross - t <= Math.Max(0.1, SystemHost.Timescale * 0.05))
            {
                act = true;
                var entry = OrbitPropagation.StateAt(f.Elements, tCross);
                if (IsFinite(entry.Position) && IsFinite(entry.Velocity)) { cel = entry; epoch = tCross; }
            }
        }
        if (!act) return;
        if (!VoxelBerthRegistry.TryGetCell(node.Name, reg, out Vector3D cell)) return;

        Chart chart = Chart.Of(node.Name, t);
        Vector3D target = cell + chart.FromInertial(cel.Position);
        double speed = chart.VelFromInertial(cel.Position, cel.Velocity).Length();
        _arrivalNoticed.Remove(f.Id);
        EntryHost.ForgetGlow(f);   // (its plasma's air direction was in the rails' axes: off by the chart's angle after the move)
        // At or under the cap (reentry's band or an airless border's clamp makes sure); if not, capped here.
        // (the entry's cap and margin, as the client's arrival: EntryHost.Cap honours the harness's TestCap - 2026-10-15)
        double cap = EntryHost.Cap;
        bool capped = EntryHost.OverCap(speed);
        double k = capped ? cap / speed : 1.0;
        var went = new HashSet<long>();   // (moved with an earlier member's joined set: its queued position is already its arrival)
        foreach (var g in grids)
        {
            if (went.Contains(g.Id)) continue;
            // (a dead one - deleted, its Unregister not run yet - threw here, 2026-10-15: it arrives nowhere. Past this a dead grid
            //  reads as static: the static branch would read its transform (a throw) and give it a conic)
            if (!GridMembers.TryPosition(g, out Vector3D gAt)) continue;
            Vector3D off = gAt - anchorPos;
            Vector3D vRel = GridMembers.Velocity(g);
            Vector3D dest = cell + chart.FromInertial(cel.Position + off);   // berth offsets are inertial
            if (!GridMembers.IsDynamic(g))
            {
                // (a static grid with joints - a ship docked to the station - moved mid-tick left the constraint
                //  stretched across thousands of km: the set moves together, before the physics step)
                if (GridMembers.IsConstrained(g))
                {
                    var set = new List<Entity>();
                    Vector3D d = dest - gAt;
                    if (!GridMembers.MoveSet(g, set, out string why)) { set.Clear(); set.Add(g.Entity); Event($"arrival: grid {g.Id}'s joined set not movable ({why}): it moves alone"); }
                    lock (_deferred) foreach (var e in set) _deferred.Add((e, e.Data.GetWorldTransform().Position + d, Vector3D.Zero));
                }
                // (moved straight mid-tick before: a static moved so can stay in its old physics world while its box leaves it -
                //  the "still active bodies in native world" crash, 2026-10-17. The teleport phase checks its landing - HavokMoves)
                else MoveGrid(g, dest, null, allowStatic: true);
                // A static grid keeps its orbit too (placed each tick along it, as a HighSpeed grid): dropped
                // without its velocity it fell straight down (the game made it dynamic).
                var sel = CaptureMath.CaptureElements(new StateVector(cel.Position + off, cel.Velocity), node.Mu, epoch);
                if (IsFinite(sel.SemiMajorAxis)) _gridHighSpeed[g.Id] = (node.Name, sel);
                continue;
            }
            var arrV = chart.VelFromInertial(cel.Position + off, cel.Velocity + vRel) * k;
            DevReentry.Expect(g.Id, dest, arrV, chart.FromInertialRotation(), g.Entity);   // (DEV: the reentry test checks the handover)
            MoveGrid(g, dest, arrV, out var withArr, default, chart.FromInertialRotation());
            foreach (var w in withArr) went.Add(w.Id);
        }
        // The player, if a member, arrives with the same offset from the anchor.
        long fid = f.Id;
        FrameHost.RequestArrival(fid, node.Name, target, anchorPos, cel.Position, cel.Velocity, epoch);
        SystemHost.Frames.Dissolve(fid);
        AnchorAccel.Remove(fid);
        Event($"ARRIVE grid frame #{fid} -> {node.Name}: {grids.Count} grid(s), r={cel.Position.Length() / 1000:F1} km |v|={speed:F0} m/s{(capped ? $" -> capped to {cap:F0}" : "")}");
    }

    /// <summary>Save: the grid HighSpeed conics. Caller holds FramesLock.</summary>
    internal static List<(long id, string body, KeplerianElements el)> GridHighSpeedEntries()
    {
        var l = new List<(long, string, KeplerianElements)>();
        foreach (var kv in _gridHighSpeed) l.Add((kv.Key, kv.Value.body, kv.Value.el));
        return l;
    }

    /// <summary>Load: put a grid back on its HighSpeed conic.</summary>
    internal static void RestoreGridHighSpeed(long id, string body, KeplerianElements el)
    {
        // (a corrupt saved entry - NaN elements - matched neither exit test: MoveGrid(NaN) and ~60 log lines a second)
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.Eccentricity) || !IsFinite(el.MeanMotion) || el.Mu <= 0 || string.IsNullOrEmpty(body)) return;
        _gridHighSpeed[id] = (body, el);
    }

    /// <summary>Grids above the cap in a planet cell ride their conic (analytic HighSpeed, as the player).</summary>
    private static void StepGridHighSpeed()
    {
        if (_gridHighSpeed.Count == 0) return;
        double t = SystemHost.Now;
        var reg = SystemHost.Registry;
        var done = new List<long>();
        var refold = new List<(long id, string body, KeplerianElements el)>();
        foreach (var kv in _gridHighSpeed)
        {
            var g = GridMembers.Get(kv.Key);
            if (g != null && SystemHost.Frames.FindByMember(g.Id) != null) { done.Add(kv.Key); continue; } // framed: rails own it
            if (g == null || !VoxelBerthRegistry.TryGetCell(kv.Value.body, reg, out Vector3D cell)) { done.Add(kv.Key); continue; }
            var def = reg.FindDefinition(kv.Value.body);
            var el = kv.Value.el;
            Vector3D thrust = Chart.Of(kv.Value.body, t).ToInertial(GridMembers.ThrustDv(g));
            if (thrust.LengthSquared() > 1e-12)
            {
                // Grid thrust in HighSpeed: fold the frame's thrust impulse into the conic (as the player's).
                var node = reg.Find(kv.Value.body);
                StateVector s0 = OrbitPropagation.StateAt(el, t);
                var el2 = node != null ? CaptureMath.CaptureElements(new StateVector(s0.Position, s0.Velocity + thrust), node.Mu, t) : el;
                if (IsFinite(el2.SemiMajorAxis) && IsFinite(el2.MeanMotion)) { el = el2; refold.Add((kv.Key, kv.Value.body, el2)); }
            }
            StateVector st = OrbitPropagation.StateAt(el, t);
            Chart chart = Chart.Of(kv.Value.body, t);
            Vector3D vChart = chart.VelFromInertial(st.Position, st.Velocity);
            double speed = vChart.Length();
            double floor = (def?.RadiusMeters ?? 0) + FrameHost.SurfaceGuard;
            bool dynamic = GridMembers.IsDynamic(g);
            if ((dynamic && speed < FrameHost.SpeedCap * FrameHost.HighSpeedExitFraction) || st.Position.Length() < floor)
            {
                Vector3D v = speed > FrameHost.SpeedCap ? vChart * (FrameHost.SpeedCap / speed) : vChart;
                if (dynamic) GridMembers.SetVelocity(g, v);
                done.Add(kv.Key);
                Event($"grid {g.Id} HighSpeed off ({speed:F0} m/s, alt {(st.Position.Length() - (def?.RadiusMeters ?? 0)) / 1000:F1} km)");
                continue;
            }
            MoveGrid(g, cell + chart.FromInertial(st.Position), Vector3D.Zero, allowStatic: true);   // (a static on its conic: through the teleport phase, its landing checked)
        }
        foreach (var r in refold) if (!done.Contains(r.id)) _gridHighSpeed[r.id] = (r.body, r.el);
        foreach (long id in done) _gridHighSpeed.Remove(id);
    }
}
