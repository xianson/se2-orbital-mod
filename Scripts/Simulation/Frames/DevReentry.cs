using System;
using System.Collections.Generic;
using SEAerospace;
using SEAerospace.Entry;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV: reentry, top to bottom, checked every server tick. `reentrytest ID` arms it on a grid (put it on an orbit through
/// the atmosphere first: gridorbit ID Verdure APO PERI); `reentrytest` reports. Stages and what each must do:
///  1. RAILS (a member of a frame about the planet): the state finite every tick; the band's braking only while inside the
///     band and over the cap; heat only from the band; the glow handed to the aero mod while braking.
///  2. HANDOVER (the frame arrives: ServerFrames.TryMaterializeGrids records what it moved the grid to - Expect): within
///     3 ticks the grid is where it was sent, at the velocity, and turned into the planet's rotating frame (orientation and
///     spin as the arrival rotated them: within 0.5 deg); its speed at or under the world's cap.
///  3. AIR (physics, the planet's frame): no teleport (each tick's motion matches its velocity), the aero mod flying it
///     (published flow) in the atmosphere, its plasma while fast; down to the ground (or a timeout).
/// Every check is a PASS/FAIL line in the report, with the numbers.
/// </summary>
public static class DevReentry
{
    static long _id;
    static bool _armed;
    static string _phase = "idle", _body;
    static double _t0, _lastWall, _armWall;
    static readonly List<string> _log = new List<string>();
    static readonly List<string> _fails = new List<string>();
    static int _checks, _followTicks = -1;
    static readonly List<long> _joined = new List<long>();

    // rails
    static double _railsTicks, _railsNaN, _bandTicks, _brakeOutside, _heatOutside, _maxHeat, _entrySpeed = double.NaN, _lastRailSpeed, _glowAtBrake, _brakeTicks;
    static double _lastAirSpeed = double.NaN, _lastHeat;
    static int _glowWritesStart;
    // handover
    static bool _expect; static int _expectTicks;
    static Vector3D _expPos, _expVel, _expFrom; static Quaternion _expQ; static Vector3 _expW; static double _expWall;
    static double _railWall, _railTicksWall, _airWall0 = double.NaN, _lagS = double.NaN, _restSince = double.NaN;
    static double _handPosErr = double.NaN, _handVelErr = double.NaN, _handAngErr = double.NaN, _handSpeed = double.NaN;
    // air
    static Vector3D _lastP, _lastV; static bool _haveLast;
    /// <summary>The test ends here (m above the radius): a ship falling with its dampeners off is not crashed.</summary>
    public static double StopAltitude = 2000;
    static double _atmoMoving, _airFrom = double.NaN; static int _flowGapRun;
    static System.Reflection.MethodInfo _atmo;
    static double _airTicks, _maxJump, _maxJumpAt, _flowTicks, _atmoTicks, _maxMach, _plasmaTicks, _maxPlasma, _minAlt = double.MaxValue, _touchdownSpeed = double.NaN;
    static System.Reflection.MethodInfo _flight, _entry;

    public static string Arm(long id)
    {
        var g = GridMembers.Get(id);
        if (g?.Entity == null || !g.IsServer) return "no server grid " + id;
        _id = id; _armed = true; _phase = "waiting for a frame about a planet"; _body = null;
        _log.Clear(); _fails.Clear(); _checks = 0;
        _railsTicks = _railsNaN = _bandTicks = _brakeOutside = _heatOutside = _maxHeat = _brakeTicks = _glowAtBrake = 0; _entrySpeed = double.NaN; _lastAirSpeed = double.NaN; _lastHeat = 0;
        _glowWritesStart = EntryHost.GlowWrites;
        _followTicks = -1;
        _expect = false; _handPosErr = _handVelErr = _handAngErr = _handSpeed = double.NaN;
        _atmoMoving = 0; _airFrom = double.NaN; _airWall0 = double.NaN; _lagS = double.NaN; _restSince = double.NaN; _railWall = Wall();
        _haveLast = false; _airTicks = _maxJump = _maxJumpAt = _flowTicks = _atmoTicks = _maxMach = _plasmaTicks = _maxPlasma = 0; _minAlt = double.MaxValue; _touchdownSpeed = double.NaN;
        _armWall = Wall(); _t0 = SystemHost.Now; _lastWall = _armWall;
        // the grids joined to it (sub-grids, docked): they must arrive with it
        _joined.Clear();
        var set = new List<Entity>();
        if (GridMembers.IsConstrained(g) && GridMembers.MoveSet(g, set, out _))
            foreach (var o in GridMembers.All()) if (o.IsServer && o.Id != id && set.Contains(o.Entity)) _joined.Add(o.Id);
        return $"reentry test armed on grid {id} '{g.DisplayName}' ({_joined.Count} grids joined to it)";
    }

    /// <summary>The arrival moved a grid: where to, at what velocity, and the rotation it applied (ServerFrames).</summary>
    public static void Expect(long id, Vector3D dest, Vector3D vel, Quaternion rot, Entity e)
    {
        if (!_armed || id != _id || e == null) return;
        try
        {
            var wt = e.Data.GetWorldTransform();
            _expQ = Quaternion.Normalize(rot * wt.Orientation);
            var rb = e.Data.Get<Keen.VRage.Physics.Data.RigidBodyData>();
            _expW = Vector3.Transform(rb.AngularVelocity, rot);
        }
        catch { _expQ = Quaternion.Identity; }
        _expPos = dest; _expVel = vel; _expWall = Wall(); _expect = true; _expectTicks = 0; _expFrom = e.Data.GetWorldTransform().Position;
        if (SystemHost.Timescale > 1) { SystemHost.Timescale = 1; Log("handover: warp off (physics from here)"); }
        Log($"handover: arrival sent the grid to the planet's frame at |v| {vel.Length():F0} m/s");
    }

    /// <summary>Server tick (ServerFrames, after the frames' update).</summary>
    /// <summary>gameDt: this tick's game time (s) - motion is checked against it (a hitch slows the game: wall time overstated it).</summary>
    public static void ServerTick(double gameDt)
    {
        if (!_armed) return;
        double wall = Wall(), dt = gameDt; _lastWall = wall;
        var g = GridMembers.Get(_id);
        if (g?.Entity == null) { Finish("the grid is gone"); return; }
        ProximityFrame f; lock (ServerFrames.FramesLock) f = SystemHost.Frames?.FindByMember(_id);
        if (f != null) Rails(f);
        else Air(g, dt);
        if (wall - _armWall > 1800) Finish("timed out after 30 min");
    }

    static void Rails(ProximityFrame f)
    {
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        if (def == null || !def.HasAtmosphere) { _phase = $"rails about {f.ParentBodyName} (no atmosphere: waiting)"; return; }
        if (_body == null) { _body = f.ParentBodyName; Log($"rails: in frame #{f.Id} about {_body}"); }
        _phase = "rails";
        _railsTicks++;
        double now = SystemHost.Now;
        var st = OrbitPropagation.StateAt(f.Elements, now);
        if (!GridMembers.Finite(st.Position) || !GridMembers.Finite(st.Velocity)) { _railsNaN++; return; }
        var b = EntryHost.BandOf(f.ParentBodyName);
        double r = st.Position.Length();
        var chart = Chart.Of(f.ParentBodyName, now);
        double air = chart.VelFromInertial(st.Position, st.Velocity).Length();
        double heat = EntryHost.HeatOf(f.Id);
        bool inBand = b.IsValid && r <= b.Top && r >= b.Bottom - 1;
        if (inBand) { _bandTicks++; if (double.IsNaN(_entrySpeed)) { _entrySpeed = air; Log($"rails: entered the band at {(r - def.RadiusMeters) / 1000:F1} km, air speed {air:F0} m/s (cap {EntryHost.Cap:F0})"); } }
        // braking: the air speed dropped faster than gravity along the path could explain - measured as a loss of
        // specific energy relative to the rotating air between ticks (the band's rule removes energy; a free orbit keeps it)
        if (!double.IsNaN(_lastAirSpeed))
        {
            bool braked = air < _lastAirSpeed - 0.5 && heat > _lastHeat + 1e-6;
            if (braked) { _brakeTicks++; if (!inBand) _brakeOutside++; if (EntryHost.GlowWrites > _glowWritesStart) _glowAtBrake++; }
            if (heat > _lastHeat + 1e-6 && !inBand) _heatOutside++;
        }
        _lastAirSpeed = air; _lastHeat = heat; _maxHeat = Math.Max(_maxHeat, heat); _lastRailSpeed = air;
    }

    static void Air(OrbitalGridComponent g, double dt)
    {
        if (_body == null) { _phase = "waiting for a frame about a planet"; return; }
        if (_railsTicks > 0 && _phase == "rails") RailsReport();
        _phase = "air";
        Vector3D p = GridMembers.Position(g), v = GridMembers.Velocity(g);
        // handover: the first ticks after the arrival
        if (_expect)
        {
            _expectTicks++;
            var e = g.Entity;
            double since = Wall() - _expWall;
            bool moved = (p - _expFrom).Length() > 1000;   // (off its berth: the deferred move has happened)
            double pe = (p - _expPos).Length(), ve = (v - _expVel).Length();
            double ae = 0;
            try { ae = Angle(e.Data.GetWorldTransform().Orientation, _expQ) * 180 / Math.PI; } catch { }
            if (moved || _expectTicks > 30)
            {
                _lagS = since; _followTicks = 0;
                Log($"handover: placed {since * 1000:F0} ms after the arrival computed it (behind its orbit by {since * _expVel.Length():F0} m at {_expVel.Length():F0} m/s)");
                _handPosErr = pe; _handVelErr = ve; _handAngErr = ae; _handSpeed = v.Length();
                _expect = false;
                Check(moved, "handover: moved into the planet's frame (off its berth)");
                Check(pe < 2 + v.Length() / 60.0 * 2, $"handover: placed where the arrival sent it ({pe:F1} m off, its motion since included)");
                Check(ve < 2, $"handover: at the arrival's velocity ({ve:F2} m/s off)");
                Check(_handSpeed <= FrameHost.SpeedCap * 1.02 + 1, $"handover: at or under the cap ({_handSpeed:F0} m/s, cap {FrameHost.SpeedCap:F0})");
                if (_joined.Count > 0)
                {
                    int with = 0; double worst = 0;
                    foreach (long jid in _joined) { var jg = GridMembers.Get(jid); if (jg == null) continue; double d = (GridMembers.Position(jg) - p).Length(); worst = Math.Max(worst, d); if (d < 200) with++; }
                    Check(with == _joined.Count, $"handover: the grids joined to it arrived with it ({with} of {_joined.Count}; farthest {worst:F0} m)");
                }
            }
        }
        // after the handover: how the orientation, and the cockpit's target, follow (ticks 1..120)
        if (_followTicks >= 0 && !_expect)
        {
            _followTicks++;
            if (_followTicks is 1 or 2 or 5 or 10 or 30 or 60 or 120)
            {
                double ae2 = 0, te = double.NaN;
                try
                {
                    ae2 = Angle(g.Entity.Data.GetWorldTransform().Orientation, _expQ) * 180 / Math.PI;
                    if (g.Entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>(out var tcd)) te = Angle(tcd.TargetOrientation, _expQ) * 180 / Math.PI;
                }
                catch { }
                Log($"after the handover, tick {_followTicks}: {ae2:F2} deg off the rotated orientation; the cockpit target {(double.IsNaN(te) ? "none" : te.ToString("F2") + " deg off it")}");
                // (the rotation lands with the deferred move: tick 2)
                if (_followTicks == 2) Check(ae2 < 0.5, $"handover: its orientation turned into the planet's frame ({ae2:F2} deg off the rotated orientation)");
                if (_followTicks == 10 && !double.IsNaN(te)) Check(te < 1, $"handover: the cockpit's target turned with it ({te:F2} deg off)");
                if (_followTicks == 120) Check(ae2 < 5, $"handover: still on its rotated orientation 2 s later ({ae2:F1} deg off - the gyros chase the cockpit's target)");
            }
            if (_followTicks >= 120) _followTicks = -1;
        }
        // the planet below
        PlanetBeacon nb = null; double nd = double.MaxValue;
        foreach (var pb in DevHarness.Planets()) { double dd = (pb.Center - p).Length(); if (dd < nd) { nd = dd; nb = pb; } }
        var def = SystemHost.Registry?.FindDefinition(_body);
        double alt = nd - (def?.RadiusMeters ?? 0);
        _minAlt = Math.Min(_minAlt, alt);
        _airTicks++;
        if (double.IsNaN(_airWall0)) _airWall0 = Wall();
        if (_haveLast && dt > 1e-4 && dt < 0.5)
        {
            double jump = ((p - _lastP) - (_lastV + v) * 0.5 * dt).Length();
            if (jump > _maxJump) { _maxJump = jump; _maxJumpAt = alt; }
        }
        _lastP = p; _lastV = v; _haveLast = true;
        bool inAtmo = def != null && alt < def.AtmosphereHeightMeters;
        if (inAtmo)
        {
            _atmoTicks++;
            float rho = Density(g.Entity);
            if (rho > 1e-4f && double.IsNaN(_airFrom)) { _airFrom = alt; Log($"air: density over 1e-4 kg/m3 from {alt:F0} m (the atmosphere's top {def.AtmosphereHeightMeters:F0} m)"); }
            if (v.Length() > 5 && rho > 1e-4f)
            {
                _atmoMoving++;
                if (Flight(g.Entity, out float mach))
                {
                    if (_flowTicks == 0) Log($"air: the aero mod flies it from moving tick {_atmoMoving} ({alt:F0} m, {v.Length():F0} m/s)");
                    _flowTicks++; _maxMach = Math.Max(_maxMach, mach); _flowGapRun = 0;
                }
                else if (_flowTicks > 0 && ++_flowGapRun == 30) Log($"air: no published flow for 30 ticks at {alt:F0} m, {v.Length():F0} m/s");
            }
            if (Plasma(g.Entity, out float s) && s > 0.05f) { _plasmaTicks++; _maxPlasma = Math.Max(_maxPlasma, s); }
        }
        if (v.Length() < 2) { if (double.IsNaN(_restSince)) _restSince = Wall(); else if (Wall() - _restSince > 10) { Finish($"at rest at {alt:F0} m"); return; } }
        else _restSince = double.NaN;
        if (alt < StopAltitude) { _touchdownSpeed = v.Length(); Finish($"down to {alt:F0} m at {v.Length():F0} m/s"); }
    }

    static void RailsReport()
    {
        Check(_railsNaN == 0, $"rails: the orbit's state finite every tick ({_railsNaN} bad of {_railsTicks})");
        Check(_bandTicks > 0, $"rails: the pass went through the band ({_bandTicks} ticks inside)");
        Check(_brakeOutside == 0, $"rails: braking only inside the band ({_brakeOutside} of {_brakeTicks} braking ticks outside)");
        Check(_heatOutside == 0, $"rails: heat only from the band ({_heatOutside} ticks heating outside it)");
        if (_entrySpeed > EntryHost.Cap) Check(_brakeTicks > 0, $"rails: an arrival over the cap ({_entrySpeed:F0} m/s) was braked ({_brakeTicks} ticks, peak heat {_maxHeat:F0} J/kg)");
        if (_brakeTicks > 0) Check(_glowAtBrake > 0, $"rails: the glow handed to the aero mod while braking ({_glowAtBrake} of {_brakeTicks} ticks, {EntryHost.GlowWrites - _glowWritesStart} writes)");
        Check(_lastRailSpeed <= EntryHost.Cap * 1.02 + 1, $"rails: at the border at or under the cap ({_lastRailSpeed:F0} m/s, cap {EntryHost.Cap:F0})");
    }

    static void Finish(string why)
    {
        if (!_armed) return;
        _armed = false;
        if (_phase == "rails") RailsReport();
        if (_railsTicks > 0 && double.IsNaN(_handPosErr)) Check(false, "handover: never seen (no arrival into the planet's frame)");
        if (_airTicks > 0)
        {
            Check(_maxJump < 5, $"air: no teleport (worst tick {_maxJump:F1} m off its velocity, at {_maxJumpAt:F0} m)");
            double airRate = _airTicks / Math.Max(1e-3, Wall() - _airWall0);
            Check(airRate > 20, $"air: simulated at full rate ({airRate:F0} ticks/s)");
            if (_atmoMoving > 0) Check(_flowTicks > _atmoMoving * 0.98, $"air: the aero mod flying it wherever there is air and it moves ({_flowTicks} of {_atmoMoving} ticks, top Mach {_maxMach:F2})");
            if (_maxMach > 2) Check(_plasmaTicks > 0, $"air: plasma while fast (Mach {_maxMach:F1}: {_plasmaTicks} ticks, peak {_maxPlasma:F2})");
        }
        _phase = "done: " + why;
        Log("end: " + why);
    }

    public static string Report()
    {
        var head = $"reentry test {(_armed ? "running" : "")} [{_phase}] - {_checks - _fails.Count}/{_checks} passed";
        return head + " || " + string.Join(" || ", _log);
    }

    static void Check(bool ok, string what) { _checks++; if (!ok) _fails.Add(what); Log((ok ? "PASS " : "FAIL ") + what); }
    static void Log(string s) { _log.Add(s); Keen.VRage.Library.Diagnostics.Log.Default?.Info("[ORBIT-REENTRY] " + s); }

    static bool Flight(Entity e, out float mach)
    {
        mach = 0;
        if (_flight == null) Lookup();
        if (_flight == null) return false;
        var a = new object[] { e, null, null, null, null, null };
        if (!(bool)_flight.Invoke(null, a)) return false;
        mach = (float)a[3];
        return true;
    }

    static bool Plasma(Entity e, out float s)
    {
        s = 0;
        if (_entry == null) Lookup();
        if (_entry == null) return false;
        var a = new object[] { e, null, null };
        if (!(bool)_entry.Invoke(null, a)) return false;
        s = (float)a[1];
        return true;
    }

    static void Lookup()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("AeroMod.AeroApi"); if (t == null) continue;
            _flight = t.GetMethod("TryGetFlight"); _entry = t.GetMethod("TryGetEntry"); _atmo = t.GetMethod("TryGetAtmosphere"); break;
        }
    }

    /// <summary>The air density at the grid as the aero mod has it (kg/m3; 0 none or unknown).</summary>
    static float Density(Entity e)
    {
        if (_atmo == null) Lookup();
        if (_atmo == null) return 0f;
        var a = new object[] { e, null, null, null, null };
        return (bool)_atmo.Invoke(null, a) ? (float)a[1] : 0f;
    }

    static double Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(Quaternion.Inverse(a) * b);
        return 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
