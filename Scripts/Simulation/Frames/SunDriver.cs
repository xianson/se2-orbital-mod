using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Sun;
using SEAerospace;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The sun, consistent across frames (the user, 2026-10-07). The sun is not Delfos: it is outside the Delfos system. In a
/// planet's frame it is the game's own sun - its angle is shared by every player and runs on the game's clock (warp does not
/// move it); the mod leaves it alone there. In any other frame it is that same sun turned by the rotation a crossing applies
/// to your ship (the planet's chart to inertial axes), so the light does not jump when you cross and stays where it is
/// relative to you. The planets spin on the game's clock too (SystemHost.SpinNow), so neither depends on warp.
///
/// Uses the render side's ISunDebug.Override (client only, not synced, not saved): each client lights its own frame; the
/// world's sun settings are never changed.
/// </summary>
public static class SunDriver
{
    public static bool Enabled = true;
    /// <summary>Unit vector from the azimuth/elevation the game uses for its sun (the same convention in and out).</summary>
    public static Vector3D DirectionToSun { get; private set; }
    public static string Status = "-";
    /// <summary>The light as drawn now (world axes of the frame you are in; the game's azimuth/elevation convention).</summary>
    public static Vector3D Light;
    private static bool _overriding;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, Vector3D observerWorld, double t)
    {
        ISunDebug sun = null;
        try { sun = session.Get<ISunDebug>(); } catch { }
        if (sun == null) { Status = "no ISunDebug"; return; }
        var f = FrameHost.PlayerFrame;
        Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent game = null;
        try { game = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent>(); } catch { }
        if (game == null) { Status = "no game sun"; return; }
        var (az, el) = game.GetSunDirection();
        Vector3D v = Vector3D.CreateFromAzimuthAndElevation(MathHelper.ToRadians(az), MathHelper.ToRadians(el));
        if (!Enabled || !SystemHost.Built || f == null)
        {
            if (_overriding) { sun.ResetOverride(); _overriding = false; }
            Light = v;
            Status = !Enabled ? "off" : "the game's sun (a planet's frame)";
            return;
        }
        // the frame's planet's chart to inertial: what a crossing out of that planet's frame does to your ship
        v = Chart.Of(f.ParentBodyName, t).ToInertial(v);
        var (a2, e2) = Vector3D.GetAzimuthAndElevation(v);
        sun.Override((float)MathHelper.ToDegrees(a2), (float)MathHelper.ToDegrees(e2));
        _overriding = true;
        DirectionToSun = -v; Light = v;
        Status = $"frame #{f.Id} ({f.ParentBodyName}): game sun az {az:F1} el {el:F1} -> az {MathHelper.ToDegrees(a2):F1} el {MathHelper.ToDegrees(e2):F1}";
    }

    /// <summary>The observer's position in the root (star) frame.</summary>
    public static bool TryObserverCelestial(Vector3D world, double t, out Vector3D cel)
    {
        cel = default;
        var reg = SystemHost.Registry;
        var f = FrameHost.PlayerFrame;
        if (f != null)
        {
            var parent = reg.Find(f.ParentBodyName);
            if (parent == null) return false;
            StateVector s = OrbitPropagation.StateAt(f.Elements, t);
            cel = parent.OriginInRoot(t).Position + s.Position + (world - f.BerthCenter);
            return true;
        }
        string body; Vector3D cell;
        bool inCell = VoxelBerthRegistry.TryCellContaining(world, reg, out body, out cell);
        if (!inCell && !SystemHost.TryNearestCell(world, out body, out cell)) return false;
        var node = reg.Find(body);
        if (node == null) return false;
        // Legacy space is an inertial window; only a planet cell is the rotating chart.
        cel = node.OriginInRoot(t).Position + (inCell ? Chart.Of(body, t).ToInertial(world - cell) : world - cell);
        return true;
    }
}
