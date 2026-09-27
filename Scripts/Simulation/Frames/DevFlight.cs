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
        _cmdDir = dirWorld; _cmdK = k; _cmdOn = k > 0 && dirWorld.LengthSquared() > 1e-9;
        if (!FrameHost.Seated)
        {
            var ch = FrameHost.PlayerCharacter(session);
            if (ch != null) try { ch.Data.Set(new ControlData { Movement = _cmdOn ? Local(ch.Data.GetWorldTransform().Orientation, dirWorld, k) : Vector3.Zero, Rotation = Vector3.Zero }); } catch { }
        }
    }

    private static Vector3 Local(Quaternion q, Vector3D dirWorld, double k)
    {
        Vector3D local = Vector3D.Transform(dirWorld, Quaternion.Inverse(q));
        double m = Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z)));
        return m > 1e-6 ? new Vector3((float)(local.X / m), (float)(local.Y / m), (float)(local.Z / m)) * (float)k : Vector3.Zero;
    }

    /// <summary>Server tick: a seated pilot's command goes to the grid they sit in.</summary>
    private static bool _cmdWasOn;
    public static void ServerCommandTick()
    {
        if (!FrameHost.Seated || (!_cmdOn && !_cmdWasOn) || Busy) return;
        OrbitalGridComponent best = null; double bd = 100;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer) continue;
            double d = (GridMembers.Position(g) - FrameHost.PlayerPosition).Length();
            if (d < bd && GridMembers.Mass(g) > 500) { bd = d; best = g; }
        }
        _cmdWasOn = _cmdOn;
        if (best == null) return;
        var mv = _cmdOn ? Local(best.Entity.Data.GetWorldTransform().Orientation, _cmdDir, _cmdK) : Vector3.Zero;
        _move = mv;
        Write(best.Entity, _cmdOn);
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

    /// <summary>Every frame on the client tick: the same on the client copy (a piloted grid simulates there).</summary>
    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
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
