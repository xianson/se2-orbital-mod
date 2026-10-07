using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV: fly a ship without a human. Seat the local character in a grid's cockpit (the game's own
/// ISeat.TrySetPilotAsync, what pressing F calls), and hold a thrust command on the grid
/// (ControlData.Movement, the vector a pilot's keys write, read by the grid's thrusters) for a time.
/// </summary>
public static class DevFlight
{
    public static string Status = "";
    public static string Info = "";
    private static Vector3 _move;
    private static double _until;
    private static long _gridId, _seatGrid;

    /// <summary>Client: seat the character in the cockpit nearest the named grid (or the nearest seat).</summary>
    public static string Seat(Keen.VRage.Core.Game.Systems.Session session, string gridName)
    {
        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return "no character";
        Vector3D target = ch.Data.GetWorldTransform().Position;
        if (!string.IsNullOrEmpty(gridName))
        {
            bool found = false;
            foreach (var g in GridMembers.All())
                if (g.IsServer && g.DisplayName.IndexOf(gridName, StringComparison.OrdinalIgnoreCase) >= 0 && GridMembers.Mass(g) > 500)
                { target = GridMembers.Position(g); _seatGrid = g.Id; found = true; break; }
            if (!found) return $"no grid '{gridName}'";
        }
        Entity best = null; double bd = double.MaxValue;
        foreach (var e in session.GetEntitiesOfType<SeatComponent>())
        {
            double d = (e.Data.GetWorldTransform().Position - target).Length();
            if (d < bd) { bd = d; best = e; }
        }
        if (best == null) return "no seat";
        var seat = best.TryGet<SeatComponent>() as ISeat;
        if (seat == null) return "no ISeat";
        SeatAsync(seat, ch);
        return $"seating in the seat {bd:F0} m from '{gridName}'";
    }

    /// <summary>Client: seat the character in the cockpit nearest grid `id` (server position).</summary>
    public static string SeatId(Keen.VRage.Core.Game.Systems.Session session, long id)
    {
        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return "no character";
        // (never seat to seat: the game's seat recharge (CharacterChargeStatFromSeatResourceComponent) links a second
        // sink without dropping the first, and the server crashes on the orphan at the next unseat - the game never does it)
        if (FrameHost.Seated) return "already seated: unseat first";
        Vector3D target;
        lock (ServerFrames.GridPositions) if (!ServerFrames.GridPositions.TryGetValue(id, out target)) return $"no grid {id}";
        Entity best = null; double bd = double.MaxValue;
        foreach (var e in session.GetEntitiesOfType<SeatComponent>())
        {
            double d = (e.Data.GetWorldTransform().Position - target).Length();
            if (d < bd) { bd = d; best = e; }
        }
        if (best == null || bd > 60) return $"no seat within 60 m of grid {id}";
        var seat = best.TryGet<SeatComponent>() as ISeat;
        if (seat == null) return "no ISeat";
        _seatGrid = id;
        SeatAsync(seat, ch);
        return $"seating in the seat {bd:F0} m from grid {id}";
    }

    /// <summary>Client: the character out of whatever seat it is in (the seat's own TryClearPilotAsync).</summary>
    public static string Unseat(Keen.VRage.Core.Game.Systems.Session session)
    {
        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return "no character";
        foreach (var e in session.GetEntitiesOfType<SeatComponent>())
        {
            var seat = e.TryGet<SeatComponent>() as ISeat;
            if (seat?.Pilot != ch) continue;
            UnseatAsync(seat);
            return "leaving the seat";
        }
        return "not seated";
    }

    private static async void UnseatAsync(ISeat seat)
    {
        try { bool ok = await seat.TryClearPilotAsync(); Status = "unseat -> " + ok; }
        catch (Exception e) { Status = "unseat failed: " + e.Message; }
    }

