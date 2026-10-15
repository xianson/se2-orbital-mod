using System.Collections.Concurrent;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The SERVER half of the frame manager (SE-Aerospace FrameManager): everything that touches server
/// entities (grids) runs here, on the server scene's thread, ticked from <see cref="ServerPlanetBeacon"/>'s
/// job. The client half (<see cref="FrameHost"/>) owns the local player's character, which SE2 simulates
/// client-side. Both halves share the frame registry under <see cref="FramesLock"/>.
///
/// Per frame with grid members (SE1 UpdateFrame):
///  - ANCHOR = the heaviest dynamic grid (SE1 ElectAnchor). The server pins it to the berth, reads its
///    velocity, zeroes it, and folds it into the rails (FrameRails.FoldDrain). The folded slice / dt is
///    the frame's acceleration A, published in <see cref="AnchorAccel"/>.
///  - OTHER MEMBERS get the differential gravity of the berth (CW) minus A, as a velocity change
///    (SE2 has no force accumulator; the engine's own impulse helpers do the same). Characters get the
///    same from the client half. A member beyond the slot radius SPLITS into its own frame.
///  - When the anchor is the player (no grids), the client drains and publishes A; grid members here
///    still get CW − A.
///  - ARRIVAL for a grid-anchored frame happens here: every grid member is relocated into the planet
///    cell at the crossing state (HighSpeed above the cap), and the player member is handed to the
///    client with its offset.
/// </summary>
public static class ServerFrames
{
    public const double AttachRadius = 5000.0;   // m: grids this close to a stowing player join its frame
    public const double AttachRelSpeed = 100.0;  // m/s: and moving with it (not landed below it)
    public const double PinTolerance = 200.0;    // m: re-pin the anchor grid (and shift the frame) past this
    public const double SlotRadius = 10000.0;    // m: a frame's radius - one size for every frame (the user, 2026-10-07: ~10 km)
    /// <summary>A static grid or asteroid captures a frame within CaptureEnterRadius of it, and a rider leaves
    /// only past CaptureRadius (hysteresis: no flapping at the edge).</summary>
    public const double CaptureRadius = 10000.0, CaptureEnterRadius = 8000.0;

    /// <summary>A frame's anchor is static (a static grid or an asteroid): its position, else false.</summary>
    public static bool StaticAnchorOf(ProximityFrame f, out Vector3D pos)
    {
        pos = f.BerthCenter;
        if (f.AnchorEntityId == AsteroidAnchorId) return true;
        if (!GridMembers.IsGridId(f.AnchorEntityId)) return false;
        var g = GridMembers.Get(f.AnchorEntityId);
        if (g == null || GridMembers.IsDynamic(g)) return false;
        pos = GridMembers.Position(g);
        return true;
    }

    /// <summary>An anchor's joined entities, and when they were looked up (wall seconds).</summary>
    private static readonly Dictionary<long, (HashSet<Keen.VRage.DCS.Components.Entity> set, double at)> _joinedOf = new Dictionary<long, (HashSet<Keen.VRage.DCS.Components.Entity>, double)>();

    /// <summary>The warp each member's velocity was last scaled for.</summary>
    private static readonly Dictionary<long, double> _gridN = new Dictionary<long, double>();
    /// <summary>The physical velocity the warp steering set on a member last step (what physics then did to it: thrust, a bump).</summary>
    private static readonly Dictionary<long, Vector3D> _vSet = new Dictionary<long, Vector3D>();

    /// <summary>A new system (another world): nothing of the last one's frames - trackers, warp scales, conics, holds, notices.</summary>
    public static void ResetWorld()
    {
        // (queued moves were the old world's entities: run in the new one, each threw)
        lock (_deferred) _deferred.Clear();
        lock (_deferredRot) _deferredRot.Clear();
        lock (GridMembers.Pending) GridMembers.Pending.Clear();
        _rockOf.Clear(); _rebasePending.Clear(); _lastSteerNow.Clear(); _mergeRefused.Clear(); _everFramed.Clear(); _unframedSince.Clear();
        _joinedOf.Clear(); _gridN.Clear(); _vSet.Clear(); _born.Clear(); _dirtySince.Clear(); _trueRel.Clear(); _settle.Clear();
        lock (AnchorAccel) AnchorAccel.Clear();
        lock (GridPositions) GridPositions.Clear();
        _gridHighSpeed.Clear(); _trackers.Clear(); Holding.Clear(); _arrivalNoticed.Clear();
        System.Threading.Volatile.Write(ref _views, EmptyViews);
        System.Threading.Volatile.Write(ref _mergeAhead, double.NaN);
    }

    /// <summary>When each frame was first seen (wall seconds): a fresh frame may still move off a dirty slot.</summary>
    private static readonly Dictionary<long, double> _born = new Dictionary<long, double>(), _dirtySince = new Dictionary<long, double>();
    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The anchor id of a frame anchored by an asteroid (not an entity we track by id).</summary>
    public const long AsteroidAnchorId = -2;

    /// <summary>
    /// A frame's static anchor: a static grid in it (a station, a base on its rock), or a placed asteroid
    /// near its berth. Static things cannot move: they are the frame's fixed point, and everything else
    /// (the player too) moves about them normally.
    /// </summary>
    static bool StaticAnchor(ProximityFrame f, out long id, out Vector3D pos)
    {
        // A FRAME IS ALWAYS ON A STATIC GRID OR A ROCK when one is in it (the user, 2026-10-07): a station, an asteroid base, a
        // wreck, a rock - whatever is fixed in the frame is its anchor, no other condition. (Rules that asked for a reason - a
        // grid of yours within 2 km - contradicted the pinned anchor: a ship-anchored frame could never come near the rock.)
        id = 0; pos = default;
        // a static member (in at 8 km, out at 10: hysteresis)
        foreach (long m in f.Members)
        {
            if (!GridMembers.IsGridId(m)) continue;
            var g = GridMembers.Get(m);
            double reach = f.AnchorEntityId == m ? CaptureRadius : CaptureEnterRadius;
            if (g != null && g.IsServer && !GridMembers.IsDynamic(g) && (GridMembers.Position(g) - f.BerthCenter).Length() <= reach)
            { id = m; pos = GridMembers.Position(g); return true; }
        }
        // a static grid in no frame within the frame: it joins it, and anchors it (the world's static grids only)
        foreach (var g in GridMembers.Statics())
        {
            if (!g.IsServer || g.Entity == null || GridMembers.IsDynamic(g)) continue;
            if ((GridMembers.Position(g) - f.BerthCenter).Length() > CaptureEnterRadius) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            if (SystemHost.Frames.AddMember(f, g.Id)) { Event($"frame #{f.Id}: static grid {g.Id} '{g.DisplayName}' in it joins it"); id = g.Id; pos = GridMembers.Position(g); return true; }
        }
        // a rock - only on a stable orbit (MembershipRules.StaticAnchorKind, tested offline): a static grid on a decaying orbit
        // is made a ship, a rock cannot be - nor moved. Unstable, the rock is left out: the frame's grids or the player anchor
        // it (a rock-anchored frame let go here goes to the election below, as when its rock is deleted).
        if (SEAerospace.Frames.MembershipRules.StaticAnchorKind(false, true, OrbitStable(f)) != SEAerospace.Frames.MembershipRules.StaticKind.Rock)
        {
            if (_rockOf.Remove(f.Id) && f.AnchorEntityId == AsteroidAnchorId)
                Event($"frame #{f.Id}: its orbit meets {f.ParentBodyName}'s border (periapsis {f.Elements.PeriapsisRadius / 1000:F0} km): not anchored on its rock - a rock cannot leave its berth");
            pos = default;
            return false;
        }
        // (the same rock while it anchors: one 1 km from the last is another)
        double rr = f.AnchorEntityId == AsteroidAnchorId ? CaptureRadius : CaptureEnterRadius;
        if (AsteroidBridge.NearestAsteroid(f.BerthCenter, rr, out pos))
        {
            if (f.AnchorEntityId == AsteroidAnchorId && _rockOf.TryGetValue(f.Id, out var was) && (was - pos).Length() > 1000)
                Event($"frame #{f.Id}: its rock is another now ({(was - pos).Length() / 1000:F1} km from the last)");
            _rockOf[f.Id] = pos;
            id = AsteroidAnchorId; return true;
        }
        pos = default;
        return false;
    }
    /// <summary>The rock a rock-anchored frame is on (its box centre), to tell a change of rock.</summary>
    static readonly Dictionary<long, Vector3D> _rockOf = new Dictionary<long, Vector3D>();

    /// <summary>A rock that does not persist anchors a frame only when one of its grids is this close to it (m).</summary>
    public const double RockReach = 2000.0;

    /// <summary>Guards the frame registry and frame state across the client and server threads.</summary>
    public static readonly object FramesLock = new object();

