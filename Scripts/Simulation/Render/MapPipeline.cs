using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The colonization map's own rendering pipeline, driven from the mod (reflection: the render
/// types live in VRage.Render, which mod scripts cannot reference):
///  - SectorModel: builds a runtime mesh the way MapSectorsMesherComponent does (one sub-part
///    and mesh section per sector, named after the sector, UV = distance to the edge for the
///    material's gradient) and swaps it into the game's sector renderer, which then draws it
///    with its own material, colonization colours and selection highlight.
///  - Ui: an immediate screen-space draw batch (the one the fast-travel lanes use): smooth
///    lines and the map's own font.
/// </summary>
public static partial class MapPipeline
{
    // ───────────────────────────── UI layer ─────────────────────────────

    private static object _batch;
    private static readonly List<BoundingBox2> _placed = new List<BoundingBox2>();

    /// <summary>Keep map labels out of a screen area (the sector list, the title) for this frame.</summary>
    public static void Reserve(Vector2 min, Vector2 max) => _placed.Add(new BoundingBox2(min, max));

    // ---- label hysteresis: a label shown lately keeps its place unless clearly overlapped; a new
    // one needs a clearly free place (labels at a collision edge flickered as orbits moved a pixel).
    private static readonly Dictionary<string, (double at, int cand)> _shown = new Dictionary<string, (double, int)>();
    static double NowS() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public static string LabelKey(string text)
    {
        if (text == null) return "";
        int i = 0; while (i < text.Length && !char.IsDigit(text[i])) i++;
        return text.Substring(0, i);
    }
    /// <summary>Shown within the last 0.3 s: its last candidate (0.. ), else -1.</summary>
    public static int ShownLately(string key) => _shown.TryGetValue(key, out var v) && NowS() - v.at < 0.3 ? v.cand : -1;
    public static void MarkShown(string key, int cand) => _shown[key] = (NowS(), cand);
    /// <summary>Margin (px) for a collision test: generous for a new label, lenient for one already shown.</summary>
    public static float Hysteresis(string key) { float u = UiScale(ScreenSize.Y); return ShownLately(key) >= 0 ? -3f * u : 3f * u; }

    /// <summary>No map label (or reserved area) placed this frame overlaps the box.</summary>
    public static bool Free(Vector2 min, Vector2 max)
    {
        var box = new BoundingBox2(min, max);
        foreach (var p in _placed) if (p.Intersects(box)) return false;
        return true;
    }
    private static MethodInfo _drawLine, _drawString;
    private static MethodInfo _getUiSystem, _mkBatch; private static Type _uiSysFor, _mkBatchFor;
    private static readonly object[] _mkBatchArgs = { 60, "OrbitalMap" };   // (only read by Invoke: one array for every call)
    private static object _font;
    private static Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent _cam;

