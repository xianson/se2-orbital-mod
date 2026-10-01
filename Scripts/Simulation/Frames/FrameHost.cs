using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The SE2 port of SE-Aerospace's FrameManager, v1: the local player's conjunction frame.
///
/// Same design (docs/architecture-proximity-frames.md, proxy-rendering-frames.md in SE-Aerospace):
///  - STOW: leaving a planet's keep envelope on an escaping arc, the player's state is converted
///    to orbital elements about the deepest SOI, a conjunction frame is created, and the player is
///    teleported into its berth (a cell of the conjunction lattice) with motion cleared.
///  - TREADMILL: in the berth the player (the anchor) is pinned; each tick its velocity is read,
///    zeroed, and folded into the frame's orbit (FrameRails.FoldDrain): thrust bends the orbit
///    instead of moving the player. The sky (proxies) slides past as the rails advance.
///  - ARRIVAL: when the rails orbit is inside, or about to cross into, the parent's shell, the
///    crossing state is converted into the planet cell and the player is teleported there with
///    that velocity (the exact state patch), and the frame dissolves.
///
/// v1 limits (all documented in the README):
///  - Runs on the CLIENT thread for the LOCAL player: SE2 character movement is client-authoritative
///    (server-only velocity writes did not stick in testing). Single player / listen host.
///  - Only an EVA character is framed; a seated player is skipped. Grids and multi-member frames
///    (CW forces, split, merge) come next.
///  - Above the world speed cap the planet cell runs HighSpeed v1 (StepHighSpeed): virtual velocity,
///    model gravity, per-tick position stepping written on client and server. No thrust while in
///    HighSpeed yet, and client/server writes jitter the observed speed.
///  - Capture requires having been inside a planet's keep first (or the harness `stow` command),
///    instead of the full total-partition axiom, so loading a world never yanks the player away.
/// </summary>
public static class FrameHost
{
    public const double FoldThreshold = 0.03;      // m/s (SE1 FrameManager)
    public const double MaxApparentAccel = 100.0;  // m/s² (SE1 FrameManager)
    public const double MaterializeLead = 15.0;    // s (SE1 MaterializeLeadSeconds)
    public const double PinTolerance = 50.0;       // m: re-pin the anchor to the berth beyond this drift
    public const double SpeedCap = 990.0;          // m/s: the world cap, raised to 1000 (SpeedLimitInjector)
    public const double TeleportSettle = 3.0;      // s: give a teleport this long to land

    /// <summary>The frame the local observer renders from. Null = legacy space (no frame; literal world).</summary>
    public static ObserverFrame? Observer;
    /// <summary>Body whose planet cell the observer is in (planet frame), else null.</summary>
    public static string ObserverPlanet;
    public static string ObserverPlanetName => ObserverPlanet;

    private static Vector3D _lastPos, _lastVel;
    /// <summary>On foot (not seated, not HighSpeed): the character's own position and physics velocity this
    /// tick (world), for the orbit readout (the camera-differenced estimate was noisy).</summary>
    public static bool FootState(out Vector3D pos, out Vector3D vel)
    {
        pos = _lastPos; vel = _lastVel;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        return !Seated && !_hsActive && now - _footWall < 0.25 && IsFinite(pos) && IsFinite(vel);
    }
    private static double _footWall;

