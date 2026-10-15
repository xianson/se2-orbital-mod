using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>In flight: HUD points and markers, and the game's own GPS marker look.</summary>
public static partial class MapPipeline
{
    /// <summary>A world point on the HUD: its screen position, clamped to the game's marker ellipse when off screen.</summary>
    public static bool HudPoint(Vector3D world, out Vector2 s, out bool edge)
    {
        s = default; edge = false;
        if (_cam == null) return false;
        var wt = _cam.Entity.Data.GetWorldTransform();
        Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward;
        Vector2 size = ScreenSize, centre = size * 0.5f;
        bool front = Vector3D.Dot(world - wt.Position, fwd) > 1e-6;
        s = _cam.WorldToScreenPoint(front ? world : wt.Position - (world - wt.Position));
        float mx = size.X * 0.12f, my = size.Y * 0.14f;
        edge = !front || s.X < mx || s.Y < my || s.X > size.X - mx || s.Y > size.Y - my;
        if (edge)
        {
            Vector2 dir = s - centre;
            if (!front) dir = -dir;
            if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
            float ax = centre.X - mx, ay = centre.Y - my;
            s = centre + dir * (1f / (float)Math.Sqrt(dir.X * dir.X / (ax * ax) + dir.Y * dir.Y / (ay * ay)));
        }
        return true;
    }

    static readonly Vector2[] _dia = new Vector2[5];
    /// <summary>
    /// A HUD marker for a frame-transferred GPS point: a ringed diamond, its name and distance; off
    /// screen (or behind the camera) it sits on the screen edge in its true direction.
    /// </summary>
    public static void HudMarker(Vector3D world, string name, string distance, ColorSRGB color)
    {
        if (_batch == null || _drawLine == null || _cam == null) return;
        try
        {
            var wt = _cam.Entity.Data.GetWorldTransform();
            Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward;
            Vector2 size = ScreenSize, centre = size * 0.5f;
            bool front = Vector3D.Dot(world - wt.Position, fwd) > 1e-6;
            Vector2 s = _cam.WorldToScreenPoint(front ? world : wt.Position - (world - wt.Position));
            float mx = size.X * 0.12f, my = size.Y * 0.14f;   // clear of the HUD panels in the corners
            bool edge = !front || s.X < mx || s.Y < my || s.X > size.X - mx || s.Y > size.Y - my;
            if (edge)
            {
                Vector2 dir = s - centre;
                if (!front) dir = -dir;
                if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
                float ax = centre.X - mx, ay = centre.Y - my;   // on an ellipse, as the game clamps its markers
                s = centre + dir * (1f / (float)Math.Sqrt(dir.X * dir.X / (ax * ax) + dir.Y * dir.Y / (ay * ay)));
            }
            float u = UiScale(size.Y);
            float r = 9f * u;
            Vector2 up = new Vector2(0, -r), rt = new Vector2(r, 0);
            var dia = _dia; dia[0] = s + up; dia[1] = s + rt; dia[2] = s - up; dia[3] = s - rt; dia[4] = s + up;   // (one array, reused)
            if (!ScreenIcon("diamond", s, 8f * u, color)) for (int i = 0; i < 4; i++) Seg(dia[i], dia[i + 1], color, 2f * u);
            ScreenDot(s, 2.5f * u, color);
            ScreenText(s + new Vector2(14, -14) * u, name ?? "", color, 0.62f * u);
            ScreenText(s + new Vector2(14, 4) * u, distance + (edge ? "  >" : ""), new ColorSRGB(0.85f, 0.9f, 1f, 0.9f), 0.55f * u);
        }
        catch { }
    }

    // ── the game's own GPS marker look (GPSMarkerHelpers.DrawSingleMarker) ──
    private static object _gpsSettings, _lodFull;
    private static long _gpsRetryAt;
    private static MethodInfo _drawSingle;
    public static string GpsError = "";

