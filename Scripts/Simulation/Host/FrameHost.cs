using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// The SE2 port of SE-Aerospace's FrameManager: the local player's conjunction frame.
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
/// Limits:
///  - Runs on the CLIENT thread for the LOCAL player: SE2 character movement is client-authoritative
///    (server-only velocity writes did not stick in testing). Single player / listen host. Grids are the
///    server half's (ServerFrames); seated, the grid you sit in carries you and its frame is yours.
///  - Over the world speed cap in a planet's cell there is no HighSpeed for the player any more: an arrival
///    comes in capped (TryMaterialize), a saved HighSpeed conic goes onto the rails (RestoreHighSpeed).
///  - Capture requires having been inside a planet's keep first (or the harness `stow` command),
///    instead of the full total-partition axiom, so loading a world never yanks the player away.
/// </summary>
public static partial class FrameHost
{
    public const double FoldThreshold = 0.03;      // m/s (SE1 FrameManager)
    public const double MaxApparentAccel = 100.0;  // m/s² (SE1 FrameManager)
    public const double PinTolerance = 50.0;       // m: re-pin the anchor to the berth beyond this drift
    public const double SpeedCap = 990.0;          // m/s: the world cap, raised to 1000 (SpeedLimitInjector)
    public const double TeleportSettle = 3.0;      // s: give a teleport this long to land

    /// <summary>The frame the local observer renders from. Null = legacy space (no frame; literal world).</summary>
    public static ObserverFrame? Observer;
    /// <summary>Body whose planet cell the observer is in (planet frame), else null.</summary>
    public static string ObserverPlanet;

    private static Vector3D _lastPos, _lastVel;
    /// <summary>On foot (not seated): the character's own position and physics velocity this
    /// tick (world), for the orbit readout (the camera-differenced estimate was noisy).</summary>
    public static bool FootState(out Vector3D pos, out Vector3D vel)
    {
        pos = _lastPos; vel = _lastVel;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        return !Seated && now - _footWall < 0.25 && IsFinite(pos) && IsFinite(vel);
    }
    private static double _footWall;

    /// <summary>Seated: your place (the character in its seat) and the seat grid's own physics velocity - what planning
    /// already flies from. (2026-10-12, the user: "the orbit propagation is wildly spazzy in drag on ascent" - the HUD's
    /// orbit read the camera's position differenced over time: a chase camera lagging, shaking and swung by the mouse,
    /// near orbital speed, where Ap and Pe swing with every m/s.)</summary>
    public static bool SeatState(out Vector3D pos, out Vector3D vel)
    {
        pos = _lastPos; vel = _seatVel;
        return Seated && _seatVelOk && IsFinite(pos) && IsFinite(vel);
    }

