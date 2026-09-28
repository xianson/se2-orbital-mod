using Keen.VRage.Core;
using Keen.Game2.Client.WorldObjects.ColonizationMap;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The map camera, with pan and orbit. The game's map camera looks from one fixed angle and moves
/// only by WASD and the wheel; this keeps those (its zoom and its focus are read back each frame from
/// the game's own camera controller) and adds, on top, the viewing angle and a pan of our own:
///  - right-drag: orbit (turn about the focus, tilt from low to straight down)
///  - left-drag on empty map, or middle-drag: pan (the map follows the cursor)
///  - a right click without dragging still opens the context menu
/// The result goes to the render camera as an override while the map is open, and is dropped when
/// it closes.
/// </summary>
public static class MapCamera
{
    public static bool Enabled = true;
    public static string Status = "-";
    /// <summary>A pan or orbit is in progress: the map's own clicks and hovers stand aside.</summary>
    public static bool Dragging => _mode != Mode.None;
    /// <summary>A drag ended this frame: its button release is not a click.</summary>
    public static bool DragEnded;
    /// <summary>The zoom: the game's camera distance to its focus (0 when not driving the camera).</summary>
    public static double Distance;
    /// <summary>Where the camera looks on the map plane (world), while driving the camera.</summary>
    public static Vector3D? Focus;
    /// <summary>DEV: camera distance to use instead of the game's zoom (0 = the game's).</summary>
    public static double DevZoom;
    private static Vector3D _focusGame, _mapPos;
    private static QuaternionD _mapQ = QuaternionD.Identity;

    /// <summary>
    /// The zoom is virtual: the camera is always RenderDistance from the map's centre, and the map is
    /// scaled instead (world = RenderDistance / Distance per map unit). The game's wheel zoom (Distance)
    /// then spans the whole system down to a low orbit with no near- or far-plane limits. The view's
    /// centre (FocusV) is in map units about the map's origin (the body the map is about).
    /// </summary>
    public const double RenderDistance = 1.0;
    public static double Scale => Distance > 0 ? RenderDistance / Distance : 1.0;
    public static Vector3D FocusV => _pan;
    /// <summary>A map point (map units) in the world.</summary>
    public static Vector3D ToWorld(Vector3D v) => _mapPos + _mapQ * ((v - _pan) * Scale);
    /// <summary>A world point back to map units.</summary>
    public static Vector3D FromWorld(Vector3D w)
    {
        Vector3D d = w - _mapPos;
        var l = new Vector3D(Vector3D.Dot(d, _mapQ * Vector3D.UnitX), Vector3D.Dot(d, _mapQ * Vector3D.UnitY), Vector3D.Dot(d, _mapQ * Vector3D.UnitZ));
        return _pan + l / Scale;
    }
    /// <summary>The map's origin moved by -dv (it follows another body now): keep the view where it is.</summary>
    public static void Shift(Vector3D dv) { _pan += dv; if (_panGoal.HasValue) _panGoal = _panGoal.Value + dv; }

    /// <summary>Centre the view on a point of the map (world); from the next frame.</summary>
    static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

    public static void PanTo(Vector3D world, bool smooth = false)
    {
        if (!Focus.HasValue || !Finite(world)) return;
        Vector3D v = FromWorld(world);
        if (!Finite(v)) return;
        if (smooth) _panGoal = v;
        else { _pan = v; _panGoal = null; }
    }

    /// <summary>Glide the zoom to a camera distance (on top of the game's wheel zoom, which still works).</summary>
    public static void ZoomTo(double dist)
    {
        if (_map == null || !(dist > 0)) return;
        PlanetRenderBridge.SetMapTargetDistance(_map, (float)Math.Clamp(dist, _map.MinDistance, _map.MaxDistance));
    }
    private static ColonizationMapSessionComponent _map;

    private static Vector3D? _panGoal;
    private static double _lastWall;
    public const double MinZoomDistance = 0.08;   // closer, the globes clip at the camera's near plane

