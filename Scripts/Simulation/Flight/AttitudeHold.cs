using System;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// ATTITUDE HOLD (as KSP's SAS buttons): while seated, the ship keeps pointing its nose - the cockpit's forward - along
/// prograde, retrograde, normal, anti-normal, radial out or radial in of its orbit, tracking it as the orbit turns. It
/// writes the cockpit's target orientation (TargetControlData: what the mouse moves) every frame; the gyros, and the aero
/// mod's thruster steering, turn the ship to it. It holds a target of its own, turned by the smallest turn as the orbit
/// direction turns (not re-derived from the attitude each frame: then it chased its own wobble). Chosen in the map's
/// right-click menu or cycled with the ';' key; steering with the mouse (a few degrees, on the pilot's own input), leaving
/// the seat or losing the orbit lets go. Rolling is the pilot's: a roll is kept, the nose stays. Client only.
/// LEVEL (2026-10-12, the user: "can we add a horizon align?"): wings level and the nose on the horizon, on the heading it
/// had when engaged - kept horizontal as the ground curves under the ship (its up: away from the planet's centre). Roll is
/// held too. No orbit needed, only a planet's cell.
/// KEYS: ';' turns the selected mode on and off; Ctrl+; selects the next (AlignButton shows it, lit while holding).
/// </summary>
public static class AttitudeHold
{
    public enum Mode { Off, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn, Level }
    public static Mode Current = Mode.Off;
    /// <summary>The mode chosen (never Off): what ';' turns on. Ctrl+; cycles it (2026-10-12, the user: "the cycled mode
    /// distinct from the on/off of alignment"); changed while holding, the hold follows it.</summary>
    public static Mode Selected = Mode.Level;
    public static string Status = "off";
    /// <summary>The hold lets go when the pilot moved the target's nose this far (deg) within about <see cref="SteerTau"/>.
    /// (At 0.5 deg in one frame it let go at any twitch of the mouse - the user: "acts like on-off".)</summary>
    public const double ReleaseDeg = 4.0;
    /// <summary>The pilot's steering is summed over about this long (s, a leak): a deliberate move adds up, a hand resting on
    /// the mouse - a pixel now and then, put back each frame - does not.</summary>
    public const double SteerTau = 0.5;
    /// <summary>A ship (or the wanted direction) turned more than this in one tick is a crossing's turn of the world (deg), not
    /// motion: the hold starts again from the attitude.</summary>
    const double JumpDeg = 20.0;

    static Entity _grid;
    static Quaternion _hold, _lastQ;   // the target held (the grid's orientation wanted), and the grid's at the last tick
    static bool _hasSet;
    static double _steer, _lastWall;   // the pilot's summed nose moves (deg, leaking), and when they were last summed

    public static string Label(Mode m) => m switch
    {
        Mode.Prograde => "Prograde", Mode.Retrograde => "Retrograde", Mode.Normal => "Normal", Mode.AntiNormal => "Anti-normal",
        Mode.RadialOut => "Radial out", Mode.RadialIn => "Radial in", Mode.Level => "Level", _ => "Off",
    };

    /// <summary>Hold this mode (Off: let go). A mode held is the one selected too (the map's menu, the harness).</summary>
    public static void Set(Mode m)
    {
        if (m != Mode.Off) Selected = m;
        Current = m; _hasSet = false; _grid = null; _steer = 0;
        Status = m == Mode.Off ? "off" : "holding " + Label(m);
    }

    /// <summary>';': the selected mode on, or off. A mode that cannot hold here says why and stays off.</summary>
    public static void Toggle()
    {
        if (Current != Mode.Off) { Set(Mode.Off); return; }
        if (!Available(Selected)) { Status = $"off ({Label(Selected)}: {(Selected == Mode.Level ? "no planet here" : "no orbit, not moving over a planet")})"; return; }
        Set(Selected);
    }

