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
    public const double SpeedCap = 290.0;          // m/s: SE2 world cap is 300
    public const double TeleportSettle = 3.0;      // s: give a teleport this long to land

    /// <summary>The frame the local observer renders from. Null = legacy space (no frame; literal world).</summary>
    public static ObserverFrame? Observer;
    /// <summary>Body whose planet cell the observer is in (planet frame), else null.</summary>
    public static string ObserverPlanet;
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
        _mult = gravityMultiplier > 0 ? gravityMultiplier : 1.0;
        if (!SystemHost.EnsureBuilt(gravityMultiplier)) return;
        double dt = SystemHost.AdvanceClock();
        if (dt <= 0) return; // once per frame
        var reg = SystemHost.Registry;
        var frames = SystemHost.Frames;
        if (reg == null || frames == null) return;
        double t = SystemHost.Now;

        Entity ch = PlayerCharacter(session);
        Debug = ch == null ? "no character" : IsSeated(ch) ? "seated (skipped)" : $"eva tpPending={_tpPending} wasInKeep={_wasInKeep}";
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

            var frame = frames.FindByMember(id);
            PlayerFrame = frame;
            if (frame != null) { _hsActive = false; UpdatePlayerFrame(session, ch, frame, pos, vel, t, dt); }
            else if (!_tpPending)
            {
                if (_hsActive && !landed) vel = StepHighSpeed(ch, pos, vel, dt);
                TryStow(session, ch, id, pos, _hsActive ? _hsVel : vel, t);
            }
        }

        PublishObserver(camera.Position, reg, t);

        // Warp lock (SE1 WarpPolicy, simplified): warp only advances the rails, so it is allowed only
        // while the player coasts in a conjunction. Materialized (in a planet cell or legacy space) = x1.
        if (PlayerFrame == null && SystemHost.Timescale != 1.0)
        {
            Event($"warp x{SystemHost.Timescale} -> x1 (player is materialized; warp is rails-only)");
            SystemHost.Timescale = 1.0;
        }
        if (PlayerFrame != null && Observer.HasValue)
            OrbitDisplay.DrawFrameOrbit(session, camera, Observer.Value, PlayerFrame, reg, t);
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
            Event($"STOW (orbit command) -> frame #{pf.Id} about {pb}: a={pel.SemiMajorAxis / 1000:F1} km e={pel.Eccentricity:F3}");
            return;
        }
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
        {
            _wasInKeep = false;
            if (!ForceStow) return; // legacy space: v1 never captures here unless forced
        }
        GravityBody node = body != null ? reg.Find(body) : null;
        BodyDefinition def = body != null ? reg.FindDefinition(body) : null;
        if (node == null) { ForceStow = false; return; }

        StateVector borg = node.OriginInRoot(t);
        Vector3D rel = pos - cell;
        Vector3D cel = borg.Position + rel;
        Vector3D celVel = borg.Velocity + vel;
        double d = rel.Length();
        double shell = PlanetBerths.ShellRadius(def);
        double keep = PlanetBerths.KeepRadius(def);
        Debug += $" cell={body} d={d / 1000:F1}km shell={shell / 1000:F1} keep={keep / 1000:F1}";

        KeplerianElements elBody = OrbitalMath.ToElements(new StateVector(rel, vel), node.Mu, t);
        if (d < keep)
        {
            _wasInKeep = true;
            if (!ForceStow)
            {
                if (d < shell * PlanetBerths.StowShellMargin) return;                       // voxel frame owns low flight
                if (!IsFinite(elBody.SemiMajorAxis) || !FrameRails.EscapesShell(elBody, t, shell, keep, MaterializeLead)) return;
            }
        }
        else if (!_wasInKeep && !ForceStow) return; // v1 gate: only departures from a planet are captured

        GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
        StateVector porg = parent.OriginInRoot(t);
        var el = OrbitalMath.ToElements(new StateVector(cel - porg.Position, celVel - porg.Velocity), parent.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;

        var frame = SystemHost.Frames.CreateFrame(parent.Name, el, id);
        if (frame == null) return;
        _hsActive = false;
        ForceStow = false;
        _wasInKeep = false;
        StartTeleport(session, frame.BerthCenter, Vector3D.Zero, t);
        Event($"STOW -> frame #{frame.Id} orbiting {parent.Name}: r={(cel - porg.Position).Length() / 1000:F1} km " +
              $"|v|={vel.Length():F1} m/s a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F3} slot={frame.BerthSlotId} berth={ServerPlanetBeacon.Fmt(frame.BerthCenter)}");
    }

    // ───────────────────────────── treadmill (conjunction frame) ─────────────────────────────

    private static void UpdatePlayerFrame(Keen.VRage.Core.Game.Systems.Session session, Entity ch, ProximityFrame f,
                                          Vector3D pos, Vector3D vel, double t, double dt)
    {
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
            }
        }

        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        FrameRails.FoldDrain(f, t, FoldThreshold, MaxApparentAccel * dt, ref cur, out Vector3D slice);
        if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;

        TryReparent(f, t);
        TryMaterialize(session, f, t);
    }

    /// <summary>Patched conics: leave the parent's SOI or enter a child's -> re-express the orbit there.</summary>
    private static void TryReparent(ProximityFrame f, double t)
    {
        var reg = SystemHost.Registry;
        GravityBody parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        StateVector cel = parent.StateInRoot(cur, t);
        GravityBody now = reg.Root.DeepestSoiContaining(cel.Position, t);
        if (now == null || now == parent) return;
        StateVector norg = now.OriginInRoot(t);
        var el = OrbitalMath.ToElements(new StateVector(cel.Position - norg.Position, cel.Velocity - norg.Velocity), now.Mu, t);
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

        Vector3D worldPos = cellCenter + cel.Position;   // planet cells are 1:1 inertial windows (no spin in v1)
        Vector3D worldVel = cel.Velocity;
        double speed = worldVel.Length();
        // Above the cap the planet cell runs HighSpeed: land with zero physics velocity and carry the
        // true velocity virtually (StepHighSpeed). Below it, plain physics.
        bool hs = speed > SpeedCap;
        Vector3D applied = hs ? Vector3D.Zero : worldVel;
        _hsActive = hs;
        _hsVel = worldVel;
        if (hs)
        {
            // HighSpeed senses gravity through the physics step; jetpack dampeners would cancel it.
            var noDamp = new PlayerRequest { Dampeners = false };
            ServerPlanetBeacon.ApplyToCharacter(session, noDamp, "client");
            ServerPlanetBeacon.PendingPlayer = noDamp;
        }

        long fid = f.Id;
        SystemHost.Frames.Dissolve(fid);
        PlayerFrame = null;
        _wasInKeep = true;
        StartTeleport(session, worldPos, applied, t);
        Event($"ARRIVE frame #{fid} -> {node.Name} cell: r={cel.Position.Length() / 1000:F1} km (shell {shell / 1000:F1}) " +
              $"|v|={speed:F0} m/s{(hs ? " -> HighSpeed" : "")}");
    }

    // ───────────────────────────── HighSpeed (planet cell, above the cap) ─────────────────────────────

    public const double HighSpeedExitFraction = 0.9;  // drop back to physics below cap × this
    public const double SurfaceGuard = 2000.0;        // m above the body radius: never step closer

    private static bool _hsActive;
    private static double _mult = 1.0;
    private static Vector3D _hsVel;
    public static bool HighSpeedActive => _hsActive;
    public static Vector3D HighSpeedVelocity => _hsVel;

    /// <summary>
    /// SE-Aerospace's HighSpeed (FnB virtual velocity + teleport stepping), SE2 form. The true velocity
    /// lives in <see cref="_hsVel"/>; the character's physics velocity is zeroed every tick and whatever
    /// the engine added during the tick (planet gravity, jetpack thrust) is read back and folded into
    /// the virtual velocity: the same force-sensor idea as the conjunction anchor drain. The character
    /// is then stepped by v·dt. Below cap × 0.9 it hands back to physics; near the surface it stops
    /// stepping (surface guard) and hands back at the cap: no tunnelling into terrain.
    /// Returns the velocity to report this tick.
    /// </summary>
    private static Vector3D StepHighSpeed(Entity ch, Vector3D pos, Vector3D measured, double dt)
    {
        // Gravity from the MODEL (patched 1/r² law × world multiplier): the per-tick physics zeroing
        // wipes the engine's own gravity step before it can be sensed (seen in game: hsV never changed).
        // Thrust is not yet folded in while in HighSpeed (SE1 read it from GetAcceleration).
        if (VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out string gb, out Vector3D gc)
            && SystemHost.BeaconOf.TryGetValue(gb, out var beacon))
        {
            var law = beacon.Gravity; law.Multiplier = _mult;
            Vector3D r = pos - gc;
            double dist = r.Length();
            if (dist > 1) _hsVel += r * (-law.At(dist) * dt / dist);
        }
        double speed = _hsVel.Length();
        if (speed < SpeedCap * HighSpeedExitFraction)
        {
            _hsActive = false;
            SetVelocity(ch, _hsVel);
            Event($"HighSpeed off at {speed:F0} m/s (physics takes over)");
            return _hsVel;
        }

        Vector3D next = pos + _hsVel * dt;
        var reg = SystemHost.Registry;
        if (VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
        {
            var def = reg.FindDefinition(body);
            double floor = (def != null ? def.RadiusMeters : 0) + SurfaceGuard;
            if ((next - cell).Length() < floor)
            {
                _hsActive = false;
                Vector3D capped = _hsVel * (SpeedCap / speed);
                SetVelocity(ch, capped);
                Event($"HighSpeed surface guard at {((pos - cell).Length() - (def?.RadiusMeters ?? 0)) / 1000:F1} km alt: " +
                      $"{speed:F0} m/s -> {SpeedCap:F0} m/s physics");
                return capped;
            }
        }

        SetVelocity(ch, Vector3D.Zero);
        var wt = ch.Data.GetWorldTransform();
        ch.Data.Set(new WorldTransform(next, wt.Orientation));
        // The client write alone is overridden by the character controller (seen in game);
        // the transform also goes through the server, the way EntityAdmin teleports.
        ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Position = next, Velocity = Vector3D.Zero };
        return _hsVel;
    }

    /// <summary>
    /// Harness: put the player on a given orbit around a planet, starting at APOAPSIS (SE1's /orbit
    /// command). Framed: replaces the frame's elements. Not framed: stows with those elements.
    /// </summary>
    public static string SetOrbit(string body, double apoAltKm, double periAltKm, double incDeg)
    {
        var reg = SystemHost.Registry;
        var node = reg?.Find(body);
        var def = reg?.FindDefinition(body);
        if (node == null || def == null) return "no such body";
        double R = def.RadiusMeters;
        double ra = R + Math.Max(apoAltKm, periAltKm) * 1000, rp = R + Math.Min(apoAltKm, periAltKm) * 1000;
        double a = 0.5 * (ra + rp);
        double va = Math.Sqrt(node.Mu * (2.0 / ra - 1.0 / a));
        double inc = incDeg * Math.PI / 180.0;
        var pos = new Vector3D(ra, 0, 0);
        var vel = new Vector3D(0, Math.Cos(inc), Math.Sin(inc)) * va;
        var el = OrbitalMath.ToElements(new StateVector(pos, vel), node.Mu, SystemHost.Now);
        if (!IsFinite(el.SemiMajorAxis)) return "degenerate orbit";
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
            Observer = new ObserverFrame(cell, o.Position, o.Velocity, node);
            ObserverPlanet = body;
            return;
        }
        Observer = null;
        ObserverPlanet = null;
    }

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

    private static Entity PlayerCharacter(Keen.VRage.Core.Game.Systems.Session session)
    {
        var list = new List<Entity>();
        return session.TryFillAliveCharacters(list) && list.Count > 0 ? list[0] : null;
    }

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

    private static void Event(string s)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss} {s}";
        Log.Default?.Info("[ORBIT-FRAME] " + s);
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
