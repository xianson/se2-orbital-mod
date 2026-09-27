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
    /// <summary>The zoom: the game's camera distance to its focus (0 when not driving the camera).</summary>
    public static double Distance;
    /// <summary>Where the camera looks on the map plane (world), while driving the camera.</summary>
    public static Vector3D? Focus;
    private static Vector3D _focusGame;

    /// <summary>Centre the view on a point of the map (world); from the next frame.</summary>
    public static void PanTo(Vector3D world) { if (Focus.HasValue) _pan = world - _focusGame; }

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
        Vector3D up = q * Vector3D.Up, east = q * Vector3D.Right, north = q * Vector3D.Forward;
        Vector3D gf = (Vector3D)g.Orientation.GetForward();
        double den = Vector3D.Dot(gf, up);
        if (Math.Abs(den) < 1e-4) { Status = "camera edge-on"; return; }
        double d = Vector3D.Dot(mapPos - g.Position, up) / den;
        if (!(d > 0)) { Status = "camera faces away"; return; }
        Vector3D focusGame = g.Position + gf * d;
        if (!_init)
        {
            // Start from the game's angle, so opening the map looks as it always has.
            _pitch = Math.Clamp(Math.Asin(Math.Clamp(-Vector3D.Dot(gf, up), -1, 1)), MinPitch, MaxPitch);
            _yaw = Math.Atan2(Vector3D.Dot(gf, east), Vector3D.Dot(gf, north));
            _pan = Vector3D.Zero; _init = true;
        }

        // Mouse drags.
        bool l = MapInput.Left, r = MapInput.Right, m = MapInput.Middle;
        if (haveMouse)
        {
            bool free = !Maneuvers.ClaimsMouse && CleanMap.Hovered == null && !MapMenu.Open;
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
                    double k = d * 1.0 / px;
                    Vector3D right = Math.Cos(_yaw) * east - Math.Sin(_yaw) * north;
                    Vector3D fwd = Math.Sin(_yaw) * east + Math.Cos(_yaw) * north;
                    double tilt = Math.Max(0.35, Math.Sin(_pitch));   // screen-up covers more ground when tilted
                    _pan += (-dm.X * right + dm.Y / tilt * fwd) * k;
                }
            }
        }
        if (!l) _lArmed = false;
        if (!r) _rArmed = false;
        if (!m) _mArmed = false;
        _lWas = l; _rWas = r; _mWas = m;

        // Keep the focus on the map.
        double lim = Math.Max(0.5, map.MaxDistance);
        if (_pan.Length() > lim) _pan = _pan * (lim / _pan.Length());

        // The camera: about the focus, at the game's distance, from our angle.
        _focusGame = focusGame;
        Vector3D focus = focusGame + _pan;
        Focus = focus;
        Vector3D dir = Vector3D.Normalize(Math.Cos(_pitch) * (Math.Sin(_yaw) * east + Math.Cos(_yaw) * north) - Math.Sin(_pitch) * up);
        Vector3D camUp = Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(dir, up), dir));
        var wt = new WorldTransform(focus - dir * d, Quaternion.CreateFromForwardUp((Vector3)dir, (Vector3)camUp));
        cam.SetTransformOverride(wt);
        _overridden = true; Distance = d;
        Status = $"yaw {_yaw * 180 / Math.PI:F0} pitch {_pitch * 180 / Math.PI:F0} dist {d:F3} pan {_pan.Length():F3}{(Dragging ? " " + _mode : "")}";
    }

    /// <summary>The map closed (or the camera is someone else's): hand the camera back and start fresh next time.</summary>
    public static void Release(Keen.VRage.Core.Game.Systems.Session session)
    {
        _mode = Mode.None; _init = false; Distance = 0; Focus = null;
        if (!_overridden) return;
        _overridden = false;
        try { var cam = SpecCam.CameraOf(session); cam?.SetTransformOverride(null); cam?.SetNextTransitionNonSmooth(); } catch { }
    }

    /// <summary>DEV: mapcam [yawDeg pitchDeg [panX panZ]] | mapcam reset</summary>
    public static string Dev(string[] a)
    {
        if (a.Length > 1 && a[1] == "reset") { _init = false; return "map camera reset"; }
        if (a.Length > 2) { _yaw = double.Parse(a[1]) * Math.PI / 180; _pitch = Math.Clamp(double.Parse(a[2]) * Math.PI / 180, MinPitch, MaxPitch); _init = true; }
        return "map camera: " + Status;
    }
}