    /// <summary>The player's orbit about a planet while materialized in its cell (HighSpeed: its conic).</summary>
    public static bool TryGetLocalOrbit(string body, double t, out KeplerianElements el)
    {
        el = default;
        if (_hsActive && _hsBody == body) { el = _hsEl; return true; }
        var reg = SystemHost.Registry;
        var node = reg?.Find(body);
        if (node == null || !VoxelBerthRegistry.TryGetCell(body, reg, out Vector3D cell)) return false;
        if (!VoxelBerthRegistry.TryCellContaining(_lastPos, reg, out string b, out _) || b != body) return false;
        var ch = Chart.Of(body, t);
        Vector3D lr = _lastPos - cell;
        el = CaptureMath.CaptureElements(new StateVector(ch.ToInertial(lr), ch.VelToInertial(lr, _lastVel)), node.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return false;
        // On the ground, or a hop that never climbs well clear of it, is not an orbit: no orbit is
        // drawn and there is nothing to plan from (standing still, it is a fall to the planet's centre).
        Grounded = IsGrounded(body, el);
        return !Grounded;
    }

    /// <summary>The last local trajectory asked for was ground-bound (see TryGetLocalOrbit).</summary>
    public static bool Grounded;

    /// <summary>Periapsis inside the planet and apoapsis under max(15 km, 20% of its radius) above the surface.</summary>
    public static bool IsGrounded(string body, KeplerianElements el)
    {
        double r = SystemHost.Registry?.FindDefinition(body)?.RadiusMeters ?? 0;
        if (!(r > 0)) return false;
        double ap = el.IsElliptic ? el.SemiMajorAxis * (1 + el.Eccentricity) : double.PositiveInfinity;
        return el.PeriapsisRadius < r && ap < r + Math.Max(15000, 0.2 * r);
    }
    /// <summary>The local player's conjunction frame, if framed.</summary>
    public static ProximityFrame PlayerFrame;
    /// <summary>Harness: capture on the next tick regardless of the keep gate.</summary>
    public static bool ForceStow;
    public static string LastEvent = "";
    public static string Debug = "";

    private static readonly Dictionary<Entity, long> _ids = new Dictionary<Entity, long>();
    private static long _nextId = 1;
    private static bool _wasInKeep;

    // A teleport in flight: its target, and the velocity to apply once it lands.
    private static bool _tpPending;
    private static Vector3D _tpTarget;
    private static Vector3D _tpVelocity;
    private static double _tpStarted;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double gravityMultiplier)
    {
        TickRate.Client.Count();
        _mult = gravityMultiplier > 0 ? gravityMultiplier : 1.0;
        if (!SystemHost.Built)
        {
            try
            {
                var sun = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent>();
                SystemHost.WorldSunPeriod = sun != null && sun.SunRotation ? sun.SunPeriod.TotalSeconds : 0;
            }
            catch { }
        }
        if (!SystemHost.EnsureBuilt(gravityMultiplier)) return;
        double dt = SystemHost.AdvanceClock(session);
        if (dt <= 0) return; // once per frame
        TickRate.Draw.Count();
        var reg = SystemHost.Registry;
        var frames = SystemHost.Frames;
        if (reg == null || frames == null) return;
        double t = SystemHost.Now;

        Entity ch = PlayerCharacter(session);
        Seated = ch != null && IsSeated(ch);
        Debug = ch == null ? "no character" : Seated ? "seated" : $"eva tpPending={_tpPending} wasInKeep={_wasInKeep}";
        if (ch != null && !IsSeated(ch))
        {
            long id = IdOf(ch);
            Vector3D pos = ch.Data.GetWorldTransform().Position;
            Vector3D vel = ch.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;

            // A teleport landing this tick: the velocity read above predates it (the client still
            // held the pre-teleport velocity), so it must not be drained into the rails.
            bool landed = false;
            if (_tpPending) { SettleTeleport(session, ch, pos, t); landed = !_tpPending; }
            if (landed) vel = _tpVelocity;
            _lastPos = pos; _lastVel = _hsActive ? _hsVel : vel; _footWall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

            _playerId = id;
            lock (ServerFrames.FramesLock)
            {
                SavedState.TryApply(id);
                if (ApplyServerRequests(session, ch, pos, t)) { PublishObserver(camera.Position, reg, t); return; }
                var frame = frames.FindByMember(id);
                PlayerFrame = frame;
                if (frame != null) { _hsActive = false; UpdatePlayerFrame(session, ch, frame, pos, vel, t, dt); }
                else if (!_tpPending)
                {
                    if (_hsActive && !landed) vel = StepHighSpeed(ch, pos, vel, dt);
                    else if (!landed) vel = ApplyFictitious(ch, pos, vel, dt);
                    if (!_hsActive && !ForceStow && EncounterFrames.TryAdoptPlayer(id, pos)) { PublishObserver(camera.Position, reg, t); return; }
                    TryStow(session, ch, id, pos, _hsActive ? _hsVel : vel, t);
                }
            }
        }

        else if (ch != null)
        {
            // Seated: the grid you sit in carries you. Its frame is yours (observer, warp, planning),
            // and your state is the camera's (the seat moves with the grid).
            Vector3D pos = ch.Data.GetWorldTransform().Position;
            _lastPos = pos; _lastVel = OrbitDisplay.MeasuredVelocity;
            lock (ServerFrames.FramesLock) PlayerFrame = SeatedFrame(pos);
        }

        PublishObserver(camera.Position, reg, t);
        if (ch != null) DelfosHeat.Apply(session, ch, ch.Data.GetWorldTransform().Position);   // the client copy (effect, warning)

        // Warp lock (SE1 WarpPolicy, simplified): warp only advances the rails, so it is allowed only
        // while the player coasts in a conjunction. Materialized (in a planet cell or legacy space) = x1.
        if (PlayerFrame == null && SystemHost.Timescale != 1.0)
        {
            Event($"warp x{SystemHost.Timescale} -> x1 (player is materialized; warp is rails-only)");
            SystemHost.Timescale = 1.0;
        }
        if (MapView.Visible) OrbitDisplay.Clear();
        else if (PlayerFrame != null && Observer.HasValue)
            OrbitDisplay.DrawFrameOrbit(session, camera, Observer.Value, PlayerFrame, reg, t);
        Guard("MapInput.Poll", () => MapInput.Poll());
        Guard("DevFlight.ClientTick", () => DevFlight.ClientTick(session));
        WarpControl.Session = session;
        Guard("WarpControl.Tick", () => WarpControl.Tick());
        Guard("MapView.Tick", () => MapView.Tick(session, camera, t));
        Guard("FrameMarkers.Tick", () => FrameMarkers.Tick(session, camera, t));
        Guard("Maneuvers.HudTick", () => Maneuvers.HudTick(session, camera, t));
        Guard("AutoBurn.Tick", () => AutoBurn.Tick(session, t));
        Guard("OrbitHud.Draw", () => OrbitHud.Draw(session));
        Guard("WarpBar", () => WarpBar.DrawHud(session));
        // On rails the game's SPD (your velocity in the frame) is 0: show your speed about the body.
        Guard("HudSpeed", () => { if (PlayerFrame != null && OrbitHud.Current != null) GameUi.SetHudSpeed(session, (float)OrbitHud.Current.Speed); });
        Guard("SunDriver.Tick", () => SunDriver.Tick(session, camera.Position, t));
        Guard("StarProxy.Tick", () => StarProxy.Tick(session, camera, t));
    }

    // ───────────────────────────── stow (planet cell -> conjunction) ─────────────────────────────

