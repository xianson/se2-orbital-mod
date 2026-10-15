using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
///  - OTHER MEMBERS get the exact differential gravity of the berth (g(anchor + offset) - g(anchor), not the
///    linearised Clohessy-Wiltshire form) minus A, as a velocity change
///    (SE2 has no force accumulator; the engine's own impulse helpers do the same). Characters get the
///    same from the client half. A member beyond the slot radius SPLITS into its own frame.
///  - When the anchor is the player (no grids), the client drains and publishes A; grid members here
///    still get that differential gravity − A.
///  - ARRIVAL for a grid-anchored frame happens here: every grid member is relocated into the planet
///    cell at the crossing state (a dynamic grid's velocity scaled down to the cap; a static grid rides its
///    conic as a grid HighSpeed entry), and the player member is handed to the
///    client with its offset.
/// </summary>
public static partial class ServerFrames
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

    /// <summary>Guards the frame registry and frame state across the client and server threads.</summary>
    public static readonly object FramesLock = new object();

    // Members steered along their own orbits in warp: their true relative velocity, and ticks left after warp ends.
    private static readonly Dictionary<long, Vector3D> _trueRel = new Dictionary<long, Vector3D>();
    private static readonly Dictionary<long, int> _settle = new Dictionary<long, int>();
    private const int SettleTicks = 30;
    /// <summary>The most a member may be carried at in warp (m/s; the world's cap is 1000). The setting Warp.SafetySpeed.</summary>
    internal static double WarpMemberSpeed = 800;
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
    /// <summary>Frame id -> the frame's acceleration A (m/s²) from the last fold (published by the anchor owner).</summary>
    public static readonly Dictionary<long, Vector3D> AnchorAccel = new Dictionary<long, Vector3D>();

    private static long _lastTickStamp;
    private static DateTime _lastGameTime;
    /// <summary>A new world (WorldReset): its game clock starts again (a game time earlier than the last world's read as
    /// paused - no server step until it caught up).</summary>
    internal static void ResetClock() { System.Threading.Interlocked.Exchange(ref _lastTickStamp, 0); _lastGameTime = default; _frameClaim.Reset(); }
    private static int _tick;

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

    // (one step at a time, 2026-10-15: a second planet's job through the 8 ms claim while the first still ran stepped
    //  TickBody alongside it, on the same unlocked lists - "Collection was modified", the views and snapshot interleaved.
    //  A job finding a step running leaves the frame to it)
    static int _stepping;
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _stepping, 1, 0) != 0) return;
        try { TickClaimed(session); }
        finally { System.Threading.Volatile.Write(ref _stepping, 0); }
    }

    // (2026-10-15: claimed by GAME time - FrameClaim, as WarpBill. The 8 ms wall gate let a planet's job starting >8 ms after
    //  the winner run the step's head again in the same frame - SpawnGuard, WarmBerths, AsteroidBridge, the sweeps, the
    //  settings poll, the tick-rate count (the locked step itself then stopped at its "paused" return, game dt 0))
    static readonly SEAerospace.Time.FrameClaim _frameClaim = new SEAerospace.Time.FrameClaim();
    static void TickClaimed(Keen.VRage.Core.Game.Systems.Session session)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = System.Threading.Interlocked.Read(ref _lastTickStamp);
        double dt = (now - last) / (double)System.Diagnostics.Stopwatch.Frequency;
        Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime gt = null;
        try { gt = session?.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>(); } catch { gt = null; }
        if (gt != null)
        {
            // one claim per game frame however late a job starts. The game time standing still (paused): the head - settings
            // poll, sweeps - still runs, at most every 0.1 s of wall time (the locked step stops at its "paused" return)
            if (!_frameClaim.TryClaim(gt.CurrentGameTime.Ticks, 0.25, out _) && last != 0 && dt < 0.1) return;
        }
        // (no game time: the wall gate as before - half a server tick)
        else if (last != 0 && dt < 0.008) return;
        if (System.Threading.Interlocked.CompareExchange(ref _lastTickStamp, now, last) != last) return;
        // (the section timers are per thread: an overlapping second job keeps its own)
        _slow.Clear();
        try { TickBody(session, gt, now, dt); } finally { LastSlow = _slow.ToString(); }
    }

    static void TickBody(Keen.VRage.Core.Game.Systems.Session session, Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime gt, long now, double dt)
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
        // (the positions EVERY tick: a seated player's grid is matched against them - PlayerOrbit. 2026-10-15: every 5th tick
        //  the views were built INSTEAD, and the positions were a tick stale)
        { long _s0 = Sw(); RefreshGridPositions(); if (_tick % 5 == 0) BuildViews(); Sec("views", _s0); }
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

    private static readonly List<ProximityFrame> _tickFrames = new List<ProximityFrame>();
    private static bool HasGrid(List<OrbitalGridComponent> grids, long id) { foreach (var g in grids) if (g.Id == id) return true; return false; }

    /// <summary>A grid left the scene: its per-grid state goes with it.</summary>
    internal static void Forget(long gridId)
    {
        // (all per-grid state: _gridN, _trueRel and _settle outlived their grid - a stale _gridN rescaled a later joiner)
        // (and _joinedOf - its HashSet of entities kept a deleted anchor's joined grids alive - and the invariants' grid sets, 2026-10-15)
        lock (FramesLock) { Holding.Remove(gridId); _gridHighSpeed.Remove(gridId); _gridN.Remove(gridId); _trueRel.Remove(gridId); _settle.Remove(gridId); _vSet.Remove(gridId); _joinedOf.Remove(gridId); _everFramed.Remove(gridId); _unframedSince.Remove(gridId); }
        lock (GridPositions) GridPositions.Remove(gridId);
        EncounterFrames.ForgetKind(gridId);
    }

    /// <summary>A frame leaves the registry (FrameRegistry.Retiring - every Dissolve, a merge's incomer; FramesLock held): its
    /// per-frame state goes with it. (2026-10-15: AnchorAccel was dropped at 4 of the 8 dissolve sites - a stale acceleration
    /// could push a reused frame id; the entry state at none, EntryHost.Forget had no caller.)</summary>
    internal static void FrameRetiring(ProximityFrame f, bool merged)
    {
        long id = f.Id;
        AnchorAccel.Remove(id);
        _rockOf.Remove(id); _rebasePending.Remove(id); _lastSteerNow.Remove(id); _arrivalNoticed.Remove(id); _born.Remove(id); _dirtySince.Remove(id);
        EntryHost.Retire(f, merged);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string CellR(Vector3D p)
    {
        var reg = SystemHost.Registry;
        if (reg != null && VoxelBerthRegistry.TryCellContaining(p, reg, out string b, out Vector3D c)) return $"{(p - c).Length() / 1000:F1}km({b})";
        return "-";
    }

    private static void Event(string s) => FrameHost.Event(s);   // (the same [ORBIT-FRAME] line and LastEvent)

}

