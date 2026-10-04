using System;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV: how well a piloted ship turns. `turntest DEG x|y|z` (seated): the cockpit's target orientation (TargetControlData,
/// what the mouse moves) turned once by DEG about the grid's own axis (x pitch, y yaw, z roll), held there; the gyros -
/// and the aero mod's thrusters, when they steer - turn the ship to it. Measured on the client copy from its orientation
/// each frame: the first time within 2 degrees, the peak rate, the overshoot past the target, the time it settles
/// (within 1 degree for 2 s). `turntest` alone: the last result.
/// </summary>
public static class DevTurn
{
    public static string Status = "idle";
    static Entity _grid;
    static Quaternion _target, _lastQ;
    static double _t0, _lastT, _deg, _peak, _within = double.NaN, _settled = double.NaN, _calmSince = double.NaN, _overshoot, _minErr = double.MaxValue;
    static bool _active, _reached;
    static string _axis = "y";
    const double Timeout = 40;

    public static string Start(Keen.VRage.Core.Game.Systems.Session client, double deg, string axis)
    {
        if (!FrameHost.Seated) return "not seated";
        Entity best = null; double bd = 100;
        foreach (var e in client.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
        {
            if (!e.Data.Has<TargetControlData>()) continue;
            double d = (e.Data.GetWorldTransform().Position - FrameHost.PlayerPosition).Length();
            if (d < bd) { bd = d; best = e; }
        }
        if (best == null) return "no piloted grid near you (no TargetControlData)";
        var q = best.Data.GetWorldTransform().Orientation;
        Vector3 ax = axis == "x" ? Vector3.UnitX : axis == "z" ? Vector3.UnitZ : Vector3.UnitY;
        _target = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(ax, (float)(deg * Math.PI / 180)));
        _grid = best; _deg = deg; _axis = axis;
        _t0 = _lastT = Wall(); _lastQ = q;
        _peak = 0; _within = _settled = _calmSince = double.NaN; _overshoot = 0; _minErr = double.MaxValue; _reached = false;
        _active = true;
        Status = $"turning {deg:F0} deg about {axis}";
        return Status;
    }

    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!_active || _grid == null) return;
        double now = Wall(), t = now - _t0, dt = now - _lastT;
        if (!FrameHost.Seated) { Finish("left the seat"); return; }
        var q = _grid.Data.GetWorldTransform().Orientation;
        if (_grid.Data.TryGet<TargetControlData>(out var tcd)) { tcd.TargetOrientation = _target; _grid.Data.Set(tcd); }
        double err = Angle(q, _target) * 180 / Math.PI;
        if (dt > 1e-3)
        {
            double rate = Angle(_lastQ, q) * 180 / Math.PI / dt;
            if (rate > _peak && rate < 360) _peak = rate;   // (a teleport is no rate)
            _lastQ = q; _lastT = now;
        }
        // overshoot: once it has come close, how far it swings off again (the far side of the target)
        _minErr = Math.Min(_minErr, err);
        if (!_reached && err <= 2) { _reached = true; _within = t; }
        if (_reached) _overshoot = Math.Max(_overshoot, err - _minErr);
        if (err <= 1) { if (double.IsNaN(_calmSince)) _calmSince = t; else if (t - _calmSince >= 2 && double.IsNaN(_settled)) { _settled = _calmSince; Finish("settled"); return; } }
        else _calmSince = double.NaN;
        if (t > Timeout) { Finish($"timed out, {err:F1} deg off"); return; }
        Status = $"turning {_deg:F0} about {_axis}: {err:F1} deg off at {t:F1} s, peak {_peak:F1} deg/s";
    }

    static void Finish(string why)
    {
        _active = false;
        Status = $"turn {_deg:F0} deg about {_axis}: {why} | within 2 deg {(double.IsNaN(_within) ? "never" : _within.ToString("F1") + " s")}"
               + $" | settled {(double.IsNaN(_settled) ? "no" : _settled.ToString("F1") + " s")} | peak {_peak:F1} deg/s | overshoot {_overshoot:F1} deg";
    }

    static double Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(Quaternion.Inverse(a) * b);
        return 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
