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
/// ATTITUDE HOLD (as KSP's SAS buttons): while seated, the ship keeps pointing its nose - the way the pilot faces - along
/// prograde, retrograde, normal, anti-normal, radial out or radial in of its orbit, tracking it as the orbit turns. It
/// writes the cockpit's target orientation (TargetControlData: what the mouse moves) every frame; the gyros, and the aero
/// mod's thruster steering, turn the ship to it. The smallest turn from the current attitude, so roll is kept. Chosen in
/// the map's right-click menu or cycled with the ';' key; moving the mouse (the target moved off what the hold set),
/// leaving the seat or losing the orbit lets go. Client only.
/// </summary>
public static class AttitudeHold
{
    public enum Mode { Off, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn }
    public static Mode Current = Mode.Off;
    public static string Status = "off";
    /// <summary>The hold lets go when the cockpit's target moved more than this off what it set (deg): the pilot steering.</summary>
    public const double ReleaseDeg = 0.5;

    static Entity _grid;
    static Quaternion _lastSet;
    static bool _hasSet;

    public static string Label(Mode m) => m switch
    {
        Mode.Prograde => "Prograde", Mode.Retrograde => "Retrograde", Mode.Normal => "Normal", Mode.AntiNormal => "Anti-normal",
        Mode.RadialOut => "Radial out", Mode.RadialIn => "Radial in", _ => "Off",
    };

    public static void Set(Mode m) { Current = m; _hasSet = false; _grid = null; Status = m == Mode.Off ? "off" : "holding " + Label(m); }

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

        var ch = FrameHost.PlayerCharacter(session);
        if (ch == null) return;
        if (_grid == null || !_grid.Data.Has<TargetControlData>() || (_grid.Data.GetWorldTransform().Position - ch.Data.GetWorldTransform().Position).Length() > 200)
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
        // The pilot steering: the cockpit moved the target off what we set - hand the stick back.
        if (_hasSet && Angle(tcd.TargetOrientation, _lastSet) * 180 / Math.PI > ReleaseDeg) { Release("you steered"); return; }

        var q = _grid.Data.GetWorldTransform().Orientation;
        // the nose: the way the seated pilot faces, in the grid's frame
        Vector3D noseW = Vector3D.Transform(new Vector3D(0, 0, -1), ch.Data.GetWorldTransform().Orientation);
        Vector3D noseL = Vector3D.Transform(noseW, Quaternion.Inverse(q));
        Vector3D u = Vector3D.Transform(noseL, q), b = Vector3D.Normalize(dir);
        double ang = Math.Acos(Math.Clamp(Vector3D.Dot(Vector3D.Normalize(u), b), -1, 1));
        Quaternion target = q;
        if (ang > 1e-5)
        {
            Vector3D axis = Vector3D.Cross(u, b);
            if (axis.LengthSquared() < 1e-12) axis = Math.Abs(u.X) < 0.9 ? Vector3D.Cross(u, Vector3D.UnitX) : Vector3D.Cross(u, Vector3D.UnitY);
            target = Quaternion.Normalize(Quaternion.CreateFromAxisAngle((Vector3)Vector3D.Normalize(axis), (float)ang) * q);
        }
        tcd.TargetOrientation = target;
        _grid.Data.Set(tcd);
        _lastSet = target; _hasSet = true;
        Status = $"holding {Label(Current)}: {ang * 180 / Math.PI:F1} deg off";
    }

    static void Release(string why) { Current = Mode.Off; _hasSet = false; _grid = null; Status = "off (" + why + ")"; }

    static double Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(Quaternion.Inverse(a) * b);
        return 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
    }
}
