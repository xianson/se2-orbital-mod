using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Sun;
using SEAerospace;
using SEAerospace.Orbital;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// The sun, consistent across frames (the user, 2026-10-07; one sun, 2026-10-10). The sun is not Delfos: it is outside the
/// Delfos system - a fixed direction in inertial space. Drawn as it is in orbit, and turned into a planet's rotating chart in
/// that planet's frame: the planet's day is its own spin (SystemHost.SpinNow, the game's clock - warp does not move it), and
/// orbit, sky and terrain all see the same sun.
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
        Vector3D v = SunDir(MathHelper.ToRadians(az), MathHelper.ToRadians(el));
        string planet = f != null ? f.ParentBodyName : FrameHost.ObserverPlanet;
        if (!Enabled || !SystemHost.Built || planet == null)
        {
            if (_overriding) { sun.ResetOverride(); _overriding = false; }
            Light = v;
            Status = !Enabled ? "off" : "the game's sun (no frame)";
            return;
        }
        // ONE SUN (2026-10-10, "the sun atmosphere lighting mismatched" on a night entry - the user): the sun is a fixed
        // direction in inertial space, taken once from the game's sun through the planet's chart; every frame draws THAT -
        // the orbit frame as it is, a planet's frame turned into its rotating chart. It used to be the game's own sun in a
        // planet's frame and the game's sun turned by the chart in orbit: those agree only if the game turns its sun exactly
        // against our planet's spin - same axis (the world's sun axis is private; we spin about +Z), same clock (the game's
        // sun runs on time of day and jumps at midnight unless its period divides a day; we spin on the total game time).
        // Where they did not, the day side, the sky and the sun disagreed between orbit and planet - and after arrival.
        var chart = Chart.Of(planet, t);
        if (!_haveInertial) { _inertialLight = chart.ToInertial(v); _haveInertial = true; }
        v = f != null ? _inertialLight : chart.FromInertial(_inertialLight);
        // ON THE MAP (2026-10-18, UI audit cosmetic: "the skybox sun = a second sun next to the Delfos dot"): the sky's sun put
        // behind the map's own star, as the map camera sees it - one sun on the map, and the globes lit from it. Off the map,
        // the next tick puts the world's sun back.
        if (MapView.Visible && CleanMap.Enabled && CleanMap.StarWorld is Vector3D star && MapPipeline.CameraPosition is Vector3D cam)
        {
            Vector3D toStar = star - cam;
            if (toStar.LengthSquared() > 1e-12) v = -Vector3D.Normalize(toStar);
        }
        var (a2, e2) = Vector3D.GetAzimuthAndElevation(v);
        sun.Override((float)MathHelper.ToDegrees(a2), (float)MathHelper.ToDegrees(e2));
        _overriding = true;
        DirectionToSun = -v; Light = v;
        // (the line made again only when what it prints changes - the frame, the planet, any angle by a tenth of a degree -
        //  or another status replaced it: a new string (two) every frame before, all of this system's garbage, 2026-10-14)
        double a2d = MathHelper.ToDegrees(a2), e2d = MathHelper.ToDegrees(e2);
        long fid = f != null ? f.Id : long.MinValue;
        double kAz = System.Math.Round(az * 10.0), kEl = System.Math.Round(el * 10.0), kA2 = System.Math.Round(a2d * 10), kE2 = System.Math.Round(e2d * 10);
        if (!ReferenceEquals(Status, _said) || fid != _saidFrame || planet != _saidPlanet || kAz != _saidAz || kEl != _saidEl || kA2 != _saidA2 || kE2 != _saidE2)
        {
            _said = $"{(f != null ? $"frame #{f.Id} ({planet})" : $"{planet}'s chart")}: game sun az {az:F1} el {el:F1} -> az {a2d:F1} el {e2d:F1}";
            _saidFrame = fid; _saidPlanet = planet; _saidAz = kAz; _saidEl = kEl; _saidA2 = kA2; _saidE2 = kE2;
        }
        Status = _said;
    }
    static string _said, _saidPlanet; static long _saidFrame; static double _saidAz, _saidEl, _saidA2, _saidE2;

    /// <summary>The direction for an azimuth and elevation, as the game's own <c>Vector3D.GetAzimuthAndElevation</c> (which
    /// makes the sun's az/el, and reads ours back for the override) defines them - its exact inverse (round trip under 1e-13
    /// rad). Vector3D.CreateFromAzimuthAndElevation is not: in this build it applies each rotation twice, so the sun turned
    /// through it and read back came out elsewhere - in orbit the sun stood wherever that put it, not the game's sun turned
    /// by the chart (2026-10-10: the night entry whose sky and sun did not match; globesun.sh).</summary>
    public static Vector3D SunDir(double azimuth, double elevation)
        => new Vector3D(-Math.Cos(elevation) * Math.Sin(azimuth), Math.Sin(elevation), -Math.Cos(elevation) * Math.Cos(azimuth));

    /// <summary>The sun's fixed inertial light direction (taken from the game's sun the first tick a planet's chart is known).</summary>
    private static Vector3D _inertialLight;
    private static bool _haveInertial;
    /// <summary>A new session: take the sun again.</summary>
    public static void Reset() { _haveInertial = false; }

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
