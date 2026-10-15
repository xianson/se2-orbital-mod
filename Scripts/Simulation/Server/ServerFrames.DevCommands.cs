using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): DEV harness requests run on the server thread - launch, level, fly test, dampeners.
public static partial class ServerFrames
{
    /// <summary>DEV: put a grid (with its joined group) on a circular orbit this high over the planet it is at.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, double altKm, string body, double speed)> GridLaunch = new System.Collections.Concurrent.ConcurrentQueue<(long, double, string, double)>();
    /// <summary>DEV: level a grid - top away from the planet, nose (-Z) horizontal on its heading - its joined group with it.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<long> GridLevel = new System.Collections.Concurrent.ConcurrentQueue<long>();
    static void DoGridLevel(long id)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g == null || !g.IsServer || reg == null) { Event($"gridlevel: no server grid {id}"); return; }
        if (!GridMembers.TryPosition(g, out Vector3D pos)) { Event($"gridlevel: grid {id} is gone"); return; }   // (dead: its orientation read threw - 2026-10-15)
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out Vector3D cell)) { Event("gridlevel: not at a planet"); return; }
        Vector3D up = Vector3D.Normalize(pos - cell);
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        Vector3D f = Vector3D.Transform(new Vector3D(0, 0, -1), q);
        Vector3D h = f - up * Vector3D.Dot(f, up);
        if (h.LengthSquared() < 1e-6) { Vector3D r = Vector3D.Transform(new Vector3D(1, 0, 0), q); h = Vector3D.Cross(up, r); }
        h = Vector3D.Normalize(h);
        var target = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        var turn = Quaternion.Normalize(target * Quaternion.Inverse(q));
        bool ok = MoveGrid(g, pos, Vector3D.Zero, out var with, default, turn);
        Event($"gridlevel: grid {id} '{g.DisplayName}' levelled ({(ok ? $"{with.Count} grid(s) with it" : "refused")})");
    }
    /// <summary>DEV: a grid (its joined set too) at altKm over a planet, moving sideways at speed (NaN: circular orbit). body: that
    /// planet's cell from anywhere (a test ship in open space into the air), else the cell it is in.</summary>
    static void DoGridLaunch(long id, double altKm, string body = null, double speed = double.NaN)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g == null || !g.IsServer || reg == null) { Event($"gridlaunch: no server grid {id}"); return; }
        if (!GridMembers.TryPosition(g, out Vector3D pos)) { Event($"gridlaunch: grid {id} is gone"); return; }
        Vector3D cell;
        if (body != null) { if (!VoxelBerthRegistry.TryGetCell(body, reg, out cell)) { Event($"gridlaunch: no cell for {body}"); return; } }
        else if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out body, out cell)) { Event("gridlaunch: not at a planet (name one: gridlaunch <id> <altKm> <planet>)"); return; }
        var node = reg.Find(body); var def = reg.FindDefinition(body);
        if (node == null || def == null) return;
        double t = SystemHost.Now, r = def.RadiusMeters + altKm * 1000;
        var chart = Chart.Of(body, t);
        Vector3D up = Vector3D.Normalize(chart.ToInertial(pos - cell));
        Vector3D side = Vector3D.Cross(Vector3D.UnitZ, up);
        if (side.LengthSquared() < 1e-9) side = Vector3D.Cross(Vector3D.UnitX, up);
        Vector3D pI = up * r, vI = Vector3D.Normalize(side) * (double.IsNaN(speed) ? Math.Sqrt(node.Mu / r) : speed);
        bool ok = MoveGrid(g, cell + chart.FromInertial(pI), chart.VelFromInertial(pI, vI), out var with);
        Event($"gridlaunch: grid {id} '{g.DisplayName}' to a {altKm:F0} km circular orbit of {body}: {(ok ? $"moving ({with.Count} grid(s) with it)" : "refused")}");
    }

    /// <summary>DEV (FlyTest, Ctrl+T seated): a grid AltitudeM over the ground under it, level on its cockpit's heading, flying
    /// along its nose at cruise speed. nose, top: the cockpit's forward and up in the grid's axes.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, Vector3 nose, Vector3 top)> FlyTest = new System.Collections.Concurrent.ConcurrentQueue<(long, Vector3, Vector3)>();
    static System.Reflection.MethodInfo _ftGround, _ftTop; static int _ftGen = -1;
    static void DoFlyTest(long id, Vector3 noseL, Vector3 topL)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        if (g?.Entity == null || !g.IsServer || reg == null) { OrbitalMod.FlyTest.Status = Event2($"flytest: no server grid {id}"); return; }
        if (!GridMembers.TryPosition(g, out Vector3D pos)) { OrbitalMod.FlyTest.Status = Event2($"flytest: grid {id} is gone"); return; }
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) { OrbitalMod.FlyTest.Status = Event2("flytest: not at a planet"); return; }
        var def = reg.FindDefinition(body);
        if (def == null) return;
        // the aero mod's ground ray and cruise speed (AeroApi; bound again when the aero assembly changes - AeroLink)
        int ftGen = AeroLink.Generation;
        if (ftGen != _ftGen)
        {
            _ftGen = ftGen; _ftGround = _ftTop = null;
            var t = AeroLink.Find("AeroMod.AeroApi");
            if (t != null)
            {
                // (explicit parameter types throughout: an aero overload added later must not make a lookup ambiguous - API.md)
                _ftGround = t.GetMethod("TryGetGroundHeight", new[] { typeof(Entity), typeof(float).MakeByRefType() });
                _ftTop = t.GetMethod("GetTopSpeedAt", new[] { typeof(Entity), typeof(Vector3), typeof(float) });
            }
        }
        double rNow = (pos - cell).Length(), rGround = def.RadiusMeters; string over = "sea level";
        if (_ftGround != null)
        {
            var ga = new object[] { g.Entity, null };
            try { if ((bool)_ftGround.Invoke(null, ga) && (float)ga[1] >= 0f) { rGround = rNow - (float)ga[1]; over = "the ground"; } } catch { }
        }
        float speed = float.NaN;
        try { if (_ftTop != null) speed = (float)_ftTop.Invoke(null, new object[] { g.Entity, noseL, OrbitalMod.FlyTest.CruiseThrottle }); } catch { }
        if (!(speed > 1f) || float.IsInfinity(speed)) speed = OrbitalMod.FlyTest.FallbackSpeed;
        // level: the cockpit's nose horizontal on its heading, its top away from the planet
        Vector3D up = Vector3D.Normalize(pos - cell);
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        Vector3D noseW = Vector3D.Transform((Vector3D)noseL, q), topW = Vector3D.Transform((Vector3D)topL, q);
        Vector3D h = noseW - up * Vector3D.Dot(noseW, up);
        if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(up, Vector3D.Cross(topW, up));   // (nose straight up or down: its top's heading)
        h = Vector3D.Normalize(h);
        var have = Quaternion.CreateFromForwardUp((Vector3)noseW, (Vector3)topW);
        var want = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        var turn = Quaternion.Normalize(want * Quaternion.Inverse(have));
        double r = rGround + OrbitalMod.FlyTest.AltitudeM;
        bool ok = MoveGrid(g, cell + up * r, h * speed, out var with, default, turn);
        OrbitalMod.FlyTest.Status = Event2($"flytest: grid {id} '{g.DisplayName}' {OrbitalMod.FlyTest.AltitudeM:F0} m over {over}, level, {speed:F0} m/s ({(ok ? $"{with.Count} grid(s) with it" : "refused")})");
    }
    static string Event2(string s) { Event(s); return s; }

    /// <summary>DEV: move a grid (server), unless it has joints.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, Vector3D d, bool group)> GridMove = new System.Collections.Concurrent.ConcurrentQueue<(long, Vector3D, bool)>();

    /// <summary>DEV: a grid's dampeners on / off (server), as the game's own toggle does.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, bool on)> GridDamp = new System.Collections.Concurrent.ConcurrentQueue<(long, bool)>();
    static void DoGridDamp(long id, bool on)
    {
        try
        {
            var g = GridMembers.Get(id);
            var e = g?.IsServer == true ? g.Entity : null;
            if (e == null) { Event($"griddamp: no server grid {id}"); return; }
            if (on) e.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.DampeningData));
            else e.Data.TryRemove<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>();
            string thrust = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)
                ? $"thrust +{mt.Regular.Positive} -{mt.Regular.Negative}" : "no thrust data";
            Event($"griddamp: grid {id} '{g.DisplayName}' dampeners {(on ? "on" : "off")}; {thrust}");
        }
        catch (Exception ex) { Event("griddamp failed: " + ex.Message); }
    }
}