    /// <summary>Frame id -> the frame's acceleration A (m/s²) from the last fold (published by the anchor owner).</summary>
    // Members steered along their own orbits in warp: their true relative velocity, and ticks left after warp ends.
    private static readonly Dictionary<long, Vector3D> _trueRel = new Dictionary<long, Vector3D>();
    private static readonly Dictionary<long, int> _settle = new Dictionary<long, int>();
    private const int SettleTicks = 30;
    /// <summary>The most a member may be carried at in warp (m/s; the world's cap is 1000).</summary>
    private const double WarpMemberSpeed = 800;
    /// <summary>A ship within this of a station's box (m) is held at the station through warp.</summary>
    public const double StationHoldMargin = 150;
    static bool AtStation(OrbitalGridComponent g, List<BoundingBoxD> stations)
    {
        var gb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data);
        Vector3D p = GridMembers.Position(g);
        foreach (var s in stations)
            if (gb.HasValue ? s.Intersects(gb.Value) : s.Contains(p)) return true;
        return false;
    }

    /// <summary>Warp down to the highest level that carries a member moving at trueSpeed relative to you.</summary>
    private static string _fastestName = "?";
    private static void LimitWarp(double trueSpeed)
    {
        double was = SystemHost.Timescale, to = 1;
        foreach (double l in WarpControl.Levels) if (l * trueSpeed <= WarpMemberSpeed * 0.75 && l < was) to = l;
        SystemHost.Timescale = to;
        Event($"warp x{was:F0} -> x{to:F0}: a member moves {trueSpeed:F2} m/s relative to its frame (x{was:F0} would need {was * trueSpeed:F0} m/s) - {_fastestName}");
        WarpControl.PendingSay = $"Warp limited to ×{to:N0}: a ship moving nearby";
    }

    // The fastest TRUE relative speed (m/s) among the members of YOUR frame that the limit above judges - left by the server's
    // last step of that frame, so a raise can be clamped before it runs a tick it cannot carry (raised first and limited after,
    // one tick at x1000 flung the ship that tripped it). Written on the server thread, read on the client's: under its lock.
    private static readonly object _fastLock = new object();
    private static double _fastTrue, _fastAt = double.NegativeInfinity;
    private static long _fastFrame, _fastGrid;
    private static string _fastName;
    // (the grid's id and name kept, not a message: this runs every tick, the text only when a raise is clamped)
    static void PublishFastest(long frameId, double trueSpeed, OrbitalGridComponent g)
    {
        lock (_fastLock) { _fastFrame = frameId; _fastTrue = trueSpeed; _fastAt = Wall(); _fastGrid = g != null ? g.Id : 0; _fastName = g?.DisplayName; }
    }

    /// <summary>The highest warp up to <paramref name="want"/> that the limit would leave standing in your frame (as LimitWarp
    /// picks it, 0.75 under the line: raised to just under it, a ship speeding up a little dropped it again); never below
    /// <paramref name="cur"/> (a clamp of a raise, not a cut). <paramref name="why"/> names the ship when it clamped.</summary>
    public static double ClampWarpRaise(double want, double cur, out string why)
    {
        why = null;
        if (want <= cur || want <= 1.0) return want;
        var pf = FrameHost.PlayerFrame;
        double v; string name;
        lock (_fastLock)
        {
            // (only your frame's, and only fresh: a frame not stepped for a while has nothing moving in it to judge)
            if (pf == null || pf.Id != _fastFrame || Wall() - _fastAt > 2.0) return want;
            v = _fastTrue; name = $"grid {_fastGrid} '{_fastName}' in frame #{_fastFrame}";
        }
        if (want * v <= WarpMemberSpeed * 0.75) return want;
        double to = 1;
        foreach (double l in WarpControl.Levels) if (l * v <= WarpMemberSpeed * 0.75 && l <= want) to = l;
        to = Math.Max(to, cur);
        if (to < want)
        {
            why = name;
            Event($"warp raise x{cur:F0} -> x{want:F0} held at x{to:F0}: a member moves {v:F2} m/s relative to its frame (x{want:F0} would need {want * v:F0} m/s) - {name}");
        }
        return to;
    }
    public static readonly Dictionary<long, Vector3D> AnchorAccel = new Dictionary<long, Vector3D>();

    private static long _lastTickStamp;
    private static DateTime _lastGameTime;
    private static int _tick;

    /// <summary>Snapshot for the harness status (built on the server thread). Its text is made only while someone reads it
    /// (asked within SnapshotAskedSeconds), else every SnapshotIdleSeconds: the harness's own text, every half second for
    /// nobody, was 0.14 MB/s (a profile, 2026-10-15). The grid positions it also refreshes are refreshed as ever.</summary>
    public static string GridSnapshot { get { System.Threading.Volatile.Write(ref _snapAsked, Wall()); return _gridSnapshot; } }
    static volatile string _gridSnapshot = "";
    static double _snapAsked = double.NegativeInfinity, _snapBuilt = double.NegativeInfinity;
    public static double SnapshotAskedSeconds = 10, SnapshotIdleSeconds = 5;

    /// <summary>Grid id -> last known world position (server snapshot, for the harness).</summary>
    public static readonly Dictionary<long, Vector3D> GridPositions = new Dictionary<long, Vector3D>(); // lock GridPositions

    /// <summary>Server-side view of the player character's position.</summary>
    public static Vector3D PlayerPosition;

    /// <summary>DEV: zero every dynamic server grid's linear velocity on the next server tick.</summary>
    public static volatile bool StopAllGrids;
    /// <summary>DEV: log every dynamic player grid and whether it is held by a constraint.</summary>
    public static volatile bool DevFreeGrids;

    // ── requests from the client half ──
    /// <summary>RefVel is inertial; Body (null = no chart) and Time give the chart the grids' velocities convert from.</summary>
    public sealed class AttachRequest { public long FrameId; public Vector3D RefPos; public Vector3D RefVel; public Vector3D Berth; public string Body; public double Time; }
    public static readonly ConcurrentQueue<AttachRequest> Attach = new ConcurrentQueue<AttachRequest>();

    /// <summary>DEV: put a grid on its own orbit (a frame with the grid as anchor).</summary>
    public sealed class GridOrbitRequest { public long GridId; public string Body; public KeplerianElements El; }
    public static readonly ConcurrentQueue<GridOrbitRequest> GridOrbit = new ConcurrentQueue<GridOrbitRequest>();
    /// <summary>DEV: grids to close on the server's next tick (an anchor destroyed, for the scenario tests).</summary>
    public static readonly ConcurrentQueue<long> GridDelete = new ConcurrentQueue<long>();

    /// <summary>Grid HighSpeed (analytic, like the player's): grid id -> (body, elements).</summary>
    private static readonly Dictionary<long, (string body, KeplerianElements el)> _gridHighSpeed =
        new Dictionary<long, (string, KeplerianElements)>();

    /// <summary>The tick's slow sections (over 2 ms), for the [HITCH] line (ServerPlanetBeacon): what took the time.</summary>
    /// <summary>(Per thread: every planet's beacon job runs Tick, two can overlap - each reads back its own, ServerPlanetBeacon.)</summary>
    [ThreadStatic] public static string LastSlow;
    public static int LastFrameCount;
    [ThreadStatic] static System.Text.StringBuilder _slowT;
    static System.Text.StringBuilder _slow => _slowT ??= new System.Text.StringBuilder();
    static long Sw() => System.Diagnostics.Stopwatch.GetTimestamp();
    static string Ms(long a, long b) => a == 0 || b == 0 || b < a ? "-" : ((b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0");
    // (a section under 2 ms - nearly all - is told by its ticks alone: no conversion, no thread-static read. _secFloor is the
    //  2 ms mark rounded down, so every section over 2 ms still reaches the same test as before)
    static readonly long _secFloor = (long)(System.Diagnostics.Stopwatch.Frequency * 0.002);
    static void Sec(string name, long t0)
    {
        long d = Sw() - t0;
        if (d <= _secFloor) return;
        double ms = d * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > 2) _slow.Append(name).Append(' ').Append(ms.ToString("F0")).Append(" ms, ");
    }

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // once per frame - claimed atomically: the planet beacons' jobs call this in parallel, and two could both pass a
        // plain check and run the whole frame step twice
        long last = System.Threading.Interlocked.Read(ref _lastTickStamp);
        double dt = (now - last) / (double)System.Diagnostics.Stopwatch.Frequency;
        // (half a server tick: a planet's job starting more than 4 ms after the winner claimed the frame step again - and ran
        //  the drain, the fold and the warp steering twice)
        if (last != 0 && dt < 0.008) return;
        if (System.Threading.Interlocked.CompareExchange(ref _lastTickStamp, now, last) != last) return;
        // (the section timers are per thread: an overlapping second job keeps its own)
        _slow.Clear();
        try { TickBody(session, now, dt); } finally { LastSlow = _slow.ToString(); }
    }

    static void TickBody(Keen.VRage.Core.Game.Systems.Session session, long now, double dt)
    {
        TickRate.Server.Count();
        if (!ReferenceEquals(session, _noticeSession)) { _noticeSession = session; _arrivalNoticed.Clear(); }   // (frame ids restart in a new world)
        if (!OrbitalSettings.SawServer) OrbitalSettings.SawServer = true;
        OrbitalSettings.Poll();   // (the settings files, every ~2 s)
        SavedState.ApplyRadarSettings();
        { long _s0 = Sw(); SpawnGuard.Tick(AsteroidBridge.Generator(session)); Sec("spawnguard", _s0); }   // (takes FramesLock itself, only to read the frames: the zones' calls after it)
        StationKeepCharge.Sweep();   // (thrusters of grids no longer holding station ask nothing more)
        GridMembers.SweepTombs();   // (grids gone for good: forgotten once their tombstones expire)
        { long _s0 = Sw(); WarmBerths.Tick(session); Sec("warmberths", _s0); }   // (berths kept warm: no engine stall when a frame takes one)
   // no encounters on a planet's border (before they materialize); reads frames
        { long _s0 = Sw(); AsteroidBridge.Tick(session); Sec("asteroidbridge", _s0); }   // no procedural asteroids, ever (encounters and our own system place them)
        // Physics runs on game time (it slows and pauses with the game), so the tidal velocity
        // changes must use game-time dt too; the wall-clock dt above only gates once-per-frame.
        try
        {
            var gt = session.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>();
            if (gt != null)
            {
                DateTime g = gt.CurrentGameTime;
                SystemHost.SpinNow = g.Ticks / (double)TimeSpan.TicksPerSecond;   // (the planets spin on the game's clock)
                SEAerospace.Orbital.GravityBody.SpinClock ??= SystemHost.SpinAt;
                double gdt = _lastGameTime == default ? 0 : (g - _lastGameTime).TotalSeconds;
                _lastGameTime = g;
                if (gdt <= 0) return;   // paused
                dt = gdt;
            }
        }
        catch { }
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60.0;
        _tick++;

        var player = PlayerCache.Of(session);
        if (player != null) PlayerPosition = player.Data.GetWorldTransform().Position;

        if (StopAllGrids)
        {
            StopAllGrids = false;
            int n = 0;
            foreach (var g in GridMembers.All())
                if (g.IsServer && GridMembers.IsDynamic(g) && GridMembers.SetVelocity(g, Vector3D.Zero)) n++;
            Log.Default?.Info($"[ORBIT-DEV] stopped {n} dynamic grids");
        }

        if (SystemHost.Built && SystemHost.Frames != null)
        {
            EntryHost.ServerBeforeLock();   // (the aero hook: never called under FramesLock - API.md's lock rule)
            lock (FramesLock)
            {
                while (Attach.TryDequeue(out var req)) DoAttach(req);
                while (GridOrbit.TryDequeue(out var go)) DoGridOrbit(go);
                while (GridDamp.TryDequeue(out var gd)) DoGridDamp(gd.id, gd.on);
                while (GridLaunch.TryDequeue(out var gl)) DoGridLaunch(gl.id, gl.altKm, gl.body, gl.speed);
                while (FlyTest.TryDequeue(out var ft)) DoFlyTest(ft.id, ft.nose, ft.top);
                while (GridLevel.TryDequeue(out long glv)) DoGridLevel(glv);
                while (GridDelete.TryDequeue(out long gd))
                {
                    var g = GridMembers.Get(gd);
                    if (g?.Entity == null || !g.IsServer) { Event($"griddel: no server grid {gd}"); continue; }
                    try { g.Session.MarkEntityForClose(g.Entity); Event($"griddel: grid {gd} '{g.DisplayName}' closed (DEV)"); }
                    catch (Exception e) { Event($"griddel: {gd}: {e.Message}"); }
                }
                while (GridMove.TryDequeue(out var gm))
                {
                    var g = GridMembers.Get(gm.id);
                    if (g == null || !g.IsServer) Event($"gridmove: no server grid {gm.id}");
                    else if (!gm.group && GridMembers.IsConstrained(g)) Event($"gridmove: grid {gm.id} has joints: not moved (Havok cannot migrate them)");
                    else
                    {
                        // (group: the grid and everything joined to it, moved together, before the physics step)
                        var set = new List<Entity>();
                        if (!gm.group) set.Add(g.Entity);
                        else if (!GridMembers.MoveSet(g, set, out string why)) { Event($"gridmove: grid {gm.id} not moved: {why}"); continue; }
                        lock (_deferred) foreach (var e in set) _deferred.Add((e, e.Data.GetWorldTransform().Position + gm.d, Vector3D.Zero));
                        Event($"gridmove: {set.Count} entit(ies) to move by {gm.d.Length():F0} m (before the next physics step)");
                    }
                }
                // A restored frame whose saved berth was refused (inside a planet's frame): its grids follow it to the
                // fresh one, all together, once they are all loaded (or given up after ~10 s).
                foreach (var pf in SystemHost.Frames.Frames)
                {
                    if (pf.PendingBerthShift.LengthSquared() < 1) continue;
                    bool all = true;
                    foreach (long mid in pf.Members) if (GridMembers.IsGridId(mid) && GridMembers.Get(mid) == null) { all = false; break; }
                    if (!all && ++pf.PendingBerthWait < 600) continue;
                    // (moved now, their new places queued - Position reads them - and the player with them: enqueued for the next
                    //  tick, the frame step in between found them 60+ km from the fresh berth and let every one go)
                    int moved = 0; var wentB = new HashSet<long>();
                    foreach (long mid in pf.Members)
                        if (GridMembers.IsGridId(mid) && !wentB.Contains(mid) && GridMembers.Get(mid) is OrbitalGridComponent bg && bg.IsServer)
                        {
                            if (MoveGrid(bg, GridMembers.Position(bg) + pf.PendingBerthShift, null, out var withB)) { moved++; foreach (var w in withB) wentB.Add(w.Id); }
                        }
                    FrameHost.RequestShift(pf.Id, pf.PendingBerthShift);
                    Event($"frame #{pf.Id}: saved berth was inside a planet's frame - {moved} grid(s) follow it {pf.PendingBerthShift.Length() / 1000:F1} km to the fresh one{(all ? "" : " (some never loaded)")}");
                    pf.PendingBerthShift = Vector3D.Zero; pf.PendingBerthWait = 0;
                }
                if (_tick % 60 == 0 && SystemHost.Built && SystemHost.Frames != null) EncounterFrames.Prune();
                { long _s0 = Sw(); EncounterFrames.ServerTick(session, _tick); Sec("encounters", _s0); }
                DevFlight.ServerTick();
                DevFlight.ServerCommandTick();
                var frames = _tickFrames; frames.Clear(); frames.AddRange(SystemHost.Frames.Frames); LastFrameCount = frames.Count;   // (a snapshot: frames dissolve as they arrive)
                { long _s0 = Sw(); EntryHost.ServerTick(frames, SystemHost.Now); Sec("entry", _s0); }   // reentry (the braking band, an airless border's clamp): before arrivals
                { long _s0 = Sw(); foreach (var f in frames) { long _f0 = Sw(); UpdateGridFrame(f, dt); double _fms = (Sw() - _f0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (_fms > 15) { _slow.Append("frame #").Append(f.Id).Append(" (").Append(f.Members.Count).Append(" members) ").Append(_fms.ToString("F0")).Append(" ms [rules ").Append(Ms(_ugf[0], _ugf[1])).Append(" anchor-elect ").Append(Ms(_ugf[1], _ugf[2])).Append(" anchor ").Append(Ms(_ugf[2], _ugf[3])).Append(" others ").Append(Ms(_ugf[3], Sw())).Append("], "); } } Sec("frames", _s0); }
                StepGridHighSpeed();
                { long _s0 = Sw(); if (_tick % 10 == 0 && SavedState.Idle) StowLoneGrids(); Sec("stow", _s0); }   // (not before the save is applied: a frame made now took a saved frame's id)
                ApplyFictitious(dt);
                DevReentry.ServerTick(dt);
                { long _s0 = Sw(); int ph = (int)(_tick % MergeScreenInterval); ScreenMergesApply(MergeScreenInterval * dt, ph); ScreenMergesCollect(ph); Sec("merges", _s0); }
                { long _s0 = Sw(); if (_tick % 60 == 0) CheckInvariants(); Sec("invariants", _s0); }   // (the frames' rules, judged once a second: Invariants)
            }
            // The generator's work the locked step planned (rock volumes out and in, its sectors rebuilt, its bodies counted):
            // the engine's, and slow at times - out here it no longer holds the client's HUD, map and player tick on FramesLock.
            { long _s0 = Sw(); AsteroidFrames.ServerAfterLock(session); Sec("asteroids", _s0); }
            // (the aero calls the locked step collected: the moves it told of, a frame's ballistic coefficient, a border's air -
            //  never made under FramesLock: API.md's lock rule)
            FlushAeroNoteMoved();
            { long _s0 = Sw(); EntryHost.ServerAfterLock(); Sec("entry-aero", _s0); }
            { long _s0 = Sw(); ScreenMergesCompute(); Sec("merges-unlocked", _s0); }   // (the screen's slow part, off the lock)
        }

        { long _s0 = Sw(); if (_tick % 30 == 0) { BuildSnapshot(); AdoptLooseGrids(); } Sec("snapshot", _s0); }
        { long _s0 = Sw(); if (_tick % 5 == 0) BuildViews();
        else RefreshGridPositions(); Sec("views", _s0); }   // (every tick: a seated player's grid is matched against them - PlayerOrbit)
        DevStress.Tick();
        if (DevFreeGrids)
        {
            DevFreeGrids = false;
            int n = 0;
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer || !GridMembers.IsDynamic(g) || EncounterFrames.IsNpc(g)) continue;
                bool held = GridMembers.IsConstrained(g);
                Log.Default?.Info($"[ORBIT-DEV] grid {g.Id} '{g.DisplayName}' dynamic m={GridMembers.Mass(g):F0} kg constrained={held} at {ServerPlanetBeacon.Fmt(GridMembers.Position(g))} {CellR(GridMembers.Position(g))}");
                if (!held) n++;
            }
            Log.Default?.Info($"[ORBIT-DEV] free grids: {n}");
        }
    }

    // ───────────────────────────── attach (grids join a stowing player) ─────────────────────────────

    private static void DoAttach(AttachRequest req)
    {
        var frame = SystemHost.Frames.Get(req.FrameId);
        if (frame == null) return;
        int n = 0;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || !GridMembers.IsDynamic(g)) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D p = GridMembers.Position(g);
            if ((p - req.RefPos).Length() > AttachRadius) continue;
            if (EncounterFrames.IsNpc(g)) continue;   // NPCs keep their own world
            // only what flies with you: a ship landed on the ground below (a small moon's stow altitude is a few km) was taken
            // into orbit with a stowing walker. A grid on its conic (HighSpeed) has no physical velocity: not judged by it.
            if (!_gridHighSpeed.ContainsKey(g.Id) && (GridMembers.Velocity(g) - req.RefVel).Length() > AttachRelSpeed) continue;
            if (!SystemHost.Frames.AddMember(frame, g.Id)) continue;
            // A grid riding its conic (HighSpeed) has zero physical velocity: its true velocity is the
            // conic's. Leave HighSpeed first, or StepGridHighSpeed keeps dragging it off the berth.
            Chart chart = req.Body != null ? Chart.Of(req.Body, req.Time) : default;
            Vector3D cellC = default;
            bool inCell = req.Body != null && VoxelBerthRegistry.TryGetCell(req.Body, SystemHost.Registry, out cellC);
            Vector3D v = inCell ? chart.VelToInertial(p - cellC, GridMembers.Velocity(g)) : GridMembers.Velocity(g);
            if (_gridHighSpeed.TryGetValue(g.Id, out var hs))
            {
                var st = OrbitPropagation.StateAt(hs.el, SystemHost.Now);
                if (IsFinite(st.Velocity)) v = st.Velocity;
                _gridHighSpeed.Remove(g.Id);
            }
            // Same relative placement in the berth; velocity relative to the frame (the player's own
            // velocity went into the rails).
            // (MoveGrid: a jointed grid comes with its whole joined group, or not at all if it is locked to a base)
            if (!MoveGrid(g, req.Berth + (inCell ? chart.ToInertial(p - req.RefPos) : p - req.RefPos), v - req.RefVel, out var withA, default, inCell ? chart.ToInertialRotation() : (Quaternion?)null))
            { SystemHost.Frames.RemoveMember(g.Id); continue; }
            foreach (var o in withA) if (SystemHost.Frames.FindByMember(o.Id) == null) SystemHost.Frames.AddMember(frame, o.Id);
            n++;
        }
        if (n > 0) Event($"ATTACH {n} grid(s) within {AttachRadius / 1000:F0} km -> frame #{frame.Id}");
    }

    /// <summary>Free-flying grids in a spinning planet's cell (the rotating chart): Coriolis + centrifugal.</summary>
    private static void ApplyFictitious(double dt)
    {
        var reg = SystemHost.Registry;
        double t = SystemHost.Now;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || _gridHighSpeed.ContainsKey(g.Id) || !GridMembers.IsDynamic(g)) continue;
            // (a framed grid first - one lookup - before the cell search: both only skip, so their order is free; most grids
            //  are framed and were cell-searched first, every tick, 2026-10-15)
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) continue;
            if (EncounterFrames.IsNpc(g)) continue;   // NPCs: no relative-motion terms
            Chart c = Chart.Of(body, t);
            if (!c.Spin) continue;
            Vector3D r = pos - cell, v = GridMembers.Velocity(g);
            double alt = r.Length() - (reg.FindDefinition(body)?.RadiusMeters ?? 0);
            if (v.Length() < OrbitalConfig.FictitiousMinSpeed && alt < OrbitalConfig.FictitiousMinAltitude) continue;
            Vector3D dv = c.Fictitious(r, v) * dt;
            if (IsFinite(dv)) GridMembers.AddVelocity(g, dv);
        }
    }

    // ───────────────────────────── stow (grids without a player) ─────────────────────────────

    /// <summary>
    /// Total partition for grids: a grid in a planet cell that is beyond the keep, or above the shell
    /// on an arc that escapes it, belongs on the rails. Grids near the player are left to the player's
    /// own stow, which takes them along (one frame, not two). Grids within the attach radius of the
    /// stowing grid join its frame.
    /// </summary>
    static readonly List<Entity> _stowMoveSet = new List<Entity>();   // (StowLoneGrids': server thread, under FramesLock)
    private static void StowLoneGrids()
    {
        var reg = SystemHost.Registry;
        double t = SystemHost.Now;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || !GridMembers.IsDynamic(g)) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            // Grids near a player on foot go with the player's own stow. A seated player's stow does
            // not run: the ship they fly stows here (and they with it, as its child).
            if (!FrameHost.Seated && (pos - PlayerPosition).Length() <= AttachRadius) continue;
            if (EncounterFrames.IsNpc(g)) continue;
            lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;   // (going with its group this tick)
            if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
            {
                if (!OrbitalConfig.CaptureLegacySpace || !SystemHost.TryNearestCell(pos, out body, out cell)) continue;
            }
            GravityBody node = reg.Find(body);
            BodyDefinition def = reg.FindDefinition(body);
            if (node == null || def == null) continue;

            Vector3D vel = GridMembers.Velocity(g);
            if (_gridHighSpeed.TryGetValue(g.Id, out var hs))
            {
                var st = OrbitPropagation.StateAt(hs.el, t);
                if (IsFinite(st.Velocity)) vel = st.Velocity;
            }
            bool legacy = !VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out _);
            Chart chart = legacy ? default : Chart.Of(body, t);
            Vector3D relChart = pos - cell;
            Vector3D rel = chart.ToInertial(relChart);
            if (!_gridHighSpeed.ContainsKey(g.Id)) vel = chart.VelToInertial(relChart, vel);
            double d = rel.Length();
            double shell = PlanetBerths.ShellRadius(def);
            double keep = PlanetBerths.KeepRadius(def);
            if (d < shell * PlanetBerths.StowShellMargin) continue;
            // locked to a base: stays. (Asked here, last of the filters - every one above only skips and reads nothing it could
            //  change, so the order is free: a grid inside the shell - a landed ship, a base - ran this physics query and joint
            //  search ~6 times a second, with a new list each, 2026-10-15)
            if (GridMembers.IsConstrained(g) && !GridMembers.MoveSet(g, _stowMoveSet, out _)) { _stowMoveSet.Clear(); continue; }
            _stowMoveSet.Clear();
            // (STOWED AS SOON AS IT IS OUT: past the conversion radius R_c by the stow floor's 5 % (StowShellMargin - the hysteresis:
            //  back down, the release is at R_c), whatever its orbit. Inside the keep it also had to be LEAVING (FrameRails.EscapesShell:
            //  apoapsis above 1.5 R_c, or hyperbolic): a spaceplane in a low orbit - apoapsis under ~46 km at Verdure - never left the
            //  planet frame, silently (the user, 2026-10-10: "need to stow as soon as you pass the nominal boundary, with hysteresis"))

            StateVector borg = node.OriginInRoot(t);
            Vector3D cel = borg.Position + rel;
            GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
            StateVector porg = parent.OriginInRoot(t);
            var el = CaptureMath.CaptureElements(new StateVector(cel - porg.Position, borg.Velocity + vel - porg.Velocity), parent.Mu, t);
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) continue;
            var frame = SystemHost.Frames.CreateFrame(parent.Name, el, g.Id);
            if (frame == null) continue;
            _gridHighSpeed.Remove(g.Id);
            // (turned out of the planet's rotating chart, as attached grids and arrivals are: unturned, the ship a seated player
            //  flies left every spinning planet facing off by the chart's angle - planet and sky jumped)
            if (!MoveGrid(g, frame.BerthCenter, Vector3D.Zero, out var withS, default, legacy ? (Quaternion?)null : chart.ToInertialRotation())) { SystemHost.Frames.Dissolve(frame.Id); continue; }
            foreach (var o in withS) { _gridHighSpeed.Remove(o.Id); if (SystemHost.Frames.FindByMember(o.Id) == null) SystemHost.Frames.AddMember(frame, o.Id); }
            Event($"STOW grid {g.Id} '{g.DisplayName}' -> frame #{frame.Id} orbiting {parent.Name}: r={(cel - porg.Position).Length() / 1000:F1} km " +
                  $"|v|={vel.Length():F0} m/s a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F3} slot={frame.BerthSlotId}");
            DoAttach(new AttachRequest { FrameId = frame.Id, RefPos = pos, RefVel = vel, Berth = frame.BerthCenter, Body = legacy ? null : body, Time = t });
        }
    }

    private static void DoGridOrbit(GridOrbitRequest r)
    {
        var g = GridMembers.Get(r.GridId);
        if (g == null || !g.IsServer || !GridMembers.IsDynamic(g)) { Event($"gridorbit: grid {r.GridId} not found / not dynamic"); return; }
        var old = SystemHost.Frames.FindByMember(g.Id);
        if (old != null) SystemHost.Frames.RemoveMember(g.Id);
        var f = SystemHost.Frames.CreateFrame(r.Body, r.El, g.Id);
        if (f == null) { Event("gridorbit: no free berth"); return; }
        _gridHighSpeed.Remove(g.Id);
        if (!MoveGrid(g, f.BerthCenter, Vector3D.Zero, out var withG)) { SystemHost.Frames.Dissolve(f.Id); return; }
        // Joined to others (sub-grids on hinges, docked): they go with it, into its new frame (they were moved, never
        // framed - left in no frame, or in the old one; the reentry test saw the Hercules arrive with 1 member of 7)
        foreach (var o in withG)
        {
            if (o.Id == g.Id) continue;
            if (SystemHost.Frames.FindByMember(o.Id) != null) SystemHost.Frames.RemoveMember(o.Id);
            SystemHost.Frames.AddMember(f, o.Id);
        }
        Event($"gridorbit: grid {g.Id} '{g.DisplayName}' (+{withG.Count(o => o.Id != g.Id)} joined) -> frame #{f.Id} about {r.Body} a={r.El.SemiMajorAxis / 1000:F1} km e={r.El.Eccentricity:F3} slot={f.BerthSlotId}");
    }

    // ───────────────────────────── merge (SE1 ScreenMerges / ExecuteMerge) ─────────────────────────────

    public const int MergeScreenInterval = 30;       // server ticks between screens (SE1 SeamCheckInterval)
    public const double TcaHorizonSeconds = 1800.0;  // SE1: 30 min look-ahead
    public const int TcaSamples = 48;
    public static readonly RendezvousParams Rendezvous = RendezvousParams.Default;
    private static readonly Dictionary<(long, long), RendezvousTracker> _trackers = new Dictionary<(long, long), RendezvousTracker>();

    /// <summary>
    /// Merge is a rendezvous (SE1): per same-SOI pair, the closest approach over the next 30 min
    /// decides whether to feed the sticky tracker (miss below 10 km and rel speed below 1000 m/s) or
    /// starve it (fly-by). A latched pair merges on the first screen where it is ALSO close now
    /// (inside the enter range) and slow now (below the physics cap), so an incomer never coasts out of the berth.
    /// </summary>
    // THE MERGE SCREEN, its slow part off FramesLock (2026-10-13): each pair's closest approach (ClosestApproach.Find, 48
    // samples over 30 min) ran inside the lock - 50-70 ms every half second with 73 frames, and the client's frame waits on
    // that lock while seated (FrameHost): the whole screen froze for it. Now, under the lock, this tick's share of the pairs
    // is listed (their elements copied) and last tick's answers applied; the approaches are found between the two, unlocked.
    // Each pair is still screened once per MergeScreenInterval ticks, in its own tick of the round (by its ids).
    struct ScreenJob { public long A, B; public KeplerianElements EA, EB; public double T; public ApproachEvent Ev; }
    static readonly List<ProximityFrame> _screenFrames = new List<ProximityFrame>();
    static readonly List<(long, long)> _screenDead = new List<(long, long)>();
    // (handed between the three as whole lists: Collect queues its list, Compute drains the queue and hands its results back
    //  under FramesLock, Apply drains them under it - an overlapping second tick never shares a list, nor loses one)
    static readonly System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>> _screenJobs = new System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>>();
    static readonly List<ScreenJob> _screenDone = new List<ScreenJob>();
    static readonly System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>> _screenLists = new System.Collections.Concurrent.ConcurrentQueue<List<ScreenJob>>();   // (a pool; ConcurrentBag is not whitelisted)

    /// <summary>Under FramesLock: this tick's pairs to screen, their elements copied (cheap filters only).</summary>
    // (each frame's own facts - finite elements, radial band, epoch passed, a site, anyone non-NPC in it - found once per
    //  listing, not once per pair: HasNonNpc walked a frame's members for every pair it was in, 2026-10-15. Under FramesLock:
    //  no frame changes during the listing. The filters are the same ones, cheapest first - all are pure, their order is free)
    static readonly List<byte> _screenFacts = new List<byte>();
    static readonly List<double> _screenPe = new List<double>(), _screenAp = new List<double>();
    const byte FactFinite = 1, FactBand = 2, FactMutable = 4, FactSite = 8, FactNonNpcKnown = 16, FactNonNpc = 32;
    static bool ScreenNonNpc(int i, ProximityFrame f)
    {
        byte x = _screenFacts[i];
        if ((x & FactNonNpcKnown) == 0) { x |= FactNonNpcKnown; if (EncounterFrames.HasNonNpc(f)) x |= FactNonNpc; _screenFacts[i] = x; }
        return (x & FactNonNpc) != 0;
    }
    private static void ScreenMergesCollect(int phase)
    {
        List<ScreenJob> jobs = null;
        double t = SystemHost.Now;
        var frames = _screenFrames; frames.Clear(); frames.AddRange(SystemHost.Frames.Frames);   // (reused: this runs every tick)
        double er = Rendezvous.EnterRangeMeters;
        var facts = _screenFacts; facts.Clear(); _screenPe.Clear(); _screenAp.Clear();
        bool screenAll = !EncounterFrames.Enabled;   // (EncounterFrames.ShouldScreen: encounters off, every pair is screened)
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            byte x = 0;
            if (Finite(f.Elements)) x |= FactFinite;
            if (RadialBand(f.Elements, out double pe, out double ap)) x |= FactBand;
            if (FrameRails.CanMutateAt(f.Elements, t)) x |= FactMutable;
            if (!screenAll && EncounterFrames.IsSite(f)) x |= FactSite;
            facts.Add(x); _screenPe.Add(pe); _screenAp.Add(ap);
        }
        for (int a = 0; a < frames.Count; a++)
        {
            var fa = frames[a];
            byte xa = facts[a];
            if ((xa & FactFinite) == 0) continue;
            bool bandA = (xa & FactBand) != 0;
            double peA = _screenPe[a], apA = _screenAp[a];
            for (int b = a + 1; b < frames.Count; b++)
            {
                var fb = frames[b];
                byte xb = facts[b];
                if ((xb & FactFinite) == 0) continue;
                if (fa.ParentBodyName != fb.ParentBodyName) continue;
                if ((int)(((ulong)(fa.Id < fb.Id ? fa.Id : fb.Id) * 7919UL + (ulong)(fa.Id < fb.Id ? fb.Id : fa.Id)) % (ulong)MergeScreenInterval) != phase) continue;
                if (bandA && (xb & FactBand) != 0 && (apA + er < _screenPe[b] || _screenAp[b] + er < peA)) continue;
                if ((xa & FactMutable) == 0 || (xb & FactMutable) == 0) continue;
                // (EncounterFrames.ShouldScreen, on the facts: two sites never; else anyone non-NPC in either)
                if (!screenAll && (((xa & xb & FactSite) != 0) || !(ScreenNonNpc(a, fa) || ScreenNonNpc(b, fb)))) continue;
                (jobs ??= (_screenLists.TryDequeue(out var pooled) ? pooled : new List<ScreenJob>())).Add(new ScreenJob { A = Math.Min(fa.Id, fb.Id), B = Math.Max(fa.Id, fb.Id),
                    EA = fa.Id < fb.Id ? fa.Elements : fb.Elements, EB = fa.Id < fb.Id ? fb.Elements : fa.Elements, T = t });
            }
        }
        if (jobs != null) _screenJobs.Enqueue(jobs);
    }

    /// <summary>OFF FramesLock: the listed pairs' closest approaches (the slow part), into the done list.</summary>
    private static void ScreenMergesCompute()
    {
        while (_screenJobs.TryDequeue(out var jobs))
        {
            (_screenNowT ??= new Dictionary<long, StateVector>()).Clear();   // (one listing: one time, one element set per frame)
            (_screenKeptT ??= new Dictionary<long, (KeplerianElements, StateVector)>()).Clear();   // (one time: the kept copies' states at it)
            for (int i = 0; i < jobs.Count; i++)
            {
                var d = jobs[i];
                d.Ev = Approach(d);
                jobs[i] = d;
            }
            lock (FramesLock) _screenDone.AddRange(jobs);
            jobs.Clear(); _screenLists.Enqueue(jobs);   // (back to the pool: a new list per screen tick before)
        }
    }

    // A PAIR'S APPROACH, KEPT (a profile, 2026-10-14: the screen's approaches were a tenth of a core - every pair's 30 min
    // look-ahead found anew every half second, two coasting orbits giving the same answer each time). Reused while both
    // orbits are the same ones (SameOrbit: the shape and where along it - a re-epoched copy is the same orbit), the answer
    // a clear fly-by (no approach, or a miss over ReuseMissRanges enter ranges), its approach still ahead, and no older
    // than ReuseSeconds. A pair that may merge, or whose orbit changed (someone thrusting), is found anew every screen.
    // (SameOrbit: two Kepler solves an orbit, against the screen's ~100)
    public static double ScreenReuseSeconds = 30.0, ScreenReuseMissRanges = 4.0;
    static readonly Dictionary<(long, long), ScreenJob> _screenKept = new Dictionary<(long, long), ScreenJob>();
    static double _screenKeptSwept;
    public static int ScreenFound, ScreenReused, ScreenNew, ScreenOld, ScreenClose, ScreenPassed, ScreenMoved, ScreenApart;
    public static string ScreenStats => $"found {ScreenFound} reused {ScreenReused} | new {ScreenNew} old {ScreenOld} close {ScreenClose} passed {ScreenPassed} moved {ScreenMoved} | proven apart {ScreenApart}";
    /// <summary>The most steps (two state solves each, against Find's ~1500) the fly-by proof may take before Find runs.</summary>
    public static int ScreenApartSteps = 48;

    static ApproachEvent Approach(in ScreenJob d)
    {
        lock (_screenKept)
        {
            if (!_screenKept.TryGetValue((d.A, d.B), out var k)) ScreenNew++;
            else if (!(d.T - k.T < ScreenReuseSeconds && d.T >= k.T)) ScreenOld++;
            else if (k.Ev.Found && !(k.Ev.MissDistance > ScreenReuseMissRanges * Rendezvous.EnterRangeMeters)) ScreenClose++;
            // (an approach at the window's very start is a pair moving apart - its closest is now: kept as long as its orbits
            //  are, its minimum can only change by the horizon's end, ScreenReuseSeconds later, half an hour ahead)
            else if (k.Ev.Found && !(k.Ev.Time > d.T) && !(k.Ev.Time <= k.T + 1.0)) ScreenPassed++;
            else if (!SameOrbit(k.EA, d.EA, d.T, d.A) || !SameOrbit(k.EB, d.EB, d.T, d.B)) ScreenMoved++;
            else { ScreenReused++; return k.Ev; }
        }
        // A PROVEN FLY-BY (2026-10-15): when the two cannot come within the enter range anywhere in the window - the separation
        // changes no faster than their two top speeds (ClosestApproach.CertainlyApart, tested offline against a dense search and
        // Find) - Find could only answer a miss of at least that range, which ScreenMergesApply treats exactly as no approach:
        // its tracker starved, no merge-ahead noted. So it answers "none" without the search. Not kept: the next screen asks
        // anew (the proof, or Find - never an answer older than this screen).
        if (ClosestApproach.CertainlyApart(d.EA, d.EB, d.T, TcaHorizonSeconds, Rendezvous.EnterRangeMeters, ScreenApartSteps))
        {
            System.Threading.Interlocked.Increment(ref ScreenApart);
            return ApproachEvent.None;
        }
        var ev = ClosestApproach.Find(d.EA, d.EB, d.T, TcaHorizonSeconds, TcaSamples);
        ScreenFound++;
        lock (_screenKept)
        {
            var kept = d; kept.Ev = ev; _screenKept[(d.A, d.B)] = kept;
            if (d.T - _screenKeptSwept > 120.0)
            {
                _screenKeptSwept = d.T;
                var old = new List<(long, long)>();
                foreach (var kv in _screenKept) if (d.T - kv.Value.T > 120.0) old.Add(kv.Key);
                foreach (var key in old) _screenKept.Remove(key);
            }
        }
        return ev;
    }

    /// <summary>The same orbit as far as the screen can tell: where the two put the frame now, and how fast, agree to
    /// SameOrbitMeters and SameOrbitMps. (Elements themselves differ from tick to tick for a frame re-osculated every tick
    /// - a Lagrange ride, a site - so they were compared by the state they give, at the time of the screen.)</summary>
    static bool SameOrbit(in KeplerianElements x, in KeplerianElements y, double t, long yId)
    {
        if (x.Mu != y.Mu) return false;
        var sx = KeptNow(yId, x, t); var sy = ListedNow(yId, y, t);
        if (!IsFinite(sx.Position) || !IsFinite(sy.Position) || !IsFinite(sx.Velocity) || !IsFinite(sy.Velocity)) return false;
        return (sx.Position - sy.Position).LengthSquared() < SameOrbitMeters * SameOrbitMeters
            && (sx.Velocity - sy.Velocity).LengthSquared() < SameOrbitMps * SameOrbitMps;
    }
    public static double SameOrbitMeters = 20.0, SameOrbitMps = 0.02;

    // A FRAME'S LISTED STATE, ONCE PER SCREEN (2026-10-15: SameOrbit solved each pair's current orbits anew - a frame in 70 pairs
    // was solved 70 times at the same time on the same elements). One listing (ScreenMergesCollect) copies each frame's elements
    // once, at one time: every job of a list has the same T and the same elements for a frame id, so its state at T is kept by
    // id for that list. Per thread: two overlapping ticks may compute at once.
    [ThreadStatic] static Dictionary<long, StateVector> _screenNowT;
    // (and the KEPT elements' state at the listing's time, by frame id: pairs kept at the same screen hold the same copy of a
    //  frame's elements - SameOrbit solved it once per pair. Reused only for bit-identical elements: StateAt is a pure function
    //  of the elements and the time, and every job of a list has the same time)
    [ThreadStatic] static Dictionary<long, (KeplerianElements el, StateVector s)> _screenKeptT;
    static bool SameBits(in KeplerianElements a, in KeplerianElements b) =>
        BitConverter.DoubleToInt64Bits(a.SemiMajorAxis) == BitConverter.DoubleToInt64Bits(b.SemiMajorAxis)
        && BitConverter.DoubleToInt64Bits(a.Eccentricity) == BitConverter.DoubleToInt64Bits(b.Eccentricity)
        && BitConverter.DoubleToInt64Bits(a.Inclination) == BitConverter.DoubleToInt64Bits(b.Inclination)
        && BitConverter.DoubleToInt64Bits(a.Raan) == BitConverter.DoubleToInt64Bits(b.Raan)
        && BitConverter.DoubleToInt64Bits(a.ArgPeriapsis) == BitConverter.DoubleToInt64Bits(b.ArgPeriapsis)
        && BitConverter.DoubleToInt64Bits(a.TrueAnomaly) == BitConverter.DoubleToInt64Bits(b.TrueAnomaly)
        && BitConverter.DoubleToInt64Bits(a.Mu) == BitConverter.DoubleToInt64Bits(b.Mu)
        && BitConverter.DoubleToInt64Bits(a.Epoch) == BitConverter.DoubleToInt64Bits(b.Epoch);
    static StateVector KeptNow(long id, in KeplerianElements el, double t)
    {
        var c = _screenKeptT;
        if (c == null || _screenNowT == null) return OrbitPropagation.StateAt(el, t);
        if (c.TryGetValue(id, out var k) && SameBits(k.el, el)) return k.s;
        var s = OrbitPropagation.StateAt(el, t);
        c[id] = (el, s);
        return s;
    }
    static StateVector ListedNow(long id, in KeplerianElements el, double t)
    {
        var c = _screenNowT;
        if (c == null) return OrbitPropagation.StateAt(el, t);
        if (c.TryGetValue(id, out var s)) return s;
        s = OrbitPropagation.StateAt(el, t);
        c[id] = s;
        return s;
    }

    /// <summary>Under FramesLock: last tick's approaches judged - the rendezvous trackers, the merges (on the frames as they
    /// are NOW: a pair gone or changed since is judged on its current state, ExecuteMerge's own checks unchanged).</summary>
    private static void ScreenMergesApply(double dt, int phase)
    {
        double t = SystemHost.Now;
        double er = Rendezvous.EnterRangeMeters;
        foreach (var j in _screenDone)
        {
            var fa = SystemHost.Frames.Get(j.A); var fb = SystemHost.Frames.Get(j.B);
            if (fa == null || fb == null) continue;   // (merged or dissolved since)
            // (the screen's own filters again, on the frames as they are now: a reparent or a new epoch since the listing)
            if (fa.ParentBodyName != fb.ParentBodyName || !EncounterFrames.ShouldScreen(fa, fb)
                || !FrameRails.CanMutateAt(fa.Elements, t) || !FrameRails.CanMutateAt(fb.Elements, t)) continue;
            var ev = j.Ev;
            bool ok = ev.Found && IsFinite(ev.MissDistance) && IsFinite(ev.RelativeSpeed);
            double miss = ok ? ev.MissDistance : double.PositiveInfinity;
            double rel = ok ? ev.RelativeSpeed : double.PositiveInfinity;
            var key = (j.A, j.B);
            if (!_trackers.TryGetValue(key, out var tr)) { tr = new RendezvousTracker(); _trackers[key] = tr; }
            if (miss < er && rel < Rendezvous.EnterRelSpeedMps && ev.Time > t && FrameHost.PlayerFrame != null
                && (FrameHost.PlayerFrame.Id == fa.Id || FrameHost.PlayerFrame.Id == fb.Id))
                NoteMergeAhead(ev.Time);
            bool merged = miss < er && rel < Rendezvous.EnterRelSpeedMps
                ? tr.Update(miss, rel, dt, Rendezvous)
                : tr.Update(double.PositiveInfinity, double.PositiveInfinity, dt, Rendezvous);
            if (!merged) continue;
            if (!Finite(fa.Elements) || !Finite(fb.Elements)) continue;
            var sa = OrbitPropagation.StateAt(fa.Elements, t);
            var sb = OrbitPropagation.StateAt(fb.Elements, t);
            if (!IsFinite(sa.Position) || !IsFinite(sb.Position) || !IsFinite(sa.Velocity) || !IsFinite(sb.Velocity)) continue;
            if ((sb.Position - sa.Position).Length() >= er) continue;
            if ((sb.Velocity - sa.Velocity).Length() >= FrameHost.SpeedCap) continue;
            ExecuteMerge(fa, fb, sa, sb);
            _trackers.Remove(key);
        }
        _screenDone.Clear();
        if (phase != 0) return;   // (the dead pairs swept once a round)
        var dead = _screenDead; dead.Clear();
        foreach (var k in _trackers.Keys) if (SystemHost.Frames.Get(k.Item1) == null || SystemHost.Frames.Get(k.Item2) == null) dead.Add(k);
        foreach (var k in dead) _trackers.Remove(k);
    }

    static readonly Dictionary<long, double> _mergeRefused = new Dictionary<long, double>();
    static readonly HashSet<long> _rebasePending = new HashSet<long>();
    static readonly Dictionary<long, double> _lastSteerNow = new Dictionary<long, double>();
    static double _steerStep;

    /// <summary>A frame's orbit stays clear of its planet's border (periapsis over the frame border with 5 km to spare, and
    /// bound - an escape trajectory is no place for a station either). The star: always stable.</summary>
    internal static bool OrbitStable(ProximityFrame f)
    {
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        var node = SystemHost.Registry?.Find(f.ParentBodyName);
        if (def == null || node == null || node.IsRoot || string.IsNullOrEmpty(def.ParkSubtype)) return true;
        // (the rule itself is game-free: MembershipRules.OrbitStable, tested offline)
        return SEAerospace.Frames.MembershipRules.OrbitStable(f.Elements, PlanetBerths.ShellRadius(def));
    }
    private static void ExecuteMerge(ProximityFrame fa, ProximityFrame fb, StateVector sa, StateVector sb)
    {
        ProximityFrame host, inc; StateVector sHost, sInc;
        int pa = EncounterFrames.HostPriority(fa), pb = EncounterFrames.HostPriority(fb);
        if (pa > pb || (pa == pb && FrameMass(fa) >= FrameMass(fb))) { host = fa; inc = fb; sHost = sa; sInc = sb; }
        else { host = fb; inc = fa; sHost = sb; sInc = sa; }

        // Anchors sit at their berths (pinned), so the incomer moves by berth-to-berth plus the
        // celestial separation, and gains the celestial relative velocity. Members keep their own
        // offsets and velocities relative to their old anchor.
        Vector3D dPos = sInc.Position - sHost.Position;
        Vector3D dVel = sInc.Velocity - sHost.Velocity;
        Vector3D translation = host.BerthCenter + dPos - inc.BerthCenter;
        if (!IsFinite(translation) || !IsFinite(dVel)) return;
        // (in warp a member's physical velocity is N x its true one - the gridN rescale: the true relative velocity added
        //  unscaled was cut by N at the merge, a free braking)
        Vector3D dVelPhys = dVel * Math.Max(1.0, SystemHost.Timescale);
        // Called off, before anything moves, when it could not be done whole: a member (or you) would land past the host's
        // edge (split straight back out), the incomer holds a static grid (it cannot move), or a ship of it cannot be moved
        // (joined to something static). A merge half done stranded ships at a freed berth.
        string why = null;
        Vector3D hostAt = StaticAnchorOf(host, out Vector3D hsa) ? hsa : host.BerthCenter;
        foreach (long id in inc.Members)
        {
            if (!GridMembers.IsGridId(id))
            {
                if (id == FrameHost.PlayerId && ServerPlanetBeacon.PlayerState(out Vector3D pp, out _) && (pp + translation - hostAt).Length() > SlotRadius - 500) { why = "you would land past the host's edge"; break; }
                continue;
            }
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer) continue;
            if (!GridMembers.IsDynamic(g)) { why = $"it holds a static grid ({g.DisplayName})"; break; }
            if ((GridMembers.Position(g) + translation - hostAt).Length() > SlotRadius - 500) { why = $"'{g.DisplayName}' would land past the host's edge"; break; }
            if (GridMembers.IsConstrained(g) && !GridMembers.MoveSet(g, new List<Keen.VRage.DCS.Components.Entity>(), out string mw)) { why = $"'{g.DisplayName}' cannot be moved ({mw})"; break; }
        }
        if (why != null)
        {
            long key = Math.Min(host.Id, inc.Id) * 100000 + Math.Max(host.Id, inc.Id);
            if (!_mergeRefused.TryGetValue(key, out double at) || Wall() - at > 60) { _mergeRefused[key] = Wall(); Event($"MERGE frame #{inc.Id} -> #{host.Id} called off: {why}"); }
            return;
        }
        int n = 0;
        var went = new HashSet<long>();   // (moved with an earlier member's joined set: not moved twice)
        foreach (long id in new List<long>(inc.Members))
        {
            if (!GridMembers.IsGridId(id) || went.Contains(id)) continue;
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer) continue;
            // Its true relative velocity in the host, kept for the warp steering (as every member's in warp): in physics it is
            // N x that, and the game caps physical speed near 1000 m/s - a merge at x1000 with 8.5 m/s between the frames
            // asked 8500 m/s and got ~980, a ship cut to a ninth of its speed, 2.7 km off its orbit after.
            double nm = Math.Max(1.0, SystemHost.Timescale);
            Vector3D trueOld = _trueRel.TryGetValue(g.Id, out var kt) ? kt : GridMembers.Velocity(g) / nm;
            MoveGrid(g, GridMembers.Position(g) + translation, null, out var withM, dVelPhys);
            foreach (var w in withM) went.Add(w.Id);
            // (its velocity is in this warp's scale now: recorded so, for the rescale when the warp changes)
            _gridN[g.Id] = nm; _settle.Remove(g.Id); _vSet.Remove(g.Id);
            if (nm > 1.0) _trueRel[g.Id] = trueOld + dVel; else _trueRel.Remove(g.Id);
            foreach (var w in withM) { _gridN[w.Id] = nm; _trueRel.Remove(w.Id); _settle.Remove(w.Id); }
            n++;
        }
        FrameHost.RequestMerge(inc.Id, translation, dVelPhys);   // the player, if a member of the incomer
        long hostId = host.Id, incId = inc.Id;
        SystemHost.Frames.MergeInto(host, inc);
        AnchorAccel.Remove(incId);
        Event($"MERGE frame #{incId} -> #{hostId}: {n} grid(s) moved, sep {dPos.Length() / 1000:F2} km, |dv| {dVel.Length():F1} m/s (host now {host.Members.Count} members)");
    }

    /// <summary>When the player's frame next meets another closely enough to merge (NaN: none seen): warp stops a minute
    /// before it (SystemHost.AdvanceClock), so the pass is screened at x1 - in warp the screens are hundreds of game seconds
    /// apart and a merge went by between two of them.</summary>
    public static double MergeAhead => System.Threading.Volatile.Read(ref _mergeAhead);
    private static double _mergeAhead = double.NaN;
    private static void NoteMergeAhead(double tca)
    {
        double cur = System.Threading.Volatile.Read(ref _mergeAhead);
        if (double.IsNaN(cur) || cur < SystemHost.Now || tca < cur) System.Threading.Volatile.Write(ref _mergeAhead, tca);
    }
    public const double MergeWarpLead = 60.0;

    private static double FrameMass(ProximityFrame f)
    {
        double m = 0;
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) { m += 100; continue; }   // a character
            var g = GridMembers.Get(id);
            if (g != null) m += GridMembers.Mass(g);
        }
        return m;
    }

    private static bool RadialBand(KeplerianElements el, out double rPe, out double rAp)
    {
        rPe = rAp = 0;
        double a = el.SemiMajorAxis, e = el.Eccentricity;
        if (!IsFinite(a) || a <= 0 || !IsFinite(e) || e < 0 || e >= 1) return false;
        rPe = a * (1 - e); rAp = a * (1 + e);
        return true;
    }

    private static bool Finite(KeplerianElements el) => IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion);

    // ───────────────────────────── per-frame update (grids) ─────────────────────────────

    private static readonly List<ProximityFrame> _tickFrames = new List<ProximityFrame>();
    private static bool HasGrid(List<OrbitalGridComponent> grids, long id) { foreach (var g in grids) if (g.Id == id) return true; return false; }

    static readonly Dictionary<long, double> _unframedSince = new Dictionary<long, double>();
    static readonly HashSet<long> _everFramed = new HashSet<long>();
    // (CheckInvariants' own, kept: a dictionary, a set and lists per check before - 26 KB/s, 2026-10-15. Server thread, under
    //  FramesLock)
    static readonly Dictionary<Keen.VRage.DCS.Components.Entity, OrbitalGridComponent> _invByEntity = new Dictionary<Keen.VRage.DCS.Components.Entity, OrbitalGridComponent>();
    static readonly HashSet<long> _invSeen = new HashSet<long>();
    static readonly List<long> _invGone = new List<long>();
    /// <summary>The server's invariants (Invariants), once a second, under FramesLock, after the frame step.</summary>
    internal static void CheckInvariants()
    {
        if (!Invariants.Enabled || SystemHost.Frames == null || SystemHost.Registry == null) return;
        try
        {
            double now = Wall(), N = Math.Max(1.0, SystemHost.Timescale);
            var byEntity = _invByEntity; byEntity.Clear();
            var all = GridMembers.All();
            foreach (var g in all) if (g.IsServer && g.Entity != null) byEntity[g.Entity] = g;
            foreach (var f in SystemHost.Frames.Frames) foreach (long m in f.Members) if (GridMembers.IsGridId(m)) _everFramed.Add(m);
            foreach (var f in SystemHost.Frames.Frames)
            {
                // anchor-alive
                if (GridMembers.IsGridId(f.AnchorEntityId) && GridMembers.Get(f.AnchorEntityId) == null && !GridMembers.Tombstoned(f.AnchorEntityId))
                    Invariants.Report("anchor-alive", "f" + f.Id, $"frame #{f.Id}: its anchor {f.AnchorEntityId} is no live grid ({f.Members.Count} member(s))");
                bool isStatic = StaticAnchorOf(f, out Vector3D sAt);
                double leave = LeaveRadius(isStatic) + 5000;
                foreach (long m in f.Members)
                {
                    if (!GridMembers.IsGridId(m) || !(GridMembers.Get(m) is OrbitalGridComponent g) || !g.IsServer || g.Entity == null) continue;
                    if (f.IsEncounter || EncounterFrames.IsNpc(g) || !GridMembers.IsDynamic(g)) continue;
                    Vector3D p = GridMembers.Position(g);
                    // past-edge (beyond the leave radius with 5 km to spare: a split is due within a tick or two)
                    double away = isStatic ? (p - sAt).Length() : (p - f.BerthCenter).Length();
                    if (away > leave && away < 60000) Invariants.Report("past-edge", "g" + m, $"grid {m} '{g.DisplayName}' {away / 1000:F1} km from frame #{f.Id}'s {(isStatic ? "static anchor" : "centre")}, unsplit");
                    // ship-together
                    // (the joint search kept for a second per grid - JoinedSet, the frame step's own: it ran a search of its own for
                    //  every jointed member every second before, beside the step's)
                    if (GridMembers.IsConstrained(g))
                    {
                        var set = JoinedSet(g, now);
                        if (set != null)
                            foreach (var e in set)
                                if (byEntity.TryGetValue(e, out var o) && o != g && GridMembers.IsDynamic(o))
                                {
                                    var of = SystemHost.Frames.FindByMember(o.Id);
                                    if (of != f) Invariants.Report("ship-together", "g" + m, $"grid {m} '{g.DisplayName}' in frame #{f.Id}, joined grid {o.Id} '{o.DisplayName}' in {(of == null ? "no frame" : "frame #" + of.Id)}");
                                }
                    }
                    // warp-state
                    if (N <= 1.0 && _trueRel.ContainsKey(m) && !_settle.ContainsKey(m))
                        Invariants.Report("warp-state", "g" + m, $"grid {m} '{g.DisplayName}' still steered at x1 (its warp state left over)");
                }
            }
            // on-rails: a player ship (dynamic, not an encounter's or NPC) in open space, in no frame for over 10 s
            var seen = _invSeen; seen.Clear();
            foreach (var g in all)
            {
                if (!g.IsServer || g.Entity == null || !GridMembers.IsDynamic(g) || EncounterFrames.IsNpc(g) || EncounterFrames.IsEncounterGrid(g)) continue;
                if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
                if (!_everFramed.Contains(g.Id)) continue;   // (a ship let go of its frame: one never framed - the world's own, encounters off - is not this rule's)
                lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;
                Vector3D p = GridMembers.Position(g);
                if (VoxelBerthRegistry.TryCellContaining(p, SystemHost.Registry, out _, out _)) continue;   // (a planet's space: no frame there)
                seen.Add(g.Id);
                if (!_unframedSince.TryGetValue(g.Id, out double since)) { _unframedSince[g.Id] = now; continue; }
                if (now - since > 10) Invariants.Report("on-rails", "g" + g.Id, $"grid {g.Id} '{g.DisplayName}' in open space in no frame for {now - since:F0} s (no orbit: warp leaves it behind)");
            }
            var gone = _invGone; gone.Clear();
            foreach (long id in _unframedSince.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (long id in gone) _unframedSince.Remove(id);
            gone.Clear(); seen.Clear(); byEntity.Clear();
        }
        catch (Exception ex) { Invariants.Report("checker", "x", "the check itself failed: " + ex.Message); }
    }

    /// <summary>The frame's members joined to this grid (its ship: sub-grids, docked craft), itself included - from the
    /// joint search kept once a second per grid (_joinedOf).</summary>
    /// <summary>(into r, cleared first: the caller's kept list - UpdateGridFrame's anchor body and steering set, each used within
    /// its own step; a new list per call before, every tick for a jointed anchor)</summary>
    static List<OrbitalGridComponent> JoinedMembers(ProximityFrame f, OrbitalGridComponent g, List<OrbitalGridComponent> grids, List<OrbitalGridComponent> r)
    {
        var set = JoinedSet(g, Wall());
        r.Clear();
        if (set == null) { r.Add(g); return r; }
        foreach (var o in grids) if (o.Entity != null && set.Contains(o.Entity)) r.Add(o);
        if (!r.Contains(g)) r.Add(g);
        return r;
    }

    /// <summary>A grid's joined entities (GridMembers.MoveSet, itself included; null: joined to something static, or none
    /// found), kept per grid for a second (_joinedOf) - every caller's: the frame step's anchor and steering, the rules'
    /// Joined, the invariants. (One kept scratch list for the search: server thread, under FramesLock - a new one per search
    /// before.)</summary>
    static HashSet<Keen.VRage.DCS.Components.Entity> JoinedSet(OrbitalGridComponent g, double wj)
    {
        if (_joinedOf.TryGetValue(g.Id, out var cj) && wj - cj.at < 1.0) return cj.set;
        var l = _moveSetScratch; l.Clear();
        var set = GridMembers.MoveSet(g, l, out _) ? new HashSet<Keen.VRage.DCS.Components.Entity>(l) : null;
        l.Clear();
        _joinedOf[g.Id] = (set, wj);
        return set;
    }
    static readonly List<Keen.VRage.DCS.Components.Entity> _moveSetScratch = new List<Keen.VRage.DCS.Components.Entity>();
    static readonly List<OrbitalGridComponent> _bodyScratch = new List<OrbitalGridComponent>(), _steerScratch = new List<OrbitalGridComponent>(), _frameAll = new List<OrbitalGridComponent>();

    /// <summary>The game behind the membership rules (Core/Frames/MembershipRules): server grids by their mod id.</summary>
    internal sealed class GridWorld : SEAerospace.Frames.IFrameWorld
    {
        public static readonly GridWorld I = new GridWorld();
        static OrbitalGridComponent G(long id) => GridMembers.Get(id) is OrbitalGridComponent g && g.IsServer && g.Entity != null ? g : null;
        public bool IsLive(long id) => G(id) != null;
        public bool MayReturn(long id) => GridMembers.Tombstoned(id);
        public bool IsDynamic(long id) => G(id) is OrbitalGridComponent g && GridMembers.IsDynamic(g);
        public bool IsNpc(long id) => G(id) is OrbitalGridComponent g && EncounterFrames.IsNpc(g);
        public double Mass(long id) => G(id) is OrbitalGridComponent g ? GridMembers.Mass(g) : 0;
        public long FrameOf(long id) => SystemHost.Frames?.FindByMember(id)?.Id ?? -1;
        // (the joint search once a second per grid, its entities mapped to ids through the live registry once a tick)
        static readonly Dictionary<Keen.VRage.DCS.Components.Entity, long> _ids = new Dictionary<Keen.VRage.DCS.Components.Entity, long>();
        // (the answer in a kept list of this thread's: the rules enumerate it at once and keep nothing - ElectAnchor, per frame
        //  per tick; a new array or list per call was ~0.1 MB/s, 2026-10-15. The entity -> id map built again only when grids
        //  come or go - GridMembers.All's snapshot changes - not every tick: an entity's id is fixed while it is registered)
        [ThreadStatic] static List<long> _joinedOut;
        static List<OrbitalGridComponent> _idsOf;
        public IReadOnlyCollection<long> Joined(long id)
        {
            var r = _joinedOut ??= new List<long>();
            r.Clear();
            var g = G(id);
            if (g == null || !GridMembers.IsConstrained(g)) { r.Add(id); return r; }
            var set = JoinedSet(g, Wall());
            if (set == null) { r.Add(id); return r; }
            var allNow = GridMembers.All();
            if (!ReferenceEquals(_idsOf, allNow)) { _ids.Clear(); foreach (var o in allNow) if (o.IsServer && o.Entity != null) _ids[o.Entity] = o.Id; _idsOf = allNow; }
            foreach (var e in set) if (_ids.TryGetValue(e, out long oid)) r.Add(oid);
            if (!r.Contains(id)) r.Add(id);
            return r;
        }
    }
    private static readonly List<OrbitalGridComponent> _frameGrids = new List<OrbitalGridComponent>();   // (UpdateGridFrame's: one frame at a time, from Tick)
    /// <summary>(diagnostics: UpdateGridFrame's phase marks - start, members/anchor rules, anchor step, others - for a slow frame)</summary>
    [ThreadStatic] static long[] _ugfT;
    static long[] _ugf => _ugfT ??= new long[4];
    private static void UpdateGridFrame(ProximityFrame f, double dt)
    {
        _ugf[0] = Sw(); _ugf[1] = _ugf[2] = _ugf[3] = 0;
        // (a restored frame whose berth was moved, its grids still loading: not stepped until they follow - its members are
        //  at the old berth, and every one would be "moved out" of it)
        if (f.PendingBerthShift.LengthSquared() >= 1) return;
        var reg = SystemHost.Registry;
        var parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;

        // Grid members (server copies, still alive and dynamic).
        var grids = _frameGrids; grids.Clear();
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) continue;
            var g = GridMembers.Get(id);
            if (g != null && g.IsServer && GridMembers.IsDynamic(g)) grids.Add(g);
        }
        // A static anchor (a static grid, or an asteroid) comes first: nothing is pinned or drained, the
        // rest (the player too: a rider, not the anchor) move about it normally.
        bool isStatic = false; Vector3D staticPos = default;
        if (!_born.ContainsKey(f.Id))
        {
            _born[f.Id] = Wall();
            // (one entry per frame id, for good: dissolved frames' entries dropped now and then)
            if (_born.Count > 256) { var gone = new List<long>(); foreach (long k in _born.Keys) if (SystemHost.Frames.Get(k) == null) gone.Add(k); foreach (long k in gone) _born.Remove(k); }
        }
        // (no move off a "dirty" slot any more, and no young frame kept off a rock: a frame is on whatever static is in it)
        // A station exists only on a stable orbit: a static-anchored frame whose orbit dips into its planet's border (a station
        // pushed off its orbit, a frame captured on a low one) has its static grids made ships (the game's own static/dynamic
        // switch) - then it is a ship's frame, and arrives as one. (The orbits the mod spawns things on are stable to begin with.)
        // (StaticAnchor once per frame step, not twice - it scans the world's static grids and its rocks, 7.5 ms/s in a profile,
        //  2026-10-15: the second call answered the same, its first having made any join and noted any rock - asked again
        //  only after the block below, which may have made its static grids ships)
        long sid = 0;
        bool staticHit = !f.IsEncounter && StaticAnchor(f, out sid, out staticPos);
        if (staticHit && sid != AsteroidAnchorId && !OrbitStable(f))
        {
            int made = 0;
            foreach (long m in new List<long>(f.Members))
                if (GridMembers.IsGridId(m) && GridMembers.Get(m) is OrbitalGridComponent sg && sg.IsServer && !GridMembers.IsDynamic(sg))
                    try { var gp = sg.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss.GridPhysicsComponent>(); if (gp != null) { gp.SetGridMotion(false); GridMembers.ForgetMotion(sg); made++; } } catch { }
            if (made > 0) Event($"frame #{f.Id}: its orbit meets {f.ParentBodyName}'s border (periapsis {(f.Elements.PeriapsisRadius / 1000):F0} km): {made} static grid(s) made ship(s) - a station needs a stable orbit");
            staticHit = StaticAnchor(f, out sid, out staticPos);
        }
        if (staticHit)
        {
            isStatic = true;
            if (f.AnchorEntityId != sid)
            {
                Event($"frame #{f.Id}: anchor -> {(sid == AsteroidAnchorId ? "an asteroid" : $"static grid {sid}")} (the player rides)");
                f.AnchorEntityId = sid;
                AnchorAccel.Remove(f.Id);   // no one folds thrust into a static anchor's frame
            }
            // The frame is the anchor's: its centre moves onto the anchor and its orbit becomes the anchor's.
            // Nothing in the world moves (moving a static grid off its rock made the game turn it dynamic,
            // and an asteroid cannot move at all): only the frame's reference point does.
            Vector3D off = staticPos - f.BerthCenter;
            if (off.Length() > PinTolerance)
            {
                double tn = SystemHost.Now;
                StateVector c0 = OrbitPropagation.StateAt(f.Elements, tn);
                var el = CaptureMath.CaptureElements(new StateVector(c0.Position + off, c0.Velocity), f.Elements.Mu, tn);
                if (IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion))
                {
                    f.Elements = el;
                    f.BerthCenter = staticPos;
                    Event($"frame #{f.Id}: centred on its static anchor ({off.Length() / 1000:F2} km; its orbit is the anchor's)");
                }
            }
        }
        else if (!f.IsEncounter && (f.AnchorEntityId == AsteroidAnchorId || (GridMembers.IsGridId(f.AnchorEntityId) && !HasGrid(grids, f.AnchorEntityId))))
        {
            // The static anchor (or a grid anchor) is gone: the player anchors again if nothing else can. (Not while the grid
            // may be coming back - an area respawn re-adds it within moments, its old id kept: GridMembers.Tombstoned.)
            if (grids.Count == 0 && f.HasMember(FrameHost.PlayerId) && !GridMembers.Tombstoned(f.AnchorEntityId))
            {
                // The centre moves back onto the player (with the player's orbit), as it moved onto the anchor.
                if (ServerPlanetBeacon.PlayerState(out Vector3D pp, out Vector3D pv) && (pp - f.BerthCenter).Length() > PinTolerance)
                {
                    double tn = SystemHost.Now;
                    StateVector c0 = OrbitPropagation.StateAt(f.Elements, tn);
                    // (position only: the player's own velocity is drained into the rails by the client next tick - added here
                    //  too, it went in twice)
                    var el = CaptureMath.CaptureElements(new StateVector(c0.Position + (pp - f.BerthCenter), c0.Velocity), f.Elements.Mu, tn);
                    if (IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion)) { f.Elements = el; f.BerthCenter = pp; }
                }
                f.AnchorEntityId = FrameHost.PlayerId;
                AnchorAccel.Remove(f.Id);   // (the old grid anchor's last acceleration would push every later joiner for good)
                Event($"frame #{f.Id}: anchor -> the player");
            }
        }
        _ugf[1] = Sw(); if (grids.Count == 0 && !isStatic) return;   // (a static anchor's frame still steps: its riders, its arrival)

        // Anchor election: keep a live grid anchor, else the heaviest PLAYER grid (SE1 ElectAnchor).
        // NPC grids never anchor, and an encounter frame has no anchor at all: its origin is the berth
        // (the site, pinned) and its orbit is its own (a site's ephemeris, a procedural spawn's conic).
        OrbitalGridComponent anchor = null;
        bool elected = false;
        // (the anchor's joint check, made once in the anchor step and read again for the joined set below: a physics query each -
        //  the two were asked the same thing within the same step, nothing between them changes a joint)
        bool anchorJointed = false;
        if (!f.IsEncounter && !isStatic)
        {
            // The rules (Core/Frames/MembershipRules.ElectAnchor, tested offline on a fake world): a live grid anchor kept -
            // unless a heavier grid of its own ship is in the frame (a ship is anchored by its heaviest grid: a light sub-grid
            // kept as anchor had its velocity drained alone while the hull pushed it back through the joint); one able to
            // come back (an area respawn): wait; else the heaviest live player grid (a player-anchored frame hands over to it).
            // (The player fallback, no grid left at all, is above.)
            long prevAnchor = f.AnchorEntityId;
            var choice = SEAerospace.Frames.MembershipRules.ElectAnchor(GridWorld.I, f.Id, f.AnchorEntityId, f.Members, FrameHost.PlayerId, false, out long aid);
            // (waiting for a respawning anchor: no acceleration of the old one pushing the riders meanwhile)
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Wait) AnchorAccel.Remove(f.Id);
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Keep || choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Grid)
                foreach (var g in grids) if (g.Id == aid) { anchor = g; break; }
            if (choice == SEAerospace.Frames.MembershipRules.AnchorChoice.Grid && anchor != null)
            {
                bool sameShip = GridMembers.IsGridId(prevAnchor) && GridWorld.I.IsLive(prevAnchor);
                Event($"frame #{f.Id}: anchor -> grid {anchor.Id} '{anchor.DisplayName}' ({GridMembers.Mass(anchor):F0} kg){(sameShip ? $" - the heaviest of the ship grid {prevAnchor} is part of" : "")}");
                f.AnchorEntityId = anchor.Id;
                elected = true;
            }
        }

        _ugf[2] = Sw(); double t = SystemHost.Now;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D A = Vector3D.Zero;
        Vector3D anchorPos = isStatic ? staticPos : f.BerthCenter;

        // An anchor moved out of its berth by something else (the game's fast travel, an admin move): not this frame's any
        // more - out of it quietly (pinned back, it was dragged home; split, it got an orbit made of a berth-to-berth offset)
        if (anchor != null && SEAerospace.Frames.PlayerOrbit.MovedOut(f, GridMembers.Position(anchor)))
        {
            SystemHost.Frames.RemoveMember(anchor.Id);
            Event($"frame #{f.Id}: anchor grid {anchor.Id} '{anchor.DisplayName}' left - {(GridMembers.Position(anchor) - f.BerthCenter).Length() / 1000:F0} km from the berth (moved by something else)");
            anchor = null;
        }
        if (anchor != null)
        {
            // Anchor owner = server: pin + drain + fold.
            anchorPos = GridMembers.Position(anchor);
            if ((anchorPos - f.BerthCenter).Length() > PinTolerance)
            {
                // A NEW anchor (the old one destroyed, undocked, a handover from the player): the frame's orbit moves onto it
                // first - the re-pin below shifts every member by its offset, and without this the whole frame jumped that far
                // in space (up to 20 km: the map, a plan, an arrival point all off by it). A drifting anchor's re-pin keeps the
                // rails: its motion is already folded in.
                // (a rebase refused - the orbit committed to a coming change of body: the re-pin waits for it, tried each tick;
                //  re-pinned anyway, the frame jumped by the new anchor's offset, up to 10 km)
                bool repin = true;
                if (elected || _rebasePending.Contains(f.Id))
                {
                    if (FrameRails.TryRebaseShift(f, t, anchorPos - f.BerthCenter)) _rebasePending.Remove(f.Id);
                    else
                    {
                        if (_rebasePending.Add(f.Id)) Event($"frame #{f.Id}: the new anchor's offset waits to be folded into the orbit (rebase refused for now)");
                        repin = false;
                    }
                }
                if (repin)
                {
                    // Re-pin by shifting the WHOLE frame back (members keep their offsets; no rails change).
                    Vector3D shift = f.BerthCenter - anchorPos;
                    // (MoveGrid: a jointed grid's wheels, rotor parts and docked ships must come too, before the physics step)
                    var went = new HashSet<long>();   // (a member moved with another's joined set is not moved twice)
                    foreach (var g in grids)
                        if (!went.Contains(g.Id)) { MoveGrid(g, GridMembers.Position(g) + shift, null, out var withP); foreach (var w in withP) went.Add(w.Id); }
                    FrameHost.RequestShift(f.Id, shift);
                    anchorPos = f.BerthCenter;
                }
            }
            // The anchor's motion drained into the rails - the whole ship's, if it is joined to others in the frame: their
            // mass-weighted velocity, and every one of them zeroed (one body; the anchor alone zeroed was refilled through
            // the joints, and drained again).
            Vector3D vA = GridMembers.Velocity(anchor);
            anchorJointed = GridMembers.IsConstrained(anchor);
            List<OrbitalGridComponent> body = anchorJointed ? JoinedMembers(f, anchor, grids, _bodyScratch) : null;
            if (body != null && body.Count > 1)
            {
                double mt = 0; Vector3D p = Vector3D.Zero;
                foreach (var bg in body) { double m = Math.Max(1.0, GridMembers.Mass(bg)); Vector3D v = GridMembers.Velocity(bg); if (!IsFinite(v)) continue; p += v * m; mt += m; }
                if (mt > 0) vA = p / mt;
            }
            if (IsFinite(vA))
            {
                if (body != null && body.Count > 1) foreach (var bg in body) GridMembers.SetVelocity(bg, Vector3D.Zero);
                else GridMembers.SetVelocity(anchor, Vector3D.Zero);
                f.PendingDrainDv += vA;
            }
            var fold = FrameRails.FoldDrain(f, t, FrameHost.FoldThreshold, FrameHost.MaxApparentAccel * dt, ref cur, out Vector3D slice);
            // (the anchor's burn per GAME second: the drained impulse is true dv, and in warp a real step is N game steps - as
            //  slice / dt, riders were pushed N times too hard)
            if (fold == FrameRails.FoldResult.Folded)
            {
                A = slice / (dt * Math.Max(1.0, SystemHost.Timescale));
                // members steered in warp keep their TRUE relative velocity: the frame's own change takes its share off it
                // (their absolute motion is theirs - an anchor's burn moved them with it)
                foreach (var g in grids) if (g != anchor && _trueRel.TryGetValue(g.Id, out var tr)) _trueRel[g.Id] = tr - slice;
            }
            if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;
            AnchorAccel[f.Id] = A;
        }
        else if (!f.IsEncounter && !isStatic && AnchorAccel.TryGetValue(f.Id, out var pa)) A = pa;   // the player anchors (client folds)

        // Other members: CW differential gravity minus the frame acceleration; split beyond the slot.
        // NPC grids are Newtonian inside the frame: no relative-motion terms, and they never split off.
        _ugf[3] = Sw(); double mu = f.Elements.Mu;
        Vector3D rA = cur.Position;
        Vector3D gA = Grav(rA, mu);
        // A Lagrange site: its own simple dynamics (EncounterFrames.LagrangeDynamics).
        Func<Vector3D, Vector3D, Vector3D> lag = null;
        if (f.IsEncounter) EncounterFrames.LagrangeDynamics(f.Id, t, out lag);
        // Warp N: relative motion N times faster, as the rails. In warp (and for a moment after) each member is
        // steered along its own Kepler orbit (see below); at x1 the differential gravity is integrated as ever.
        double N = Math.Max(1.0, SystemHost.Timescale);
        double fastest = 0; OrbitalGridComponent fastestG = null;
        // the game clock's step since this frame's last step (the steering's horizon)
        { double tnow = SystemHost.Now; _steerStep = _lastSteerNow.TryGetValue(f.Id, out double ln) && tnow > ln && tnow - ln < 10 * dt * N ? tnow - ln : 0; _lastSteerNow[f.Id] = tnow; }   // the largest physical speed a member needs this tick (N x its true relative speed)
        // what is joined to the anchor (docked, on its rotors and hinges): one body with it - never station-keeping on its
        // own (a docked ship with dampeners on skipped the relative pull while the joint dragged it: the anchor's thrust,
        // drained over the anchor's mass alone, counted double for a tug pushing a ship of its own mass)
        HashSet<Keen.VRage.DCS.Components.Entity> joined = null;
        if (anchor != null && anchorJointed)
        {
            // (the joint search once a second per anchor, not every tick)
            joined = JoinedSet(anchor, Wall());
        }
        else if (anchor != null) _joinedOf.Remove(anchor.Id);   // (no joints left - undocked, a hinge broken: no joined set kept)
        // The frame's stations (static grids): their boxes, grown by StationHoldMargin. A ship parked at one, or locked to
        // one, is not steered along its own orbit in warp - it keeps its place at the station. (Steered at N x its drift, or
        // teleported along its orbit in a frame nobody is in, ships went into Concordia at x1000 and broke a piece off it:
        // 2026-10-08, ~20 wreck pieces in one tick.)
        List<BoundingBoxD> stations = null;
        if (N > 1.0)
            foreach (var sg in grids)
            {
                if (sg.Entity == null || GridMembers.IsDynamic(sg)) continue;
                var sb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(sg.Entity.Data);
                if (!sb.HasValue) continue;
                var bb = sb.Value; bb.Inflate(StationHoldMargin);
                (stations ??= new List<BoundingBoxD>()).Add(bb);
            }
        foreach (var g in grids)
        {
            // Not this frame's any more (a split earlier this tick took it with its joined set into the new frame): not
            // judged here. Its queued position is already its new berth's - read as "6,731 km from its berth, moved by
            // something else", it was taken out of the frame it had just joined: a jointed ship left on no rails.
            if (!SEAerospace.Frames.MembershipRules.StillJudged(GridWorld.I, f.Id, g.Id)) continue;
            // (an anchor keeps no member's warp state: one left from before it anchored - its scale at x1 - multiplied the
            //  velocity a merge gave it at x1000 by 1000 again, once it was a member of the host)
            if (g == anchor) { _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id); continue; }
            if (EncounterFrames.IsNpc(g) && !DevNpcRelative) continue;
            if (stations != null && GridMembers.IsDynamic(g) && AtStation(g, stations))
            {
                // held at the station: no warp steering, no rescale - its velocity zeroed relative to the frame
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id); _vSet.Remove(g.Id);
                GridMembers.SetVelocity(g, Vector3D.Zero);
                continue;
            }
            Vector3D rRel = GridMembers.Position(g) - anchorPos;
            if (_gridN.TryGetValue(g.Id, out double n0) && Math.Abs(n0 - N) > 1e-9)
            {
                GridMembers.SetVelocity(g, GridMembers.Velocity(g) * (N / n0));
                _vSet.Remove(g.Id);   // (a rescale is no thrust)
                if (N <= 1.0 && n0 > 1.0) _settle[g.Id] = SettleTicks;   // (warp over: steer a moment longer)
            }
            _gridN[g.Id] = N;
            if (SEAerospace.Frames.PlayerOrbit.MovedOut(f, GridMembers.Position(g)))
            {
                // moved out by something else (fast travel): out of the frame quietly, framed again where it now is
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id);
                SystemHost.Frames.RemoveMember(g.Id);
                Event($"grid {g.Id} '{g.DisplayName}' left frame #{f.Id}: {(GridMembers.Position(g) - f.BerthCenter).Length() / 1000:F0} km from its berth (moved by something else)");
                continue;
            }
            if (rRel.Length() > LeaveRadius(isStatic))
            {
                _gridN.Remove(g.Id); _trueRel.Remove(g.Id); _settle.Remove(g.Id);
                SplitGrid(f, g, cur, rRel, GridMembers.Velocity(g) / N, t);
                continue;
            }
            Vector3D accel = lag != null ? lag(rRel, GridMembers.Velocity(g) / N) : (Grav(rA + rRel, mu) - gA) - A;
            // Station-keeping (dampeners on, and thrust on the side that cancels the pull): no relative force, as
            // SE1 and as a rider with dampeners on. Its dampeners only null its own motion: it holds exactly.
            // (judged against the TRUE pull at any warp, as the on-foot rider: warp compresses time, not the ship's authority -
            //  tested against N^2 x the pull, every ship let go of its station as warp rose, and its dampeners then fought the
            //  steering along its own orbit: it lagged the plot)
            if ((joined == null || !joined.Contains(g.Entity)) && StationKeeping(g, accel, N)) { _trueRel.Remove(g.Id); continue; }
            // In warp: along its own orbit, exactly. Integrating the N^2 forces in N*dt steps blew up near a low
            // periapsis (x1000: 16.7 s steps against ~30 s relative dynamics flung members at hundreds of m/s);
            // instead its true state (the frame's + its offset) goes N*dt along its own Kepler orbit, and it gets
            // the velocity that carries it there in this physics step. Its true relative velocity is kept here, not
            // read back from the physics (a velocity set as warp changes did not always hold), and the steering
            // goes on for SettleTicks after warp ends, so it leaves warp at its true speed.
            _settle.TryGetValue(g.Id, out int settle);
            // (a grid held by joints, to the anchor or to anything, cannot be steered: it holds with them)
            // A ship of several grids (rotors, hinges, wheels, docked craft): steered as ONE body - its heaviest grid works the
            // step along its own orbit, and every grid of it gets that velocity. (Skipped outright, it froze in warp while the
            // plot showed it drifting.) One joined to the anchor moves with the anchor: nothing to steer.
            List<OrbitalGridComponent> steerSet = null;
            if ((N > 1.0 || settle > 0) && GridMembers.IsConstrained(g))
            {
                steerSet = JoinedMembers(f, g, grids, _steerScratch);
                bool withAnchor = anchor != null && steerSet.Contains(anchor);
                OrbitalGridComponent root = g; double rm = GridMembers.Mass(g);
                foreach (var o in steerSet) { double m = GridMembers.Mass(o); if (m > rm) { rm = m; root = o; } }
                if (withAnchor || root != g) { _trueRel.Remove(g.Id); _settle.Remove(g.Id); _vSet.Remove(g.Id); continue; }
            }
            if ((N > 1.0 || settle > 0) && lag == null && IsFinite(rRel))
            {
                // Thrust or a bump since the last step (what physics made of the velocity set then): into the true relative
                // velocity, physical / N - kept as it was, a burn or a collision in warp never reached the orbit.
                if (_trueRel.TryGetValue(g.Id, out var kept0) && _vSet.TryGetValue(g.Id, out var vSet0))
                {
                    Vector3D dvPhys = GridMembers.Velocity(g) - vSet0;
                    if (IsFinite(dvPhys) && dvPhys.Length() < 50) _trueRel[g.Id] = kept0 + dvPhys / N;
                }
                Vector3D vRel = _trueRel.TryGetValue(g.Id, out var kept) ? kept : GridMembers.Velocity(g) / N;
                var mine = CaptureMath.CaptureElements(new StateVector(rA + rRel, cur.Velocity + vRel), mu, t);
                // (the clock's own step since the last: the client advances it, not always by exactly dt x N)
                double tn = t + (_steerStep > 0 ? _steerStep : dt * N);
                StateVector mineNext = OrbitPropagation.StateAt(mine, tn), frameNext = OrbitPropagation.StateAt(f.Elements, tn);
                Vector3D relNext = mineNext.Position - frameNext.Position;
                if (IsFinite(relNext))
                {
                    Vector3D vPhys = (relNext - rRel) / dt;
                    _trueRel[g.Id] = mineNext.Velocity - frameNext.Velocity;
                    // A frame you are not in (nobody to see it): a member too fast to carry in this warp is PLACED on its own
                    // orbit each tick, not pushed there - so a cluster drifting far away (the world's own ships, merged into
                    // one frame) no longer holds everyone's warp at x10. Yours keeps the physics' limit (you see its ships).
                    bool unseen = FrameHost.PlayerFrame == null || FrameHost.PlayerFrame.Id != f.Id;
                    // (judged on what the limiter judges - N x its true speed - not this tick's catch-up: the clock's step and the
                    //  physics tick differ, the catch-up came in under the line while N x speed was over it, and a frame you are
                    //  not in held your warp at x10 - the user, 2026-10-08: "my warp is mine alone")
                    if (unseen && (vPhys.Length() > WarpMemberSpeed || N * _trueRel[g.Id].Length() > 0.5 * WarpMemberSpeed)
                        && GridMembers.SetPosition(g, f.BerthCenter + relNext))
                    {
                        GridMembers.SetVelocity(g, Vector3D.Zero);
                        if (settle > 0) { if (--settle == 0) { _settle.Remove(g.Id); _trueRel.Remove(g.Id); } else _settle[g.Id] = settle; }
                        continue;
                    }
                    GridMembers.SetVelocity(g, vPhys);
                    _vSet[g.Id] = vPhys;
                    if (steerSet != null) foreach (var o in steerSet) if (o != g) GridMembers.SetVelocity(o, vPhys);   // (the whole ship)
                    if (!unseen && N * _trueRel[g.Id].Length() > fastest) { fastest = N * _trueRel[g.Id].Length(); fastestG = g; }   // (its own orbit's speed, not a catch-up)
                    if (settle > 0) { if (--settle == 0) { _settle.Remove(g.Id); _trueRel.Remove(g.Id); } else _settle[g.Id] = settle; }
                    continue;
                }
            }
            _trueRel.Remove(g.Id); _vSet.Remove(g.Id);
            // At x1 nothing is steered, and its velocity IS its true relative one: judged as the steering would judge it in
            // warp (the same members - not an encounter's, not one off the chart), so a raise from x1 knows it too.
            if (N <= 1.0 && lag == null && IsFinite(rRel) && FrameHost.PlayerFrame is ProximityFrame pf1 && pf1.Id == f.Id)
            {
                double v1 = GridMembers.Velocity(g).Length();
                if (IsFinite(v1) && v1 > fastest) { fastest = v1; fastestG = g; }
            }
            if (IsFinite(accel)) GridMembers.AddVelocity(g, accel * (dt * N * N));
        }
        // Warp is limited by what the physics can carry: a member moving relative to you needs N times its speed,
        // and the world caps speed (as KSP limits warp by circumstance).
        if (N > 1.0 && fastest > WarpMemberSpeed) { _fastestName = $"grid {fastestG?.Id} '{fastestG?.DisplayName}' in frame #{f.Id}"; LimitWarp(fastest / N); }
        // (and left for a raise to read: your frame only - the unseen ones never limit)
        if (FrameHost.PlayerFrame is ProximityFrame pf2 && pf2.Id == f.Id) PublishFastest(f.Id, fastest / N, fastestG);

        // encounter frames never arrive; a player-anchored frame with no grids arrives client-side - with grids in it, here
        // (the client moved only the player and left the ships at the berth with no frame)
        if (f.IsEncounter || (anchor == null && !isStatic && grids.Count == 0)) return;
        FrameHost.TryReparent(f, t);
        // A static anchor's frame arrives too (its orbit meets the planet): everything in it drops into
        // the planet's space, static grids placed at their offsets (an asteroid itself stays).
        // (only grids still this frame's: one split off or let go earlier this tick has its queued position at another berth -
        //  carried along, it landed 100+ km off in the planet's space)
        var all = _frameAll; all.Clear();   // (kept: one frame at a time - TryMaterializeGrids keeps nothing of it)
        foreach (var g in grids) if (SEAerospace.Frames.MembershipRules.StillJudged(GridWorld.I, f.Id, g.Id)) all.Add(g);
        if (isStatic)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent sg && sg.IsServer && !GridMembers.IsDynamic(sg)) all.Add(sg);
        TryMaterializeGrids(f, all, anchor != null ? GridMembers.Position(anchor) : isStatic ? staticPos : f.BerthCenter, t);   // (player-anchored: the berth is the player)
    }

    // ───────────────────────────── DEV: station-keeping test grids ─────────────────────────────

    /// <summary>DEV: NPC grids feel relative motion too (to test station-keeping on the wrecks a test world has).</summary>
    public static bool DevNpcRelative;
    /// <summary>
    /// Every long move of a grid goes through here. A grid with no joints moves at once, as it always has. A
    /// grid with joints (wheels, rotors, hinges, docked or locked ships) moves with everything joined to it,
    /// all by the same offset, where the game's fast travel moves ships (before the physics step): moved
    /// later in the frame, Havok cannot migrate the joints and the server dies. Anything static in the group
    /// (a base it is locked to) and it does not move: false. setVel: its velocity after (all of the group);
    /// addVel: added to each instead. A group already moving this tick is not moved again (true).
    /// </summary>
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, Vector3D addVel = default, Quaternion? rotate = null)
        => MoveGrid(g, target, setVel, out _, addVel, rotate);

    /// <summary>As above; with: the other grids that move with it (its joined group), for the caller's frame bookkeeping.</summary>
    /// <summary>rotate: turns the grid (its orientation and spin; a joined set rigidly, about this grid) - crossing between
    /// a planet's rotating chart and the inertial frames, positions and velocities were turned and orientations not: the
    /// ship arrived rotated against its own motion (and its cockpit's target, FrameHost, with it).</summary>
    /// <summary>
    /// A second move of a jointed group in the same tick (a re-pin, then an arrival, a merge or a split): added to the one
    /// already queued for the whole group - it returned "moved" and did nothing (the group stayed in the empty berth while
    /// the frame arrived, or landed 400 km off the host and split off on a wrong orbit). A second turn is not composed.
    /// </summary>
    static bool AddToQueuedMove(OrbitalGridComponent g, (Vector3D p, Vector3D v) queued, Vector3D target, Vector3D? setVel, Vector3D addVel,
                                Quaternion? rotate, List<OrbitalGridComponent> with)
    {
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel)) return false;
        var set = new List<Entity>();
        if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' second move refused: {why}"); return false; }
        var inSet = new HashSet<Entity>(set);
        Vector3D delta = target - queued.p;
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
        lock (_deferred)
            for (int i = 0; i < _deferred.Count; i++)
            {
                var d = _deferred[i];
                if (!inSet.Contains(d.Item1)) continue;
                var np = d.Item2 + delta; var nv = setVel ?? d.Item3 + addVel;
                if (!GridMembers.Finite(np) || !GridMembers.Finite(nv)) continue;
                _deferred[i] = (d.Item1, np, nv);
                if (byEntity.TryGetValue(d.Item1, out var og))
                {
                    lock (GridMembers.Pending) GridMembers.Pending[og.Id] = (np, nv);
                    if (og != g) with.Add(og);
                }
            }
        if (rotate.HasValue) Event($"grid {g.Id} '{g.DisplayName}': a second move this tick turned nothing (only the first move's turn is applied)");
        Event($"grid {g.Id} '{g.DisplayName}': a second move this tick, {delta.Length() / 1000:F1} km more, added to the queued one");
        return true;
    }

    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, out List<OrbitalGridComponent> with, Vector3D addVel = default, Quaternion? rotate = null)
    {
        with = new List<OrbitalGridComponent>();
        (Vector3D p, Vector3D v) queued;
        bool isQueued;
        lock (GridMembers.Pending) isQueued = GridMembers.Pending.TryGetValue(g.Id, out queued);
        if (isQueued) return AddToQueuedMove(g, queued, target, setVel, addVel, rotate, with);
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel) || (setVel.HasValue && !GridMembers.Finite(setVel.Value)))
        { GridMembers.NaNRefused++; Event($"grid {g.Id} '{g.DisplayName}' not moved: non-finite target"); return false; }
        // (the motion cache - IsDynamic, 0.25 s - can still call a grid just made a station dynamic: a move would make the game
        //  turn it back. Asked live here, where it matters)
        if (g.Dyn && !GridMembers.IsDynamicNow(g)) { GridMembers.ForgetMotion(g); Event($"grid {g.Id} '{g.DisplayName}' not moved: it was just made static"); return false; }
        if (!GridMembers.IsConstrained(g))
        {
            Vector3D v = setVel ?? GridMembers.Velocity(g) + addVel;
            Vector3D from = GridMembers.Position(g);
            if (!GridMembers.SetPosition(g, target)) return false;   // (callers must not frame a grid that never got there)
            GridMembers.SetVelocity(g, v);
            if (rotate.HasValue) { GridMembers.Rotate(g.Entity, rotate.Value); NoteTargetTurn(g.Entity, from, target, rotate.Value); }
            return true;
        }
        var set = new List<Entity>();
        if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' not moved: {why}"); return false; }
        Vector3D delta = target - GridMembers.Position(g);
        Vector3D pivot = GridMembers.Position(g);
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
        lock (_deferred)
            foreach (var e in set)
            {
                Vector3D v0 = e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
                Vector3D v = setVel ?? v0 + addVel;
                Vector3D p = e.Data.GetWorldTransform().Position + delta;
                if (rotate.HasValue) p = target + Vector3D.Transform(e.Data.GetWorldTransform().Position - pivot, rotate.Value);   // (the set turns rigidly about this grid)
                if (!GridMembers.Finite(p) || !GridMembers.Finite(v)) continue;   // (never hand Havok a NaN)
                _deferred.Add((e, p, v));
                if (rotate.HasValue) lock (_deferredRot) _deferredRot[e] = rotate.Value;
                if (byEntity.TryGetValue(e, out var og))
                {
                    lock (GridMembers.Pending) GridMembers.Pending[og.Id] = (p, v);
                    if (og != g) with.Add(og);
                }
            }
        Event($"grid {g.Id} '{g.DisplayName}' and {set.Count - 1} joined entit(ies): moving {delta.Length() / 1000:F1} km before the next physics step");
        return true;
    }

    /// <summary>Grid moves done where fast travel does them (ServerPlanetBeacon's teleport phase, before the physics step).</summary>
    private static readonly List<(Entity e, Vector3D p, Vector3D v)> _deferred = new List<(Entity, Vector3D, Vector3D)>();
    private static readonly Dictionary<Entity, Quaternion> _deferredRot = new Dictionary<Entity, Quaternion>();
    private static long _deferredStamp;

    /// <summary>The queued moves, all in one go (once a frame, whichever beacon's job comes first).</summary>
    /// <summary>Piloted grids the server just turned (crossing between a planet's rotating chart and the inertial frames): the
    /// client turns their cockpit's target with them (TargetControlData is the client's - what the mouse moves; left
    /// unturned, the gyros swung the ship back by the chart's angle: 80 deg within 2 s of the handover, seated - DevReentry).
    /// from / to: the grid's position before and after (the client's copy is near one of them).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(Vector3D from, Vector3D to, Quaternion rot)> TargetTurns = new System.Collections.Concurrent.ConcurrentQueue<(Vector3D, Vector3D, Quaternion)>();

    /// <summary>Grids moved across a planet's chart border (rails <-> the planet's frame), counted: on rails a grid sits
    /// still in its berth while its frame carries the orbit, so a crossing steps its speed (0 <-> ~1 km/s) in one tick -
    /// the client quiets the game's acceleration camera shake for it (FrameHost; it shook the cockpit 19 deg).</summary>
    public static int ChartCrossings;


    static void NoteTargetTurn(Entity e, Vector3D from, Vector3D to, Quaternion rot)
    {
        System.Threading.Interlocked.Increment(ref ChartCrossings);
        try { if (e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) TargetTurns.Enqueue((from, to, rot)); } catch { }
    }

    /// <summary>Tell the aero mod a grid was moved (AeroApi.NoteMoved: its structure ignores the physics contacts of the next
    /// steps - a teleport is no collision). Silent when the aero mod is absent or older.</summary>
    public static void AeroNoteMoved(Entity e)
    {
        if (e == null) return;
        GridEdits.Moved(e);   // (the move stamp - OrbitalApi.TryGetGridEdits: the pull side of this push)
        // (under FramesLock - a frame step's moves: told right after the lock, FlushAeroNoteMoved, still before the next physics
        //  step; never a call into aero while holding our lock - API.md's lock rule)
        if (System.Threading.Monitor.IsEntered(FramesLock)) { lock (_noteMovedLater) _noteMovedLater.Add(e); return; }
        CallNoteMoved(e);
    }

    /// <summary>The moves made under FramesLock, told to the aero mod (after the lock).</summary>
    public static void FlushAeroNoteMoved()
    {
        Entity[] todo;
        lock (_noteMovedLater) { if (_noteMovedLater.Count == 0) return; todo = _noteMovedLater.ToArray(); _noteMovedLater.Clear(); }
        foreach (var e in todo) CallNoteMoved(e);
    }
    static readonly List<Entity> _noteMovedLater = new List<Entity>();

    static void CallNoteMoved(Entity e)
    {
        var fn = System.Threading.Volatile.Read(ref _noteMoved);
        if (fn == null)
        {
            // (looked up again every few seconds until found - the aero mod can load later; the LAST loaded assembly that has
            //  the method wins: an older or stale copy may be loaded too. Bound once as a typed delegate: no argument array,
            //  no Invoke per move)
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < System.Threading.Volatile.Read(ref _noteMovedRetryAt)) return;
            System.Threading.Volatile.Write(ref _noteMovedRetryAt, now + 5 * System.Diagnostics.Stopwatch.Frequency);
            System.Reflection.MethodInfo mi = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t; try { t = asm.GetType("AeroMod.AeroApi"); } catch { continue; }
                var m = t?.GetMethod("NoteMoved", new[] { typeof(Entity) });   // (explicit parameter types: a later overload must not make it ambiguous)
                if (m != null) mi = m;
            }
            if (mi == null) return;
            fn = (Action<Entity>)Delegate.CreateDelegate(typeof(Action<Entity>), mi, false);
            if (fn == null) return;   // (an unexpected signature: as absent)
            System.Threading.Volatile.Write(ref _noteMoved, fn);
        }
        try { fn(e); } catch { }
    }
    static Action<Entity> _noteMoved; static long _noteMovedRetryAt;

    public static void RunDeferredMoves()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        List<(Entity e, Vector3D p, Vector3D v)> todo;
        List<long> done;
        lock (_deferred)
        {
            if (_deferred.Count == 0) return;
            todo = new List<(Entity, Vector3D, Vector3D)>(_deferred);
            _deferred.Clear();
            lock (GridMembers.Pending) done = new List<long>(GridMembers.Pending.Keys);   // (queued with these moves, under this lock)
        }
        _deferredStamp = now;
        foreach (var (e, p, v) in todo)
        {
            try
            {
                GridMembers.Unmanage(e);   // (out of its world area first: else the game unloads and respawns it)
                var wt = e.Data.GetWorldTransform();
                Quaternion rot; bool turn; lock (_deferredRot) { turn = _deferredRot.TryGetValue(e, out rot); if (turn) _deferredRot.Remove(e); }
                e.Data.SetWorldTransform(new WorldTransform(p, turn ? Quaternion.Normalize(rot * wt.Orientation) : wt.Orientation));
                AeroNoteMoved(e);   // (its next physics contacts are the move's, not loads)
                if (turn) NoteTargetTurn(e, wt.Position, p, rot);
                ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb))
                {
                    var was = rb.LinearVelocity;
                    rb.LinearVelocity = (Vector3)v; if (turn) rb.AngularVelocity = Vector3.Transform(rb.AngularVelocity, rot);
                    GridEdits.Wrote(e, rb.LinearVelocity - was);   // (OrbitalApi.TryGetGridEdits; a wheel or rotor part of the set: not recorded)
                }
            }
            catch (Exception ex) { Event("deferred move failed: " + ex.Message); }
        }
        lock (GridMembers.Pending) foreach (long id in done) GridMembers.Pending.Remove(id);
        Event($"moved {todo.Count} grid(s) in the teleport phase");
    }

    /// <summary>DEV: put a grid (with its joined group) on a circular orbit this high over the planet it is at.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, double altKm, string body, double speed)> GridLaunch = new System.Collections.Concurrent.ConcurrentQueue<(long, double, string, double)>();
    /// <summary>DEV: level a grid - top away from the planet, nose (-Z) horizontal on its heading - its joined group with it.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<long> GridLevel = new System.Collections.Concurrent.ConcurrentQueue<long>();
    static void DoGridLevel(long id)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g == null || !g.IsServer || reg == null) { Event($"gridlevel: no server grid {id}"); return; }
        Vector3D pos = GridMembers.Position(g);
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out Vector3D cell)) { Event("gridlevel: not at a planet"); return; }
        Vector3D up = Vector3D.Normalize(pos - cell);
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        Vector3D f = Vector3D.Transform(new Vector3D(0, 0, -1), q);
        Vector3D h = f - up * Vector3D.Dot(f, up);
        if (h.LengthSquared() < 1e-6) { Vector3D r = Vector3D.Transform(new Vector3D(1, 0, 0), q); h = Vector3D.Cross(up, r); }
        h = Vector3D.Normalize(h);
        var target = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        var turn = Quaternion.Normalize(target * Quaternion.Inverse(q));
        bool ok = MoveGrid(g, pos, Vector3D.Zero, out var with, default, turn);
        Event($"gridlevel: grid {id} '{g.DisplayName}' levelled ({(ok ? $"{with.Count} grid(s) with it" : "refused")})");
    }
    /// <summary>DEV: a grid (its joined set too) at altKm over a planet, moving sideways at speed (NaN: circular orbit). body: that
    /// planet's cell from anywhere (a test ship in open space into the air), else the cell it is in.</summary>
    static void DoGridLaunch(long id, double altKm, string body = null, double speed = double.NaN)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g == null || !g.IsServer || reg == null) { Event($"gridlaunch: no server grid {id}"); return; }
        Vector3D pos = GridMembers.Position(g);
        Vector3D cell;
        if (body != null) { if (!VoxelBerthRegistry.TryGetCell(body, reg, out cell)) { Event($"gridlaunch: no cell for {body}"); return; } }
        else if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out body, out cell)) { Event("gridlaunch: not at a planet (name one: gridlaunch <id> <altKm> <planet>)"); return; }
        var node = reg.Find(body); var def = reg.FindDefinition(body);
        if (node == null || def == null) return;
        double t = SystemHost.Now, r = def.RadiusMeters + altKm * 1000;
        var chart = Chart.Of(body, t);
        Vector3D up = Vector3D.Normalize(chart.ToInertial(pos - cell));
        Vector3D side = Vector3D.Cross(Vector3D.UnitZ, up);
        if (side.LengthSquared() < 1e-9) side = Vector3D.Cross(Vector3D.UnitX, up);
        Vector3D pI = up * r, vI = Vector3D.Normalize(side) * (double.IsNaN(speed) ? Math.Sqrt(node.Mu / r) : speed);
        bool ok = MoveGrid(g, cell + chart.FromInertial(pI), chart.VelFromInertial(pI, vI), out var with);
        Event($"gridlaunch: grid {id} '{g.DisplayName}' to a {altKm:F0} km circular orbit of {body}: {(ok ? $"moving ({with.Count} grid(s) with it)" : "refused")}");
    }

    /// <summary>DEV (FlyTest, Ctrl+T seated): a grid AltitudeM over the ground under it, level on its cockpit's heading, flying
    /// along its nose at cruise speed. nose, top: the cockpit's forward and up in the grid's axes.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, Vector3 nose, Vector3 top)> FlyTest = new System.Collections.Concurrent.ConcurrentQueue<(long, Vector3, Vector3)>();
    static System.Reflection.MethodInfo _ftGround, _ftTop; static bool _ftResolved;
    static void DoFlyTest(long id, Vector3 noseL, Vector3 topL)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g?.Entity == null || !g.IsServer || reg == null) { OrbitalMod.FlyTest.Status = Event2($"flytest: no server grid {id}"); return; }
        Vector3D pos = GridMembers.Position(g);
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) { OrbitalMod.FlyTest.Status = Event2("flytest: not at a planet"); return; }
        var def = reg.FindDefinition(body);
        if (def == null) return;
        // the aero mod's ground ray and cruise speed (AeroApi; found once)
        if (!_ftResolved)
        {
            _ftResolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("AeroMod.AeroApi");
                if (t == null) continue;
                // (explicit parameter types throughout: an aero overload added later must not make a lookup ambiguous - API.md)
                _ftGround = t.GetMethod("TryGetGroundHeight", new[] { typeof(Entity), typeof(float).MakeByRefType() });
                _ftTop = t.GetMethod("GetTopSpeedAt", new[] { typeof(Entity), typeof(Vector3), typeof(float) });
                break;
            }
        }
        double rNow = (pos - cell).Length(), rGround = def.RadiusMeters; string over = "sea level";
        if (_ftGround != null)
        {
            var ga = new object[] { g.Entity, null };
            try { if ((bool)_ftGround.Invoke(null, ga) && (float)ga[1] >= 0f) { rGround = rNow - (float)ga[1]; over = "the ground"; } } catch { }
        }
        float speed = float.NaN;
        try { if (_ftTop != null) speed = (float)_ftTop.Invoke(null, new object[] { g.Entity, noseL, OrbitalMod.FlyTest.CruiseThrottle }); } catch { }
        if (!(speed > 1f) || float.IsInfinity(speed)) speed = OrbitalMod.FlyTest.FallbackSpeed;
        // level: the cockpit's nose horizontal on its heading, its top away from the planet
        Vector3D up = Vector3D.Normalize(pos - cell);
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        Vector3D noseW = Vector3D.Transform((Vector3D)noseL, q), topW = Vector3D.Transform((Vector3D)topL, q);
        Vector3D h = noseW - up * Vector3D.Dot(noseW, up);
        if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(up, Vector3D.Cross(topW, up));   // (nose straight up or down: its top's heading)
        h = Vector3D.Normalize(h);
        var have = Quaternion.CreateFromForwardUp((Vector3)noseW, (Vector3)topW);
        var want = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        var turn = Quaternion.Normalize(want * Quaternion.Inverse(have));
        double r = rGround + OrbitalMod.FlyTest.AltitudeM;
        bool ok = MoveGrid(g, cell + up * r, h * speed, out var with, default, turn);
        OrbitalMod.FlyTest.Status = Event2($"flytest: grid {id} '{g.DisplayName}' {OrbitalMod.FlyTest.AltitudeM:F0} m over {over}, level, {speed:F0} m/s ({(ok ? $"{with.Count} grid(s) with it" : "refused")})");
    }
    static string Event2(string s) { Event(s); return s; }

    /// <summary>DEV: move a grid (server), unless it has joints.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, Vector3D d, bool group)> GridMove = new System.Collections.Concurrent.ConcurrentQueue<(long, Vector3D, bool)>();

    /// <summary>DEV: a grid's dampeners on / off (server), as the game's own toggle does.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, bool on)> GridDamp = new System.Collections.Concurrent.ConcurrentQueue<(long, bool)>();
    static void DoGridDamp(long id, bool on)
    {
        try
        {
            var g = GridMembers.Get(id);
            var e = g?.IsServer == true ? g.Entity : null;
            if (e == null) { Event($"griddamp: no server grid {id}"); return; }
            if (on) e.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.DampeningData));
            else e.Data.TryRemove<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>();
            string thrust = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)
                ? $"thrust +{mt.Regular.Positive} -{mt.Regular.Negative}" : "no thrust data";
            Event($"griddamp: grid {id} '{g.DisplayName}' dampeners {(on ? "on" : "off")}; {thrust}");
        }
        catch (Exception ex) { Event("griddamp failed: " + ex.Message); }
    }

    /// <summary>Grids holding station, for the harness: id -> name.</summary>
    public static readonly Dictionary<long, string> Holding = new Dictionary<long, string>();

    /// <summary>A grid left the scene: its per-grid state goes with it.</summary>
    internal static void Forget(long gridId)
    {
        // (all per-grid state: _gridN, _trueRel and _settle outlived their grid - a stale _gridN rescaled a later joiner)
        lock (FramesLock) { Holding.Remove(gridId); _gridHighSpeed.Remove(gridId); _gridN.Remove(gridId); _trueRel.Remove(gridId); _settle.Remove(gridId); _vSet.Remove(gridId); }
        lock (GridPositions) GridPositions.Remove(gridId);
        EncounterFrames.ForgetKind(gridId);
    }

    /// <summary>
    /// A grid holding station (SE1: dampeners + thrust authority): its dampeners on, and thrust on every side
    /// the force that cancels the pull (−m·a, in the grid's own axes) points to. The game's MaxThrustData:
    /// Positive = the push along each +axis, Negative = along each −axis (Thrust6Directions.Clamp).
    /// A ship turned so it cannot push against the pull drifts until it can.
    /// </summary>
    static bool StationKeeping(OrbitalGridComponent g, Vector3D accel, double N = 1.0)
    {
        bool on = false;
        try
        {
            var e = g.Entity;
            if (e != null && IsFinite(accel) && GridMembers.Mass(g) > 0 && e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>()
                && e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt))
            {
                Vector3D need = -accel * GridMembers.Mass(g);   // world
                var q = (QuaternionD)e.Data.GetWorldTransform().Orientation;
                Vector3D l = QuaternionD.Inverse(q) * need;       // the grid's axes
                var P = mt.Regular.Positive; var Ng = mt.Regular.Negative;
                bool Ok(double c, float pos, float neg) => Math.Abs(c) < 1e-6 || (c > 0 ? pos >= c : neg >= -c);   // (no pull along an axis needs no thrust there)
                on = Ok(l.X, P.X, Ng.X) && Ok(l.Y, P.Y, Ng.Y) && Ok(l.Z, P.Z, Ng.Z);
                // (not free: what making the true force would cost, charged to its thrusters, x N in warp)
                if (on) StationKeepCharge.ChargeGrid(e, l, mt, N);
            }
        }
        catch { }
        if (on != Holding.ContainsKey(g.Id))
        {
            if (on) Holding[g.Id] = g.DisplayName; else Holding.Remove(g.Id);
            Event($"grid {g.Id} '{g.DisplayName}' {(on ? "holds station (dampeners, thrust): no relative pull" : "free: the relative pull is on it again")}");
        }
        return on;
    }

    /// <summary>SE1 ExecuteSplits: the member becomes its own frame from its celestial state.</summary>
    private static void SplitGrid(ProximityFrame f, OrbitalGridComponent g, StateVector cur, Vector3D rRel, Vector3D vRel, double t)
    {
        long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vRel), f.Elements.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.SplitOff(f, g.Id, f.ParentBodyName, el);
        if (nf == null) return;
        long s1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Vector3D p = GridMembers.Position(g);
        Vector3D vShip = GridMembers.Velocity(g);
        // You on foot inside it (or on it: its box, 30 m to spare): you go with it - the same move, the same change of
        // velocity, into its new frame. (Left behind, the ship vanished around you and you split off alone.)
        bool carryPlayer = false;
        if (f.HasMember(FrameHost.PlayerId) && !FrameHost.Seated && ServerPlanetBeacon.PlayerState(out Vector3D ppos, out _))
        {
            var bx = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data);
            if (bx.HasValue) { var bb = bx.Value; bb.Inflate(30); carryPlayer = bb.Contains(ppos); }
        }
        if (!MoveGrid(g, nf.BerthCenter, Vector3D.Zero, out var withG)) { SystemHost.Frames.Dissolve(nf.Id); SystemHost.Frames.AddMember(f, g.Id); return; }
        long s2 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Joined to others (docked, wheels on a sub-grid): they go with it, into its new frame.
        // (MembershipRules.SplitTakes: the old frame's grids of the ship, and its grids in no frame - left out, on no rails)
        var withIds = new List<long>(); foreach (var o in withG) withIds.Add(o.Id);
        foreach (long oid in SEAerospace.Frames.MembershipRules.SplitTakes(GridWorld.I, f.Id, g.Id, withIds))
        {
            if (SystemHost.Frames.FindByMember(oid) == f) SystemHost.Frames.RemoveMember(oid);
            SystemHost.Frames.AddMember(nf, oid);
        }
        if (carryPlayer)
        {
            FrameHost.RequestMerge(f.Id, nf.BerthCenter - p, -vShip);   // (the player's move: requested while still in the old frame)
            SystemHost.Frames.RemoveMember(FrameHost.PlayerId); SystemHost.Frames.AddMember(nf, FrameHost.PlayerId);
            Event($"SPLIT: the player, on foot in grid {g.Id}, goes with it to frame #{nf.Id}");
        }
        Event($"SPLIT grid {g.Id} '{g.DisplayName}' from frame #{f.Id} at {rRel.Length() / 1000:F1} km -> frame #{nf.Id} (slot {nf.BerthSlotId})");
        SpawnGuard.Refresh();   // (its new berth closed to encounters now if nobody is in it: one spawned there in the next tick)
        long s3 = System.Diagnostics.Stopwatch.GetTimestamp(); double ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms(s0, s3) > 30) Event($"split of grid {g.Id} took {ms(s0, s3):F0} ms (frame {ms(s0, s1):F0}, move {ms(s1, s2):F0}, members and guard {ms(s2, s3):F0})");
    }

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
        bool capped = speed > FrameHost.SpeedCap;
        double k = capped ? FrameHost.SpeedCap / speed : 1.0;
        var went = new HashSet<long>();   // (moved with an earlier member's joined set: its queued position is already its arrival)
        foreach (var g in grids)
        {
            if (went.Contains(g.Id)) continue;
            Vector3D off = GridMembers.Position(g) - anchorPos;
            Vector3D vRel = GridMembers.Velocity(g);
            Vector3D dest = cell + chart.FromInertial(cel.Position + off);   // berth offsets are inertial
            if (!GridMembers.IsDynamic(g))
            {
                // (a static grid with joints - a ship docked to the station - moved mid-tick left the constraint
                //  stretched across thousands of km: the set moves together, before the physics step)
                if (GridMembers.IsConstrained(g))
                {
                    var set = new List<Entity>();
                    Vector3D d = dest - GridMembers.Position(g);
                    if (!GridMembers.MoveSet(g, set, out string why)) { set.Clear(); set.Add(g.Entity); Event($"arrival: grid {g.Id}'s joined set not movable ({why}): it moves alone"); }
                    lock (_deferred) foreach (var e in set) _deferred.Add((e, e.Data.GetWorldTransform().Position + d, Vector3D.Zero));
                }
                else GridMembers.SetPosition(g, dest);
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
        Event($"ARRIVE grid frame #{fid} -> {node.Name}: {grids.Count} grid(s), r={cel.Position.Length() / 1000:F1} km |v|={speed:F0} m/s{(capped ? $" -> capped to {FrameHost.SpeedCap:F0}" : "")}");
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
            MoveGrid(g, cell + chart.FromInertial(st.Position), Vector3D.Zero);
        }
        foreach (var r in refold) if (!done.Contains(r.id)) _gridHighSpeed[r.id] = (r.body, r.el);
        foreach (long id in done) _gridHighSpeed.Remove(id);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static Vector3D Grav(Vector3D r, double mu)
    {
        double d = r.Length();
        return d > 1 ? r * (-mu / (d * d * d)) : Vector3D.Zero;
    }

    /// <summary>
    /// A new player grid in a frame's space - a piece broken off, a cockpit section after a hit, a rotor head built, a paste -
    /// joins the nearest frame within its split radius. The encounter system did this only while encounters are on (a world
    /// setting): off, a ship that broke up left its pieces, and a player seated in one, in no frame for good (no orbit, warp
    /// refused, left behind at the next arrival). Encounters on: theirs (EncounterFrames.ProcessNewGrids), not raced here.
    /// </summary>
    private static void AdoptLooseGrids()
    {
        // (with encounters on too - it returned then, and a grid let go of its frame in open space was never framed again;
        //  the encounter system's own grids are left to it, and a static grid is never adopted from 20 km out)
        if (SystemHost.Frames == null || SystemHost.Registry == null) return;
        lock (FramesLock)
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer || SystemHost.Frames.FindByMember(g.Id) != null || EncounterFrames.IsNpc(g)) continue;
                if (EncounterFrames.Enabled && EncounterFrames.IsEncounterGrid(g)) continue;
                if (!GridMembers.IsDynamic(g) || GridMembers.Tombstoned(g.Id)) continue;
                lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;
                Vector3D pos = GridMembers.Position(g);
                if (VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out _, out _)) continue;   // (a planet's: its own stow)
                ProximityFrame near = null; double d = SlotRadius;
                foreach (var f in SystemHost.Frames.Frames)
                {
                    double dd = (pos - f.BerthCenter).Length();
                    if (dd < d) { d = dd; near = f; }
                }
                if (near != null && SystemHost.Frames.AddMember(near, g.Id))
                    Event($"loose grid {g.Id} '{g.DisplayName}' joins frame #{near.Id} ({d / 1000:F1} km from its centre)");
            }
    }

    /// <summary>What the client may know of a server grid (ServerFrames.View): read from this snapshot, never from the server's
    /// entities (the client read them directly - a static check, a box, a name, a mass - and one path asked Aero to build a
    /// table on the server's entity from the client).</summary>
    public sealed class GridView
    {
        public long Id; public Vector3D Position; public BoundingBoxD Box; public bool HasBox, Dynamic; public string Name; public double Mass;
        /// <summary>Its owner's identity (the server's ownership), and whether it is an encounter's (NPC or procedural).</summary>
        public Keen.Game2.Simulation.GameSystems.Ownership.IdentityId Owner; public bool HasOwner, Encounter;
        /// <summary>Holding station in its frame (dampeners, thrust to cancel the pull): no relative force on it (StationKeeping).</summary>
        public bool Holding;
        /// <summary>Joined to its frame's anchor (a sub-grid of the anchor ship, docked to it): it moves as one with the anchor -
        /// seated in it, you are in the anchor (the frame's orbit), not a rider drifting about it.</summary>
        public bool AnchorJoined;
        /// <summary>In warp, the TRUE velocity relative to its frame the steering keeps (its physical one is N x the average
        /// over the step, a chord: the seated plot built on it drifted off the motion).</summary>
        public Vector3D TrueRel; public bool HasTrueRel;
    }

    /// <summary>A sensor block as the client may know it: on which grid, where, what, working, how strong.</summary>
    public struct SensorView { public long Grid; public Vector3D Position; public bool Radar, Working; public double Power; }
    private static List<SensorView> _sensorViews = new List<SensorView>();
    public static List<SensorView> SensorViews => System.Threading.Volatile.Read(ref _sensorViews);
    /// <summary>The grid the player's seat is on, as the server has it (0: not seated) - BuildViews.</summary>
    public static long PlayerSeatGrid;
    public static List<GridView> AllViews() => new List<GridView>(System.Threading.Volatile.Read(ref _views).Views);
    /// <summary>The snapshot's grids without a copy (published whole each snapshot, never changed after).</summary>
    public static GridViews ViewValues => new GridViews(System.Threading.Volatile.Read(ref _views).Views);
    /// <summary>A snapshot's views to enumerate (foreach: no allocation - a struct enumerator, as the dictionary's values had).
    /// (Not the array itself: a var of a mod type array is banned in game, VRS1001 - and it could be written to.)</summary>
    public readonly struct GridViews
    {
        readonly GridView[] _a;
        public GridViews(GridView[] a) { _a = a; }
        public int Count => _a.Length;
        public GridView this[int i] => _a[i];
        public Enumerator GetEnumerator() => new Enumerator(_a);
        public struct Enumerator
        {
            readonly GridView[] _a; int _i;
            public Enumerator(GridView[] a) { _a = a; _i = -1; }
            public bool MoveNext() => ++_i < _a.Length;
            public GridView Current => _a[_i];
        }
    }
    /// <summary>One published snapshot: its views (in GridMembers.All's order, as the dictionary's values were) and id -> index.</summary>
    sealed class ViewSnap { public GridView[] Views; public Dictionary<long, int> Index; }
    static readonly ViewSnap EmptyViews = new ViewSnap { Views = new GridView[0], Index = new Dictionary<long, int>() };
    private static ViewSnap _views = EmptyViews;

    /// <summary>A server grid as of the last snapshot (every 5 server ticks).</summary>
    public static bool View(long id, out GridView v)
    {
        var s = System.Threading.Volatile.Read(ref _views);
        if (s.Index.TryGetValue(id, out int i)) { v = s.Views[i]; return true; }
        v = null; return false;
    }

    // THE VIEWS: published whole and never changed after (readers enumerate them across FramesLock waits - Contacts, Targets:
    // recycled dictionaries could be cleared under them, a review, 2026-10-14). The scratch sets are kept (a profile: four
    // new ones per build).
    // (2026-10-15, a profile: still 0.68 MB/s - a new dictionary and a new view per grid, 12 times a second. Now a snapshot is
    //  an array and an id -> index map; the map is kept while the grids are the same ones in the same order, and a grid whose
    //  view would read exactly as the last one keeps that object - a view is never changed once published, so sharing it is
    //  safe. Nothing changed at all: the last snapshot stays.)
    static readonly Dictionary<Entity, long> _bvByEntity = new Dictionary<Entity, long>();
    static readonly HashSet<Keen.VRage.DCS.Components.Entity> _bvJoined = new HashSet<Keen.VRage.DCS.Components.Entity>();
    static readonly HashSet<long> _bvHolding = new HashSet<long>();
    static readonly Dictionary<long, Vector3D> _bvTrueRel = new Dictionary<long, Vector3D>();
    static readonly List<GridView> _bvViews = new List<GridView>();
    static readonly List<SensorView> _bvSens = new List<SensorView>();
    static readonly List<TelescopeComponent> _bvScopes = new List<TelescopeComponent>();
    static readonly List<RadarComponent> _bvRadars = new List<RadarComponent>();
    /// <summary>The server seat the player sat in at the last snapshot: asked first (every seat of the world was scanned for
    /// it, 12 times a second).</summary>
    static Entity _bvSeat;

    static bool SameView(GridView o, long id, Vector3D pos, bool hasBox, BoundingBoxD box, bool dyn, string name, double mass,
        Keen.Game2.Simulation.GameSystems.Ownership.IdentityId owner, bool hasOwner, bool enc, bool holding, bool joined, bool hasTr, Vector3D tr)
        => o.Id == id && o.Position.X == pos.X && o.Position.Y == pos.Y && o.Position.Z == pos.Z && o.HasBox == hasBox
           && (!hasBox || (o.Box.Min.X == box.Min.X && o.Box.Min.Y == box.Min.Y && o.Box.Min.Z == box.Min.Z
                           && o.Box.Max.X == box.Max.X && o.Box.Max.Y == box.Max.Y && o.Box.Max.Z == box.Max.Z))
           && o.Dynamic == dyn && o.Name == name && o.Mass == mass && o.HasOwner == hasOwner && o.Owner.Equals(owner) && o.Encounter == enc
           && o.Holding == holding && o.AnchorJoined == joined && o.HasTrueRel == hasTr
           && (!hasTr || (o.TrueRel.X == tr.X && o.TrueRel.Y == tr.Y && o.TrueRel.Z == tr.Z));

    static bool EntityAlive(Entity e)
    {
        if (e == null) return false;
        var d = e.Data; var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }

    private static void BuildViews()
    {
        var prev = System.Threading.Volatile.Read(ref _views);
        var views = _bvViews; views.Clear();
        var byEntity = _bvByEntity; byEntity.Clear();
        // what is joined to each frame's anchor (the joint search the member loop keeps, once a second per anchor)
        var joinedAll = _bvJoined; joinedAll.Clear();
        var holding = _bvHolding; holding.Clear();
        var trueRel = _bvTrueRel; trueRel.Clear();
        lock (FramesLock)   // (the registry and these sets change on the client thread too)
        {
            foreach (var kv in _joinedOf)
            {
                if (kv.Value.set == null || Wall() - kv.Value.at > 3.0) continue;   // (a stale set: the anchor lost its joints since)
                var af = SystemHost.Frames?.FindByMember(kv.Key);
                if (af == null || af.AnchorEntityId != kv.Key) continue;
                foreach (var je in kv.Value.set) joinedAll.Add(je);
            }
            foreach (long hid in Holding.Keys) holding.Add(hid);
            foreach (var kv in _trueRel) trueRel[kv.Key] = kv.Value;
        }
        bool changed = false;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || g.Entity == null) continue;
            Vector3D pos = GridMembers.Position(g); bool dyn = GridMembers.IsDynamic(g); string name = g.DisplayName; double mass = GridMembers.Mass(g);
            bool hold = holding.Contains(g.Id), joined = joinedAll.Contains(g.Entity);
            bool hasTr = trueRel.TryGetValue(g.Id, out var tr);
            // (TryGet: no exception - ~200 ms each in SE2 - for a grid whose box is not set yet, every 5 ticks)
            var tb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data);
            Keen.Game2.Simulation.GameSystems.Ownership.IdentityId owner = default; bool hasOwner = false, enc = false;
            try
            {
                var own = g.Session?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent>();
                if (own != null) { owner = own.TryGetOwnerOf(g.Entity); hasOwner = true; }
                enc = EncounterFrames.IsEncounterGrid(g);
            }
            catch { }
            BoundingBoxD box = tb.HasValue ? tb.Value : default;
            // (the last snapshot's view of this grid: at the same place in the array when nothing came or went)
            int vi = views.Count;
            GridView v = vi < prev.Views.Length && prev.Views[vi].Id == g.Id ? prev.Views[vi]
                       : prev.Index.TryGetValue(g.Id, out int pi) ? prev.Views[pi] : null;
            if (v == null || !SameView(v, g.Id, pos, tb.HasValue, box, dyn, name, mass, owner, hasOwner, enc, hold, joined, hasTr, tr))
            {
                v = new GridView { Id = g.Id, Position = pos, Dynamic = dyn, Name = name, Mass = mass, Holding = hold, AnchorJoined = joined };
                if (hasTr) { v.TrueRel = tr; v.HasTrueRel = true; }
                if (tb.HasValue) { v.Box = tb.Value; v.HasBox = true; }
                v.Owner = owner; v.HasOwner = hasOwner; v.Encounter = enc;
            }
            if (vi >= prev.Views.Length || !ReferenceEquals(prev.Views[vi], v)) changed = true;
            views.Add(v);
            byEntity[g.Entity] = g.Id;
        }
        if (views.Count != prev.Views.Length) changed = true;
        if (changed)
        {
            // (the map kept while the ids are the same, in the same order: the arrays index alike)
            bool sameIds = views.Count == prev.Views.Length;
            for (int i = 0; sameIds && i < views.Count; i++) sameIds = views[i].Id == prev.Views[i].Id;
            Dictionary<long, int> index = prev.Index;
            if (!sameIds) { index = new Dictionary<long, int>(views.Count); for (int i = 0; i < views.Count; i++) index[views[i].Id] = i; }
            System.Threading.Volatile.Write(ref _views, new ViewSnap { Views = views.ToArray(), Index = index });
        }
        views.Clear();
        // the player's seat grid, by the server's own seat (its pilot the server character): the client pairs by this id
        long seatGrid = 0;
        try
        {
            var ss = ServerPlanetBeacon.ServerSession; var sch = ss != null ? PlayerCache.Of(ss) : null;
            if (sch != null && sch.Data.Has<Keen.VRage.Core.Game.Components.EntityParentData>())
            {
                // (the last seat first - still alive, its pilot still the player: the scan over every seat found that one)
                Entity found = null;
                var last = _bvSeat;
                if (EntityAlive(last) && (last.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == sch) found = last;
                else
                    foreach (var se in ss.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>())
                    {
                        if (!((se.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat)?.Pilot == sch)) continue;
                        found = se;
                        break;
                    }
                _bvSeat = found;
                if (found != null)
                {
                    var ge = found.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;
                    if (ge != null && byEntity.TryGetValue(ge, out long gid)) seatGrid = gid;
                }
            }
            else _bvSeat = null;
        }
        catch { _bvSeat = null; }
        System.Threading.Interlocked.Exchange(ref PlayerSeatGrid, seatGrid);
        // the sensor blocks (their grid by its top entity) - published anew only when one changed (a new list per build before)
        var sens = _bvSens; sens.Clear();
        bool sensOk = true;
        try
        {
            var scopes = _bvScopes; scopes.Clear(); lock (SensorBlocks.Telescopes) scopes.AddRange(SensorBlocks.Telescopes);
            var radars = _bvRadars; radars.Clear(); lock (SensorBlocks.Radars) radars.AddRange(SensorBlocks.Radars);
            foreach (var c in scopes)
            {
                var top = c.Entity?.GetTopLevelParent();
                if (top != null && byEntity.TryGetValue(top, out long gid))
                    sens.Add(new SensorView { Grid = gid, Position = c.Entity.Data.GetWorldTransform().Position, Radar = false, Working = c.Working });
            }
            foreach (var c in radars)
            {
                var top = c.Entity?.GetTopLevelParent();
                if (top != null && byEntity.TryGetValue(top, out long gid))
                    sens.Add(new SensorView { Grid = gid, Position = c.Entity.Data.GetWorldTransform().Position, Radar = true, Working = c.Working, Power = c.Power });
            }
        }
        catch { sensOk = false; }
        _bvScopes.Clear(); _bvRadars.Clear();
        var was = System.Threading.Volatile.Read(ref _sensorViews);
        bool sameSens = sensOk && was.Count == sens.Count;
        for (int i = 0; sameSens && i < sens.Count; i++)
        {
            SensorView x = was[i], y = sens[i];
            sameSens = x.Grid == y.Grid && x.Position.X == y.Position.X && x.Position.Y == y.Position.Y && x.Position.Z == y.Position.Z
                       && x.Radar == y.Radar && x.Working == y.Working && x.Power == y.Power;
        }
        // (a read that failed part way publishes what it had, as before)
        if (!sameSens) System.Threading.Volatile.Write(ref _sensorViews, new List<SensorView>(sens));
        sens.Clear();
    }

    /// <summary>The client's StaticAnchorOf: from the snapshot (StaticAnchorOf itself reads the server's entities).</summary>
    public static bool StaticAnchorView(ProximityFrame f, out Vector3D pos)
    {
        pos = f.BerthCenter;
        if (f.AnchorEntityId == AsteroidAnchorId) return true;
        if (!GridMembers.IsGridId(f.AnchorEntityId) || !View(f.AnchorEntityId, out var v) || v.Dynamic) return false;
        pos = v.Position;
        return true;
    }

    /// <summary>The server grids' positions, every tick (the client reads them: never the server entities).</summary>
    private static void RefreshGridPositions()
    {
        try
        {
            var grids = GridMembers.All();
            lock (GridPositions) foreach (var gg in grids) if (gg.IsServer) GridPositions[gg.Id] = GridMembers.Position(gg);
        }
        catch { }
    }

    static readonly List<OrbitalGridComponent> _snapGrids = new List<OrbitalGridComponent>();
    [ThreadStatic] static Vector3D _snapP;
    static System.Text.StringBuilder _snapSb;   // (taken whole by the builder - Interlocked - never shared while in use)
    static readonly Comparison<OrbitalGridComponent> _snapByDistance =
        (x, y) => (GridMembers.Position(x) - _snapP).LengthSquared().CompareTo((GridMembers.Position(y) - _snapP).LengthSquared());
    private static void BuildSnapshot()
    {
        // (the positions every time, the text only when wanted - GridSnapshot)
        try { var every = GridMembers.All(); lock (GridPositions) foreach (var gg in every) if (gg.IsServer) GridPositions[gg.Id] = GridMembers.Position(gg); }
        catch { }
        double wallNow = Wall();
        if (!(wallNow - System.Threading.Volatile.Read(ref _snapAsked) < SnapshotAskedSeconds) && wallNow - _snapBuilt < SnapshotIdleSeconds) return;
        _snapBuilt = wallNow;
        try
        {
            var grids = _snapGrids; grids.Clear(); grids.AddRange(GridMembers.All());
            grids.RemoveAll(g => !g.IsServer);
            Vector3D p = PlayerPosition;
            // (the comparison made once, its point per thread; the builder kept per thread and cleared: the text is the same,
            //  without a closure, a delegate and the builder's chunks per build - 124 KB/s in a profile, 2026-10-15)
            _snapP = p;
            grids.Sort(_snapByDistance);
            var sb = System.Threading.Interlocked.Exchange(ref _snapSb, null) ?? new System.Text.StringBuilder();
            sb.Clear();
            sb.AppendLine($"grids {grids.Count} server-side (nearest to player first)");
            for (int i = 0; i < Math.Min(8, grids.Count); i++)
            {
                var g = grids[i];
                ProximityFrame fr;
                lock (FramesLock) fr = SystemHost.Frames?.FindByMember(g.Id);
                sb.AppendLine($"  grid {g.Id} '{g.DisplayName}' d={(GridMembers.Position(g) - p).Length() / 1000:F2}km " +
                              $"r={CellR(GridMembers.Position(g))} hs={_gridHighSpeed.ContainsKey(g.Id)} " +
                              $"dyn={GridMembers.IsDynamic(g)} m={GridMembers.Mass(g):F0}kg v={GridMembers.Velocity(g).Length():F1} " +
                              $"frame={(fr != null ? fr.Id + (fr.AnchorEntityId == g.Id ? "(anchor)" : "") : "-")}");
            }
            lock (FramesLock)
            {
                if (SystemHost.Frames != null)
                    foreach (var f in SystemHost.Frames.Frames)
                    {
                        sb.Append($"  frame #{f.Id} anchor={f.AnchorEntityId} members:");
                        foreach (long id in f.Members)
                        {
                            var g = GridMembers.IsGridId(id) ? GridMembers.Get(id) : null;
                            if (g == null) { sb.Append($" {id}(" + (GridMembers.IsGridId(id) ? "gone" : "char") + ")"); continue; }
                            sb.Append($" {id}(srv={g.IsServer} dyn={GridMembers.IsDynamic(g)} dB={(GridMembers.Position(g) - f.BerthCenter).Length() / 1000:F2}km)");
                        }
                        sb.AppendLine();
                    }
            }
            sb.AppendLine($"  serverPlayer={ServerPlanetBeacon.Fmt(PlayerPosition)} gridHS={_gridHighSpeed.Count}");
            _gridSnapshot = sb.ToString();
            sb.Clear(); System.Threading.Volatile.Write(ref _snapSb, sb);   // (handed back: an overlapping tick took none, or made its own)
            grids.Clear();
        }
        catch (Exception e) { _gridSnapshot = "grids ? " + e.Message; _snapGrids.Clear(); }
    }

    private static string CellR(Vector3D p)
    {
        var reg = SystemHost.Registry;
        if (reg != null && VoxelBerthRegistry.TryCellContaining(p, reg, out string b, out Vector3D c)) return $"{(p - c).Length() / 1000:F1}km({b})";
        return "-";
    }

    private static void Event(string s)
    {
        FrameHost.LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-FRAME] " + s);
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}

