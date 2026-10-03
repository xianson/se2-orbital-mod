using Keen.VRage.Core;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.Game2.Client.GameSystems.PlayerControl;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV spectator camera: overrides the render camera's world transform (CameraComponent's public
/// SetTransformOverride, applied every frame after the camera controllers), so screenshots can be
/// taken from anywhere while the player stays where it is. The override is re-applied every tick
/// from a target that may move (a planet's displayed position, the player, the map diorama).
/// </summary>
public static class SpecCam
{
    public enum Target { None, Fixed, Planet, Player, Map, Point, Sun, MapBody, Grid }
    private static Target _target;
    private static string _planet;
    private static double _dist, _bearing, _elev;
    private static Vector3D _fixedLook;
    private static long _grid;
    public static string Status = "off";
    /// <summary>The spectator transform while active: proxies and orbit lines must be built for this viewpoint.</summary>
    public static WorldTransform? Current;

    public static string Planet(string planet, double distKm, double bearingDeg, double elevDeg)
    { _target = Target.Planet; _planet = planet; _dist = distKm * 1000; _bearing = bearingDeg; _elev = elevDeg; return $"spectator: {planet} at {distKm} km"; }

    public static string Player(double distKm, double bearingDeg, double elevDeg)
    { _target = Target.Player; _dist = distKm * 1000; _bearing = bearingDeg; _elev = elevDeg; return $"spectator: player at {distKm} km"; }

    /// <summary>Orbit a grid, the bearing measured from its nose (0 ahead, 90 its right side, 180 behind), the
    /// elevation from its own deck: the same view of it however it is turned or moving (screenshots of effects).</summary>
    public static string Grid(long id, double distKm, double bearingDeg, double elevDeg)
    { _target = Target.Grid; _grid = id; _dist = distKm * 1000; _bearing = bearingDeg; _elev = elevDeg; return $"spectator: grid {id} at {distKm} km"; }

    /// <summary>Over the colonization map diorama; distance in map units (the map camera uses 0.2-1).</summary>
    public static string Map(double dist, double bearingDeg, double elevDeg)
    { _target = Target.Map; _dist = dist; _bearing = bearingDeg; _elev = elevDeg; return $"spectator: map at {dist} units"; }

    /// <summary>Orbit a fixed point (the orbital map diorama); re-issued every tick by its owner.</summary>
    public static void Orbit(Vector3D point, double dist, double bearingDeg, double elevDeg)
    { _target = Target.Point; _fixedLook = point; _dist = dist; _bearing = bearingDeg; _elev = elevDeg; }

    /// <summary>From the player, look straight at where the model says the sun is.</summary>
    public static string Sun() { _target = Target.Sun; return "spectator: looking at the model sun"; }

    /// <summary>Frame a planet globe on the colonization map (distance in map units).</summary>
    public static string MapBody(string body, double dist, double bearingDeg, double elevDeg)
    { _target = Target.MapBody; _planet = body; _dist = dist; _bearing = bearingDeg; _elev = elevDeg; return $"spectator: map globe {body}"; }

    public static string Off(Keen.VRage.Core.Game.Systems.Session session)
    {
        _target = Target.None;
        Current = null;
        var cam = Camera(session);
        cam?.SetTransformOverride(null);
        cam?.SetNextTransitionNonSmooth();
        Status = "off";
        return "spectator off";
    }

    /// <summary>Client tick. lookAt: resolved by the caller for planets (frame-aware display position).</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, Func<string, Vector3D?> planetPos, Vector3D playerPos)
    {
        if (_target == Target.None) return;
        var cam = Camera(session);
        if (cam == null) { Status = "no camera"; return; }
        Vector3D look;
        switch (_target)
        {
            case Target.Planet:
                var p = planetPos(_planet);
                if (!p.HasValue) { Status = "no planet " + _planet; return; }
                look = p.Value; break;
            case Target.Player: look = playerPos; break;
            case Target.Grid:
            {
                var g = GridMembers.Get(_grid);
                if (g?.Entity == null) { Status = "no grid " + _grid; return; }
                var gw = g.Entity.Data.GetWorldTransform();
                Vector3D gf = Vector3D.Normalize(WorldTransform.TransformDirection(Vector3.Forward, gw));
                Vector3D gu = Vector3D.Normalize(WorldTransform.TransformDirection(Vector3.Up, gw));
                Vector3D gr = Vector3D.Cross(gf, gu);
                double gb = _bearing * Math.PI / 180, ge = _elev * Math.PI / 180;
                Vector3D gpos = gw.Position + (gf * Math.Cos(gb) + gr * Math.Sin(gb)) * Math.Cos(ge) * _dist + gu * Math.Sin(ge) * _dist;
                Vector3 gfwd = (Vector3)Vector3D.Normalize(gw.Position - gpos);
                var gwt = new WorldTransform(gpos, Quaternion.CreateFromForwardUp(gfwd, (Vector3)gu));
                Current = gwt; cam.SetTransformOverride(gwt); Status = $"Grid {_grid} dist={_dist:F0} bearing={_bearing} elev={_elev}"; return;
            }
            case Target.Point: look = _fixedLook; break;
            case Target.MapBody:
                if (!MapView.GlobePos.TryGetValue(_planet, out look)) { Status = "no map globe " + _planet; return; }
                break;
            case Target.Sun:
            {
                Vector3D sd = SunDriver.Enabled ? SunDriver.DirectionToSun : StarProxy.Direction(new WorldTransform(playerPos), SystemHost.Now) ?? Vector3D.Zero;
                if (sd.LengthSquared() < 0.5) { Status = "no sun direction"; return; }
                var w0 = new WorldTransform(playerPos + sd * 3000, Quaternion.CreateFromForwardUp((Vector3)sd, Math.Abs(sd.Y) > 0.98 ? Vector3.UnitZ : Vector3.UnitY));
                Current = w0; cam.SetTransformOverride(w0); Status = "Sun"; return;
            }
            case Target.Map:
                var map = MapView.Map(session);
                if (map == null) { Status = "no map"; return; }
                look = map.MapEntity.Data.GetWorldTransform().Position; break;
            default: return;
        }
        double b = _bearing * Math.PI / 180, e = _elev * Math.PI / 180;
        var offset = new Vector3D(Math.Cos(e) * Math.Sin(b), Math.Sin(e), Math.Cos(e) * Math.Cos(b)) * _dist;
        Vector3D pos = look + offset;
        Vector3 fwd = (Vector3)Vector3D.Normalize(look - pos);
        Vector3 up = Math.Abs(fwd.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        var wt = new WorldTransform(pos, Quaternion.CreateFromForwardUp(fwd, up));
        Current = wt;
        cam.SetTransformOverride(wt);
        Status = $"{_target} {(_target == Target.Planet ? _planet : "")} dist={_dist:F2} bearing={_bearing} elev={_elev}";
    }

    public static CameraComponent CameraOf(Keen.VRage.Core.Game.Systems.Session session) => Camera(session);

    private static CameraComponent Camera(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var players = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
            var e = players?.LocalPlayerController?.CameraSystem?.RenderCameraEntity;
            return e?.TryGet<CameraComponent>();
        }
        catch { return null; }
    }
}
