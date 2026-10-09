using System;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// ATTITUDE HOLD (as KSP's SAS buttons): while seated, the ship keeps pointing its nose - the cockpit's forward - along
/// prograde, retrograde, normal, anti-normal, radial out or radial in of its orbit, tracking it as the orbit turns. It
/// writes the cockpit's target orientation (TargetControlData: what the mouse moves) every frame; the gyros, and the aero
/// mod's thruster steering, turn the ship to it. It holds a target of its own, turned by the smallest turn as the orbit
/// direction turns (not re-derived from the attitude each frame: then it chased its own wobble). Chosen in the map's
/// right-click menu or cycled with the ';' key; steering with the mouse (a few degrees, on the pilot's own input), leaving
/// the seat or losing the orbit lets go. Rolling is the pilot's: a roll is kept, the nose stays. Client only.
/// </summary>
public static class AttitudeHold
{
    public enum Mode { Off, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn }
    public static Mode Current = Mode.Off;
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
        Mode.RadialOut => "Radial out", Mode.RadialIn => "Radial in", _ => "Off",
    };

    public static void Set(Mode m) { Current = m; _hasSet = false; _grid = null; _steer = 0; Status = m == Mode.Off ? "off" : "holding " + Label(m); }

    /// <summary>Off -> Prograde -> ... -> Radial in -> Off.</summary>
    public static void Cycle() => Set((Mode)(((int)Current + 1) % 7));

    /// <summary>The HUD card's line (null when off).</summary>
    public static string HudLine() => Current == Mode.Off ? null : $"Hold: {Label(Current)}   ( ; cycles )";

    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (FrameHost.Seated && MapInput.KeyPressed(KeyboardInputs.OemSemicolon)) Cycle();
        if (Current == Mode.Off) return;
        if (!FrameHost.Seated) { Release("left the seat"); return; }
        double t = SystemHost.Now;
        if (!Maneuvers.Base(t, out _, out var el)) { Release("no orbit"); return; }
        var st = OrbitPropagation.StateAt(el, t);
        Maneuvers.Axes(st, out var P, out var N, out var R);
        Vector3D dir = Current switch
        {
            Mode.Prograde => P, Mode.Retrograde => -P, Mode.Normal => N, Mode.AntiNormal => -N,
            Mode.RadialOut => R, Mode.RadialIn => -R, _ => Vector3D.Zero,
        };
        if (dir.LengthSquared() < 1e-12) return;
        // (in a frame, inertial is world; in a planet's cell, its rotating chart - as the burn direction)
        if (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanet != null) dir = Chart.Of(FrameHost.ObserverPlanet, t).FromInertial(dir);
        dir = Vector3D.Normalize(dir);

        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return;
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
            if (_grid == null) { Status = "holding " + Label(Current) + ": no piloted grid"; return; }
        }
        if (!_grid.Data.TryGet<TargetControlData>(out var tcd)) return;
        var q = _grid.Data.GetWorldTransform().Orientation;

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
        if (_hasSet && ang * 180 / Math.PI > JumpDeg)
        {
            // (the wanted direction jumped - a crossing's axes, a body changed: from the attitude again, not a flip of the held one)
            _hold = q; u = Vector3D.Normalize(Vector3D.Transform(noseL, q));
            ang = Math.Acos(Math.Clamp(Vector3D.Dot(u, dir), -1, 1));
        }
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