/// <summary>Ticks per second over the last second (harness diagnostics).</summary>
/// <summary>What the mod's own code costs per frame (ms): the average and the worst over the last full second.</summary>
public sealed class ModCost
{
    public static readonly ModCost Client = new ModCost(), Server = new ModCost(), Map = new ModCost();
    // (kept in Stopwatch ticks and added with Interlocked, no lock: every planet's beacon job stops Server at once, in parallel,
    //  and waited on each other's lock - ModCost.Stop was 1.4 ms/s in a profile, 2026-10-15. The second that ends is published
    //  by the one caller whose exchange moves the window on; a sample racing it lands in the next second's average)
    private long _windowStart, _sumTicks, _maxTicks; private int _n;
    public volatile float AvgMs, MaxMs, PeakMs;   // (Peak: the worst frame ever, for one-off stalls)
    public static long Start()
    {
        if (FrameHost.AllocWatch) _allocMark = GC.GetAllocatedBytesForCurrentThread();
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }
    // (DEV, allocs on: a named section's bytes since the last Start - the map draws on one thread)
    [ThreadStatic] static long _allocMark;   // (per thread: the server's Start must not move the map's mark)
    string _name, _key;   // (the allocation watch's key, made once)
    public void Stop(long start)
    {
        if (_name != null && FrameHost.AllocWatch) FrameHost.AllocAdd(_key ??= "map:" + _name, GC.GetAllocatedBytesForCurrentThread() - _allocMark);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long d = now - start;
        if (System.Threading.Volatile.Read(ref _windowStart) == 0) System.Threading.Interlocked.CompareExchange(ref _windowStart, now, 0L);
        System.Threading.Interlocked.Add(ref _sumTicks, d); System.Threading.Interlocked.Increment(ref _n);
        long mx = System.Threading.Volatile.Read(ref _maxTicks);
        while (d > mx) { long seen = System.Threading.Interlocked.CompareExchange(ref _maxTicks, d, mx); if (seen == mx) break; mx = seen; }
        double ms = d * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > PeakMs) PeakMs = (float)ms;
        long ws = System.Threading.Volatile.Read(ref _windowStart);
        if (now - ws >= System.Diagnostics.Stopwatch.Frequency && System.Threading.Interlocked.CompareExchange(ref _windowStart, now, ws) == ws)
        {
            long sum = System.Threading.Interlocked.Exchange(ref _sumTicks, 0L), max = System.Threading.Interlocked.Exchange(ref _maxTicks, 0L);
            int n = System.Threading.Interlocked.Exchange(ref _n, 0);
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (n > 0) AvgMs = (float)(sum * toMs / n);
            MaxMs = (float)(max * toMs);
        }
    }
    public override string ToString() => $"{AvgMs:F2}/{MaxMs:F1}/{PeakMs:F0}";

    /// <summary>Named parts of a costly path (the map's draw), for finding where its time goes.</summary>
    public static readonly Dictionary<string, ModCost> Sections = new Dictionary<string, ModCost>();
    // (a known name read from a copy of Sections without the lock - the map asks ~30 a frame; the copy is replaced whole, under
    //  the lock, when a name is added: the same ModCost per name as ever)
    static Dictionary<string, ModCost> _sectionsRead = new Dictionary<string, ModCost>();
    public static ModCost Sec(string name)
    {
        if (System.Threading.Volatile.Read(ref _sectionsRead).TryGetValue(name, out var known)) return known;
        lock (Sections)
        {
            if (!Sections.TryGetValue(name, out var c))
            {
                Sections[name] = c = new ModCost { _name = name };
                System.Threading.Volatile.Write(ref _sectionsRead, new Dictionary<string, ModCost>(Sections));
            }
            return c;
        }
    }
    public static string SectionList()
    {
        var sb = new System.Text.StringBuilder();
        lock (Sections) foreach (var kv in Sections) sb.Append($"{kv.Key} {kv.Value}  ");
        return sb.ToString();
    }
}