    /// <summary>Ctrl+;: the next mode selected - Prograde ... Radial in, Level, round - skipping the ones that cannot hold here
    /// (the orbit's without an orbit, Level away from a planet; in air, ';' had gone to Prograde, which let go at once, and
    /// Level was never reached). Holding: the hold follows it.</summary>
    public static void Cycle()
    {
        Selected = Next();
        if (Current != Mode.Off) Set(Selected);
        else Status = "off (selected " + Label(Selected) + ")";
    }
    /// <summary>The mode Ctrl+; selects next (the selected one when no other can hold here).</summary>
    public static Mode Next()
    {
        for (int k = 1; k < 8; k++)
        {
            var m = (Mode)(((int)Selected - 1 + k) % 7 + 1);
            if (Available(m)) return m;
        }
        return Selected;
    }
    /// <summary>Whether a mode can hold now: the orbit's need an orbit, Level a planet's cell.</summary>
    public static bool Available(Mode m)
    {
        if (m == Mode.Off) return true;
        if (m == Mode.Level)
        {
            var g = FrameHost.SeatGrid;
            return g != null && SystemHost.Registry != null && VoxelBerthRegistry.TryCellContaining(g.Data.GetWorldTransform().Position, SystemHost.Registry, out _, out _);
        }
        return Maneuvers.Base(SystemHost.Now, out _, out _) || SurfaceAxes(out _, out _, out _);
    }

