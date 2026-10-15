using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The batch's vector drawing: smooth paths, fills, the game's icons.</summary>
public static partial class MapPipeline
{
    // ── the batch's own vector drawing: smooth paths, fills, and the game's icons ──
    // DrawPath / DrawFill take a ReadOnlySpan, which reflection cannot pass: they are bound as typed
    // delegates to this frame's batch (DrawFill through a generic helper, its gradient type is not
    // visible to mods).
    private delegate void PathFn(ReadOnlySpan<QuadraticBezier2> s, ColorSRGB c, float w, bool ignoreBounds);
    private delegate void FillFn<T>(ReadOnlySpan<QuadraticBezier2> s, ColorSRGB c, T gradient, bool ignoreBounds);
    private static MethodInfo _drawPathM, _drawFillM, _drawImageM, _callFill;
    private static PathFn _path;
    private static Action<QuadraticBezier2[], int, ColorSRGB> _fill;
    private static object _mapConfig;
    private static readonly Dictionary<string, object> _icons = new Dictionary<string, object>();
    public static string VectorStatus = "-";
    private static QuadraticBezier2[] _qb = new QuadraticBezier2[256];

    static void CallFill<T>(Delegate d, QuadraticBezier2[] a, int n, ColorSRGB c) => ((FillFn<T>)d)(new ReadOnlySpan<QuadraticBezier2>(a, 0, n), c, default(T), false);