    static object[] _markerArgs; static object _boxedCentre; static Vector2 _boxedCentreAt;
    static readonly object BoxedTrue = true, BoxedFalse = false;
    /// <summary>
    /// Draw a GPS marker exactly as the game's HUD does (its icon, name, colour; the edge arrow when off
    /// screen), at a world point, showing the given distance. False if the game's drawing is unavailable.
    /// </summary>
    public static bool GameMarker(Keen.VRage.Core.Game.Systems.Session session, object marker, Vector3D world, double distance)
    {
        if (_batch == null || _cam == null || marker == null) return false;
        try
        {
            if (_gpsSettings == null && (System.Diagnostics.Stopwatch.GetTimestamp() < _gpsRetryAt || !BuildGpsSettings(session))) return false;
            var wt = _cam.Entity.Data.GetWorldTransform();
            Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward;
            Vector2 size = ScreenSize, centre = size * 0.5f;
            bool front = Vector3D.Dot(world - wt.Position, fwd) > 1e-6;
            Vector2 s = _cam.WorldToScreenPoint(front ? world : wt.Position - (world - wt.Position));
            float mx = size.X * 0.06f, my = size.Y * 0.08f;
            bool edge = !front || s.X < mx || s.Y < my || s.X > size.X - mx || s.Y > size.Y - my;
            if (edge)
            {
                Vector2 dir = s - centre;
                if (!front) dir = -dir;
                if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
                // On an ellipse inside the screen, as the game clamps its own markers.
                float ax = centre.X - mx, ay = centre.Y - my;
                float k = 1f / (float)Math.Sqrt(dir.X * dir.X / (ax * ax) + dir.Y * dir.Y / (ay * ay));
                s = centre + dir * k;
            }
            // (one argument array, the constants boxed once: an array and ten boxes per marker per frame were most of the
            //  HUD's garbage, 2026-10-14)
            var args = _markerArgs ??= new object[10];
            if (_boxedCentre == null || _boxedCentreAt != centre) { _boxedCentre = centre; _boxedCentreAt = centre; }
            args[0] = _gpsSettings; args[1] = _batch; args[2] = _lodFull; args[3] = _boxedCentre; args[4] = _boxedCentre;
            args[5] = Box(ref _bMarkS, s); args[6] = BoxedFalse; args[7] = edge ? BoxedTrue : BoxedFalse; args[8] = marker; args[9] = Box(ref _bMarkDist, distance);
            _drawSingle.Invoke(null, args);
            args[8] = null;
            return true;
        }
        catch (Exception e)
        {
            // (latched for a few seconds: cleared settings were rebuilt and thrown again every frame, for every marker)
            GpsError = (e.InnerException ?? e).Message; _gpsSettings = null;
            _gpsRetryAt = System.Diagnostics.Stopwatch.GetTimestamp() + 5 * System.Diagnostics.Stopwatch.Frequency;
            return false;
        }
    }

    private static bool BuildGpsSettings(Keen.VRage.Core.Game.Systems.Session session)
    {
        var comp = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent>();
        object def = comp != null ? PlanetRenderBridge.GetMember(comp, "_definition") : null;
        object cam = comp != null ? PlanetRenderBridge.GetMember(comp, "_cameraComponent") : null;
        if (def == null) { GpsError = "no GPS definition"; return false; }
        cam ??= _cam;
        var helpers = comp.GetType().Assembly.GetType("Keen.Game2.Client.GameSystems.GPS.GPSMarkerHelpers");
        var st = helpers?.GetNestedType("MarkerDrawSettings");
        _drawSingle = helpers?.GetMethod("DrawSingleMarker");
        if (st == null || _drawSingle == null) { GpsError = "no GPSMarkerHelpers"; return false; }
        _lodFull = Enum.Parse(_drawSingle.GetParameters()[2].ParameterType, "Full");
        MethodInfo toScreen = null;
        foreach (var m in cam.GetType().GetMethods())
            if (m.Name == "ToScreenSpace" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float)) toScreen = m;
        float TS(float v) => toScreen != null ? Convert.ToSingle(toScreen.Invoke(cam, new object[] { v })) : v * 1080f;
        object box = Activator.CreateInstance(st);
        void Set(string field, object value)
        {
            var f = st.GetField(field);
            if (f == null || value == null) return;
            if (!f.FieldType.IsInstanceOfType(value))
            {
                MethodInfo conv = null;
                foreach (var t in new[] { f.FieldType, value.GetType() })
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                        if ((m.Name == "op_Implicit" || m.Name == "op_Explicit") && m.ReturnType == f.FieldType && m.GetParameters()[0].ParameterType.IsInstanceOfType(value)) conv = m;
                if (conv == null) return;
                value = conv.Invoke(null, new[] { value });
            }
            f.SetValue(box, value);
        }
        object D(string n) => PlanetRenderBridge.GetMember(def, n);
        Set("Font", D("Font"));
        Set("RegularFontSize", D("RegualarFontSize") ?? D("RegularFontSize"));
        Set("FocusFontSize", D("FocusFontSize"));
        Set("OutsideScreenFontSize", D("OutsideScreenFontSize"));
        Set("BunchIcon", D("BunchIcon"));
        Set("ArrowIcon", D("ArrowIcon"));
        Set("BunchColor", D("BunchColor"));
        Set("Margin", TS(0.005f));
        Set("FocusedBunchMaxMarkers", D("FocusedBunchMaxMarkers"));
        Set("ShortBunchMaxMarkers", D("ShortBunchMaxMarkers"));
        Set("NewLineMargin", TS(0.0005f));
        Set("IconHalfSize", TS(0.0175f));
        Set("MarkerBBInflateAmount", TS(0.02f));
        object tso = D("TextShadowOffset");
        Set("TextShadowOffset", TS(tso != null ? Convert.ToSingle(tso) : 0.001f));
        Set("MaxNameWidth", 7f);
        Set("Opacity", 1f);
        if (st.GetField("Font")?.GetValue(box) == null) { GpsError = "GPS font not set"; return false; }
        _gpsSettings = box;
        return true;
    }
}