    /// <summary>The player's orbit about a planet while materialized in its cell.</summary>
    public static bool TryGetLocalOrbit(string body, double t, out KeplerianElements el)
    {
        el = default;
        var reg = SystemHost.Registry;
        var node = reg?.Find(body);
        if (node == null || !VoxelBerthRegistry.TryGetCell(body, reg, out Vector3D cell)) return false;
        if (!VoxelBerthRegistry.TryCellContaining(_lastPos, reg, out string b, out _) || b != body) return false;
        var ch = Chart.Of(body, t);
        Vector3D lr = _lastPos - cell;
        lock (_localOwn) el = _localOwn.GetState(body.GetHashCode(), new StateVector(ch.ToInertial(lr), ch.VelToInertial(lr, _lastVel)), node.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return false;
        // On the ground, or a hop that never climbs well clear of it, is not an orbit: no orbit is
        // drawn and there is nothing to plan from (standing still, it is a fall to the planet's centre).
        Grounded = IsGrounded(body, el);
        return !Grounded;
    }

    /// <summary>The local orbit, kept while it still says where you are (not re-solved for every frame's noise).</summary>
    static readonly RendezvousPlot.OwnOrbit _localOwn = new RendezvousPlot.OwnOrbit();

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
    public static ProximityFrame PlayerFrame => _pub.Frame;

    // The player as of one tick (Core/Frames/PlayerState): built fresh each tick from its defaults (_cur, FrameHost's own),
    // published whole at PublishObserver (_pub, everyone else's). The names here read the published one.
    private static PlayerState _cur = new PlayerState();
    private static volatile PlayerState _pub = PlayerState.None;
    /// <summary>The player's state as of the last whole tick.</summary>
    public static PlayerState State => _pub;
    /// <summary>Harness: capture on the next tick regardless of the keep gate.</summary>
    public static bool ForceStow;
    public static string LastEvent = "";
    public static string Debug = "";

    // (weak: a dead character - every respawn makes a new one - is not kept alive by its id)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, System.Runtime.CompilerServices.StrongBox<long>> _ids = new System.Runtime.CompilerServices.ConditionalWeakTable<Entity, System.Runtime.CompilerServices.StrongBox<long>>();
    private static long _nextId = 1;
    private static bool _wasInKeep;
    private static object _session;

    /// <summary>A new session (another world loaded in the same process, should statics outlive it): nothing of the last
    /// one's player - its frame, ride, seat, pending teleport / arrival / merge, warp scale, observer - and its game clock.</summary>
    internal static void NewSession()
    {
        _cur = new PlayerState(); _pub = PlayerState.None;
        SeatGrid = null; SeatBlock = null; _seatChar = null; _lastChar = null; _seatVelOk = false;
        _tpPending = false; _riderN = 0; _riderFrame = -1; _wasInKeep = false;
        DropPending(); _pendingOrbit = null; ForceStow = false;
        Observer = null; ObserverPlanet = null;
        Maneuvers.ResetOwnOrbit();
        SystemHost.ResetClock(); SunDriver.Reset();
    }

    // A teleport in flight: its target, and the velocity to apply once it lands.
    private static bool _tpPending;
    private static Vector3D _tpTarget;
    private static Vector3D _tpVelocity;
    private static double _tpStarted;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double gravityMultiplier)
    {
        TickRate.Client.Count();
        PlanetRenderBridge.CameraPos = camera.Position;
        if (!ReferenceEquals(session, _session)) { _session = session; WorldReset.ClientSession(); }
        if (!OrbitalSettings.SawClient) OrbitalSettings.SawClient = true;
        OrbitalSettings.Poll();   // (the settings files, every ~2 s)
        SettingsTab.Tick(session);   // (the Options screen's Mods tab, Ui/SettingsTab.cs: a timestamp compare; 4 Hz a UI-thread check)
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
        if (!ReferenceEquals(ch, _lastChar))
        {
            // a new character (a respawn, a death): nothing pending from the last one (a teleport re-sent every 3 s, a stow
            // blocked behind it, a warp scale of its walk)
            if (_lastChar != null) { _tpPending = false; _riderN = 0; DropPending(); Event("a new character: pending teleport, requests and rider scale cleared"); }
            _lastChar = ch;
        }
        _cur = new PlayerState();   // (this tick's, from its defaults: nothing carried from the last)
        _cur.Seated = ch != null && IsSeated(ch);
        Debug = ch == null ? "no character" : _cur.Seated ? "seated" : $"eva tpPending={_tpPending} wasInKeep={_wasInKeep}";
        // (the save is applied seated too: loading in a seat restored no frame, and standing up later teleported the ship)
        if (ch != null && _cur.Seated && !SavedState.Idle) lock (ServerFrames.FramesLock) SavedState.TryApply(IdOf(ch));
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
            _lastPos = pos; _lastVel = vel; _footWall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

            _playerId = id;
            lock (ServerFrames.FramesLock)
            {
                SavedState.TryApply(id);
                if (ApplyServerRequests(session, ch, pos, t)) { _cur.Frame = frames.FindByMember(id); PublishObserver(camera.Position, reg, t); return; }
                var frame = frames.FindByMember(id);
                // Far from your frame's berth (the game's fast travel, a respawn elsewhere): you are not in it any more - left
                // quietly (no split onto an orbit made of a berth-to-berth offset; no pin back as its anchor). PlayerOrbit.MovedOut.
                if (frame != null && !_tpPending && frame.PendingBerthShift.LengthSquared() < 1 && PlayerOrbit.MovedOut(frame, pos))
                {
                    frames.RemoveMember(id);
                    Event($"player left frame #{frame.Id}: {(pos - frame.BerthCenter).Length() / 1000:F0} km from its berth (moved by something else)");
                    frame = null;
                }
                // Stood up in a ship that was stowed with you seated in it: a seated character is no grid, so it was never
                // made a member - you walked off into no frame (no orbit; left behind when the frame moved). Out in the
                // rails' space, beside a framed ship: its frame (PlayerOrbit.FrameToJoin; tested offline).
                if (frame == null && !_tpPending && !ForceStow && !_pendingOrbit.HasValue && !VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out _))
                {
                    _joinGrids.Clear();
                    // (with their boxes, from the server's snapshot: measured to a ship's hull, not its origin)
                    lock (ServerFrames.GridPositions)
                        foreach (var kv in ServerFrames.GridPositions)
                            _joinGrids.Add(ServerFrames.View(kv.Key, out var gv) && gv.HasBox
                                ? new PlayerOrbit.GridState(kv.Key, kv.Value, Vector3D.Zero, gv.Box.Min, gv.Box.Max)
                                : new PlayerOrbit.GridState(kv.Key, kv.Value, Vector3D.Zero));
                    var join = PlayerOrbit.FrameToJoin(pos, _joinGrids, gid => frames.FindByMember(gid));
                    // (never a frame whose berth is out of reach: a ship framed this tick was moved to its berth 6,640 km off -
                    //  joined beside where it had stood, you were split off at once onto an orbit made of that offset)
                    if (join != null && PlayerOrbit.MovedOut(join, pos)) join = null;
                    if (join != null && frames.AddMember(join, id))
                    {
                        frame = join;
                        Event($"player joins frame #{join.Id} (stood up beside its ship: {(pos - join.BerthCenter).Length():F0} m from its centre)");
                    }
                }
                _cur.Frame = frame;
                SeatGrid = null; SeatBlock = null; SeatGridServerId = 0;
                if (frame == null) _riderN = 0;   // (no frame: the state's defaults - not riding, not at an anchor; no walk's warp scale)
                if (frame != null) UpdatePlayerFrame(session, ch, frame, pos, vel, t, dt);
                else if (!_tpPending)
                {
                    if (!landed) vel = ApplyFictitious(ch, pos, vel, dt);
                    if (!ForceStow && EncounterFrames.TryAdoptPlayer(id, pos)) { _cur.Frame = frames.FindByMember(id); PublishObserver(camera.Position, reg, t); return; }
                    TryStow(session, ch, id, pos, vel, t);
                }
            }
        }

        else if (ch != null)
        {
            // Seated: the grid you sit in carries you. Its frame is yours (observer, warp, planning),
            // and your state is the camera's (the seat moves with the grid).
            Vector3D pos = ch.Data.GetWorldTransform().Position;
            _lastPos = pos;
            // whose orbit: the grid you sit in, its frame (or the frame it sits in, pasted and not yet framed), its own
            // offset and velocity - every tick, nothing left from a walk (PlayerOrbit; tested offline)
            _seatChar = ch;
            // (on foot's own requests do not wait for you to stand up - an orbit, a stow, a teleport asked for on foot fired
            //  when you stood up, and moved you without your ship)
            if (_pendingOrbit != null || ForceStow || _tpPending) { _pendingOrbit = null; ForceStow = false; _tpPending = false; Event("seated: on-foot requests (orbit, stow, teleport) dropped"); }
            var so = SeatedOrbit(session, pos);
            // the seat grid's own velocity (not the camera's motion over wall-clock time: slow sim speed, a swinging
            // chase camera and a burn's lag all showed in the local orbit planned from it)
            _lastVel = _seatVelOk ? _seatVel : OrbitDisplay.MeasuredVelocity;
            _riderN = 0;   // (the seat carries you: no walk's warp scale left for when you stand up)
            // (a seat on a grid joined to the anchor - a sub-grid of the anchor ship: it moves as one with the anchor, so you are
            //  in the anchor: the frame's orbit, not a rider's drift about it - which never came, in warp most of all)
            // (your offset is kept - the card, the altitude and the plan from where you sit, 67 m off the anchor's centre in a
            //  Hercules - but the joints hold you to the anchor: no drift about it to plot. Made "in the anchor" outright, the
            //  card read the anchor's own orbit as yours.)
            // (seated in the anchor itself too: its pin lets it sit up to ~40 m off the frame's centre - your orbit from where it
            //  is, still no drift plot)
            bool rigid = so.Frame != null && (so.InAnchor || (SeatGridServerId != 0 && ServerFrames.View(SeatGridServerId, out var sjv) && sjv.AnchorJoined));
            _cur.AnchorRigid = rigid;
            // (in warp, the server's true relative velocity for the seat's grid - the steering's own - not its physical one / N)
            if (so.Frame != null && !so.InAnchor && !rigid && SystemHost.Timescale > 1.0 && SeatGridServerId != 0 && ServerFrames.View(SeatGridServerId, out var stv) && stv.HasTrueRel) so.Velocity = stv.TrueRel;
            if (so.Frame != null && !so.InAnchor) so.AtAnchor = AnchorDistance(so.Frame, pos) <= 100.0;   // (the same test as on foot: its box)
            lock (ServerFrames.FramesLock) _cur.Frame = so.Frame;
            // (the anchor's pilot rides nothing: the frame's orbit is theirs - the orbit disc, no plot about themselves)
            _cur.RiderFrame = so.Frame == null ? -1 : so.Frame.Id; _cur.RiderOffset = so.Offset; _cur.RiderVelocity = so.Velocity; _cur.AtAnchor = so.AtAnchor;
            SeatedWhy = so.Why;
            long cid = IdOf(ch);
            _playerId = cid;   // (kept current seated too: loaded in a seat, it stayed 0 - the save lost your membership)
            lock (ServerFrames.FramesLock)
            {
                // a membership from walking or a stow that is not the seat's frame (the ship split off or arrived with you in
                // it): left - standing up in the old frame threw you 100+ km onto a nonsense orbit (PlayerOrbit.KeepMembershipSeated)
                var memberOf = frames.FindByMember(cid);
                if (!PlayerOrbit.KeepMembershipSeated(memberOf, so.Frame)) { frames.RemoveMember(cid); Event($"seated: left frame #{memberOf.Id} (your ship is in {(so.Frame != null ? "#" + so.Frame.Id : "no frame")})"); }
            }
            // (seated, the grid carries the player: a frame's arrival, merge or shift request is for a walker - kept, it was
            //  replayed when you stood up: thrown by the move a second time, 100+ km)
            if (_pendingArrival != null || _pendingMerge || _pendingShift.LengthSquared() > 0) { DropPending(); Event("player requests dropped: seated (the ship carries you)"); }
        }

        PublishObserver(camera.Position, reg, t);
        if (ch != null) DelfosHeat.Apply(session, ch, ch.Data.GetWorldTransform().Position);   // the client copy (effect, warning)

        // Warp lock (SE1 WarpPolicy, simplified): warp only advances the rails, so it is allowed only
        // while the player coasts in a conjunction. Materialized (in a planet cell or legacy space) = x1.
        if (_cur.Frame == null && SystemHost.Timescale != 1.0)
        {
            Event($"warp x{SystemHost.Timescale} -> x1 (player is materialized; warp is rails-only)");
            SystemHost.Timescale = 1.0;
        }
        if (MapView.Visible) OrbitDisplay.Clear();
        else if (_cur.Frame != null && Observer.HasValue)
            OrbitDisplay.DrawFrameOrbit(session, camera, Observer.Value, _cur.Frame, reg, t);
        // (state passed in, lambdas capture nothing - cached by the compiler: these allocated a closure and a delegate each,
        //  every frame)
        var ta = new TickArgs(session, camera, t);
        Guard("MapInput.Poll", static a => MapInput.Poll(), ta);
        Guard("DevFlight.ClientTick", static a => DevFlight.ClientTick(a.Session), ta);
        Guard("ApplyTargetTurns", static a => ApplyTargetTurns(a.Session), ta);
        Guard("QuietCrossingShake", static a => QuietCrossingShake(a.Session), ta);
        Guard("SnapCameraToGridTurn", static a => SnapCameraToGridTurn(a.Session, a.Camera), ta);
        Guard("CrossingFade", static a => CrossingFade.Tick(a.Session), ta);
        Guard("Invariants", static a => Invariants.ClientCheck(), ta);
        Guard("DevTurn.ClientTick", static a => DevTurn.ClientTick(a.Session), ta);
        Guard("DevReentry.ClientTick", static a => DevReentry.ClientTick(a.Session, a.Camera), ta);
        Guard("AttitudeHold.ClientTick", static a => AttitudeHold.ClientTick(a.Session), ta);
        Guard("FlyTest.ClientTick", static a => FlyTest.ClientTick(a.Session), ta);   // (DEV: harness key flytest - 1 km, level, cruise)
        Guard("QuickLaunch.ClientTick", static a => QuickLaunch.ClientTick(a.Session), ta);   // (DEV: harness key quicklaunch -> the aero mod's quick launch)
        WarpControl.Session = session;
        Guard("WarpControl.Tick", static a => WarpControl.Tick(), ta);
        Guard("MapView.Tick", static a => MapView.Tick(a.Session, a.Camera, a.T), ta);
        Guard("FrameMarkers.Tick", static a => FrameMarkers.Tick(a.Session, a.Camera, a.T), ta);
        Guard("Maneuvers.HudTick", static a => Maneuvers.HudTick(a.Session, a.Camera, a.T), ta);
        Guard("AutoBurn.Tick", static a => AutoBurn.Tick(a.Session, a.T), ta);
        Guard("OrbitHud.Draw", static a => OrbitHud.Draw(a.Session), ta);
        Guard("BuildHints.Draw", static a => BuildHints.Draw(a.Session), ta);   // (holding a block: the aimed ship's stability, top speed)
        Guard("SpdIcon.Draw", static a => SpdIcon.Draw(a.Session), ta);   // (seated: the cockpit SPD box's missing speedometer)
        Guard("StructureAlerts.Tick", static a => StructureAlerts.Tick(a.Session), ta);   // (seated: aero's structure failures on your ship, a toast)
        Guard("AlignButton.Draw", static a => AlignButton.Draw(a.Session), ta);   // (seated: the attitude hold's toggle after the dampeners, its hint)
        Guard("EffectCull.Tick", static a => EffectCull.Tick(a.Session, a.Camera.Position), ta);   // (far grids' looping block effects hidden: drawn on the sky past the far plane)
        Guard("WarpBar", static a => WarpBar.DrawHud(a.Session), ta);
        // On rails the game's SPD (your velocity in the frame) is 0: show your speed about the body.
        Guard("HudSpeed", static a => { if (_cur.Frame != null && OrbitHud.Current != null) GameUi.SetHudSpeed(a.Session, (float)OrbitHud.Current.Speed); }, ta);
        HoldActions.Hook(); HoldActions.ClientTick();   // (the attitude holds pressed as cockpit toolbar actions)
        SystemHost.ReadSpinClock(ta.Session);   // (the client's own copy of the shared game clock: the planets' spin)
        Guard("SunDriver.Tick", static a => SunDriver.Tick(a.Session, a.Camera.Position, a.T), ta);
        Guard("StarProxy.Tick", static a => StarProxy.Tick(a.Session, a.Camera, a.T), ta);
        Guard("EntryHost.ClientTick", static a => EntryHost.ClientTick(a.T), ta);
    }

    public static long PlayerId => _playerId;
    public static Vector3D PlayerPosition => _lastPos;
    /// <summary>The player riding a frame: its id, offset from the frame's centre and velocity relative to it (last tick).</summary>
    public static long RiderFrame => _pub.RiderFrame;
    public static Vector3D RiderOffset => _pub.RiderOffset;
    public static Vector3D RiderVelocity => _pub.RiderVelocity;

    // ───────────────────────────── observer frame (for renderers) ─────────────────────────────

    private static void PublishObserver(Vector3D cam, SystemRegistry reg, double t)
    {
        _pub = _cur;   // (the player's state, whole: everything after this in the tick, and the server, read it)
        var f = _cur.Frame;
        // (the frame's elements, body and berth are the server's, written each tick: copied under its lock - a torn read
        //  jittered the observer for a frame)
        KeplerianElements fel = default; string fbody = null; Vector3D fberth = default;
        if (f != null) lock (ServerFrames.FramesLock) { fel = f.Elements; fbody = f.ParentBodyName; fberth = f.BerthCenter; }
        if (f != null && reg.Find(fbody) is GravityBody parent)
        {
            StateVector cur = OrbitPropagation.StateAt(fel, t);
            StateVector cel = parent.StateInRoot(cur, t);
            var of = new ObserverFrame(fberth, cel.Position, cel.Velocity, null);
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
        // (DEV log, rate-limited: a frame that drops for a tick hides the planet proxies - say why)
        if (Observer.HasValue && OrbitalLog.Frames && _obsLostLogs < 20) { _obsLostLogs++; Log.Default?.Info($"[ORBIT] observer lost: no frame (seated {_cur.Seated}, seat grid {SeatGridServerId}, why '{SeatedWhy}')"); }
        Observer = null;
        ObserverPlanet = null;
    }
    private static int _obsLostLogs;

    // ── helpers ──

    internal static Entity PlayerCharacter(Keen.VRage.Core.Game.Systems.Session session)
    {
        return PlayerCache.Of(session);
    }

    private static void SetVelocity(Entity ch, Vector3D v)
    {
        if (!IsFinite(v)) return;
        ch.Data.Set(new RigidBodyData { LinearVelocity = (Vector3)v });
    }

    private static long IdOf(Entity e)
    {
        if (_ids.TryGetValue(e, out var box)) return box.Value;
        long id = _nextId++;
        _ids.Add(e, new System.Runtime.CompilerServices.StrongBox<long>(id));
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

    /// <summary>A frame event: the status's last event, and an [ORBIT-FRAME] line where <see cref="OrbitalLog.Frames"/> (dev).</summary>
    internal static void Event(string s)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        if (OrbitalLog.Frames) Log.Default?.Info("[ORBIT-FRAME] " + s);
    }

    /// <summary>A frame event that is a failure or lost state: the same [ORBIT-FRAME] line, in every game.</summary>
    internal static void Problem(string s)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-FRAME] " + s);
    }

}
