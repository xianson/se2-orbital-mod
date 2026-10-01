using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Sun;
using SEAerospace;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Drives the sun from the orbital model: the light comes from the star as seen from wherever the
/// observer is in the system (its frame's rails position, or its planet's position plus its offset
/// in the cell). As Verdure goes round the star, the sun moves across the sky with it; in a
/// conjunction the sun is where the star is from that point of the orbit.
///
/// Uses the render side's ISunDebug.Override (client only, not synced, not saved): the world's own
/// sun settings are never changed, so turning this off restores the vanilla day cycle.
/// Convention (SunHelper): the engine's azimuth/elevation describe the LIGHT direction, i.e. minus
/// the direction to the sun.
/// </summary>
public static class SunDriver
{
    /// <summary>Off by default: the game's sun is its own (vanilla), not Delfos (StarProxy draws Delfos).</summary>
    public static bool Enabled = false;
    public static Vector3D DirectionToSun { get; private set; }
    public static string Status = "-";
    private static bool _overriding;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, Vector3D observerWorld, double t)
    {
        ISunDebug sun = null;
        try { sun = session.Get<ISunDebug>(); } catch { }
        if (sun == null) { Status = "no ISunDebug"; return; }
        if (!Enabled || !SystemHost.Built)
        {
            if (_overriding) { sun.ResetOverride(); _overriding = false; }
            Status = "off";
            return;
        }
        if (!TryObserverCelestial(observerWorld, t, out Vector3D cel)) { Status = "no observer"; return; }
        var reg = SystemHost.Registry;
        Vector3D star = reg.Root.OriginInRoot(t).Position;
        Vector3D d = star - cel;
        if (d.LengthSquared() < 1) return;
        d = Vector3D.Normalize(d);
        if (FrameHost.PlayerFrame == null && VoxelBerthRegistry.TryCellContaining(observerWorld, reg, out string cb, out _))
            d = Chart.Of(cb, t).FromInertial(d);   // the rotating chart: day and night
        DirectionToSun = d;
        var (az, el) = Vector3.GetAzimuthAndElevation(-(Vector3)d);
        sun.Override(MathHelper.ToDegrees(az), MathHelper.ToDegrees(el));
        _overriding = true;
        Status = $"az={MathHelper.ToDegrees(az):F1} el={MathHelper.ToDegrees(el):F1} dir=({d.X:F3},{d.Y:F3},{d.Z:F3})";
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
