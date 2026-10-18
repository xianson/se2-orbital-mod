using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// ServerFrames (partial): QUICK MOVE - the aero mod's creative quick launch moved by this mod (OrbitalApi.TryQuickMove,
// 2026-10-18: the player feature went to the aero mod, the user: "ctrl+T should be in aero creative"; this mod's 1.0.1 key,
// settings and toasts with it) - and the dev fly test's level-flight move (FlyTest): the aero mod's ground ray and cruise
// speed, the turn to level, the move.
public static partial class ServerFrames
{
    /// <summary>
    /// OrbitalApi.TryQuickMove: a server grid (its joined set with it) to position, turned to orientation (its new world
    /// orientation; the set turns rigidly about it), every body at velocity - through MoveGrid (the teleport phase with the
    /// joined set; statics and sets joined to one refused - HavokMoves), kept in its planet's own physics: refused on rails or
    /// outside a planet's cell, clamped under the space border (QuickMoveRules.UnderBorder: down by at most maxDrop, else
    /// refused). Takes FramesLock (the frames' state, as the old quick launch drained under it); aero is told of the move
    /// after it (FlushAeroNoteMoved - never a call into aero under our lock). Any thread; the aero mod calls it from its
    /// server teleport-phase job, holding no lock of its own.
    /// </summary>
    internal static bool QuickMove(Entity grid, Vector3D position, Quaternion orientation, Vector3D velocity, double maxDrop, out Vector3D placed, out string reason)
    {
        placed = position; reason = null;
        OrbitalGridComponent g = null;
        if (grid != null) foreach (var c in grid.Components) if (c is OrbitalGridComponent og && og.IsServer) { g = og; break; }
        if (g == null || !EntityAlive(g.Entity)) { reason = "not a ship this mod knows on the server"; return false; }
        if (!GridMembers.Finite(position) || !GridMembers.Finite(velocity)) { reason = "no finite place"; return false; }
        bool ok;
        List<OrbitalGridComponent> with = null;
        lock (FramesLock)
        {
            var reg = SystemHost.Registry;
            ok = reg != null && GridMembers.TryPosition(g, out Vector3D pos) && QuickMovePlace(g, reg, pos, position, maxDrop, out placed, out reason);
            if (ok && !GridMembers.IsDynamicNow(g)) { ok = false; reason = "a station cannot be launched"; }
            if (ok)
            {
                var turn = Quaternion.Normalize(orientation * Quaternion.Inverse(g.Entity.Data.GetWorldTransform().Orientation));
                ok = MoveGrid(g, placed, velocity, out with, default, turn);
                if (!ok) reason = "refused (joined to a station?)";
            }
            else if (reason == null) reason = "the ship is gone";
        }
        FlushAeroNoteMoved();
        Event($"quickmove (aero's quick launch): grid {g.Id} '{g.DisplayName}' {(ok ? $"to {ServerPlanetBeacon.Fmt(placed)}, {velocity.Length():F0} m/s, {with?.Count ?? 0} grid(s) with it" : "not moved: " + reason)}");
        if (ok) reason = $"{(with?.Count ?? 0) + 1} grid(s) moved";
        return ok;
    }

    /// <summary>Where a quick move lands: not on rails, in a planet's cell, the asked place under the border (caller holds
    /// FramesLock).</summary>
    static bool QuickMovePlace(OrbitalGridComponent g, SEAerospace.SystemDef.SystemRegistry reg, Vector3D pos, Vector3D asked, double maxDrop, out Vector3D placed, out string reason)
    {
        placed = asked; reason = null;
        if (SystemHost.Frames?.FindByMember(g.Id) != null) { reason = "not on rails (only near a planet's surface)"; return false; }
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) { reason = QuickMoveRules.Reason(QuickMoveRules.Verdict.NotAtPlanet); return false; }
        var def = reg.FindDefinition(body);
        if (def == null) { reason = QuickMoveRules.Reason(QuickMoveRules.Verdict.NotAtPlanet); return false; }
        var v = QuickMoveRules.UnderBorder((pos - cell).Length(), (asked - cell).Length(), PlanetBerths.ShellRadius(def), maxDrop, out double r);
        if (v != QuickMoveRules.Verdict.Go) { reason = QuickMoveRules.Reason(v); return false; }
        placed = cell + Vector3D.Normalize(asked - cell) * r;
        return true;
    }

    // ── the dev fly test's level-flight move (FlyTest, the harness key flytest) ──
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