    /// <summary>DEV: every seat in the client and server sessions, with its distance from a point.</summary>
    public static string ListSeats(Keen.VRage.Core.Game.Systems.Session client, Vector3D at)
    {
        var sb = new System.Text.StringBuilder();
        void Scan(Keen.VRage.Core.Game.Systems.Session s, string tag)
        {
            if (s == null) return;
            int n = 0;
            foreach (var e in s.GetEntitiesOfType<SeatComponent>())
            {
                n++;
                var top = e.GetTopLevelParent();
                string name = "?";
                try { name = top?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>()?.DisplayName.EvaluateText() ?? "?"; } catch { }
                sb.Append($"{tag} '{name}' {(e.Data.GetWorldTransform().Position - at).Length() / 1000:F2} km; ");
            }
            sb.Append($"{tag} seats {n}; ");
        }
        Scan(client, "client");
        Scan(ServerPlanetBeacon.ServerSession, "server");
        return sb.ToString();
    }

    private static async void SeatAsync(ISeat seat, Entity ch)
    {
        try { bool ok = await seat.TrySetPilotAsync(ch); Status = "seat -> " + ok; }
        catch (Exception e) { Status = "seat failed: " + e.Message; }
    }

    /// <summary>Hold a thrust command (grid-local: x right, y up, z backward; each -1..1) for seconds.</summary>
    /// <summary>The harness is holding its own command.</summary>
    public static bool Busy => _until > 0;

    private static Vector3D _cmdDir; private static double _cmdK; private static bool _cmdOn;

    /// <summary>
    /// Thrust along a world direction at a fraction k of full (0 = stop): the seated grid's
    /// thrusters (server) or the jetpack (client), as the grid-local / character-local vector a
    /// pilot's keys would write.
    /// </summary>
    public static void Command(Keen.VRage.Core.Game.Systems.Session session, Vector3D dirWorld, double k)
    {
        _cmdDir = dirWorld; _cmdK = k; _cmdOn = dirWorld.LengthSquared() > 1e-9;   // k = 0: turn only
        if (!FrameHost.Seated)
        {
            var ch = FrameHost.PlayerCharacter(session);
            if (ch != null) try { ch.Data.Set(new ControlData { Movement = _cmdOn && k > 0 ? Local(ch.Data.GetWorldTransform().Orientation, dirWorld, k) : Vector3.Zero, Rotation = Vector3.Zero }); } catch { }
        }
    }

