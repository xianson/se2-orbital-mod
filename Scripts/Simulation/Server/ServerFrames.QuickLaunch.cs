using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): QUICK LAUNCH (a player's, creative only: QuickLaunch, QuickLaunchRules) and the level-flight move it
// shares with the dev fly test (FlyTest) - the aero mod's ground ray and cruise speed, the turn to level, the move.
public static partial class ServerFrames
{
    /// <summary>Client -> server: a seated pilot's quick launch (QuickLaunch's key). nose, top: the cockpit's forward and up
    /// in the grid's axes. Drained under FramesLock; the server checks everything itself (DoQuickLaunch).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(long id, Vector3 nose, Vector3 top)> QuickLaunchRequests = new System.Collections.Concurrent.ConcurrentQueue<(long, Vector3, Vector3)>();

    static void DoQuickLaunch(long id, Vector3 noseL, Vector3 topL)
    {
        var g = GridMembers.Get(id);
        var reg = SystemHost.Registry;
        bool alive = g?.Entity != null && g.IsServer && reg != null;
        // the pilot's mode, the server's own: its character (the world's creative mode, or this player's creative tools)
        bool creative = false;
        if (alive)
            try
            {
                var ch = PlayerCache.Of(g.Session);
                creative = ch != null && (g.Session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Player.GameModeSessionComponent>()?.IsCreative(ch) ?? false);
            }
            catch { creative = false; }
        Vector3D pos = default, cell = default; string body = null;
        if (alive && !GridMembers.TryPosition(g, out pos)) alive = false;
        bool atPlanet = alive && VoxelBerthRegistry.TryCellContaining(pos, reg, out body, out cell);
        bool onRails = alive && SystemHost.Frames?.FindByMember(id) != null;
        var v = QuickLaunchRules.Check(QuickLaunch.Creative, creative, System.Threading.Interlocked.Read(ref PlayerSeatGrid), id,
                                       alive, alive && !GridMembers.IsDynamicNow(g), onRails, atPlanet);
        if (v != QuickLaunchRules.Verdict.Go) { QuickLaunchSay(v, id, g); return; }
        var def = reg.FindDefinition(body);
        if (def == null) { QuickLaunchSay(QuickLaunchRules.Verdict.NotAtPlanet, id, g); return; }
        // where: over the ground (the aero ray; else over where it is), never lower, under the planet's space
        double rNow = (pos - cell).Length();
        double rGround = AeroGroundRadius(g, rNow);
        double shipR = 0;
        try { var bb = Keen.VRage.Core.Game.Data.BoundingBoxData.TryGetWorldAABB(g.Entity.Data); if (bb.HasValue) shipR = bb.Value.Size.Length() * 0.5; } catch { }
        v = QuickLaunchRules.Radius(rNow, rGround, PlanetBerths.ShellRadius(def), QuickLaunch.AltitudeM, shipR, out double r);
        if (v != QuickLaunchRules.Verdict.Go) { QuickLaunchSay(v, id, g); return; }
        double speed = QuickLaunchRules.Speed(QuickLaunch.SpeedMps, AeroCruiseSpeed(g, noseL, QuickLaunch.CruiseThrottle), QuickLaunch.FallbackSpeed, EntryHost.Cap);
        bool ok = FlyLevel(g, pos, cell, noseL, topL, r, speed, out var with);
        // (no ground ray: lifted from where it was - say that, not a height over a ground nobody measured)
        string where = double.IsNaN(rGround) ? $"{r - rNow:F0} m up" : $"{r - rGround:F0} m over the ground";
        Event($"quicklaunch: grid {id} '{g.DisplayName}' {where}, level, {speed:F0} m/s ({(ok ? $"{with.Count} grid(s) with it" : "refused")})");
        QuickLaunch.PendingSay = ok ? $"{where}, {speed:F0} m/s" : "Quick launch: refused (joined to a station?)";
    }

    static void QuickLaunchSay(QuickLaunchRules.Verdict v, long id, OrbitalGridComponent g)
    {
        Event($"quicklaunch: grid {id} '{g?.DisplayName}' not launched: {v}");
        QuickLaunch.PendingSay = QuickLaunchRules.Say(v);
    }

    // ── the level-flight move (quick launch and the dev fly test) ──
    static System.Reflection.MethodInfo _ftGround, _ftTop; static int _ftGen = -1;
    /// <summary>The aero mod's ground ray and cruise speed (AeroApi; bound again when the aero assembly changes - AeroLink).</summary>
    static void BindAeroFlight()
    {
        int ftGen = AeroLink.Generation;
        if (ftGen == _ftGen) return;
        _ftGen = ftGen; _ftGround = _ftTop = null;
        var t = AeroLink.Find("AeroMod.AeroApi");
        if (t == null) return;
        // (explicit parameter types throughout: an aero overload added later must not make a lookup ambiguous - API.md)
        _ftGround = t.GetMethod("TryGetGroundHeight", new[] { typeof(Entity), typeof(float).MakeByRefType() });
        _ftTop = t.GetMethod("GetTopSpeedAt", new[] { typeof(Entity), typeof(Vector3), typeof(float) });
    }
    /// <summary>The ground under a grid as a distance from the planet's centre (NaN: no aero mod, or no ground found).</summary>
    static double AeroGroundRadius(OrbitalGridComponent g, double rNow)
    {
        BindAeroFlight();
        if (_ftGround == null) return double.NaN;
        var ga = new object[] { g.Entity, null };
        try { if ((bool)_ftGround.Invoke(null, ga) && (float)ga[1] >= 0f) return rNow - (float)ga[1]; } catch { }
        return double.NaN;
    }
    /// <summary>The aero mod's top speed along a grid-axes direction at a throttle (NaN: none).</summary>
    static float AeroCruiseSpeed(OrbitalGridComponent g, Vector3 noseL, float throttle)
    {
        BindAeroFlight();
        float speed = float.NaN;
        try { if (_ftTop != null) speed = (float)_ftTop.Invoke(null, new object[] { g.Entity, noseL, throttle }); } catch { }
        return speed;
    }
    /// <summary>A grid (its joined set with it: MoveGrid) at r from the cell's centre straight over where it is, turned
    /// level - the cockpit's nose horizontal on its heading, its top away from the planet - flying along that nose.</summary>
    static bool FlyLevel(OrbitalGridComponent g, Vector3D pos, Vector3D cell, Vector3 noseL, Vector3 topL, double r, double speed, out List<OrbitalGridComponent> with)
    {
        Vector3D up = Vector3D.Normalize(pos - cell);
        var q = g.Entity.Data.GetWorldTransform().Orientation;
        Vector3D noseW = Vector3D.Transform((Vector3D)noseL, q), topW = Vector3D.Transform((Vector3D)topL, q);
        Vector3D h = noseW - up * Vector3D.Dot(noseW, up);
        if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(up, Vector3D.Cross(topW, up));   // (nose straight up or down: its top's heading)
        h = Vector3D.Normalize(h);
        var have = Quaternion.CreateFromForwardUp((Vector3)noseW, (Vector3)topW);
        var want = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        var turn = Quaternion.Normalize(want * Quaternion.Inverse(have));
        return MoveGrid(g, cell + up * r, h * speed, out with, default, turn);
    }
}