public sealed class TickRate
{
    public static readonly TickRate Client = new TickRate();
    public static readonly TickRate Server = new TickRate();
    /// <summary>Client frames that actually draw (after the once-per-frame dedup).</summary>
    public static readonly TickRate Draw = new TickRate();
    private long _windowStart, _last; private int _n; private double _worst, _sum, _lastPause;
    /// <summary>Server tick gaps longer than this are logged ([HITCH]).</summary>
    public static double HitchLogMs = 150;
    public volatile float PerSecond;
    /// <summary>The longest gap between two ticks in the last full second (ms): a hitch shows here.</summary>
    public volatile float WorstMs, MeanMs;
    public void Count()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        if (_last != 0)
        {
            double gap = (now - _last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; _sum += gap; if (gap > _worst) _worst = gap;
            // (the server's hitches, each one, logged with the garbage collection pause during it: to line them up
            // with whatever else the log says happened then)
            if (ReferenceEquals(this, Server))
            {
                double pause = System.GC.GetTotalPauseDuration().TotalMilliseconds;
                if (gap > HitchLogMs)
                    Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[HITCH] server tick gap {gap:F0} ms (GC pause in it {pause - _lastPause:F0} ms, gen0/1/2 {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}/{System.GC.CollectionCount(2)})");
                _lastPause = pause;
            }
        }
        _last = now;
        _n++;
        double el = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (el >= 1.0) { PerSecond = (float)(_n / el); WorstMs = (float)_worst; MeanMs = (float)(_sum / Math.Max(1, _n)); _n = 0; _worst = 0; _sum = 0; _windowStart = now; }
    }
}