    /// <summary>Faster than this over the ground (m/s), the orbit's modes fly by the surface's axes without an orbit.</summary>
    public const double SurfaceMinSpeed = 5;
    /// <summary>
    /// THE SURFACE'S AXES (2026-10-12, the user: "change mode does not work" - in air there is no orbit, so prograde ... radial
    /// in could not hold and Ctrl+; had only Level): as KSP's surface mode, from the ship's velocity over the ground (world,
    /// planet-fixed) and its place over the planet's centre - prograde along the flight path, normal across it (r x v), radial
    /// out the flight path's up. False: not in a planet's cell, or slower than SurfaceMinSpeed.
    /// </summary>
    public static bool SurfaceAxes(out Vector3D P, out Vector3D N, out Vector3D R)
    {
        P = N = R = Vector3D.Zero;
        var g = FrameHost.SeatGrid;
        if (g == null || SystemHost.Registry == null || !g.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb)) return false;
        var pos = g.Data.GetWorldTransform().Position;
        if (!VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out _, out Vector3D centre)) return false;
        Vector3D v = rb.LinearVelocity, r = pos - centre;
        if (v.Length() < SurfaceMinSpeed) return false;
        P = Vector3D.Normalize(v);
        var h = Vector3D.Cross(r, v);
        if (h.LengthSquared() < 1e-9) return false;   // (straight up or down: no sideways)
        N = Vector3D.Normalize(h);
        R = Vector3D.Cross(P, N);
        return true;
    }

    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (FrameHost.Seated && MapInput.KeyPressed(KeyboardInputs.OemSemicolon))
        {
            if (MapInput.KeyDown(KeyboardInputs.Control)) Cycle();   // (Ctrl+; : the next mode)
            else Toggle();                                            // (; : on / off)
        }
        if (Current == Mode.Off) return;
        if (!FrameHost.Seated) { Release("left the seat"); return; }
        if (Current == Mode.Level) { LevelTick(session); return; }
        double t = SystemHost.Now;
        Vector3D P, N, R; bool surface = false;
        if (Maneuvers.Base(t, out _, out var el)) Maneuvers.Axes(OrbitPropagation.StateAt(el, t), out P, out N, out R);
        else if (SurfaceAxes(out P, out N, out R)) surface = true;   // (in air: over the ground)
        else { Release("no orbit, not moving over a planet"); return; }
        Vector3D dir = Current switch
        {
            Mode.Prograde => P, Mode.Retrograde => -P, Mode.Normal => N, Mode.AntiNormal => -N,
            Mode.RadialOut => R, Mode.RadialIn => -R, _ => Vector3D.Zero,
        };
        if (dir.LengthSquared() < 1e-12) return;
        // (in a frame, inertial is world; in a planet's cell, its rotating chart - as the burn direction)
        if (!surface && FrameHost.PlayerFrame == null && FrameHost.ObserverPlanet != null) dir = Chart.Of(FrameHost.ObserverPlanet, t).FromInertial(dir);
        dir = Vector3D.Normalize(dir);

        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return;
        if (!FindGrid(session, ch)) return;
        if (!_grid.Data.TryGet<TargetControlData>(out var tcd)) return;
        var q = _grid.Data.GetWorldTransform().Orientation;
        OrbitHold(tcd, q, ch, dir);
    }

    /// <summary>The piloted grid the hold steers: the seat's (the ship you fly), else the nearest piloted grid within 200 m.</summary>
    static bool FindGrid(Keen.VRage.Core.Game.Systems.Session session, Entity ch)
    {
        var seat = FrameHost.SeatGrid;
        if (seat != null && seat.Data.Has<TargetControlData>()) _grid = seat;   // (the ship you fly - not the nearest piloted grid next to it)
        else if (_grid == null || !_grid.Data.Has<TargetControlData>() || (_grid.Data.GetWorldTransform().Position - ch.Data.GetWorldTransform().Position).Length() > 200)
        {
            _grid = null; double bd = 200;
            foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
            {
                if (!e.Data.Has<TargetControlData>()) continue;
                double d = (e.Data.GetWorldTransform().Position - ch.Data.GetWorldTransform().Position).Length();
                if (d < bd) { bd = d; _grid = e; }
            }
            if (_grid == null) { Status = "holding " + Label(Current) + ": no piloted grid"; return false; }
        }
        return true;
    }

    /// <summary>The cockpit's forward and up in the grid (TargetControlData.RelativeCockpitOrientation; before the game's first
    /// reticle update, the character's facing).</summary>
    static void CockpitAxes(in TargetControlData tcd, Quaternion q, Entity ch, out Vector3D noseL, out Vector3D topL)
    {
        var rc = tcd.RelativeCockpitOrientation;
        var r = rc.IsValidAndRotationIsNormalized() ? rc : Quaternion.Normalize(Quaternion.Inverse(q) * ch.Data.GetWorldTransform().Orientation);
        noseL = Vector3D.Normalize(Vector3D.Transform(new Vector3D(0, 0, -1), r));
        topL = Vector3D.Normalize(Vector3D.Transform(new Vector3D(0, 1, 0), r));
    }

    static Vector3D _heading;   // (Level: the heading held - world, horizontal)

    /// <summary>LEVEL: the cockpit's nose horizontal on the held heading, its top away from the planet's centre.</summary>
    static void LevelTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null || !FindGrid(session, ch)) return;
        if (!_grid.Data.TryGet<TargetControlData>(out var tcd)) return;
        var wt = _grid.Data.GetWorldTransform(); var q = wt.Orientation;
        if (SystemHost.Registry == null || !VoxelBerthRegistry.TryCellContaining(wt.Position, SystemHost.Registry, out _, out Vector3D centre)) { Release("no planet here"); return; }
        Vector3D up = Vector3D.Normalize(wt.Position - centre);
        CockpitAxes(tcd, q, ch, out var noseL, out var topL);
        if (!_hasSet || Angle(q, _lastQ) * 180 / Math.PI > JumpDeg)
        {
            // engaged (or the world turned under it): the heading is where the nose points now, laid flat
            Vector3D n = Vector3D.Transform(noseL, q);
            _heading = n - up * Vector3D.Dot(n, up);
            if (_heading.LengthSquared() < 1e-6) _heading = Vector3D.Transform(-topL, q) - up * Vector3D.Dot(Vector3D.Transform(-topL, q), up);   // (nose straight up/down: its belly's way)
            _hasSet = false; _steer = 0;
        }
        _lastQ = q;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double dtw = Math.Clamp(wall - _lastWall, 0, 0.5); _lastWall = wall;
        if (_hasSet)
        {
            // the pilot steering (mouse, roll keys): what the cockpit made of our target, on their own input - lets go
            double moved = Angle(tcd.TargetOrientation, _hold) * 180 / Math.PI;
            bool input = !_grid.Data.TryGet<ControlData>(out var cd) || cd.Rotation.LengthSquared() > 1e-10f;
            _steer *= Math.Exp(-dtw / SteerTau);
            if (input && double.IsFinite(moved)) { _steer += moved; if (_steer > ReleaseDeg) { Release("you steered"); return; } }
        }
        // the heading kept horizontal as the ground curves under the ship
        _heading -= up * Vector3D.Dot(_heading, up);
        if (_heading.LengthSquared() < 1e-9) { Release("no heading"); return; }
        _heading = Vector3D.Normalize(_heading);
        // the grid's orientation that puts the cockpit's nose on the heading and its top up
        var cockpitWant = Quaternion.CreateFromForwardUp((Vector3)_heading, (Vector3)up);
        var cockpitInGrid = Quaternion.CreateFromForwardUp((Vector3)noseL, (Vector3)topL);
        // (turned toward the level attitude at most TurnDegPerSec, from the held target - or from the attitude on engaging:
        //  placed at once, the chase camera swung with it)
        _hold = TurnToward(_hasSet ? _hold : q, Quaternion.Normalize(cockpitWant * Quaternion.Inverse(cockpitInGrid)), MaxTurn(dtw));
        tcd.TargetOrientation = _hold;
        _grid.Data.Set(tcd);
        _hasSet = true;
        Vector3D noseNow = Vector3D.Normalize(Vector3D.Transform(noseL, q)), topNow = Vector3D.Normalize(Vector3D.Transform(topL, q));
        double pitch = Math.Asin(Math.Clamp(Vector3D.Dot(noseNow, up), -1, 1)) * 180 / Math.PI;
        Vector3D right = Vector3D.Cross(noseNow, up);
        double bank = right.LengthSquared() > 1e-9 ? Math.Atan2(Vector3D.Dot(topNow, Vector3D.Normalize(right)), Vector3D.Dot(topNow, up)) * 180 / Math.PI : 0;
        Status = $"holding Level: pitch {pitch:+0.0;-0.0} deg, bank {bank:+0.0;-0.0} deg";
    }

    /// <summary>The held target's fastest turn toward the wanted direction (deg/s).</summary>
    public static double TurnDegPerSec = 30;
    static Vector3D _lastDir;
    /// <summary>The turn allowed this tick (rad): its time capped at 1/30 s - a stale clock on engaging (the last tick long
    /// ago) let the first one swing 15 deg.</summary>
    static double MaxTurn(double dtw) => TurnDegPerSec * Math.PI / 180 * Math.Clamp(dtw, 1.0 / 120.0, 1.0 / 30.0);
    /// <summary>a turned toward b by at most max (rad).</summary>
    static Quaternion TurnToward(Quaternion a, Quaternion b, double max)
    {
        double ang = Angle(a, b);
        if (!(ang > max) || !double.IsFinite(ang)) return b;
        return Quaternion.Normalize(Quaternion.Slerp(a, b, (float)(max / ang)));
    }

    /// <summary>The orbit modes: the nose along dir (world), by the smallest turn of the held target.</summary>
    static void OrbitHold(TargetControlData tcd, Quaternion q, Entity ch, Vector3D dir)
    {

        // The nose: the cockpit's forward in the grid (TargetControlData.RelativeCockpitOrientation - the cockpit relative to
        // the grid, which the game keeps each frame for its reticle). Not the way the pilot's head faces: free look turned
        // the target, the game's reticle turned the head after it, and the two chased each other. (Before the game's first
        // reticle update it is unset: the character's facing, as it was.)
        Vector3D noseL;
        var rc = tcd.RelativeCockpitOrientation;
        if (rc.IsValidAndRotationIsNormalized()) noseL = Vector3D.Transform(new Vector3D(0, 0, -1), rc);
        else noseL = Vector3D.Transform(Vector3D.Transform(new Vector3D(0, 0, -1), ch.Data.GetWorldTransform().Orientation), Quaternion.Inverse(q));
        noseL = Vector3D.Normalize(noseL);

        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double dtw = Math.Clamp(wall - _lastWall, 0, 0.5); _lastWall = wall;
        // A crossing turns the ship (and its target) by the chart's angle in one tick: the held target is in the old axes.
        // (no physics turns a ship 20 deg in a tick)
        if (_hasSet && Angle(q, _lastQ) * 180 / Math.PI > JumpDeg) _hasSet = false;
        _lastQ = q;

        if (_hasSet)
        {
            // The pilot steering: what the cockpit made of the target since we set it - its nose moved, on the pilot's own
            // rotation input (ControlData.Rotation, set from the mouse and the roll keys). The game's own re-derivation of the
            // target each frame (float round trips, its roll decay) moves it a hair with no input: not the pilot, not counted.
            // (no ControlData - no cockpit input on this grid: every move counts, as before)
            Vector3D gameNose = Vector3D.Transform(noseL, tcd.TargetOrientation), heldNose = Vector3D.Transform(noseL, _hold);
            double moved = Math.Acos(Math.Clamp(Vector3D.Dot(Vector3D.Normalize(gameNose), Vector3D.Normalize(heldNose)), -1, 1)) * 180 / Math.PI;
            bool input = !_grid.Data.TryGet<ControlData>(out var cd) || cd.Rotation.LengthSquared() > 1e-10f;
            _steer *= Math.Exp(-dtw / SteerTau);
            if (input && double.IsFinite(moved))
            {
                _steer += moved;
                if (_steer > ReleaseDeg) { Release("you steered"); return; }
                // a roll is the pilot's to keep (it leaves the nose where it is): the game's target, its nose put back
                if (tcd.TargetOrientation.IsValidAndRotationIsNormalized()) _hold = tcd.TargetOrientation;
            }
        }
        else { _hold = q; _steer = 0; }   // (engaged, or after a jump: from the attitude, its roll kept)

        // The held target follows the wanted direction by the smallest turn of its nose: smooth as the orbit turns, and
        // never re-derived from the ship's own attitude (which wobbles about it while the gyros work).
        Vector3D u = Vector3D.Normalize(Vector3D.Transform(noseL, _hold));
        double ang = Math.Acos(Math.Clamp(Vector3D.Dot(u, dir), -1, 1));
        // (a jump of the WANTED direction since the last tick - not the held target lagging it under the turn limit)
        bool dirJumped = _hasSet && _lastDir.LengthSquared() > 0.5 && Math.Acos(Math.Clamp(Vector3D.Dot(_lastDir, dir), -1, 1)) * 180 / Math.PI > JumpDeg;
        _lastDir = dir;
        if (dirJumped)
        {
            // (the wanted direction jumped - a crossing's axes, a body changed: from the attitude again, not a flip of the held one)
            _hold = q; u = Vector3D.Normalize(Vector3D.Transform(noseL, q));
            ang = Math.Acos(Math.Clamp(Vector3D.Dot(u, dir), -1, 1));
        }
        // (at most TurnDegPerSec: engaging, or after a reset to the attitude, the target swung the whole way in one tick - and
        //  the game's chase camera, which aims along the target, swung with it: 2026-10-13, an independent review of a
        //  recording's half-second camera swings. The gyros chase the target as before; it only no longer jumps.)
        double maxTurn = MaxTurn(dtw);
        if (ang > maxTurn) ang = maxTurn;
        if (ang > 1e-6)
        {
            Vector3D axis = Vector3D.Cross(u, dir);
            if (axis.LengthSquared() < 1e-12) axis = Math.Abs(u.X) < 0.9 ? Vector3D.Cross(u, Vector3D.UnitX) : Vector3D.Cross(u, Vector3D.UnitY);
            _hold = Quaternion.Normalize(Quaternion.CreateFromAxisAngle((Vector3)Vector3D.Normalize(axis), (float)ang) * _hold);
        }
        tcd.TargetOrientation = _hold;
        _grid.Data.Set(tcd);
        _hasSet = true;
        // (how far the ship's nose is from the direction - the gyros' work left)
        Vector3D noseNow = Vector3D.Normalize(Vector3D.Transform(noseL, q));
        double off = Math.Acos(Math.Clamp(Vector3D.Dot(noseNow, dir), -1, 1)) * 180 / Math.PI;
        Status = $"holding {Label(Current)}: {off:F1} deg off";
    }

    static void Release(string why) { Current = Mode.Off; _hasSet = false; _grid = null; _steer = 0; Status = "off (" + why + ")"; }

    static double Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(Quaternion.Inverse(a) * b);
        return 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
    }
}
