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
                EncounterFrames.ServerTick(session, _tick);
                DevFlight.ServerTick();
                DevFlight.ServerCommandTick();
                var frames = new List<ProximityFrame>(SystemHost.Frames.Frames);
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
            if (GridMembers.IsConstrained(g)) continue;
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
            GridMembers.SetPosition(g, req.Berth + (inCell ? chart.ToInertial(p - req.RefPos) : p - req.RefPos));
            GridMembers.SetVelocity(g, v - req.RefVel);
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
            if (GridMembers.IsConstrained(g)) continue;
            if (EncounterFrames.IsNpc(g)) continue;
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
            GridMembers.SetPosition(g, frame.BerthCenter);
            GridMembers.SetVelocity(g, Vector3D.Zero);
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
        GridMembers.SetPosition(g, f.BerthCenter);
        GridMembers.SetVelocity(g, Vector3D.Zero);
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
            GridMembers.SetPosition(g, GridMembers.Position(g) + translation);
            GridMembers.SetVelocity(g, GridMembers.Velocity(g) + dVel);
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
        if (grids.Count == 0) return;

        // Anchor election: keep a live grid anchor, else the heaviest PLAYER grid (SE1 ElectAnchor).
        // NPC grids never anchor, and an encounter frame has no anchor at all: its origin is the berth
        // (the site, pinned) and its orbit is its own (a site's ephemeris, a procedural spawn's conic).
        OrbitalGridComponent anchor = null;
        if (!f.IsEncounter)
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
        Vector3D anchorPos = f.BerthCenter;

        if (anchor != null)
        {
            // Anchor owner = server: pin + drain + fold.
            anchorPos = GridMembers.Position(anchor);
            if ((anchorPos - f.BerthCenter).Length() > PinTolerance)
            {
                // Re-pin by shifting the WHOLE frame back (members keep their offsets; no rails change).
                Vector3D shift = f.BerthCenter - anchorPos;
                foreach (var g in grids) GridMembers.SetPosition(g, GridMembers.Position(g) + shift);
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
        else if (!f.IsEncounter && AnchorAccel.TryGetValue(f.Id, out var pa)) A = pa;   // the player anchors (client folds)

        // Other members: CW differential gravity minus the frame acceleration; split beyond the slot.
        // NPC grids are Newtonian inside the frame: no relative-motion terms, and they never split off.
        double mu = f.Elements.Mu;
        Vector3D rA = cur.Position;
        Vector3D gA = Grav(rA, mu);
        // A Lagrange site: its own simple dynamics (EncounterFrames.LagrangeDynamics).
        Func<Vector3D, Vector3D> lag = null;
        if (f.IsEncounter) EncounterFrames.LagrangeDynamics(f.Id, t, out lag);
        foreach (var g in grids)
        {
            if (g == anchor) continue;
            if (EncounterFrames.IsNpc(g)) continue;
            Vector3D rRel = GridMembers.Position(g) - anchorPos;
            if (rRel.Length() > SlotRadius)
            {
                SplitGrid(f, g, cur, rRel, GridMembers.Velocity(g), t);
                continue;
            }
            Vector3D accel = lag != null ? lag(rRel) : (Grav(rA + rRel, mu) - gA) - A;
            if (IsFinite(accel)) GridMembers.AddVelocity(g, accel * dt);
        }

        if (f.IsEncounter || anchor == null) return;   // encounter frames never arrive; a player-anchored frame arrives client-side
        FrameHost.TryReparent(f, t);
        TryMaterializeGrids(f, grids, anchor, t);
    }

    /// <summary>SE1 ExecuteSplits: the member becomes its own frame from its celestial state.</summary>
    private static void SplitGrid(ProximityFrame f, OrbitalGridComponent g, StateVector cur, Vector3D rRel, Vector3D vRel, double t)
    {
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vRel), f.Elements.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.SplitOff(f, g.Id, f.ParentBodyName, el);
        if (nf == null) return;
        Vector3D p = GridMembers.Position(g);
        GridMembers.SetPosition(g, nf.BerthCenter);
        GridMembers.SetVelocity(g, Vector3D.Zero);
        Event($"SPLIT grid {g.Id} '{g.DisplayName}' from frame #{f.Id} at {rRel.Length() / 1000:F1} km -> frame #{nf.Id} (slot {nf.BerthSlotId})");
    }

    // ───────────────────────────── arrival (grid-anchored frame) ─────────────────────────────

    private static void TryMaterializeGrids(ProximityFrame f, List<OrbitalGridComponent> grids, OrbitalGridComponent anchor, double t)
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

        Vector3D anchorPos = GridMembers.Position(anchor);
        Chart chart = Chart.Of(node.Name, t);
        Vector3D target = cell + chart.FromInertial(cel.Position);
        double speed = chart.VelFromInertial(cel.Position, cel.Velocity).Length();
        bool hs = speed > FrameHost.SpeedCap;
        foreach (var g in grids)
        {
            Vector3D off = GridMembers.Position(g) - anchorPos;
            Vector3D vRel = GridMembers.Velocity(g);
            GridMembers.SetPosition(g, cell + chart.FromInertial(cel.Position + off));   // berth offsets are inertial
            if (hs)
            {
                GridMembers.SetVelocity(g, Vector3D.Zero);
                var el = CaptureMath.CaptureElements(new StateVector(cel.Position + off, cel.Velocity + vRel), node.Mu, epoch);
                if (IsFinite(el.SemiMajorAxis)) _gridHighSpeed[g.Id] = (node.Name, el);
            }
            else GridMembers.SetVelocity(g, chart.VelFromInertial(cel.Position + off, cel.Velocity + vRel));
        }
        // The player, if a member, arrives with the same offset from the anchor.
        long fid = f.Id;
        FrameHost.RequestArrival(fid, node.Name, target, anchorPos, cel.Position, cel.Velocity, epoch);
        SystemHost.Frames.Dissolve(fid);
        AnchorAccel.Remove(fid);
        Event($"ARRIVE grid frame #{fid} -> {node.Name}: {grids.Count} grid(s), r={cel.Position.Length() / 1000:F1} km |v|={speed:F0} m/s{(hs ? " -> HighSpeed" : "")}");
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
            GridMembers.SetPosition(g, cell + chart.FromInertial(st.Position));
            GridMembers.SetVelocity(g, Vector3D.Zero);
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