    private enum Mode { None, Pan, Orbit }
    private static Mode _mode;
    private static bool _init, _overridden;
    private static double _yaw, _pitch;          // radians, about the map's up; pitch 0 = edge-on, pi/2 = straight down
    private static Vector3D _pan;                // map-plane offset of the focus from the game's own (world units)
    private static Vector2 _last, _pressL, _pressR, _pressM;
    private static bool _lWas, _rWas, _mWas, _lArmed, _rArmed, _mArmed;
    private const float DragStart = 5f;          // px before a press becomes a drag
    private const double MinPitch = 8 * Math.PI / 180, MaxPitch = 89.5 * Math.PI / 180;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, ColonizationMapSessionComponent map, bool haveMouse, Vector2 mouse)
    {
        var cam = SpecCam.CameraOf(session);
        if (!Enabled || cam == null || SpecCam.Current.HasValue) { Release(session); return; }
        Entity ctrl = null;
        try { ctrl = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>()?.LocalPlayerController?.CameraSystem?.ActiveCameraController; } catch { }
        if (ctrl == null) { Status = "no camera controller"; return; }

        // The game's own view: its focus on the map plane and its distance (zoom).
        var g = ctrl.Data.GetWorldTransform();
        Vector3D mapPos = map.MapEntity.Data.GetWorldTransform().Position;
        QuaternionD q = (QuaternionD)map.Orientation;
        _mapPos = mapPos; _mapQ = q;
        Vector3D up = q * Vector3D.Up, east = q * Vector3D.Right, north = q * Vector3D.Forward;
        Vector3D gf = (Vector3D)g.Orientation.GetForward();
        double den = Vector3D.Dot(gf, up);
        if (Math.Abs(den) < 1e-4) { Status = "camera edge-on"; return; }
        double d = Vector3D.Dot(mapPos - g.Position, up) / den;
        if (!(d > 0)) { Status = "camera faces away"; return; }
        Vector3D focusGame = g.Position + gf * d;
        // Better, the map camera's own state relative to the map entity: read now, it moves with the
        // map (which follows you). The controller's transform is a frame old, and when you move fast
        // (free flight, 300 m/s) that put the metre-wide map off screen: the map drew empty.
        if (PlanetRenderBridge.TryGetMapCamera(map, out var off, out var dist)) { focusGame = mapPos + (Vector3D)off; d = dist; }
        // Glides (focus on a body): ease the pan and the zoom toward their goals.
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double dt = _lastWall > 0 ? Math.Min(0.1, wall - _lastWall) : 0; _lastWall = wall;
        double ease = 1 - Math.Exp(-dt * 7);
        if (_panGoal.HasValue)
        {
            _pan += (_panGoal.Value - _pan) * ease;
            if ((_panGoal.Value - _pan).Length() < Math.Max(1e-12, d * 1e-4)) { _pan = _panGoal.Value; _panGoal = null; }
        }
        _map = map;
        if (DevZoom > 0) d = DevZoom;   // DEV: a set zoom instead of the game's wheel
        if (!_init)
        {
            // Start from the game's angle, so opening the map looks as it always has.
            _pitch = Math.Clamp(Math.Asin(Math.Clamp(-Vector3D.Dot(gf, up), -1, 1)), MinPitch, MaxPitch);
            _yaw = Math.Atan2(Vector3D.Dot(gf, east), Vector3D.Dot(gf, north));
            _pan = Vector3D.Zero; _init = true;
        }

        // Mouse drags.
        var modeBefore = _mode;
        bool l = MapInput.Left, r = MapInput.Right, m = MapInput.Middle;
        if (haveMouse)
        {
            // Left-drag pans from anywhere but a maneuver node or handle (a click still selects or adds).
            bool free = !Maneuvers.OnGizmo && !MapMenu.Open;
            if (l && !_lWas) { _pressL = mouse; _lArmed = free; }
            if (r && !_rWas) { _pressR = mouse; _rArmed = !MapMenu.Open; }
            if (m && !_mWas) { _pressM = mouse; _mArmed = true; }
            if (_mode == Mode.None)
            {
                if (r && _rArmed && (mouse - _pressR).Length() > DragStart) { _mode = Mode.Orbit; MapInput.RightDragged = true; _last = mouse; }
                else if (l && _lArmed && (mouse - _pressL).Length() > DragStart) { _mode = Mode.Pan; _last = mouse; }
                else if (m && _mArmed && (mouse - _pressM).Length() > DragStart) { _mode = Mode.Pan; _last = mouse; }
            }
            Vector2 dm = mouse - _last; _last = mouse;
            float px = Math.Max(1f, MapPipeline.ScreenSize.Y);
            if (_mode == Mode.Orbit)
            {
                if (!r) _mode = Mode.None;
                else
                {
                    _yaw += dm.X / px * Math.PI;             // a screen height of drag = half a turn
                    _pitch = Math.Clamp(_pitch + dm.Y / px * Math.PI, MinPitch, MaxPitch);
                }
            }
            else if (_mode == Mode.Pan)
            {
                if (!l && !m) _mode = Mode.None;
                else
                {
                    // Move the focus against the drag, so the map follows the cursor (about one
                    // distance per screen height at the camera's field of view).
                    // (In map units, on the map's own axes: the pan is the view's centre on the map.)
                    double k = d * 1.0 / px;
                    Vector3D right = Math.Cos(_yaw) * Vector3D.Right - Math.Sin(_yaw) * Vector3D.Forward;
                    Vector3D fwd = Math.Sin(_yaw) * Vector3D.Right + Math.Cos(_yaw) * Vector3D.Forward;
                    double tilt = Math.Max(0.35, Math.Sin(_pitch));   // screen-up covers more ground when tilted
                    _pan += (-dm.X * right + dm.Y / tilt * fwd) * k;
                    _panGoal = null;
                }
            }
        }
        DragEnded = modeBefore != Mode.None && _mode == Mode.None;
        if (!l) _lArmed = false;
        if (!r) _rArmed = false;
        if (!m) _mArmed = false;
        _lWas = l; _rWas = r; _mWas = m;

        // Keep the focus on the map.
        double lim = Math.Max(6.0, map.MaxDistance);   // (map units about the map's body: the whole system fits)
        if (_pan.Length() > lim) _pan = _pan * (lim / _pan.Length());

        // The camera: about the focus, at the game's distance, from our angle.
        _focusGame = focusGame;
        Vector3D focus = mapPos;   // the camera looks at the map's centre; the map moves under it (virtual zoom)
        Focus = focus;
        Vector3D dir = Vector3D.Normalize(Math.Cos(_pitch) * (Math.Sin(_yaw) * east + Math.Cos(_yaw) * north) - Math.Sin(_pitch) * up);
        Vector3D camUp = Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(dir, up), dir));
        var wt = new WorldTransform(focus - dir * RenderDistance, Quaternion.CreateFromForwardUp((Vector3)dir, (Vector3)camUp));
        // Never a NaN transform to the engine (it does not check; a NaN camera crashes the renderer).
        if (!Finite(wt.Position) || !wt.Orientation.IsValidAndRotationIsNormalized() || !Finite(_pan))
        {
            _pan = Vector3D.Zero; _panGoal = null;
            Release(session);
            Status = "camera reset (invalid transform)";
            return;
        }
        cam.SetTransformOverride(wt);
        _overridden = true; Distance = d;
        Status = $"yaw {_yaw * 180 / Math.PI:F0} pitch {_pitch * 180 / Math.PI:F0} dist {d:F3} pan {_pan.Length():F3}{(Dragging ? " " + _mode : "")}";
    }

    /// <summary>The map closed (or the camera is someone else's): hand the camera back and start fresh next time.</summary>
    public static void Release(Keen.VRage.Core.Game.Systems.Session session)
    {
        _mode = Mode.None; _init = false; Distance = 0; Focus = null;
        _panGoal = null; _lastWall = 0; _map = null;
        if (!_overridden) return;
        _overridden = false;
        try { var cam = SpecCam.CameraOf(session); cam?.SetTransformOverride(null); cam?.SetNextTransitionNonSmooth(); } catch { }
    }

    /// <summary>DEV: mapcam [yawDeg pitchDeg [panX panZ]] | mapcam reset</summary>
    public static string Dev(string[] a)
    {
        if (a.Length > 1 && a[1] == "reset") { _init = false; DevZoom = 0; return "map camera reset"; }
        if (a.Length > 2 && a[1] == "at") { var b = SystemHost.Registry?.Find(a[2]); if (b == null) return "no body"; _pan = CleanMap.SolarLocal(b, SystemHost.Now); _panGoal = null; return "centre on " + a[2]; }
        if (a.Length > 3 && a[1] == "pan") { _pan = new Vector3D(double.Parse(a[2]), 0, double.Parse(a[3])); return "pan " + _pan; }
        if (a.Length > 2 && a[1] == "zoom") { DevZoom = double.Parse(a[2]); return "map zoom " + DevZoom; }
        if (a.Length > 2) { _yaw = double.Parse(a[1]) * Math.PI / 180; _pitch = Math.Clamp(double.Parse(a[2]) * Math.PI / 180, MinPitch, MaxPitch); _init = true; }
        return "map camera: " + Status + " | view " + CleanMap.Status + " | " + CleanMap.Frame +  " | vec " + MapPipeline.VectorStatus + " | " + GameMap.StarDebug + " focus " + CleanMap.ViewFocus + " | click " + CleanMap.ClickDebug;
    }
}