    public static bool UiBegin(Keen.VRage.Core.Game.Systems.Session session, object mapConfiguration)
    {
        _batch = null;
        _placed.Clear();
        _picks.Clear();
        _occluders.Clear();
        // Last frame's sector areas answer this frame (orbit lines are drawn before the sectors).
        _areaPool.AddRange(_areasPrev); _areasPrev.Clear();
        var swap = _areasPrev; _areasPrev = _areas; _areas = swap;
        _areasAt = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        try
        {
            _cam = SpecCam.CameraOf(session);
            object contracts = PlanetRenderBridge.Contracts;
            // (the two methods found once per type, their arguments boxed once: every HUD system opens a batch every frame -
            //  two method lookups, an array and a box each time, 2026-10-14)
            if (contracts != null && !ReferenceEquals(contracts.GetType(), _uiSysFor)) { var ct = contracts.GetType(); _getUiSystem = ct.GetMethod("GetUISystem", Type.EmptyTypes); _uiSysFor = ct; }
            object ui = contracts != null ? _getUiSystem?.Invoke(contracts, null) : null;
            if (ui != null && !ReferenceEquals(ui.GetType(), _mkBatchFor)) { var ut = ui.GetType(); _mkBatch = ut.GetMethod("CreateImmediateMainViewBatch"); _mkBatchFor = ut; }
            var mk = ui != null ? _mkBatch : null;
            if (mk == null || _cam == null) return false;
            _batch = mk.Invoke(ui, _mkBatchArgs);
            _mapConfig = mapConfiguration;
            BindVector();
            if (_drawLine == null)
                foreach (var m in _batch.GetType().GetMethods())
                {
                    if (m.Name == "DrawLine" && m.GetParameters().Length == 7) _drawLine = m;
                    if (m.Name == "DrawString" && m.GetParameters().Length == 8) _drawString = m;
                }
            if (_font == null && mapConfiguration != null)
            {
                object def = PlanetRenderBridge.GetMember(mapConfiguration, "Font");
                var conv = def?.GetType().GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public);
                _font = conv?.Invoke(null, new[] { def });
            }
            return true;
        }
        catch (Exception e) { LastError = (e.InnerException ?? e).Message; _batch = null; return false; }
    }

    private static bool Screen(Vector3D world, out Vector2 s)
    {
        s = default;
        var wt = _cam.Entity.Data.GetWorldTransform();
        Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward;
        if (Vector3D.Dot(world - wt.Position, fwd) <= 1e-4) return false;
        s = _cam.WorldToScreenPoint(in world);
        // A point just in front of the camera projects far off screen: a line to it became a huge
        // band across the view (seen at close zoom), and such lines broke the UI batch (the game's
        // panels went blank). Beyond a generous guard band, the point is not drawn.
        var sz = ScreenSize;
        if (!(Math.Abs(s.X - sz.X * 0.5f) < sz.X * 3f) || !(Math.Abs(s.Y - sz.Y * 0.5f) < sz.Y * 3f)) return false;
        return true;
    }

    /// <summary>A smooth screen-space line between two world points (px width).</summary>
    public static void Line(Vector3D a, Vector3D b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || Occluded((a + b) * 0.5) || !Screen(a, out var sa) || !Screen(b, out var sb) || !Clip(ref sa, ref sb)) return;
        AddPick(sa, sb);
        Seg(sa, sb, color, width);
    }

    /// <summary>All map text is drawn this much larger than the sizes the callers ask for (legibility).</summary>
    public static float TextScale = 1.3f;
    /// <summary>The HUD's scale for a screen this tall: its sizes are for 1080p (2026-10-12, the user: "not sure our ui scales
    /// well with resolution" - it was max(1, h / 1080) at 46 places: never under 1, so at 480p everything kept its 1080p
    /// size, twice too big; floored at MinUiScale only for a tiny window).</summary>
    public static float UiScale(float screenHeight) => Math.Max(MinUiScale, screenHeight / 1080f);
    public static float MinUiScale = 0.4f;
    /// <summary>The map's text sizes (fixed scales, laid out for 4K): x screen height / 2160 - the 4K look kept, smaller
    /// screens the same proportions (at 640x480 the map's titles, labels and lists ran over each other, 2026-10-12) -
    /// floored at MinMapText so text stays readable in a small window.</summary>
    public static float MapText => Math.Max(MinMapText, ScreenSize.Y / 2160f);
    public static float MinMapText = 0.45f;

    /// <summary>The font's own measure of a string at scale 1 (the method is found once).</summary>
    private static Vector2 Measure(string text)
    {
        // The font's MeasureString takes a ReadOnlySpan<char>, which reflection cannot pass: bound once as a typed
        // delegate (to this font). It was looked for as a string overload that does not exist: every call searched
        // all the font's methods again (~1.5 MB/s of garbage with the map open) and measured nothing (zero).
        if (!ReferenceEquals(_measureFor, _font))
        {
            _measureFor = _font; _measureFn = null;
            try
            {
                foreach (var m in _font.GetType().GetMethods())
                {
                    if (m.Name != "MeasureString" || m.ReturnType != typeof(Vector2)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(ReadOnlySpan<char>)) continue;
                    _measureFn = (MeasureFn)Delegate.CreateDelegate(typeof(MeasureFn), _font, m);
                    break;
                }
            }
            catch { _measureFn = null; }
        }
        if (_measureFn == null) return Vector2.Zero;
        try { return _measureFn(text.AsSpan()); } catch { _measureFn = null; return Vector2.Zero; }
    }
    private delegate Vector2 MeasureFn(ReadOnlySpan<char> text);
    /// <summary>The font's own measure is bound (else MeasureText estimates).</summary>
    public static bool CanMeasure => _measureFn != null;
    private static MeasureFn _measureFn; private static object _measureFor;

    /// <summary>Text centred on a screen point, clear of labels placed before it (else left out).</summary>
    public static bool TextScreen(Vector2 s, string text, ColorSRGB color, float scale, bool dryRun = false)
    {
        if (_batch == null || _drawString == null || _font == null) return false;
        if (ClipRect.HasValue && ClipRect.Value.Contains(s) != ContainmentType.Contains) return false;
        var size = MeasureText(text, scale);
        string key = LabelKey(text); float h = Hysteresis(key);
        var test = new BoundingBox2(s - size * 0.5f - new Vector2(4 + h, 2 + h), s + size * 0.5f + new Vector2(4 + h, 2 + h));
        foreach (var placed in _placed) if (placed.Intersects(test)) return false;
        if (dryRun) return true;
        var box = new BoundingBox2(s - size * 0.5f - new Vector2(4, 2), s + size * 0.5f + new Vector2(4, 2));
        _placed.Add(box); MarkShown(key, 0);
        if (PickName != null) AddPick(box.Min, box.Max);
        ScreenText(s - size * 0.5f, text, color, scale);
        return true;
    }

    /// <summary>Text drawing off until this wall time after a failure (an exception costs ~200 ms in SE2: one per label per
    /// frame, swallowed, was a stall that never stopped).</summary>
    static double _textOffUntil;
    static void TextFailed(Exception e)
    {
        _textOffUntil = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency + 2;
        VectorStatus = "text draw failed (off 2 s): " + (e.InnerException ?? e).Message;
    }
    static bool TextOff => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency < _textOffUntil;

    /// <summary>Text in the map's own font at a world point, centred.</summary>
    public static void Text(Vector3D at, string text, ColorSRGB color, float scale)
    {
        if (TextOff || _batch == null || _drawString == null || _font == null || !Screen(at, out var s)) return;
        if (ClipRect.HasValue && ClipRect.Value.Contains(s) != ContainmentType.Contains) return;   // not outside the map's area
        scale *= TextScale;
        try
        {
            Vector2 size = Measure(text) * scale;
            if (size.X <= 0) size = new Vector2(text.Length * 12f * scale, 22f * scale);   // estimate
            // No overlapping labels (first placed wins): zoom in to reveal the rest.
            string key = LabelKey(text); float h = Hysteresis(key);
            var test = new BoundingBox2(s - size * 0.5f - new Vector2(4 + h, 2 + h), s + size * 0.5f + new Vector2(4 + h, 2 + h));
            foreach (var placed in _placed) if (placed.Intersects(test)) return;
            var box = new BoundingBox2(s - size * 0.5f - new Vector2(4, 2), s + size * 0.5f + new Vector2(4, 2));
            _placed.Add(box); MarkShown(key, 0);
            var shadow = new ColorSRGB(0f, 0f, 0f, 0.8f);
            DrawStringArgs(s - size * 0.5f + new Vector2(1.5f, 1.5f), shadow, text, scale);
            DrawStringArgs(s - size * 0.5f, color, text, scale);
        }
        catch (Exception e) { TextFailed(e); }
    }

    /// <summary>A fixed-size ring on screen around a world point (a marker for bodies too small to see at true size).</summary>
    public static void ScreenRing(Vector3D at, float radiusPx, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Screen(at, out var c)) return;
        AddPick(c, c);
        ScreenCircle(c, radiusPx, color, width);
    }

    /// <summary>A fixed-size diamond on screen around a world point (GPS markers on the map).</summary>
    public static void ScreenDiamond(Vector3D at, float r, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Screen(at, out var c)) return;
        if (ScreenIcon("diamond", c, r + width * 0.5f, color)) return;
        var d = new[] { c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), c + new Vector2(0, -r) };
        for (int i = 0; i < 4; i++) Seg(d[i], d[i + 1], color, width);
    }

    public static Vector3D? CameraPosition => _cam != null ? _cam.Entity.Data.GetWorldTransform().Position : (Vector3D?)null;
    /// <summary>The map camera's orientation (a screen-aligned offset for sizes on screen).</summary>
    public static Quaternion CameraOrientation => _cam != null ? _cam.Entity.Data.GetWorldTransform().Orientation : Quaternion.Identity;
    /// <summary>A world point on screen (false behind the camera).</summary>
    public static bool ToScreen(Vector3D world, out Vector2 s) { s = default; return _cam != null && Screen(world, out s); }

    /// <summary>A filled screen rectangle (a line as thick as the box is tall).</summary>
    public static void ScreenRect(Vector2 min, Vector2 max, ColorSRGB fill)
    {
        if (_batch == null || _drawLine == null) return;
        if (!ReferenceEquals(_solidFor, _drawLine)) { _solid = _drawLine.GetParameters()[4].DefaultValue; _solidFor = _drawLine; }
        float h = max.Y - min.Y, y = (min.Y + max.Y) * 0.5f;
        _lineArgs[0] = new Vector2(min.X, y); _lineArgs[1] = new Vector2(max.X, y); _lineArgs[2] = fill; _lineArgs[3] = h; _lineArgs[4] = _solid; _lineArgs[5] = _one; _lineArgs[6] = _false;
        _drawLine.Invoke(_batch, _lineArgs);
    }

    /// <summary>A dashed screen-space line (the batch's own dashing).</summary>
    public static void ScreenDashed(Vector2 a, Vector2 b, ColorSRGB color, float width, float dashScale = 1f)
    {
        if (_batch == null || _drawLine == null || !Clip(ref a, ref b)) return;
        if (!ReferenceEquals(_dashFor, _drawLine)) { _dash = Enum.ToObject(_drawLine.GetParameters()[4].ParameterType, 1); _dashFor = _drawLine; }
        _lineArgs[0] = a; _lineArgs[1] = b; _lineArgs[2] = color; _lineArgs[3] = width; _lineArgs[4] = _dash; _lineArgs[5] = dashScale; _lineArgs[6] = _false;
        _drawLine.Invoke(_batch, _lineArgs);
    }

    /// <summary>Size of a text in the UI font at a scale (px).</summary>
    public static Vector2 MeasureText(string text, float scale)
    {
        scale *= TextScale;   // as ScreenText draws it
        if (_font == null || string.IsNullOrEmpty(text)) return new Vector2((text?.Length ?? 0) * 12f * scale, 22f * scale);
        // Never under ~8 px a character and 20 px a line per unit scale at 1080p: the font's own
        // measure comes out short at high resolutions (4K: labels and bars ran together).
        float u = UiScale(ScreenSize.Y);
        var est = new Vector2(text.Length * 8.2f * scale * u, 20f * scale * u);
        var v = Measure(text) * scale;
        return new Vector2(Math.Max(v.X, est.X), Math.Max(v.Y, est.Y));
    }

    /// <summary>While set, lines are clipped to this screen rectangle (the map's open area: orbit and
    /// sector lines ran across the game's panels and tab bar).</summary>
    public static BoundingBox2? ClipRect;

    /// <summary>Clip a segment to ClipRect (Liang-Barsky); false when nothing is left.</summary>
    static bool Clip(ref Vector2 a, ref Vector2 b)
    {
        if (!ClipRect.HasValue) return true;
        var r = ClipRect.Value;
        float t0 = 0, t1 = 1; Vector2 d = b - a;
        bool Edge(float p, float q)
        {
            if (Math.Abs(p) < 1e-9f) return q >= 0;
            float t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (!Edge(-d.X, a.X - r.Min.X) || !Edge(d.X, r.Max.X - a.X) || !Edge(-d.Y, a.Y - r.Min.Y) || !Edge(d.Y, r.Max.Y - a.Y)) return false;
        Vector2 a0 = a;
        a = a0 + d * t0; b = a0 + d * t1;
        return true;
    }

    private static object _solid; private static MethodInfo _solidFor;

    /// <summary>One solid screen segment, clipped (the one path every shape takes).</summary>
    static void Seg(Vector2 a, Vector2 b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Clip(ref a, ref b)) return;
        // (a straight piece of path through the batch's typed DrawPath: the reflected DrawLine boxed its seven
        //  arguments into a new array per segment - ~9 MB/s of the map's garbage)
        if (_path != null)
        {
            _seg1[0] = new QuadraticBezier2 { From = a, Control = (a + b) * 0.5f, To = b };
            try { _path(new ReadOnlySpan<QuadraticBezier2>(_seg1, 0, 1), color, width, false); return; }
            catch { _path = null; }
        }
        if (!ReferenceEquals(_solidFor, _drawLine)) { _solid = _drawLine.GetParameters()[4].DefaultValue; _solidFor = _drawLine; }
        _lineArgs[0] = Box(ref _bLineA, a); _lineArgs[1] = Box(ref _bLineB, b); _lineArgs[2] = Box(ref _bLineCol, color); _lineArgs[3] = Box(ref _bLineW, width);
        _lineArgs[4] = _solid; _lineArgs[5] = _one; _lineArgs[6] = _false;
        _drawLine.Invoke(_batch, _lineArgs);
    }
    static readonly QuadraticBezier2[] _seg1 = new QuadraticBezier2[1];
    // DrawString's font parameter is a render type mods cannot name (no typed delegate): one argument array,
    // reused, with its constants boxed once (a new array per string was a good part of the HUD's garbage)
    static readonly object[] _strArgs = new object[8];
    static readonly object _false = false, _zero = 0f, _one = 1f;
    static readonly object[] _imgArgs = new object[6];
    static object _dash, _dashFor;
    static readonly List<Vector2> _discPoly = new List<Vector2>(48);
    static void DrawStringArgs(Vector2 at, ColorSRGB color, string text, float scale)
    {
        _strArgs[0] = _font; _strArgs[1] = Box(ref _bStrAt, at); _strArgs[2] = Box(ref _bStrCol, color); _strArgs[3] = text; _strArgs[4] = Box(ref _bStrScale, scale);
        _strArgs[5] = _false; _strArgs[6] = null; _strArgs[7] = _zero;
        _drawString.Invoke(_batch, _strArgs);
    }
    static readonly object[] _lineArgs = new object[7];

    // The varying arguments of the reflected draws (a point, a colour, a size, a box) each in ONE box, reused: its value
    // written in place (Unsafe.Unbox). Invoke copies an argument out of its box for the call and keeps nothing, so one box
    // serves every call. Boxed anew they were three boxes a string, two an icon, two a GPS marker - with the batch's
    // lookups, most of each HUD system's garbage (2026-10-14). Client thread only, as the argument arrays they go in.
    static object Box<T>(ref object box, T value) where T : struct
    {
        if (box == null) box = value;
        else System.Runtime.CompilerServices.Unsafe.Unbox<T>(box) = value;
        return box;
    }
    static object _bStrAt, _bStrCol, _bStrScale, _bImgBox, _bImgCol, _bLineA, _bLineB, _bLineCol, _bLineW, _bMarkS, _bMarkDist;

    /// <summary>A pick target for the current PickName (a segment on screen).</summary>
    public static void Pick(Vector2 a, Vector2 b) => AddPick(a, b);

    /// <summary>A screen-space line (px).</summary>
    public static void ScreenLine(Vector2 a, Vector2 b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Clip(ref a, ref b)) return;
        Seg(a, b, color, width);
    }

    /// <summary>A screen-space circle (px).</summary>
    public static void ScreenCircle(Vector2 c, float r, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null) return;
        // Small: a texture (a ring, or a dot when tiny); larger: a smooth closed path.
        if (r <= 18f && ScreenIcon(r < 2.2f ? "dot" : "ring", c, r + width * 0.5f, color)) return;
        if (r > 18f && _path != null)
        {
            int m = Math.Clamp((int)(r * 0.6f), 24, 96);
            var pts = new List<Vector2>(m);
            for (int i = 0; i < m; i++) { double a = 2 * Math.PI * i / m; pts.Add(c + new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r))); }
            ScreenPath(pts, true, color, width);
            return;
        }
        const int n = 20;
        Vector2 prev = c + new Vector2(r, 0);
        for (int i = 1; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector2 p = c + new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r));
            Seg(prev, p, color, width);
            prev = p;
        }
    }

    /// <summary>Screen resolution in pixels.</summary>
    public static Vector2 ScreenSize => _cam != null ? new Vector2(_cam.Resolution.X, _cam.Resolution.Y) : new Vector2(1920, 1080);

    /// <summary>Left-aligned text at a screen position (px), in the map's font; no collision test.</summary>
    public static void ScreenText(Vector2 at, string text, ColorSRGB color, float scale)
    {
        scale *= TextScale;
        if (PickName != null && !string.IsNullOrEmpty(text)) { float h = 22f * scale; AddPick(at + new Vector2(0, h * 0.5f), at + new Vector2(text.Length * 15f * scale, h * 0.5f)); }
        if (TextOff || _batch == null || _drawString == null || _font == null) return;
        try
        {
            var shadow = new ColorSRGB(0f, 0f, 0f, 0.8f);
            DrawStringArgs(at + new Vector2(1.5f, 1.5f), shadow, text, scale);
            DrawStringArgs(at, color, text, scale);
        }
        catch (Exception e) { TextFailed(e); }
    }

    /// <summary>A small filled dot on screen (concentric rings).</summary>
    public static void ScreenDot(Vector2 c, float r, ColorSRGB color)
    {
        if (_batch == null || _drawLine == null) return;
        if (ScreenIcon("dot", c, r, color)) return;
        for (float rr = 0.8f; rr <= r; rr += 1.2f)
        {
            const int n = 14;
            Vector2 prev = c + new Vector2(rr, 0);
            for (int i = 1; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                Vector2 p = c + new Vector2((float)(Math.Cos(a) * rr), (float)(Math.Sin(a) * rr));
                Seg(prev, p, color, 1.4f);
                prev = p;
            }
        }
    }

    // ── picking: what is drawn under a sector's name can be pointed at ──
    /// <summary>While set, every line, ring and screen text drawn is a pick target for this sector.</summary>
    public static string PickName;
    private struct PickSeg { public string Name; public Vector2 A, B; }
    private static readonly List<PickSeg> _picks = new List<PickSeg>();

    private static void AddPick(Vector2 a, Vector2 b)
    {
        if (PickName != null) _picks.Add(new PickSeg { Name = PickName, A = a, B = b });
    }

    /// <summary>The sector whose drawing is nearest the screen point, within radius (px), or null.</summary>
    public static string ResolvePick(Vector2 m, float radius)
    {
        string best = null; float bd = radius * radius;
        foreach (var p in _picks)
        {
            Vector2 ab = p.B - p.A;
            float L = ab.LengthSquared();
            float k = L > 1e-6f ? Math.Clamp(Vector2.Dot(m - p.A, ab) / L, 0f, 1f) : 0f;
            float d = (p.A + ab * k - m).LengthSquared();
            if (d < bd) { bd = d; best = p.Name; }
        }
        return best;
    }

    public static void UiEnd()
    {
        try { (_batch as IDisposable)?.Dispose(); } catch { }
        _batch = null;
    }
}