    private static void TryStow(Keen.VRage.Core.Game.Systems.Session session, Entity ch, long id, Vector3D pos, Vector3D vel, double t)
    {
        var reg = SystemHost.Registry;
        if (_pendingOrbit.HasValue)
        {
            var (pb, pel) = _pendingOrbit.Value;
            _pendingOrbit = null;
            ForceStow = false;
            var pf = SystemHost.Frames.CreateFrame(pb, pel, id);
            if (pf == null) return;
            _hsActive = false;
            _wasInKeep = false;
            StartTeleport(session, pf.BerthCenter, Vector3D.Zero, t);
            // Unconstrained grids within the attach radius come along (constrained ones never move: Havok).
            ServerFrames.Attach.Enqueue(new ServerFrames.AttachRequest { FrameId = pf.Id, RefPos = pos, RefVel = vel, Berth = pf.BerthCenter });
            Event($"STOW (orbit command) -> frame #{pf.Id} about {pb}: a={pel.SemiMajorAxis / 1000:F1} km e={pel.Eccentricity:F3}");
            return;
        }
        bool legacy = false;
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
        {
            _wasInKeep = false;
            if (!ForceStow && !OrbitalConfig.CaptureLegacySpace) return; // legacy space: captured only when enabled (or forced)
            if (!SystemHost.TryNearestCell(pos, out body, out cell)) { ForceStow = false; return; }
            legacy = true;
        }
        GravityBody node = body != null ? reg.Find(body) : null;
        BodyDefinition def = body != null ? reg.FindDefinition(body) : null;
        if (node == null) { ForceStow = false; return; }

        StateVector borg = node.OriginInRoot(t);
        Chart chart = legacy ? default : Chart.Of(body, t);
        Vector3D relChart = pos - cell;
        double chartSpeed = vel.Length();   // speed in the planet's world (before the inertial conversion)
        Vector3D rel = chart.ToInertial(relChart);
        if (!_hsActive) vel = chart.VelToInertial(relChart, vel);   // HighSpeed carries the inertial velocity
        Vector3D cel = borg.Position + rel;
        Vector3D celVel = borg.Velocity + vel;
        double d = rel.Length();
        double shell = PlanetBerths.ShellRadius(def);
        double keep = PlanetBerths.KeepRadius(def);
        Debug += $" cell={body} d={d / 1000:F1}km shell={shell / 1000:F1} keep={keep / 1000:F1}";

        KeplerianElements elBody = CaptureMath.CaptureElements(new StateVector(rel, vel), node.Mu, t);
        if (d < keep)
        {
            _wasInKeep = true;
            // Faster than the world allows, well above the ground: onto rails (the rails carry the speed).
            bool tooFast = def != null && chartSpeed > SpeedCap * 1.02 && d - def.RadiusMeters > SurfaceGuard * 1.5;
            if (!ForceStow && !tooFast)
            {
                if (d < shell * PlanetBerths.StowShellMargin) return;                       // voxel frame owns low flight
                if (!IsFinite(elBody.SemiMajorAxis) || !FrameRails.EscapesShell(elBody, t, shell, keep, MaterializeLead)) return;
            }
        }
        else if (!_wasInKeep && !ForceStow && !legacy) return; // v1 gate: only departures from a planet are captured

        GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
        StateVector porg = parent.OriginInRoot(t);
        var el = CaptureMath.CaptureElements(new StateVector(cel - porg.Position, celVel - porg.Velocity), parent.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;

        var frame = SystemHost.Frames.CreateFrame(parent.Name, el, id);
        if (frame == null) return;
        _hsActive = false;
        ForceStow = false;
        _wasInKeep = false;
        ServerFrames.Attach.Enqueue(new ServerFrames.AttachRequest { FrameId = frame.Id, RefPos = pos, RefVel = vel, Berth = frame.BerthCenter, Body = legacy ? null : body, Time = t });
        StartTeleport(session, frame.BerthCenter, Vector3D.Zero, t);
        Event($"STOW -> frame #{frame.Id} orbiting {parent.Name}: r={(cel - porg.Position).Length() / 1000:F1} km " +
              $"|v|={vel.Length():F1} m/s a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F3} slot={frame.BerthSlotId} berth={ServerPlanetBeacon.Fmt(frame.BerthCenter)}");
    }

    // ───────────────────────────── treadmill (conjunction frame) ─────────────────────────────

    private static void UpdatePlayerFrame(Keen.VRage.Core.Game.Systems.Session session, Entity ch, ProximityFrame f,
                                          Vector3D pos, Vector3D vel, double t, double dt)
    {
        if (f.AnchorEntityId != _playerId || f.IsEncounter || DevRider)
        {
            UpdateRider(session, ch, f, pos, vel, t, dt);
            return;
        }
        if (!_tpPending)
        {
            // Hard pin: the anchor never translates in world space.
            if ((pos - f.BerthCenter).Length() > PinTolerance)
            {
                StartTeleport(session, f.BerthCenter, Vector3D.Zero, t);
            }
            else if (IsFinite(vel))
            {
                // Force sensor: read the anchor's velocity, zero it, fold it into the orbit.
                f.PendingDrainDv += vel;
                SetVelocity(ch, Vector3D.Zero);
                // Zero the server copy too, or it re-supplies the same velocity for several ticks
                // (seen in game: a 10 m/s kick folded in ~17 times).
                if (vel.LengthSquared() > 1e-6) ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Velocity = Vector3D.Zero };
            }
        }

        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        FrameRails.FoldDrain(f, t, FoldThreshold, MaxApparentAccel * dt, ref cur, out Vector3D slice);
        if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;

        TryReparent(f, t);
        TryMaterialize(session, f, t);
    }