    private static Vector3 Local(Quaternion q, Vector3D dirWorld, double k)
    {
        Vector3D local = Vector3D.Transform(dirWorld, Quaternion.Inverse(q));
        double m = Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z)));
        return m > 1e-6 ? new Vector3((float)(local.X / m), (float)(local.Y / m), (float)(local.Z / m)) * (float)k : Vector3.Zero;
    }

    /// <summary>
    /// Server tick: a seated pilot's command goes to the grid they sit in, as a pilot would fly it:
    /// the ship turns so its strongest thrust axis points along the burn (see ClientTick: the
    /// steering is the pilot's own, written on the client), and that axis fires only once aligned.
    /// </summary>
    private static bool _cmdWasOn;
    public static string Attitude = "";
    public const double AlignDeg = 3.0;
    public static bool Aligned;
    /// <summary>Degrees between the nearest ship's main thrust axis and the next burn (NaN: none).</summary>
    public static double OffBurnDeg = double.NaN;

    public static void ServerCommandTick()
    {
        if (!FrameHost.Seated || (!_cmdOn && !_cmdWasOn) || Busy) return;
        // the ship you fly (your seat's grid) - not the nearest heavy grid: docked to a carrier, the carrier turned and burned
        OrbitalGridComponent best = FrameHost.SeatGridServerId != 0 && GridMembers.Get(FrameHost.SeatGridServerId) is OrbitalGridComponent sg && sg.IsServer ? sg : null;
        double bd = 100;
        if (best == null)
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer) continue;
                double d = (GridMembers.Position(g) - FrameHost.PlayerPosition).Length();
                if (d < bd && GridMembers.Mass(g) > 500) { bd = d; best = g; }
            }
        _cmdWasOn = _cmdOn;
        if (best == null) return;
        var e = best.Entity;
        _serverPos = GridMembers.Position(best);
        // How far the ship's main thrust is off the next burn, even while nothing flies it (warp uses it).
        {
            Vector3D bd0 = Maneuvers.BurnDirWorld;
            OffBurnDeg = bd0.LengthSquared() > 1e-9 ? Turn(e.Data.GetWorldTransform().Orientation, MainAxis(e, out _), bd0, out _, out _) * 180 / Math.PI : double.NaN;
        }
        if (!_cmdOn)
        {
            _move = Vector3.Zero; Write(e, false); Aligned = false; Attitude = ""; _steer = false;
            return;
        }
        var q = e.Data.GetWorldTransform().Orientation;
        Vector3 uLocal = MainAxis(e, out float force);
        double ang = Turn(q, uLocal, _cmdDir, out _, out _);
        Aligned = ang * 180 / Math.PI <= AlignDeg;
        _move = Aligned && _cmdK > 0 ? uLocal * (float)_cmdK : Vector3.Zero;
        Write(e, Aligned && _cmdK > 0);
        _steer = true; _steerAxis = uLocal;
        Attitude = $"{ang * 180 / Math.PI:F1} deg off, {(Aligned ? "aligned" : "turning")} ({_steerMode}), axis {uLocal} {force / 1000:F0} kN";
    }

    /// <summary>
    /// The shortest turn taking the grid-local axis (at orientation q) onto a world direction, roll
    /// left free: its angle (rad), the target orientation, and the turn's world axis.
    /// </summary>
    private static double Turn(Quaternion q, Vector3 axisLocal, Vector3D dirWorld, out Quaternion target, out Vector3D turnAxis)
    {
        Vector3D u = Vector3D.Transform((Vector3D)axisLocal, q);
        Vector3D b = Vector3D.Normalize(dirWorld);
        double ang = Math.Acos(Math.Clamp(Vector3D.Dot(u, b), -1, 1));
        target = q; turnAxis = Vector3D.Zero;
        if (ang < 1e-4) return ang;
        Vector3D axis = Vector3D.Cross(u, b);
        if (axis.LengthSquared() < 1e-12) axis = Math.Abs(u.X) < 0.9 ? Vector3D.Cross(u, Vector3D.UnitX) : Vector3D.Cross(u, Vector3D.UnitY);
        turnAxis = Vector3D.Normalize(axis);
        target = Quaternion.Normalize(Quaternion.CreateFromAxisAngle((Vector3)turnAxis, (float)ang) * q);
        return ang;
    }

    /// <summary>The grid's strongest thrust direction (grid-local unit axis) and its force (N).</summary>
    public static Vector3 MainAxis(Entity e, out float force)
    {
        force = 0; var axis = new Vector3(0, 0, -1);
        if (!e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)) return axis;
        var p = mt.Regular.Positive; var n = mt.Regular.Negative;
        float[] f = { p.X, n.X, p.Y, n.Y, p.Z, n.Z };
        Vector3[] ax = { new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1) };
        for (int i = 0; i < 6; i++) if (f[i] > force) { force = f[i]; axis = ax[i]; }
        return axis;
    }

    private static bool _burn;
    private static double _burnMin;

    /// <summary>DEV: fly the next burn with translation thrust along the marker until it completes (or seconds).</summary>
    public static string Burn(double seconds)
    {
        var r = Thrust(Vector3.Zero, seconds);
        _burn = true; _burnMin = 0;
        return "burn: " + r;
    }

    public static string Thrust(Vector3 move, double seconds)
    {
        _burn = false;
        if (!FrameHost.Seated) { _move = move; _until = Wall() + seconds; _gridId = -1; return $"jetpack thrust {move} for {seconds:F0} s"; }
        // The grid you sat into (named at `seat`), else the heaviest one within 100 m of you.
        OrbitalGridComponent best = _seatGrid != 0 ? GridMembers.Get(_seatGrid) : null; double bm = best != null ? GridMembers.Mass(best) : -1;
        if (best == null)
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer || (GridMembers.Position(g) - FrameHost.PlayerPosition).Length() > 100) continue;
                double m = GridMembers.Mass(g);
                if (m > bm) { bm = m; best = g; }
            }
        if (best == null) return "no grid under you";
        _gridId = best.Id;
        _move = move;
        _until = Wall() + seconds;
        return $"thrust {move} for {seconds:F0} s on '{best.DisplayName}' ({bm:F0} kg)";
    }

    /// <summary>Every frame on the server tick: write the command while it lasts.</summary>
    public static void ServerTick()
    {
        if (_gridId <= 0 || _until <= 0) return;
        var g = GridMembers.Get(_gridId);
        bool on = Wall() < _until;
        if (g == null) { _until = 0; return; }
        if (_burn)
        {
            // Point the translation thrust along the burn (grid-local), full on until little is left.
            if (Maneuvers.Nodes.Count == 0 || Maneuvers.BurnLeft < 0.1) { on = false; _burn = false; Status = "burn complete"; }
            else
            {
                var q = g.Entity.Data.GetWorldTransform().Orientation;
                Vector3D local = Vector3D.Transform(Maneuvers.BurnDirWorld, Quaternion.Inverse(q));
                double m = Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z)));
                float k = (float)Math.Max(0.25, Math.Min(1.0, Maneuvers.BurnLeft / 5.0));   // ease off for the last few m/s (not so far the dampeners win)
                _move = m > 1e-6 ? new Vector3((float)(local.X / m), (float)(local.Y / m), (float)(local.Z / m)) * k : Vector3.Zero;
            }
        }
        Write(g.Entity, on);
        _serverPos = GridMembers.Position(g);
        if (!on) { _until = 0; Status = "thrust done"; }
    }

    private static Vector3D _serverPos;
    private static Entity _client;

    /// <summary>
    /// Every frame on the client tick: the same on the client copy (a piloted grid simulates there,
    /// and its steering data syncs from the client). The ship is turned as the pilot turns it: in the
    /// cockpit's reticle mode by moving the target orientation the cockpit already keeps (as the
    /// game's own fast travel does; that data belongs to the cockpit and is never added or removed
    /// here), otherwise by the target angular velocity the mouse would write.
    /// </summary>
    private static bool _steer; private static Vector3 _steerAxis; private static string _steerMode = "-";
    public const double TurnRate = 0.6;   // rad/s, the most the angular mode asks for

    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (_cmdOn && FrameHost.Seated && !Busy)
        {
            var seat = FrameHost.SeatGrid;
            if (seat != null && seat.Data.Has<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>()) _client = seat;   // (the ship you fly)
            else if (_client == null || (_client.Data.GetWorldTransform().Position - _serverPos).Length() > 50)
            {
                _client = null; double bd = 50;
                foreach (var ce in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
                {
                    double d = (ce.Data.GetWorldTransform().Position - _serverPos).Length();
                    if (d < bd && ce.Data.Has<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>()) { bd = d; _client = ce; }
                }
            }
            if (_client != null)
            {
                try
                {
                    if (_steer) Steer(_client);
                    Write(_client, Aligned && _cmdK > 0);
                }
                catch (Exception ex) { Attitude = "steer: " + ex.Message; }
            }
        }
        else if (_steerMode == "angular" && _client != null)
        {
            // Hand the stick back: stop the turn we asked for (the cockpit writes it again on input).
            try { if (_client.Data.Has<AngularControlData>()) _client.Data.Set(new AngularControlData { TargetAngularVelocity = Vector3.Zero }); } catch { }
            try { Write(_client, false); } catch { }
            _steerMode = "-";
        }
        else if (_steerMode != "-" && _client != null) { try { Write(_client, false); } catch { } _steerMode = "-"; }
        if (_gridId == -1 && _until > 0)
        {
            // On foot: the jetpack reads the character's ControlData (the same vector a player's keys write).
            var ch = FrameHost.PlayerCharacter(session);
            bool on = Wall() < _until;
            if (_burn)
            {
                if (Maneuvers.Nodes.Count == 0 || Maneuvers.BurnLeft < 0.1) { on = false; _burn = false; Status = "burn complete"; }
                else if (_burnMin > 0 && Maneuvers.BurnLeft > _burnMin + 2) { on = false; _burn = false; Status = $"burn stopped: left grew to {Maneuvers.BurnLeft:F1} m/s"; }
                else if (ch != null)
                {
                    var q = ch.Data.GetWorldTransform().Orientation;
                    Vector3D local = Vector3D.Transform(Maneuvers.BurnDirWorld, Quaternion.Inverse(q));
                    double m = Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z)));
                    float k = (float)Math.Max(0.25, Math.Min(1.0, Maneuvers.BurnLeft / 5.0));
                    _burnMin = _burnMin <= 0 ? Maneuvers.BurnLeft : Math.Min(_burnMin, Maneuvers.BurnLeft);
                    _move = m > 1e-6 ? new Vector3((float)(local.X / m), (float)(local.Y / m), (float)(local.Z / m)) * k : Vector3.Zero;
                }
            }
            if (ch != null) try { ch.Data.Set(new ControlData { Movement = on ? _move : Vector3.Zero, Rotation = Vector3.Zero }); } catch (Exception e) { Status = "jetpack: " + e.Message; }
            if (!on) { _until = 0; if (Status != "burn complete") Status = "thrust done"; }
            return;
        }
        if (_gridId <= 0 || _until <= 0) return;
        if (_client == null || (_client.Data.GetWorldTransform().Position - _serverPos).Length() > 50)
        {
            _client = null; double bd = 50;
            foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
            {
                double d = (e.Data.GetWorldTransform().Position - _serverPos).Length();
                if (d < bd && e.Data.Has<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>()) { bd = d; _client = e; }
            }
        }
        if (_client != null) Write(_client, Wall() < _until);
    }

    private static void Steer(Entity e)
    {
        var q = e.Data.GetWorldTransform().Orientation;
        double ang = Turn(q, _steerAxis, _cmdDir, out var target, out var axisW);
        if (e.Data.TryGet<TargetControlData>(out var tcd))
        {
            // Reticle mode: the cockpit re-aims from this each frame; only the orientation is ours.
            tcd.TargetOrientation = target;
            e.Data.Set(tcd);
            _steerMode = "reticle";
        }
        else
        {
            // Angular mode: a rate toward the target, slowing into it (grid-local, rad/s).
            double rate = Math.Min(TurnRate, 1.5 * ang);
            Vector3D local = Vector3D.Transform(axisW * rate, Quaternion.Inverse(q));
            e.Data.Set(new AngularControlData { TargetAngularVelocity = (Vector3)local });
            _steerMode = "angular";
        }
    }

    private static void Write(Entity e, bool on)
    {
        try
        {
            Vector3 thrust = Vector3.Zero;
            if (on && e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt))
            {
                var p = mt.Regular.Positive; var n = mt.Regular.Negative;
                thrust = new Vector3(_move.X >= 0 ? _move.X * p.X : _move.X * n.X, _move.Y >= 0 ? _move.Y * p.Y : _move.Y * n.Y, _move.Z >= 0 ? _move.Z * p.Z : _move.Z * n.Z);
            }
            e.Data.Set(new ControlData { Movement = on ? _move : Vector3.Zero, Rotation = Vector3.Zero });
            string mts = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var m2) ? $"max +{m2.Regular.Positive} -{m2.Regular.Negative}" : "no MaxThrustData";
            string act = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ActiveThrustData>(out var at) ? $"active {at.ComputedThrustPerFrame}" : "no ActiveThrustData";
            string vol = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt) ? $"vol {vt.VoluntaryThrust}" : "no vol";
            Info = $"{(ReferenceEquals(e, _client) ? "client" : "server")}: {mts} | {act} | {vol} | thrust {thrust}";
            e.Data.Set(new Keen.Game2.Simulation.WorldObjects.Shared.Movement.OverriddenThrustData { DirectionalThrust = thrust });
        }
        catch (Exception ex) { Status = "thrust: " + ex.Message; }
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