    static double _iconRetryAt;
    sealed class VecBind { public MethodInfo PathM, FillM; public PathFn Path; public Action<QuadraticBezier2[], int, ColorSRGB> Fill; }
    static readonly Dictionary<object, VecBind> _vecBinds = new Dictionary<object, VecBind>();
    static int _vecKey = -1; static string _vecText;
    static void BindVector()
    {
        _path = null; _fill = null;
        try
        {
            double nowB = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            bool relook = _drawImageM == null && nowB >= _iconRetryAt;   // (icons back after a failure, not gone for good)
            if (_drawPathM == null || relook)
                foreach (var m in _batch.GetType().GetMethods())
                {
                    if (m.Name == "DrawPath" && m.GetParameters().Length == 4) _drawPathM = m;
                    if (m.Name == "DrawFill" && m.GetParameters().Length == 4) _drawFillM = m;
                    if (m.Name == "DrawImage" && m.GetParameters().Length == 6) _drawImageM = m;
                }
            // (the delegates are bound to the batch object, and the game's batches are pooled - the same few come back: bound
            //  once per batch object and kept. Rebuilt every UiBegin they were three delegates, a generic type lookup and a
            //  parameter array a frame for every HUD system, 2026-10-14)
            if (!_vecBinds.TryGetValue(_batch, out var vb) || !ReferenceEquals(vb.PathM, _drawPathM) || !ReferenceEquals(vb.FillM, _drawFillM))
            {
                vb = new VecBind { PathM = _drawPathM, FillM = _drawFillM };
                if (_drawPathM != null) vb.Path = (PathFn)Delegate.CreateDelegate(typeof(PathFn), _batch, _drawPathM);
                if (_drawFillM != null)
                {
                    Type g = _drawFillM.GetParameters()[2].ParameterType;
                    var d = Delegate.CreateDelegate(typeof(FillFn<>).MakeGenericType(g), _batch, _drawFillM);
                    _callFill ??= typeof(MapPipeline).GetMethod(nameof(CallFill), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(g);
                    vb.Fill = (Action<QuadraticBezier2[], int, ColorSRGB>)Delegate.CreateDelegate(typeof(Action<QuadraticBezier2[], int, ColorSRGB>), d, _callFill);
                }
                if (_vecBinds.Count >= 32) _vecBinds.Clear();   // (bounded: the pool hands out a few, never many)
                _vecBinds[_batch] = vb;
            }
            _path = vb.Path; _fill = vb.Fill;
            // (the same text while nothing changed - unless an icon's error was written over it meanwhile)
            int vk = (_path != null ? 1 : 0) | (_fill != null ? 2 : 0) | (_drawImageM != null ? 4 : 0);
            if (vk != _vecKey || !ReferenceEquals(VectorStatus, _vecText)) { _vecText = $"path={_path != null} fill={_fill != null} image={_drawImageM != null}"; _vecKey = vk; }
            VectorStatus = _vecText;
        }
        catch (Exception e) { VectorStatus = "bind failed: " + (e.InnerException ?? e).Message; _path = null; _fill = null; }
    }

    /// <summary>The drawn part of a polyline: its runs inside ClipRect.</summary>
    static List<List<Vector2>> Runs(IList<Vector2> pts, bool closed)
    {
        // (the lists are reused: ScreenPath draws each run before the next call - new ones every path were most
        //  of the map's garbage, ~20 MB/s with it open)
        foreach (var r in _runs) { r.Clear(); _runPool.Add(r); }
        _runs.Clear();
        List<Vector2> cur = null;
        int n = pts.Count, segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % n];
            if (!Clip(ref a, ref b)) { cur = null; continue; }
            if (cur == null || (cur[cur.Count - 1] - a).LengthSquared() > 0.01f)
            {
                if (_runPool.Count > 0) { cur = _runPool[_runPool.Count - 1]; _runPool.RemoveAt(_runPool.Count - 1); } else cur = new List<Vector2>();
                cur.Add(a); _runs.Add(cur);
            }
            cur.Add(b);
        }
        return _runs;
    }
    static readonly List<List<Vector2>> _runs = new List<List<Vector2>>(), _runPool = new List<List<Vector2>>();

    /// <summary>A smooth line through screen points (a curve through their midpoints), clipped.</summary>
    public static void ScreenPath(IList<Vector2> pts, bool closed, ColorSRGB color, float width)
    {
        if (_batch == null || pts == null || pts.Count < 2) return;
        foreach (var run in Runs(pts, closed))
        {
            if (PickName != null) for (int i = 0; i + 1 < run.Count; i++) AddPick(run[i], run[i + 1]);
            if (_path == null) { for (int i = 0; i + 1 < run.Count; i++) Seg(run[i], run[i + 1], color, width); continue; }
            bool loop = closed && run.Count == pts.Count + 1;
            int m = run.Count;
            if (_qb.Length < m + 2) _qb = new QuadraticBezier2[m * 2];
            int k = 0;
            if (m == 2) _qb[k++] = new QuadraticBezier2 { From = run[0], Control = (run[0] + run[1]) * 0.5f, To = run[1] };
            else
            {
                Vector2 Mid(int i) => (run[i] + run[i + 1]) * 0.5f;
                if (!loop) _qb[k++] = new QuadraticBezier2 { From = run[0], Control = (run[0] + Mid(0)) * 0.5f, To = Mid(0) };
                for (int i = 1; i < m - 1; i++) _qb[k++] = new QuadraticBezier2 { From = Mid(i - 1), Control = run[i], To = Mid(i) };
                if (loop) _qb[k++] = new QuadraticBezier2 { From = Mid(m - 2), Control = run[0], To = Mid(0) };
                else _qb[k++] = new QuadraticBezier2 { From = Mid(m - 2), Control = (Mid(m - 2) + run[m - 1]) * 0.5f, To = run[m - 1] };
            }
            try { _path(new ReadOnlySpan<QuadraticBezier2>(_qb, 0, k), color, width, false); }
            catch { _path = null; }
        }
    }

    /// <summary>A filled screen polygon (straight edges), clipped to ClipRect.</summary>
    public static void ScreenFill(IList<Vector2> poly, ColorSRGB color)
    {
        if (_batch == null || _fill == null || poly == null || poly.Count < 3) return;
        IList<Vector2> pts = poly;   // (copied only to be clipped)
        if (ClipRect.HasValue)
        {
            var r = ClipRect.Value;
            pts = ClipPoly(pts, v => v.X >= r.Min.X, (a, b) => a + (b - a) * ((r.Min.X - a.X) / (b.X - a.X)));
            pts = ClipPoly(pts, v => v.X <= r.Max.X, (a, b) => a + (b - a) * ((r.Max.X - a.X) / (b.X - a.X)));
            pts = ClipPoly(pts, v => v.Y >= r.Min.Y, (a, b) => a + (b - a) * ((r.Min.Y - a.Y) / (b.Y - a.Y)));
            pts = ClipPoly(pts, v => v.Y <= r.Max.Y, (a, b) => a + (b - a) * ((r.Max.Y - a.Y) / (b.Y - a.Y)));
        }
        int n = pts.Count;
        if (n < 3) return;
        if (_qb.Length < n) _qb = new QuadraticBezier2[n * 2];
        for (int i = 0; i < n; i++) { Vector2 a = pts[i], b = pts[(i + 1) % n]; _qb[i] = new QuadraticBezier2 { From = a, Control = (a + b) * 0.5f, To = b }; }
        try { _fill(_qb, n, color); } catch { _fill = null; }
    }

    static List<Vector2> ClipPoly(IList<Vector2> pts, Func<Vector2, bool> inside, Func<Vector2, Vector2, Vector2> cross)
    {
        var o = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
            bool ia = inside(a), ib = inside(b);
            if (ia) o.Add(a);
            if (ia != ib) o.Add(cross(a, b));
        }
        return o;
    }

    /// <summary>
    /// One of the colonization map's own icons (its configuration: "DefaultIcon", "LockedIcon",
    /// "PlayerIcon", "BunchIcon"), centred, tinted. False when it cannot be drawn (draw a fallback).
    /// </summary>
    public static bool ScreenIcon(string name, Vector2 c, float half, ColorSRGB color)
    {
        if (_batch == null || _drawImageM == null || (_mapConfig == null && !MapIcons.Has(name))) return false;
        if (ClipRect.HasValue && ClipRect.Value.Contains(c) != ContainmentType.Contains) return true;   // off the open area: nothing to draw
        if (!_icons.TryGetValue(name, out var h))
        {
            h = null;
            if (MapIcons.Has(name)) { h = MapIcons.Handle(name); _icons[name] = h; }
            else
            try
            {
                object raw = PlanetRenderBridge.GetMember(_mapConfig, name);
                Type want = _drawImageM.GetParameters()[0].ParameterType;
                h = raw;
                if (raw != null && raw.GetType() != want)
                {
                    h = null;
                    foreach (var m in raw.GetType().GetMethods(BindingFlags.Static | BindingFlags.Public))
                        if ((m.Name == "op_Implicit" || m.Name == "op_Explicit") && m.ReturnType == want) { h = m.Invoke(null, new[] { raw }); break; }
                    if (h == null)
                        foreach (var m in want.GetMethods(BindingFlags.Static | BindingFlags.Public))
                            if ((m.Name == "op_Implicit" || m.Name == "op_Explicit") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == raw.GetType()) { h = m.Invoke(null, new[] { raw }); break; }
                }
            }
            catch (Exception e) { VectorStatus = "icon " + name + ": " + (e.InnerException ?? e).Message; h = null; }
            _icons[name] = h;
        }
        if (h == null) return false;
        try
        {
            var box = new BoundingBox2(c - new Vector2(half, half), c + new Vector2(half, half));
            if (PickName != null) AddPick(c - new Vector2(half, 0), c + new Vector2(half, 0));
            _imgArgs[0] = h; _imgArgs[1] = Box(ref _bImgBox, box); _imgArgs[2] = Box(ref _bImgCol, color); _imgArgs[3] = _false; _imgArgs[4] = null; _imgArgs[5] = null;
            _drawImageM.Invoke(_batch, _imgArgs);
            return true;
        }
        catch (Exception e)
        {
            VectorStatus = "icon draw: " + (e.InnerException ?? e).Message;
            _drawImageM = null; _iconRetryAt = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency + 5;
            return false;
        }
    }

    public static bool CanFill => _fill != null;

    /// <summary>A filled disc on screen (px); rings when fills are not available.</summary>
    public static void ScreenDisc(Vector2 c, float r, ColorSRGB color)
    {
        if (!CanFill) { ScreenDot(c, r, color); return; }
        int n = Math.Clamp((int)(r * 0.8f), 12, 48);
        var poly = _discPoly; poly.Clear();   // (one list: ScreenFill draws it at once)
        for (int i = 0; i < n; i++) { double a = 2 * Math.PI * i / n; poly.Add(c + new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r))); }
        ScreenFill(poly, color);
    }

    /// <summary>One of our icons stretched over a screen box (the warp bar's triangles).</summary>
    public static bool ScreenIconBox(string name, Vector2 min, Vector2 max, ColorSRGB color)
    {
        if (_batch == null || _drawImageM == null || !MapIcons.Has(name)) return false;
        object h = MapIcons.Handle(name);
        if (h == null) return false;
        try { _imgArgs[0] = h; _imgArgs[1] = Box(ref _bImgBox, new BoundingBox2(min, max)); _imgArgs[2] = Box(ref _bImgCol, color); _imgArgs[3] = _false; _imgArgs[4] = null; _imgArgs[5] = null; _drawImageM.Invoke(_batch, _imgArgs); return true; }
        catch { return false; }
    }

    /// <summary>An edge arrow pointing along a screen direction (16 steps).</summary>
    public static bool ScreenArrow(Vector2 c, Vector2 dir, float half, ColorSRGB color)
    {
        double th = Math.Atan2(-dir.Y, dir.X);
        int k = ((int)Math.Round(th / (Math.PI / 8)) % 16 + 16) % 16;
        return ScreenIcon("edge" + k, c, half, color);
    }
}