    /// <summary>
    /// RIDER: the player in a frame anchored by a grid (SE1 CharacterRider / loose-object feed). Not pinned,
    /// not drained: the player floats freely relative to the anchor and feels the berth's differential
    /// gravity minus the frame acceleration A the server folded this tick. Past the slot radius the
    /// player splits into a frame of their own.
    /// </summary>
    private static void UpdateRider(Keen.VRage.Core.Game.Systems.Session session, Entity ch, ProximityFrame f,
                                    Vector3D pos, Vector3D vel, double t, double dt)
    {
        if (_tpPending) return;
        // Warp N: the rails run N times faster than real time, so relative motion must too. Forces scale by
        // N^2 (velocities come out N times the true ones, covering N times the ground per real second); on a
        // change of warp the velocity is rescaled so the motion carries on; readouts divide by N.
        // Physics warp limit (as KSP's): relative motion in warp needs N times the true speed, which must stay
        // under the world's speed cap (1000 m/s): riding free (dampeners off), warp is held to N x |v_rel| <= 900
        // m/s and x25 at most. (x10 is exact: 2 m off over 30 s; past the cap the motion was lost.)
        if (SystemHost.Timescale > 1.0 && !Dampeners && _riderN > 0)
        {
            double vTrue = vel.Length() / _riderN;
            double nMax = Math.Min(MaxRiderWarp, RiderWarpSpeed / Math.Max(1.0, vTrue));
            if (SystemHost.Timescale > nMax)
            {
                SystemHost.Timescale = Math.Max(1.0, Math.Floor(nMax));
                RiderWarpLimit = $"warp held to x{SystemHost.Timescale:F0}: relative motion at {vTrue:F0} m/s";
            }
        }
        double N = Math.Max(1.0, SystemHost.Timescale);
        if (_riderN > 0 && Math.Abs(N - _riderN) > 1e-9 && _riderFrame == f.Id)
        {
            vel *= N / _riderN;
            SetVelocity(ch, vel);
            ServerPlanetBeacon.SetRiderVelocity(vel);
        }
        _riderN = N; _riderFrame = f.Id;
        RiderFrame = f.Id; RiderOffset = pos - f.BerthCenter; RiderVelocity = vel / N;
        // At the anchor (inside its bounding box, touching it, or within 100 m of it): no rendezvous plot.
        try
        {
            double dAnchor = double.PositiveInfinity;
            if (f.AnchorEntityId == ServerFrames.AsteroidAnchorId) dAnchor = AsteroidBridge.DistanceToAsteroid(pos);
            else if (GridMembers.IsGridId(f.AnchorEntityId) && GridMembers.Get(f.AnchorEntityId) is OrbitalGridComponent ag)
                dAnchor = AsteroidBridge.BoxDistance(Keen.VRage.Core.Game.Data.BoundingBoxData.GetWorldAABB(ag.Entity), pos);
            AtAnchor = dAnchor <= 100.0;
        }
        catch { AtAnchor = false; }
        // Dampeners: station-keeping. As SE1 (a station-keeping member holds its offset with the relative force
        // nulled): with them on no relative force is applied at all, so they only null your own motion and you
        // hold EXACTLY (the anchor is pinned at the berth). Fighting the force each tick left a lag, a drift.
        try { Dampeners = ch.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>(); } catch { }
        Vector3D A = !DevRider && ServerFrames.AnchorAccel.TryGetValue(f.Id, out var a) ? a : Vector3D.Zero;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D rRel = pos - f.BerthCenter;   // the anchor is pinned at the berth
        double mu = f.Elements.Mu;
        bool isLag = EncounterFrames.LagrangeDynamics(f.Id, t, out var lag);
        // A static anchor (a station, an asteroid): you leave it past the capture radius, measured from it.
        bool isStatic = ServerFrames.StaticAnchorOf(f, out Vector3D anchorAt);
        double leave = isStatic ? ServerFrames.CaptureRadius : ServerFrames.SlotRadius;
        double away = isStatic ? (pos - anchorAt).Length() : rRel.Length();
        // The relative force (true acceleration at the true velocity), applied as a force: x N^2 in warp.
        Vector3D acc = isLag ? lag(rRel, vel / N)   // a Lagrange site's own dynamics
                     : (Grav(cur.Position + rRel, mu) - Grav(cur.Position, mu)) - A;
        // DEV check (dampeners off, not thrusting): the measured relative acceleration against the model's.
        {
            double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (_diagWall > 0 && wall - _diagWall >= 1.0 && !Dampeners)
            {
                Vector3D meas = (vel / N - _diagVel) / ((t - _diagT) > 0 ? (t - _diagT) : 1);
                Vector3D model = 0.5 * (acc + _diagAcc);
                double ratio = model.LengthSquared() > 1e-12 ? Vector3D.Dot(meas, model) / model.LengthSquared() : double.NaN;
                RiderDiag = $"rider: |a| model {model.Length():F3} measured {meas.Length():F3} m/s2, along-model ratio {ratio:F2}, warp x{N:F0}, off {rRel.Length() / 1000:F2} km, world |v| {vel.Length():F0} m/s, dv/tick {acc.Length() * dt * N * N:F1} m/s, ticks/s {_diagTicks:F0}";
                _diagTicks = 0;
            }
            _diagTicks++;
            if (_diagWall <= 0 || wall - _diagWall >= 1.0) { _diagWall = wall; _diagVel = vel / N; _diagT = t; _diagAcc = acc; }
        }
        if (!Dampeners && IsFinite(acc) && acc.LengthSquared() > 1e-12)
        {
            Vector3D dv = acc * (dt * N * N);
            SetVelocity(ch, vel + dv);
            ServerPlanetBeacon.AddRiderDv(dv);   // the server's copy too (it would overwrite the client's)
        }
        if (away > leave)
        {
            var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vel / N), mu, t);
            if (!IsFinite(el.SemiMajorAxis)) return;
            var nf = SystemHost.Frames.SplitOff(f, _playerId, f.ParentBodyName, el);
            if (nf == null) return;
            _riderN = 0;
            StartTeleport(session, nf.BerthCenter, Vector3D.Zero, t);
            Event($"SPLIT player from frame #{f.Id} at {away / 1000:F1} km -> frame #{nf.Id}");
        }
    }

    /// <summary>DEV: the rider force check (measured vs model relative acceleration).</summary>
    public static string RiderDiag = "-";
    private static double _diagWall, _diagT, _diagTicks; private static Vector3D _diagVel, _diagAcc;

    /// <summary>Riding free, warp is held so relative motion (N x its true speed) stays under the world cap.</summary>
    public const double MaxRiderWarp = 25, RiderWarpSpeed = 900;
    public static string RiderWarpLimit;

    /// <summary>DEV: ride your own frame (its centre a virtual fixed anchor), to test relative motion.</summary>
    public static bool DevRider;

    /// <summary>Riding, at the anchor itself (within 100 m of its bounding box): no rendezvous plot.</summary>
    public static bool AtAnchor;

    /// <summary>Your jetpack's dampeners (riding: on = station-keeping with the anchor).</summary>
    public static bool Dampeners;

    /// <summary>The warp the rider's velocity was last scaled for (and in which frame).</summary>
    private static double _riderN;
    private static long _riderFrame = -1;

    private static Vector3D Grav(Vector3D r, double mu)
    {
        double d = r.Length();
        return d > 1 ? r * (-mu / (d * d * d)) : Vector3D.Zero;
    }

    /// <summary>Patched conics: leave the parent's SOI or enter a child's -> re-express the orbit there.</summary>
    internal static void TryReparent(ProximityFrame f, double t)
    {
        var reg = SystemHost.Registry;
        GravityBody parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        StateVector cel = parent.StateInRoot(cur, t);
        GravityBody now = reg.Root.DeepestSoiContaining(cel.Position, t);
        if (now == null || now == parent) return;
        StateVector norg = now.OriginInRoot(t);
        var el = CaptureMath.CaptureElements(new StateVector(cel.Position - norg.Position, cel.Velocity - norg.Velocity), now.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        Event($"SOI {f.ParentBodyName} -> {now.Name} (frame #{f.Id}, a={el.SemiMajorAxis / 1000:F0} km e={el.Eccentricity:F3})");
        f.ParentBodyName = now.Name;
        f.Elements = el;
    }

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
        // Faster than the world allows: stay on rails (no HighSpeed; the rails carry any speed). The
        // world takes over once slower, or down at the surface guard, where the speed is capped.
        double alt = cel.Position.Length() - def.RadiusMeters;
        if (speed > SpeedCap && alt > SurfaceGuard) return;
        bool capped = speed > SpeedCap;
        Vector3D applied = capped ? worldVel * (SpeedCap / speed) : worldVel;
        bool hs = false;
        _hsActive = false;

        long fid = f.Id;
        SystemHost.Frames.Dissolve(fid);
        PlayerFrame = null;
        _wasInKeep = true;
        StartTeleport(session, worldPos, applied, t);
        Event($"ARRIVE frame #{fid} -> {node.Name} cell: r={cel.Position.Length() / 1000:F1} km (shell {shell / 1000:F1}) " +
              $"|v|={speed:F0} m/s{(capped ? $" -> capped to {SpeedCap:F0} at the surface guard ({alt / 1000:F1} km)" : "")}");
    }

    // ───────────────────────────── HighSpeed (planet cell, above the cap) ─────────────────────────────

    public const double HighSpeedExitFraction = 0.9;  // drop back to physics below cap × this
    public const double SurfaceGuard = 2000.0;        // m above the body radius: never step closer

    private static bool _hsActive;
    private static double _mult = 1.0;
    private static Vector3D _hsVel;
    public static bool HighSpeedActive => _hsActive;
    public static long PlayerId => _playerId;
    public static Vector3D PlayerPosition => _lastPos;
    /// <summary>The player riding a frame: its id, offset from the frame's centre and velocity relative to it (last tick).</summary>
    public static long RiderFrame = -1;
    public static Vector3D RiderOffset, RiderVelocity;

    /// <summary>Save: the player's HighSpeed conic, if riding one.</summary>
    public static bool TryGetHighSpeed(out string body, out KeplerianElements el)
    {
        body = _hsBody; el = _hsEl;
        return _hsActive && !string.IsNullOrEmpty(_hsBody);
    }

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
    public static Vector3D HighSpeedVelocity => _hsVel;
    public static string HighSpeedElements => _hsActive ? $"a={_hsEl.SemiMajorAxis / 1000:F2}km,e={_hsEl.Eccentricity:F4}" : "-";

    /// <summary>
    /// SE-Aerospace's HighSpeed, SE2 form: ANALYTIC. Inside a planet cell gravity is exactly the patched
    /// inverse-square law, so the true motion is a Kepler conic. On engage the state is converted to
    /// elements about the body (<see cref="_hsEl"/>); every tick the character is PLACED at the conic's
    /// position for the current time (client and server writes), with physics velocity zeroed. A write
    /// lost to the client/server tug is corrected by the next one instead of accumulating (the first,
    /// integrating version sank below periapsis in game for exactly that reason).
    /// Below cap × 0.9 it hands back to physics with the conic's velocity; near the surface it stops
    /// (surface guard) and hands back at the cap: no tunnelling into terrain.
    /// Thrust is not folded in yet (SE1 re-osculated from GetAcceleration). Returns the velocity.
    /// </summary>
    private static KeplerianElements _hsEl;
    private static string _hsBody;

    private static void EngageHighSpeed(string body, Vector3D relPos, Vector3D relVel, double t)
    {
        var node = SystemHost.Registry?.Find(body);
        if (node == null) { _hsActive = false; return; }
        _hsEl = CaptureMath.CaptureElements(new StateVector(relPos, relVel), node.Mu, t);
        _hsBody = body;
        _hsVel = relVel;
        _hsActive = IsFinite(_hsEl.SemiMajorAxis);
    }

    private static Vector3D _kick;
    /// <summary>DEV: give the HighSpeed player a velocity (prograde, radial, normal m/s) through physics.</summary>
    public static string Kick(double pro, double rad, double nor)
    {
        if (!_hsActive) return "not in HighSpeed";
        _kick = new Vector3D(pro, rad, nor);
        return "kick queued";
    }

    /// <summary>HighSpeed: a velocity jump the thrust does not explain, above this, is a push (collision, harness kick).</summary>
    public const double HsPushThreshold = 2.0;   // m/s per tick
    public static double HsResidualAvg, HsResidualMax;
    public static int HsFolds;
    public static bool HsThrustFold = true;
    public static string HsDiag = "";
    private static double _hsFoldLogAt;
    private static Vector3D _hsFoldedSinceLog;

    /// <summary>
    /// HighSpeed keeps the player's own delta-v. The character's thrust component applies one
    /// impulse per frame (ActiveThrustData.ComputedThrustPerFrame, body-local); that impulse times
    /// the inverse mass is the exact thrust delta-v, folded into the conic by re-osculating. The
    /// physics velocity itself is no use for small changes: the thrust job zeroes components below
    /// the movement minimum speed, which swallows a frame of gravity (~0.2 m/s). Large unexplained
    /// jumps (a collision, the harness kick) are folded from the velocity instead.
    /// </summary>
    private static void FoldHighSpeedThrust(Entity ch, Vector3D relPos, Vector3D measured, double dt, double t)
    {
        if (dt <= 0) return;
        var node = SystemHost.Registry?.Find(_hsBody);
        if (node == null) return;

        Vector3D thrust = Vector3D.Zero;
        try
        {
            if (ch.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ActiveThrustData>(out var at) &&
                ch.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) && mp.InvMass > 0)
            {
                var wt = ch.Data.GetWorldTransform();
                thrust = (Vector3D)WorldTransform.TransformDirection(at.ComputedThrustPerFrame, wt) * mp.InvMass;
            }
        }
        catch { thrust = Vector3D.Zero; }

        double r = relPos.Length();
        Vector3D gPhys = r > 1 ? relPos * (-node.Mu / (r * r * r)) : Vector3D.Zero;
        Vector3D push = IsFinite(measured) ? measured - gPhys * dt - thrust : Vector3D.Zero;
        double pm = push.Length();
        HsResidualAvg = HsResidualAvg * 0.98 + pm * 0.02;
        if (pm > HsResidualMax) HsResidualMax = pm;
        HsDiag = $"thrust={thrust.Length():F3} meas={(IsFinite(measured) ? measured.Length() : -1):F3} gdt={gPhys.Length() * dt:F3}";

        Vector3D dv = Chart.Of(_hsBody, t).ToInertial(thrust + (pm > HsPushThreshold ? push : Vector3D.Zero));   // chart axes -> inertial
        if (_kick.LengthSquared() > 0)
        {
            // DEV kick: prograde / radial / normal, injected as a push (tests the fold, not the engine).
            StateVector k = OrbitPropagation.StateAt(_hsEl, t);
            Vector3D pro = Vector3D.Normalize(k.Velocity), rad = Vector3D.Normalize(k.Position);
            Vector3D kv = pro * _kick.X + rad * _kick.Y + Vector3D.Cross(rad, pro) * _kick.Z;
            Event($"DEV kick {kv.Length():F1} m/s (prograde {_kick.X:F1}, radial {_kick.Y:F1}, normal {_kick.Z:F1})");
            dv += kv;
            _kick = Vector3D.Zero;
        }
        if (!HsThrustFold || !IsFinite(dv) || dv.LengthSquared() < 1e-12) return;
        StateVector st = OrbitPropagation.StateAt(_hsEl, t);
        var el = CaptureMath.CaptureElements(new StateVector(st.Position, st.Velocity + dv), node.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        _hsEl = el;
        HsFolds++;
        _hsFoldedSinceLog += dv;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _hsFoldLogAt > 1.0)
        {
            _hsFoldLogAt = now;
            Event($"HighSpeed dv folded: {_hsFoldedSinceLog.Length():F2} m/s -> a={el.SemiMajorAxis / 1000:F2} km e={el.Eccentricity:F4} (folds {HsFolds})");
            _hsFoldedSinceLog = Vector3D.Zero;
        }
    }

    /// <summary>Free flight in a spinning planet's cell (the rotating chart): Coriolis + centrifugal.</summary>
    private static Vector3D ApplyFictitious(Entity ch, Vector3D pos, Vector3D vel, double dt)
    {
        if (dt <= 0) return vel;
        var reg = SystemHost.Registry;
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) return vel;
        Chart c = Chart.Of(body, SystemHost.Now);
        if (!c.Spin) return vel;
        Vector3D r = pos - cell;
        double alt = r.Length() - (reg.FindDefinition(body)?.RadiusMeters ?? 0);
        if (vel.Length() < OrbitalConfig.FictitiousMinSpeed && alt < OrbitalConfig.FictitiousMinAltitude) return vel;
        Vector3D nv = vel + c.Fictitious(r, vel) * dt;
        if (!IsFinite(nv)) return vel;
        SetVelocity(ch, nv);
        return nv;
    }

    private static Vector3D StepHighSpeed(Entity ch, Vector3D pos, Vector3D measured, double dt)
    {
        var reg = SystemHost.Registry;
        if (!VoxelBerthRegistry.TryGetCell(_hsBody, reg, out Vector3D cell)) { _hsActive = false; return measured; }
        var def = reg.FindDefinition(_hsBody);
        double t = SystemHost.Now;
        FoldHighSpeedThrust(ch, pos - cell, measured, dt, t);
        StateVector st = OrbitPropagation.StateAt(_hsEl, t);
        _hsVel = st.Velocity;
        Vector3D vChart = Chart.Of(_hsBody, t).VelFromInertial(st.Position, st.Velocity);
        double speed = vChart.Length();

        if (speed < SpeedCap * HighSpeedExitFraction)
        {
            _hsActive = false;
            SetVelocity(ch, vChart);
            Event($"HighSpeed off at {speed:F0} m/s (physics takes over)");
            return _hsVel;
        }
        double floor = (def != null ? def.RadiusMeters : 0) + SurfaceGuard;
        if (st.Position.Length() < floor)
        {
            _hsActive = false;
            Vector3D capped = vChart * (SpeedCap / speed);
            SetVelocity(ch, capped);
            Event($"HighSpeed surface guard at {(st.Position.Length() - (def?.RadiusMeters ?? 0)) / 1000:F1} km alt: {speed:F0} m/s -> {SpeedCap:F0} m/s physics");
            return capped;
        }

        Vector3D target = cell + Chart.Of(_hsBody, t).FromInertial(st.Position);
        SetVelocity(ch, Vector3D.Zero);
        if (!GridMembers.Finite(target)) { GridMembers.NaNRefused++; _hsActive = false; Event("HighSpeed left: non-finite state"); return measured; }   // bad orbital state: back to physics
        var wt = ch.Data.GetWorldTransform();
        ch.Data.Set(new WorldTransform(target, wt.Orientation));
        // The client write alone is overridden by the character controller (seen in game);
        // the transform also goes through the server, the way EntityAdmin teleports.
        ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Position = target, Velocity = Vector3D.Zero };
        return _hsVel;
    }

    /// <summary>
    /// Harness: put the player on a given orbit around a planet, starting at APOAPSIS (SE1's /orbit
    /// command). Framed: replaces the frame's elements. Not framed: stows with those elements.
    /// </summary>
    public static string SetOrbit(string body, double apoAltKm, double periAltKm, double incDeg, double phaseDeg = 0)
    {
        if (!OrbitElements(body, apoAltKm, periAltKm, incDeg, phaseDeg, out var el)) return "no such body / degenerate orbit";
        // In a site (its orbit is its ephemeris) or a frame shared with others (a station, a ship, a rock you
        // rendezvoused with): you leave it and stow onto the orbit alone; setting its elements moved them all.
        var shared = PlayerFrame;
        if (shared != null && (EncounterFrames.IsSite(shared) || shared.IsEncounter || shared.Members.Count > 1))
        {
            lock (ServerFrames.FramesLock) SystemHost.Frames.RemoveMember(_playerId);
            Event($"ORBIT: left frame #{shared.Id} (shared or a site) to take the orbit alone");
            _pendingOrbit = (body, el);
            ForceStow = true;
            return "orbit queued (left the shared frame; stows next tick)";
        }
        if (PlayerFrame != null)
        {
            PlayerFrame.ParentBodyName = body;
            PlayerFrame.Elements = el;
            PlayerFrame.PendingDrainDv = Vector3D.Zero;
            Event($"ORBIT set on frame #{PlayerFrame.Id}: {body} Ap {apoAltKm} km Pe {periAltKm} km i {incDeg}°");
            return "orbit set";
        }
        _pendingOrbit = (body, el);
        ForceStow = true;
        return "orbit queued (stows next tick)";
    }

    private static (string body, KeplerianElements el)? _pendingOrbit;

    /// <summary>Harness: stow onto exactly this orbit on the next tick.</summary>
    public static void SetPendingOrbit(string body, KeplerianElements el) => _pendingOrbit = (body, el);

    /// <summary>Elements for an Ap/Pe/inclination orbit, starting at apoapsis advanced by phaseDeg of mean anomaly.</summary>
    public static bool OrbitElements(string body, double apoAltKm, double periAltKm, double incDeg, double phaseDeg,
                                     out KeplerianElements el)
    {
        el = default;
        var reg = SystemHost.Registry;
        var node = reg?.Find(body);
        var def = reg?.FindDefinition(body);
        if (node == null || def == null) return false;
        double R = def.RadiusMeters;
        double ra = R + Math.Max(apoAltKm, periAltKm) * 1000, rp = R + Math.Min(apoAltKm, periAltKm) * 1000;
        double a = 0.5 * (ra + rp);
        double va = Math.Sqrt(node.Mu * (2.0 / ra - 1.0 / a));
        double inc = incDeg * Math.PI / 180.0;
        double t = SystemHost.Now;
        var e0 = OrbitalMath.ToElements(new StateVector(new Vector3D(ra, 0, 0), new Vector3D(0, Math.Cos(inc), Math.Sin(inc)) * va), node.Mu, t);
        if (!IsFinite(e0.SemiMajorAxis)) return false;
        if (phaseDeg != 0 && IsFinite(e0.Period))
        {
            var st = OrbitPropagation.StateAt(e0, t + phaseDeg / 360.0 * e0.Period);
            e0 = OrbitalMath.ToElements(st, node.Mu, t);
            if (!IsFinite(e0.SemiMajorAxis)) return false;
        }
        el = e0;
        return true;
    }

    // ───────────────────────────── observer frame (for renderers) ─────────────────────────────

    private static void PublishObserver(Vector3D cam, SystemRegistry reg, double t)
    {
        var f = PlayerFrame;
        if (f != null && reg.Find(f.ParentBodyName) is GravityBody parent)
        {
            StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
            StateVector cel = parent.StateInRoot(cur, t);
            var of = new ObserverFrame(f.BerthCenter, cel.Position, cel.Velocity, null);
            Observer = of;
            PlanetBerths.LocalConjunctionFrame = of;
            ObserverPlanet = null;
            return;
        }
        PlanetBerths.LocalConjunctionFrame = null;
        if (VoxelBerthRegistry.TryCellContaining(cam, reg, out string body, out Vector3D cell) && reg.Find(body) is GravityBody node)
        {
            StateVector o = node.OriginInRoot(t);
            var obs = new ObserverFrame(cell, o.Position, o.Velocity, node);
            var odef = reg.FindDefinition(body);
            if (odef != null) { PlanetBerths.FillWindowSpin(ref obs, node, odef, t, cam); PlanetBerths.SetWindowSpinActive(ref obs, true); }
            Observer = obs;
            ObserverPlanet = body;
            return;
        }
        Observer = null;
        ObserverPlanet = null;
    }

    // ───────────────────────────── requests from the server half ─────────────────────────────

    private static long _playerId;
    private static Vector3D _pendingShift;
    private sealed class ArrivalRequest
    {
        public string Body; public Vector3D Target; public Vector3D AnchorPos; public Vector3D CelPos; public Vector3D CelVel; public double Epoch;
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
        _pendingArrival = new ArrivalRequest { Body = body, Target = target, AnchorPos = anchorPos, CelPos = celPos, CelVel = celVel, Epoch = epoch };
    }

    /// <summary>Returns true when this tick was consumed by a server-requested move.</summary>
    private static bool ApplyServerRequests(Keen.VRage.Core.Game.Systems.Session session, Entity ch, Vector3D pos, double t)
    {
        if (_pendingArrival != null)
        {
            var a = _pendingArrival;
            _pendingArrival = null;
            _pendingShift = Vector3D.Zero;
            Vector3D off = pos - a.AnchorPos;              // berth offset (inertial window axes)
            Vector3D relPos = a.CelPos + off;
            Chart achart = Chart.Of(a.Body, t);
            Vector3D chartVel = achart.VelFromInertial(relPos, a.CelVel);
            Vector3D arriveAt = VoxelBerthRegistry.TryGetCell(a.Body, SystemHost.Registry, out Vector3D acell)
                ? acell + achart.FromInertial(relPos) : a.Target + off;
            double speed = chartVel.Length();
            bool hs = speed > SpeedCap;
            if (hs)
            {
                EngageHighSpeed(a.Body, relPos, a.CelVel, a.Epoch);
                var noDamp = new PlayerRequest { Dampeners = false };
                ServerPlanetBeacon.ApplyToCharacter(session, noDamp, "client");
                ServerPlanetBeacon.PendingPlayer = noDamp;
            }
            PlayerFrame = null;
            _wasInKeep = true;
            StartTeleport(session, arriveAt, hs ? Vector3D.Zero : chartVel, t);
            Event($"player arrives with its grid frame at {a.Body}{(hs ? " (HighSpeed)" : "")}");
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

    // ───────────────────────────── helpers ─────────────────────────────

    private static void StartTeleport(Keen.VRage.Core.Game.Systems.Session session, Vector3D target, Vector3D velocity, double t)
    {
        _tpPending = true;
        _tpTarget = target;
        _tpVelocity = velocity;
        _tpStarted = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        PlanetRenderBridge.TeleportPlayer(session, new WorldTransform(target, Quaternion.Identity), clearMotion: true);
    }

    private static void SettleTeleport(Keen.VRage.Core.Game.Systems.Session session, Entity ch, Vector3D pos, double t)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if ((pos - _tpTarget).Length() < 200.0)
        {
            SetVelocity(ch, _tpVelocity);
            ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Velocity = _tpVelocity };
            _tpPending = false;
        }
        else if (now - _tpStarted > TeleportSettle)
        {
            Event($"teleport did not land within {TeleportSettle:F0} s (at {ServerPlanetBeacon.Fmt(pos)}); retrying");
            PlanetRenderBridge.TeleportPlayer(session, new WorldTransform(_tpTarget, Quaternion.Identity), clearMotion: true);
            _tpStarted = now;
        }
    }

    private static Vector3D ReadVelocity(Entity ch) =>
        ch.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;

    internal static Entity PlayerCharacter(Keen.VRage.Core.Game.Systems.Session session)
    {
        var list = new List<Entity>();
        return session.TryFillAliveCharacters(list) && list.Count > 0 ? list[0] : null;
    }

    /// <summary>The frame of the grid a seated player sits in (a member grid within 300 m). Caller holds FramesLock.</summary>
    private static ProximityFrame SeatedFrame(Vector3D pos)
    {
        ProximityFrame best = null; double bd = 300;
        lock (ServerFrames.GridPositions)
            foreach (var kv in ServerFrames.GridPositions)
            {
                double d = (kv.Value - pos).Length();
                if (d >= bd) continue;
                var f = SystemHost.Frames.FindByMember(kv.Key);
                if (f != null) { bd = d; best = f; }
            }
        return best;
    }

    /// <summary>True when the local character sits in a seat.</summary>
    public static bool Seated;

    private static bool IsSeated(Entity ch) => ch.Data.TryGet<Keen.VRage.Core.Game.Components.EntityParentData>(out _);

    private static void SetVelocity(Entity ch, Vector3D v)
    {
        if (!IsFinite(v)) return;
        ch.Data.Set(new RigidBodyData { LinearVelocity = (Vector3)v });
    }

    private static long IdOf(Entity e)
    {
        if (!_ids.TryGetValue(e, out long id)) { id = _nextId++; _ids[e] = id; }
        return id;
    }

    private static double NextInboundCrossing(KeplerianElements el, double tInRel, double t)
    {
        double abs = el.Epoch + tInRel;
        if (el.IsElliptic)
        {
            double period = el.Period;
            if (!IsFinite(period) || period <= 0.0) return double.NaN;
            if (abs < t) abs += Math.Ceiling((t - abs) / period) * period;
        }
        return abs;
    }

    /// <summary>
    /// Runs one part of the frame so that a fault in it is logged and skipped, not thrown into the
    /// game's job (which would crash the game and send a crash report). Each fault is logged with its
    /// stack the first few times, then counted.
    /// </summary>
    public static void Guard(string name, Action a)
    {
        try { a(); }
        catch (Exception ex) { Fault(name, ex); }
    }

    private static readonly Dictionary<string, int> _faults = new Dictionary<string, int>();
    public static string LastFault = "";

    public static void Fault(string name, Exception ex)
    {
        string key = name + ":" + ex.GetType().Name + ":" + ex.TargetSite?.Name;
        _faults.TryGetValue(key, out int n); _faults[key] = ++n;
        LastFault = $"{DateTime.Now:HH:mm:ss} {name}: {ex.GetType().Name}: {ex.Message} (x{n})";
        if (n <= 3 || n % 1000 == 0) Log.Default?.Error($"[ORBIT-FAULT] {name} (x{n}): {ex}");
    }

    private static void Event(string s)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-FRAME] " + s);
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
