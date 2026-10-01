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
    public const double PinTolerance = 200.0;    // m: re-pin the anchor grid (and shift the frame) past this
    public const double SlotRadius = 20000.0;    // m: SE1 FrameManager.SlotRadius (split threshold)
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

    /// <summary>The warp each member's velocity was last scaled for.</summary>
    private static readonly Dictionary<long, double> _gridN = new Dictionary<long, double>();

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
        id = 0; pos = default;
        foreach (long m in f.Members)
        {
            if (!GridMembers.IsGridId(m)) continue;
            var g = GridMembers.Get(m);
            // Only where the frame is: a static member elsewhere (listed by an older rule) anchors nothing.
            double reach = f.AnchorEntityId == m ? CaptureRadius : CaptureEnterRadius;   // hysteresis: in at 8 km, out at 10
            if (g != null && g.IsServer && !GridMembers.IsDynamic(g) && (GridMembers.Position(g) - f.BerthCenter).Length() <= reach)
            { id = m; pos = GridMembers.Position(g); return true; }
        }
        if (AsteroidBridge.NearestAsteroid(f.BerthCenter, f.AnchorEntityId == AsteroidAnchorId ? CaptureRadius : CaptureEnterRadius, out pos)) { id = AsteroidAnchorId; return true; }
        return false;
    }

    /// <summary>Guards the frame registry and frame state across the client and server threads.</summary>
    public static readonly object FramesLock = new object();

    /// <summary>Frame id -> the frame's acceleration A (m/s²) from the last fold (published by the anchor owner).</summary>
    public static readonly Dictionary<long, Vector3D> AnchorAccel = new Dictionary<long, Vector3D>();

    private static long _lastTickStamp;
    private static DateTime _lastGameTime;
    private static int _tick;

    /// <summary>Snapshot for the harness status (built on the server thread).</summary>
    public static volatile string GridSnapshot = "";

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

    /// <summary>Grid HighSpeed (analytic, like the player's): grid id -> (body, elements).</summary>
    private static readonly Dictionary<long, (string body, KeplerianElements el)> _gridHighSpeed =
        new Dictionary<long, (string, KeplerianElements)>();

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (now - _lastTickStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_lastTickStamp != 0 && dt < 0.004) return; // once per frame
        _lastTickStamp = now;
        TickRate.Server.Count();
        SavedState.ApplyRadarSettings();
        lock (FramesLock) SpawnGuard.Tick(AsteroidBridge.Generator(session));   // no encounters on a planet's border (before they materialize); reads frames
        AsteroidBridge.Tick(session);   // no procedural asteroids, ever (encounters and our own system place them)
        // Physics runs on game time (it slows and pauses with the game), so the tidal velocity
        // changes must use game-time dt too; the wall-clock dt above only gates once-per-frame.
        try
        {
            var gt = session.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>();
            if (gt != null)
            {
                DateTime g = gt.CurrentGameTime;
                double gdt = _lastGameTime == default ? 0 : (g - _lastGameTime).TotalSeconds;
                _lastGameTime = g;
                if (gdt <= 0) return;   // paused
                dt = gdt;
            }
        }
        catch { }
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60.0;
        _tick++;

        var chars = new List<Entity>();
        if (session.TryFillAliveCharacters(chars) && chars.Count > 0)
            PlayerPosition = chars[0].Data.GetWorldTransform().Position;

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
            lock (FramesLock)
            {
                while (Attach.TryDequeue(out var req)) DoAttach(req);
                while (GridOrbit.TryDequeue(out var go)) DoGridOrbit(go);
                while (GridDamp.TryDequeue(out var gd)) DoGridDamp(gd.id, gd.on);
                while (GridLaunch.TryDequeue(out var gl)) DoGridLaunch(gl.id, gl.altKm);
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
                EncounterFrames.ServerTick(session, _tick);
                DevFlight.ServerTick();
                DevFlight.ServerCommandTick();
                var frames = new List<ProximityFrame>(SystemHost.Frames.Frames);
                EntryHost.ServerTick(frames, SystemHost.Now);   // reentry (the braking band, an airless border's clamp): before arrivals
                foreach (var f in frames) UpdateGridFrame(f, dt);
                StepGridHighSpeed();
                if (_tick % 10 == 0) StowLoneGrids();
                ApplyFictitious(dt);
                if (_tick % MergeScreenInterval == 0) ScreenMerges(MergeScreenInterval * dt);
            }
        }

        if (_tick % 30 == 0) BuildSnapshot();
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
            if (!MoveGrid(g, req.Berth + (inCell ? chart.ToInertial(p - req.RefPos) : p - req.RefPos), v - req.RefVel, out var withA))
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
            Vector3D pos = GridMembers.Position(g);
            if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
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
            if (GridMembers.IsConstrained(g) && !GridMembers.MoveSet(g, new List<Entity>(), out _)) continue;   // locked to a base: stays
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
            var elBody = CaptureMath.CaptureElements(new StateVector(rel, vel), node.Mu, t);
            if (d < keep && (!IsFinite(elBody.SemiMajorAxis) ||
                             !FrameRails.EscapesShell(elBody, t, shell, keep, FrameHost.MaterializeLead))) continue;

            StateVector borg = node.OriginInRoot(t);
            Vector3D cel = borg.Position + rel;
            GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
            StateVector porg = parent.OriginInRoot(t);
            var el = CaptureMath.CaptureElements(new StateVector(cel - porg.Position, borg.Velocity + vel - porg.Velocity), parent.Mu, t);
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) continue;
            var frame = SystemHost.Frames.CreateFrame(parent.Name, el, g.Id);
            if (frame == null) continue;
            _gridHighSpeed.Remove(g.Id);
            if (!MoveGrid(g, frame.BerthCenter, Vector3D.Zero, out var withS)) { SystemHost.Frames.Dissolve(frame.Id); continue; }
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
        if (!MoveGrid(g, f.BerthCenter, Vector3D.Zero)) { SystemHost.Frames.Dissolve(f.Id); return; }
        Event($"gridorbit: grid {g.Id} '{g.DisplayName}' -> frame #{f.Id} about {r.Body} a={r.El.SemiMajorAxis / 1000:F1} km e={r.El.Eccentricity:F3} slot={f.BerthSlotId}");
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
    private static void ScreenMerges(double dt)
    {
        double t = SystemHost.Now;
        var frames = new List<ProximityFrame>(SystemHost.Frames.Frames);
        double er = Rendezvous.EnterRangeMeters;
        for (int a = 0; a < frames.Count; a++)
        {
            var fa = frames[a];
            if (SystemHost.Frames.Get(fa.Id) == null || !Finite(fa.Elements)) continue;
            bool bandA = RadialBand(fa.Elements, out double peA, out double apA);
            for (int b = a + 1; b < frames.Count; b++)
            {
                var fb = frames[b];
                if (SystemHost.Frames.Get(fb.Id) == null || !Finite(fb.Elements)) continue;
                if (fa.ParentBodyName != fb.ParentBodyName) continue;
                if (!EncounterFrames.ShouldScreen(fa, fb)) continue;
                if (bandA && RadialBand(fb.Elements, out double peB, out double apB) && (apA + er < peB || apB + er < peA)) continue;
                if (!FrameRails.CanMutateAt(fa.Elements, t) || !FrameRails.CanMutateAt(fb.Elements, t)) continue;

                var ev = ClosestApproach.Find(fa.Elements, fb.Elements, t, TcaHorizonSeconds, TcaSamples);
                bool ok = ev.Found && IsFinite(ev.MissDistance) && IsFinite(ev.RelativeSpeed);
                double miss = ok ? ev.MissDistance : double.PositiveInfinity;
                double rel = ok ? ev.RelativeSpeed : double.PositiveInfinity;
                var key = fa.Id < fb.Id ? (fa.Id, fb.Id) : (fb.Id, fa.Id);
                if (!_trackers.TryGetValue(key, out var tr)) { tr = new RendezvousTracker(); _trackers[key] = tr; }
                bool merged = miss < er && rel < Rendezvous.EnterRelSpeedMps
                    ? tr.Update(miss, rel, dt, Rendezvous)
                    : tr.Update(double.PositiveInfinity, double.PositiveInfinity, dt, Rendezvous);
                if (!merged) continue;

                var sa = OrbitPropagation.StateAt(fa.Elements, t);
                var sb = OrbitPropagation.StateAt(fb.Elements, t);
                if (!IsFinite(sa.Position) || !IsFinite(sb.Position) || !IsFinite(sa.Velocity) || !IsFinite(sb.Velocity)) continue;
                if ((sb.Position - sa.Position).Length() >= er) continue;
                if ((sb.Velocity - sa.Velocity).Length() >= FrameHost.SpeedCap) continue;
                ExecuteMerge(fa, fb, sa, sb);
                _trackers.Remove(key);
            }
        }
        var dead = new List<(long, long)>();
        foreach (var k in _trackers.Keys) if (SystemHost.Frames.Get(k.Item1) == null || SystemHost.Frames.Get(k.Item2) == null) dead.Add(k);
        foreach (var k in dead) _trackers.Remove(k);
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
        int n = 0;
        foreach (long id in new List<long>(inc.Members))
        {
            if (!GridMembers.IsGridId(id)) continue;
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer) continue;
            MoveGrid(g, GridMembers.Position(g) + translation, null, dVel);
            n++;
        }
        FrameHost.RequestMerge(inc.Id, translation, dVel);   // the player, if a member of the incomer
        long hostId = host.Id, incId = inc.Id;
        SystemHost.Frames.MergeInto(host, inc);
        AnchorAccel.Remove(incId);
        Event($"MERGE frame #{incId} -> #{hostId}: {n} grid(s) moved, sep {dPos.Length() / 1000:F2} km, |dv| {dVel.Length():F1} m/s (host now {host.Members.Count} members)");
    }

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

    private static void UpdateGridFrame(ProximityFrame f, double dt)
    {
        var reg = SystemHost.Registry;
        var parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;

        // Grid members (server copies, still alive and dynamic).
        var grids = new List<OrbitalGridComponent>();
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id)) continue;
            var g = GridMembers.Get(id);
            if (g != null && g.IsServer && GridMembers.IsDynamic(g)) grids.Add(g);
        }
        // A static anchor (a static grid, or an asteroid) comes first: nothing is pinned or drained, the
        // rest (the player too: a rider, not the anchor) move about it normally.
        bool isStatic = false; Vector3D staticPos = default;
        // A fresh frame that finds a rock (or a static grid it does not own) in its space landed in a dirty
        // slot (the world only streams content near someone, so nothing could see it when the slot was
        // handed out): it moves to a fresh slot, you and its ships with it; the dirty slot stays used.
        if (!_born.ContainsKey(f.Id)) _born[f.Id] = Wall();
        // (Not an encounter's rock: an encounter spawns its rock with its grids, wherever you are; only a rock
        // seen for 5 s with no encounter grid near it is taken as left in the slot.)
        bool dirty = false;
        long dsid = 0; Vector3D dpos = default;
        if (!f.IsEncounter && f.BerthSlotId >= 0 && Wall() - _born[f.Id] < 30 && StaticAnchor(f, out dsid, out dpos) && !f.HasMember(dsid))
        {
            bool encounterNear = false;
            // (A fresh encounter's grids join a frame within seconds (spawns settle 2 s); grids next to the rock
            // in no frame at all are leftovers from a save, as the rock is.)
            foreach (var o in GridMembers.All())
                if (o.IsServer && EncounterFrames.IsEncounterGrid(o) && (GridMembers.Position(o) - dpos).Length() < 3000
                    && SystemHost.Frames.FindByMember(o.Id) != null && !f.HasMember(o.Id)) { encounterNear = true; break; }
            if (!encounterNear)
            {
                if (!_dirtySince.ContainsKey(f.Id)) _dirtySince[f.Id] = Wall();
                dirty = Wall() - _dirtySince[f.Id] >= 5;
            }
            else _dirtySince.Remove(f.Id);
        }
        else _dirtySince.Remove(f.Id);
        if (dirty)
        {
            _dirtySince.Remove(f.Id);
            var alloc = SystemHost.Frames.Allocator;
            int slot = alloc.Allocate(out Vector3D fresh);
            Vector3D shift = fresh - f.BerthCenter;
            foreach (long m in f.Members)
                if (GridMembers.IsGridId(m) && GridMembers.Get(m) is OrbitalGridComponent mg && mg.IsServer && GridMembers.IsDynamic(mg))
                    MoveGrid(mg, GridMembers.Position(mg) + shift, null);
            FrameHost.RequestShift(f.Id, shift);
            Event($"frame #{f.Id}: its slot {f.BerthSlotId} holds {(dsid == AsteroidAnchorId ? "a rock" : $"grid {dsid}")}: moved to clear slot {slot} (the old one stays used)");
            f.BerthSlotId = slot; f.BerthCenter = fresh;
            return;
        }
        // A young frame is not captured by a rock or grid it does not own before the dirty-slot check has had
        // its 5 s (it was, then moved off the slot with its orbit rebased onto the rock).
        bool youngForeign = !f.IsEncounter && Wall() - _born[f.Id] < 12 && StaticAnchor(f, out long ysid, out _) && !f.HasMember(ysid) && f.AnchorEntityId != ysid;   // (rocks show on the bridge's 2 s scans)
        if (!youngForeign && !f.IsEncounter && StaticAnchor(f, out long sid, out staticPos))
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
        else if (!f.IsEncounter && (f.AnchorEntityId == AsteroidAnchorId || (GridMembers.IsGridId(f.AnchorEntityId) && !grids.Exists(g => g.Id == f.AnchorEntityId))))
        {
            // The static anchor (or a grid anchor) is gone: the player anchors again if nothing else can.
            if (grids.Count == 0 && f.HasMember(FrameHost.PlayerId))
            {
                // The centre moves back onto the player (with the player's orbit), as it moved onto the anchor.
                if (ServerPlanetBeacon.PlayerState(out Vector3D pp, out Vector3D pv) && (pp - f.BerthCenter).Length() > PinTolerance)
                {
                    double tn = SystemHost.Now;
                    StateVector c0 = OrbitPropagation.StateAt(f.Elements, tn);
                    var el = CaptureMath.CaptureElements(new StateVector(c0.Position + (pp - f.BerthCenter), c0.Velocity + pv), f.Elements.Mu, tn);
                    if (IsFinite(el.SemiMajorAxis) && IsFinite(el.MeanMotion)) { f.Elements = el; f.BerthCenter = pp; }
                }
                f.AnchorEntityId = FrameHost.PlayerId;
                Event($"frame #{f.Id}: anchor -> the player");
            }
        }
        if (grids.Count == 0 && !isStatic) return;   // (a static anchor's frame still steps: its riders, its arrival)

        // Anchor election: keep a live grid anchor, else the heaviest PLAYER grid (SE1 ElectAnchor).
        // NPC grids never anchor, and an encounter frame has no anchor at all: its origin is the berth
        // (the site, pinned) and its orbit is its own (a site's ephemeris, a procedural spawn's conic).
        OrbitalGridComponent anchor = null;
        if (!f.IsEncounter && !isStatic)
        {
            foreach (var g in grids) if (g.Id == f.AnchorEntityId && !EncounterFrames.IsNpc(g)) anchor = g;
            bool playerAnchored = f.AnchorEntityId != 0 && !GridMembers.IsGridId(f.AnchorEntityId) && f.HasMember(f.AnchorEntityId);
            if (anchor == null && !playerAnchored)
            {
                double best = -1;
                foreach (var g in grids) { if (EncounterFrames.IsNpc(g)) continue; double m = GridMembers.Mass(g); if (m > best) { best = m; anchor = g; } }
                if (anchor != null)
                {
                    Event($"frame #{f.Id}: anchor -> grid {anchor.Id} '{anchor.DisplayName}' ({best:F0} kg)");
                    f.AnchorEntityId = anchor.Id;
                }
            }
        }

        double t = SystemHost.Now;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D A = Vector3D.Zero;
        Vector3D anchorPos = isStatic ? staticPos : f.BerthCenter;

        if (anchor != null)
        {
            // Anchor owner = server: pin + drain + fold.
            anchorPos = GridMembers.Position(anchor);
            if ((anchorPos - f.BerthCenter).Length() > PinTolerance)
            {
                // Re-pin by shifting the WHOLE frame back (members keep their offsets; no rails change).
                Vector3D shift = f.BerthCenter - anchorPos;
                // (MoveGrid: a jointed grid's wheels, rotor parts and docked ships must come too, before the physics step)
                foreach (var g in grids) MoveGrid(g, GridMembers.Position(g) + shift, null);
                FrameHost.RequestShift(f.Id, shift);
                anchorPos = f.BerthCenter;
            }
            Vector3D vA = GridMembers.Velocity(anchor);
            if (IsFinite(vA))
            {
                GridMembers.SetVelocity(anchor, Vector3D.Zero);
                f.PendingDrainDv += vA;
            }
            var fold = FrameRails.FoldDrain(f, t, FrameHost.FoldThreshold, FrameHost.MaxApparentAccel * dt, ref cur, out Vector3D slice);
            if (fold == FrameRails.FoldResult.Folded) A = slice / dt;
            if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;
            AnchorAccel[f.Id] = A;
        }
        else if (!f.IsEncounter && !isStatic && AnchorAccel.TryGetValue(f.Id, out var pa)) A = pa;   // the player anchors (client folds)

        // Other members: CW differential gravity minus the frame acceleration; split beyond the slot.
        // NPC grids are Newtonian inside the frame: no relative-motion terms, and they never split off.
        double mu = f.Elements.Mu;
        Vector3D rA = cur.Position;
        Vector3D gA = Grav(rA, mu);
        // A Lagrange site: its own simple dynamics (EncounterFrames.LagrangeDynamics).
        Func<Vector3D, Vector3D, Vector3D> lag = null;
        if (f.IsEncounter) EncounterFrames.LagrangeDynamics(f.Id, t, out lag);
        // Warp N: forces x N^2 (relative motion N times faster, as the rails); velocities rescaled on a change.
        double N = Math.Max(1.0, SystemHost.Timescale);
        foreach (var g in grids)
        {
            if (g == anchor) continue;
            if (EncounterFrames.IsNpc(g) && !DevNpcRelative) continue;
            Vector3D rRel = GridMembers.Position(g) - anchorPos;
            if (_gridN.TryGetValue(g.Id, out double n0) && Math.Abs(n0 - N) > 1e-9)
                GridMembers.SetVelocity(g, GridMembers.Velocity(g) * (N / n0));
            _gridN[g.Id] = N;
            if (rRel.Length() > (isStatic ? CaptureRadius : SlotRadius))
            {
                _gridN.Remove(g.Id);
                SplitGrid(f, g, cur, rRel, GridMembers.Velocity(g) / N, t);
                continue;
            }
            Vector3D accel = lag != null ? lag(rRel, GridMembers.Velocity(g) / N) : (Grav(rA + rRel, mu) - gA) - A;
            // Station-keeping (dampeners on, and thrust on the side that cancels the pull): no relative force, as
            // SE1 and as a rider with dampeners on. Its dampeners only null its own motion: it holds exactly.
            if (StationKeeping(g, accel * (N * N))) continue;
            if (IsFinite(accel)) GridMembers.AddVelocity(g, accel * (dt * N * N));
        }

        if (f.IsEncounter || (anchor == null && !isStatic)) return;   // encounter frames never arrive; a player-anchored frame arrives client-side
        FrameHost.TryReparent(f, t);
        // A static anchor's frame arrives too (its orbit meets the planet): everything in it drops into
        // the planet's space, static grids placed at their offsets (an asteroid itself stays).
        var all = new List<OrbitalGridComponent>(grids);
        if (isStatic)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent sg && sg.IsServer && !GridMembers.IsDynamic(sg)) all.Add(sg);
        TryMaterializeGrids(f, all, anchor != null ? GridMembers.Position(anchor) : staticPos, t);
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
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, Vector3D addVel = default)
        => MoveGrid(g, target, setVel, out _, addVel);

    /// <summary>As above; with: the other grids that move with it (its joined group), for the caller's frame bookkeeping.</summary>
    public static bool MoveGrid(OrbitalGridComponent g, Vector3D target, Vector3D? setVel, out List<OrbitalGridComponent> with, Vector3D addVel = default)
    {
        with = new List<OrbitalGridComponent>();
        lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) return true;   // (with its group, this tick)
        if (!GridMembers.Finite(target) || !GridMembers.Finite(addVel) || (setVel.HasValue && !GridMembers.Finite(setVel.Value)))
        { GridMembers.NaNRefused++; Event($"grid {g.Id} '{g.DisplayName}' not moved: non-finite target"); return false; }
        if (!GridMembers.IsConstrained(g))
        {
            Vector3D v = setVel ?? GridMembers.Velocity(g) + addVel;
            if (!GridMembers.SetPosition(g, target)) return false;   // (callers must not frame a grid that never got there)
            GridMembers.SetVelocity(g, v);
            return true;
        }
        var set = new List<Entity>();
        if (!GridMembers.MoveSet(g, set, out string why)) { Event($"grid {g.Id} '{g.DisplayName}' not moved: {why}"); return false; }
        Vector3D delta = target - GridMembers.Position(g);
        var byEntity = new Dictionary<Entity, OrbitalGridComponent>();
        foreach (var o in GridMembers.All()) if (o.IsServer && o.Entity != null) byEntity[o.Entity] = o;
        lock (_deferred)
            foreach (var e in set)
            {
                Vector3D v0 = e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
                Vector3D v = setVel ?? v0 + addVel;
                Vector3D p = e.Data.GetWorldTransform().Position + delta;
                if (!GridMembers.Finite(p) || !GridMembers.Finite(v)) continue;   // (never hand Havok a NaN)
                _deferred.Add((e, p, v));
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
    private static long _deferredStamp;

    /// <summary>The queued moves, all in one go (once a frame, whichever beacon's job comes first).</summary>
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
                var wt = e.Data.GetWorldTransform();
                e.Data.SetWorldTransform(new WorldTransform(p, wt.Orientation));
                ref var rb = ref e.Data.TryGetWritePtr<Keen.VRage.Physics.Data.RigidBodyData>();
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in rb)) rb.LinearVelocity = (Vector3)v;
            }
            catch (Exception ex) { Event("deferred move failed: " + ex.Message); }
        }
        lock (GridMembers.Pending) foreach (long id in done) GridMembers.Pending.Remove(id);
        Event($"moved {todo.Count} grid(s) in the teleport phase");
    }

    /// <summary>DEV: put a grid (with its joined group) on a circular orbit this high over the planet it is at.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, double altKm)> GridLaunch = new System.Collections.Concurrent.ConcurrentQueue<(long, double)>();
    static void DoGridLaunch(long id, double altKm)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g == null || !g.IsServer || reg == null) { Event($"gridlaunch: no server grid {id}"); return; }
        Vector3D pos = GridMembers.Position(g);
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) { Event("gridlaunch: not at a planet"); return; }
        var node = reg.Find(body); var def = reg.FindDefinition(body);
        if (node == null || def == null) return;
        double t = SystemHost.Now, r = def.RadiusMeters + altKm * 1000;
        var chart = Chart.Of(body, t);
        Vector3D up = Vector3D.Normalize(chart.ToInertial(pos - cell));
        Vector3D side = Vector3D.Cross(Vector3D.UnitZ, up);
        if (side.LengthSquared() < 1e-9) side = Vector3D.Cross(Vector3D.UnitX, up);
        Vector3D pI = up * r, vI = Vector3D.Normalize(side) * Math.Sqrt(node.Mu / r);
        bool ok = MoveGrid(g, cell + chart.FromInertial(pI), chart.VelFromInertial(pI, vI), out var with);
        Event($"gridlaunch: grid {id} '{g.DisplayName}' to a {altKm:F0} km circular orbit of {body}: {(ok ? $"moving ({with.Count} grid(s) with it)" : "refused")}");
    }

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
        lock (FramesLock) { Holding.Remove(gridId); _gridHighSpeed.Remove(gridId); }
        EncounterFrames.ForgetKind(gridId);
    }

    /// <summary>
    /// A grid holding station (SE1: dampeners + thrust authority): its dampeners on, and thrust on every side
    /// the force that cancels the pull (−m·a, in the grid's own axes) points to. The game's MaxThrustData:
    /// Positive = the push along each +axis, Negative = along each −axis (Thrust6Directions.Clamp).
    /// A ship turned so it cannot push against the pull drifts until it can.
    /// </summary>
    static bool StationKeeping(OrbitalGridComponent g, Vector3D accel)
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
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vRel), f.Elements.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.SplitOff(f, g.Id, f.ParentBodyName, el);
        if (nf == null) return;
        Vector3D p = GridMembers.Position(g);
        if (!MoveGrid(g, nf.BerthCenter, Vector3D.Zero, out var withG)) { SystemHost.Frames.Dissolve(nf.Id); SystemHost.Frames.AddMember(f, g.Id); return; }
        // Joined to others (docked, wheels on a sub-grid): they go with it, into its new frame.
        foreach (var o in withG)
            if (SystemHost.Frames.FindByMember(o.Id) == f) { SystemHost.Frames.RemoveMember(o.Id); SystemHost.Frames.AddMember(nf, o.Id); }
        Event($"SPLIT grid {g.Id} '{g.DisplayName}' from frame #{f.Id} at {rRel.Length() / 1000:F1} km -> frame #{nf.Id} (slot {nf.BerthSlotId})");
    }

    // ───────────────────────────── arrival (grid-anchored frame) ─────────────────────────────

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
        // At or under the cap (reentry's band or an airless border's clamp makes sure); if not, capped here.
        bool capped = speed > FrameHost.SpeedCap;
        double k = capped ? FrameHost.SpeedCap / speed : 1.0;
        foreach (var g in grids)
        {
            Vector3D off = GridMembers.Position(g) - anchorPos;
            Vector3D vRel = GridMembers.Velocity(g);
            Vector3D dest = cell + chart.FromInertial(cel.Position + off);   // berth offsets are inertial
            if (!GridMembers.IsDynamic(g))
            {
                GridMembers.SetPosition(g, dest);
                // A static grid keeps its orbit too (placed each tick along it, as a HighSpeed grid): dropped
                // without its velocity it fell straight down (the game made it dynamic).
                var sel = CaptureMath.CaptureElements(new StateVector(cel.Position + off, cel.Velocity), node.Mu, epoch);
                if (IsFinite(sel.SemiMajorAxis)) _gridHighSpeed[g.Id] = (node.Name, sel);
                continue;
            }
            MoveGrid(g, dest, chart.VelFromInertial(cel.Position + off, cel.Velocity + vRel) * k);
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
    internal static void RestoreGridHighSpeed(long id, string body, KeplerianElements el) => _gridHighSpeed[id] = (body, el);

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
            if (speed < FrameHost.SpeedCap * FrameHost.HighSpeedExitFraction || st.Position.Length() < floor)
            {
                Vector3D v = speed > FrameHost.SpeedCap ? vChart * (FrameHost.SpeedCap / speed) : vChart;
                GridMembers.SetVelocity(g, v);
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

    private static void BuildSnapshot()
    {
        try
        {
            var grids = GridMembers.All();
            grids.RemoveAll(g => !g.IsServer);
            Vector3D p = PlayerPosition;
            grids.Sort((x, y) => (GridMembers.Position(x) - p).LengthSquared().CompareTo((GridMembers.Position(y) - p).LengthSquared()));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"grids {grids.Count} server-side (nearest to player first)");
            lock (GridPositions) foreach (var gg in grids) GridPositions[gg.Id] = GridMembers.Position(gg);
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
            GridSnapshot = sb.ToString();
        }
        catch (Exception e) { GridSnapshot = "grids ? " + e.Message; }
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
    private long _windowStart; private double _sum, _max; private int _n;
    public volatile float AvgMs, MaxMs, PeakMs;   // (Peak: the worst frame ever, for one-off stalls)
    public static long Start() => System.Diagnostics.Stopwatch.GetTimestamp();
    public void Stop(long start)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (this)
        {
            if (_windowStart == 0) _windowStart = now;
            _sum += ms; _n++; if (ms > _max) _max = ms; if (ms > PeakMs) PeakMs = (float)ms;
            if ((now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency >= 1.0)
            { AvgMs = (float)(_sum / _n); MaxMs = (float)_max; _sum = _max = 0; _n = 0; _windowStart = now; }
        }
    }
    public override string ToString() => $"{AvgMs:F2}/{MaxMs:F1}/{PeakMs:F0}";

    /// <summary>Named parts of a costly path (the map's draw), for finding where its time goes.</summary>
    public static readonly Dictionary<string, ModCost> Sections = new Dictionary<string, ModCost>();
    public static ModCost Sec(string name)
    {
        lock (Sections) { if (!Sections.TryGetValue(name, out var c)) Sections[name] = c = new ModCost(); return c; }
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
    private long _windowStart; private int _n;
    public volatile float PerSecond;
    public void Count()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        _n++;
        double el = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (el >= 1.0) { PerSecond = (float)(_n / el); _n = 0; _windowStart = now; }
    }
}
