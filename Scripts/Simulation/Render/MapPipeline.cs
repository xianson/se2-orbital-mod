using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The colonization map's own rendering pipeline, driven from the mod (reflection: the render
/// types live in VRage.Render, which mod scripts cannot reference; this file and
/// PlanetRenderBridge are the only places that reflect):
///  - SectorModel: builds a runtime mesh the way MapSectorsMesherComponent does (one sub-part
///    and mesh section per sector, named after the sector, UV = distance to the edge for the
///    material's gradient) and swaps it into the game's sector renderer, which then draws it
///    with its own material, colonization colours and selection highlight.
///  - Ui: an immediate screen-space draw batch (the one the fast-travel lanes use): smooth
///    lines and the map's own font.
/// </summary>
public static class MapPipeline
{
    // ───────────────────────────── sector model ─────────────────────────────

    public sealed class Part
    {
        public string Name;
        public readonly List<Vector3> Outline = new List<Vector3>();   // map-local, Y = 0, counter-clockwise or not
        /// <summary>Explicit triangles instead of the outline fan (for non-convex shapes such as rings).</summary>
        public readonly List<Vector3> TriVerts = new List<Vector3>();
        public readonly List<Vector2> TriUvs = new List<Vector2>();
    }

    private static Type _tRmd, _tVs0, _tVs1, _tSub;
    private static object _originalModel;      // the game's own sector model (restored on close)
    private static bool _haveOriginal;
    private static object _ourModel;           // boxed RuntimeModel we own
    private static object _renderer;           // the sector renderer that state belongs to
    public static string LastError;

    private static bool ResolveMesh()
    {
        if (_tRmd != null) return true;
        var render = PlanetRenderBridge.RenderAssembly;
        if (render == null) return false;
        _tVs0 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatPositionUV0Packed");
        _tVs1 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatNormalTangentPacked");
        _tSub = render.GetType("Keen.VRage.Render.Data.IRuntimeMeshData+SubPart");
        var rmd = render.GetType("Keen.VRage.Render.Data.RuntimeMeshData`3");
        if (_tVs0 == null || _tVs1 == null || _tSub == null || rmd == null) { LastError = "mesh types not found"; return false; }
        _tRmd = rmd.MakeGenericType(_tVs0, _tVs1, typeof(Keen.VRage.Core.Render.Data.VertexFormatNull));
        return true;
    }

    private static object NewBuffer(Type elem)
    {
        var bt = typeof(Buffer<>).MakeGenericType(elem);
        return Activator.CreateInstance(bt, new object[] { Allocator.Heap, "OrbitalMapMesh" });
    }

    /// <summary>Build the model from the parts and put it into the game's sector renderer.</summary>
    public static bool ShowParts(object sectorsRenderer, List<Part> parts)
    {
        if (sectorsRenderer == null || parts.Count == 0 || !ResolveMesh()) return false;
        try
        {
            object material = PlanetRenderBridge.GetMember(sectorsRenderer, "BaseMaterial");
            if (material == null) { LastError = "no sector material"; return false; }

            object vs0 = NewBuffer(_tVs0), vs1 = NewBuffer(_tVs1), subs = NewBuffer(_tSub);
            var idx = new Buffer<int>(Allocator.Heap, "OrbitalMapMesh");
            var sections = new Buffer<Keen.VRage.Core.Model.Data.MeshData.Section>(Allocator.Heap, "OrbitalMapMesh");
            MethodInfo add0 = vs0.GetType().GetMethod("Add", new[] { _tVs0 });
            MethodInfo add1 = vs1.GetType().GetMethod("Add", new[] { _tVs1 });
            MethodInfo addS = subs.GetType().GetMethod("Add", new[] { _tSub });
            object normal = Activator.CreateInstance(_tVs1, new object[] { new Vector3(0, 1, 0), new Vector4(0, 0, 1, 0) });
            var bb = BoundingBox.CreateInvalid();
            int vcount = 0;

            foreach (var part in parts)
            {
                if (part.TriVerts.Count >= 3)
                {
                    int st = idx.Count;
                    for (int i = 0; i + 2 < part.TriVerts.Count; i += 3)
                    {
                        for (int q = 0; q < 3; q++)
                        {
                            add0.Invoke(vs0, new object[] { Activator.CreateInstance(_tVs0, new object[] { part.TriVerts[i + q], part.TriUvs[i + q] }) });
                            add1.Invoke(vs1, new[] { normal });
                            idx.Add(vcount++);
                            bb.Include(part.TriVerts[i + q]);
                        }
                    }
                    object sb = Activator.CreateInstance(_tSub);
                    _tSub.GetField("Name").SetValue(sb, Keen.VRage.Library.Utils.StringId.Get(part.Name));
                    _tSub.GetField("IndexStart").SetValue(sb, st);
                    _tSub.GetField("IndicesCount").SetValue(sb, idx.Count - st);
                    _tSub.GetField("Material").SetValue(sb, material);
                    addS.Invoke(subs, new[] { sb });
                    int si2 = (int)subs.GetType().GetProperty("Count").GetValue(subs) - 1;
                    sections.Add(new Keen.VRage.Core.Model.Data.MeshData.Section
                    {
                        Name = new Keen.VRage.Core.Model.MeshSectionId(Keen.VRage.Library.Utils.StringId.Get(part.Name)),
                        Parts = new[] { new Keen.VRage.Core.Model.Data.MeshData.Section.SectionPart { PartIndex = si2, IndicesOffset = 0, IndicesCount = idx.Count - st } },
                    });
                    continue;
                }
                int n = part.Outline.Count;
                if (n < 3) continue;
                // Fan from the centroid (as the game does), UV.x = distance from the centre to the edge.
                Vector3 c = Vector3.Zero;
                foreach (var p in part.Outline) c += p;
                c /= n;
                int start = idx.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector3 a = part.Outline[i], b = part.Outline[(i + 1) % n];
                    Vector3 edge = b - a;
                    Vector3 nrm = new Vector3(a.Z - b.Z, 0, b.X - a.X);
                    float len = nrm.Length();
                    float dist = len > 1e-9f ? Math.Abs(Vector3.Dot(nrm / len, c - a)) : 0f;
                    add0.Invoke(vs0, new object[] { Activator.CreateInstance(_tVs0, new object[] { c, new Vector2(dist, 1f) }) });
                    add0.Invoke(vs0, new object[] { Activator.CreateInstance(_tVs0, new object[] { a, new Vector2(0f, 0f) }) });
                    add0.Invoke(vs0, new object[] { Activator.CreateInstance(_tVs0, new object[] { b, new Vector2(0f, 0f) }) });
                    for (int k = 0; k < 3; k++) add1.Invoke(vs1, new[] { normal });
                    idx.Add(vcount); idx.Add(vcount + 1); idx.Add(vcount + 2);
                    vcount += 3;
                    bb.Include(c); bb.Include(a); bb.Include(b);
                }
                object sub = Activator.CreateInstance(_tSub);
                _tSub.GetField("Name").SetValue(sub, Keen.VRage.Library.Utils.StringId.Get(part.Name));
                _tSub.GetField("IndexStart").SetValue(sub, start);
                _tSub.GetField("IndicesCount").SetValue(sub, idx.Count - start);
                _tSub.GetField("Material").SetValue(sub, material);
                addS.Invoke(subs, new[] { sub });
                int subIndex = (int)subs.GetType().GetProperty("Count").GetValue(subs) - 1;
                sections.Add(new Keen.VRage.Core.Model.Data.MeshData.Section
                {
                    Name = new Keen.VRage.Core.Model.MeshSectionId(Keen.VRage.Library.Utils.StringId.Get(part.Name)),
                    Parts = new[] { new Keen.VRage.Core.Model.Data.MeshData.Section.SectionPart { PartIndex = subIndex, IndicesOffset = 0, IndicesCount = idx.Count - start } },
                });
            }
            if (idx.Count == 0) return false;

            object rmd = Activator.CreateInstance(_tRmd);
            SetField(rmd, "_vertexStream0", vs0);
            SetField(rmd, "_vertexStream1", vs1);
            SetField(rmd, "_vertexStream2", NewBuffer(typeof(Keen.VRage.Core.Render.Data.VertexFormatNull)));
            SetField(rmd, "_indices", idx);
            SetField(rmd, "_subParts", subs);
            SetField(rmd, "_meshSections", sections);
            SetField(rmd, "_bones", NewBuffer(typeof(Keen.VRage.Core.Model.ModelBone)));
            SetField(rmd, "_debugName", "OrbitalMapSectors");
            _tRmd.GetProperty("AABB").SetValue(rmd, bb);

            object contracts = PlanetRenderBridge.Contracts;
            MethodInfo create = null;
            foreach (var m in contracts.GetType().GetMethods())
                if (m.Name == "CreateRuntimeModel" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType.Name == "IRuntimeMeshData") create = m;
            if (create == null) { LastError = "CreateRuntimeModel not found"; return false; }
            object uiType = Enum.ToObject(create.GetParameters()[1].ParameterType, (int)Keen.VRage.Core.Render.RenderRuntimeDataType.UI3D);
            object model = create.Invoke(contracts, new[] { rmd, uiType, (object)true, (object)false });

            // Swap it into the game's renderer (keep the original to restore).
            FieldInfo fModel = FindField(sectorsRenderer.GetType(), "_proceduralSectorModel");
            if (fModel == null) { LastError = "renderer model field not found"; return false; }
            // State from another renderer (another session): forget it, dispose nothing of it.
            if (!ReferenceEquals(_renderer, sectorsRenderer)) { _haveOriginal = false; _ourModel = null; _originalModel = null; _renderer = sectorsRenderer; }
            object cur = fModel.GetValue(sectorsRenderer);
            if (!_haveOriginal) { _originalModel = cur; _haveOriginal = true; }
            else if (_ourModel != null && !ReferenceEquals(cur, _ourModel))
            {
                // The game rebuilt its model meanwhile (sectors changed) and disposed ours with it:
                // what it holds now is its own; ours is gone (disposing it again crashes the renderer).
                _originalModel = cur; _ourModel = null;
            }
            fModel.SetValue(sectorsRenderer, model);
            ApplyModel(sectorsRenderer);
            DisposeModel(_ourModel);   // the previous one of ours, still ours
            _ourModel = model;
            LastError = null;
            return true;
        }
        catch (Exception e) { LastError = (e.InnerException ?? e).Message; return false; }
    }


    /// <summary>Put the game's own sector model back.</summary>
    public static void Restore(object sectorsRenderer)
    {
        if (sectorsRenderer == null || !_haveOriginal) return;
        if (!ReferenceEquals(_renderer, sectorsRenderer)) { _haveOriginal = false; _ourModel = null; _originalModel = null; return; }
        try
        {
            var f = FindField(sectorsRenderer.GetType(), "_proceduralSectorModel");
            object cur = f?.GetValue(sectorsRenderer);
            if (f != null && _ourModel != null && ReferenceEquals(cur, _ourModel))
            {
                f.SetValue(sectorsRenderer, _originalModel);
                ApplyModel(sectorsRenderer);
                DisposeModel(_ourModel);
            }
            // else: the game replaced ours (and disposed it); leave its model in place.
            _ourModel = null; _originalModel = null;
            _haveOriginal = false;
        }
        catch (Exception e) { LastError = (e.InnerException ?? e).Message; }
    }

    private static void ApplyModel(object renderer)
    {
        object handle = PlanetRenderBridge.GetMember(renderer, "ModelHandle");
        object entity = PlanetRenderBridge.GetMember(renderer, "RenderModelEntity");
        entity?.GetType().GetMethod("UpdateModel")?.Invoke(entity, new[] { handle });
        // Highlights (hover, selection) were made on the previous geometry: remake them on this one.
        try
        {
            var fh = FindField(renderer.GetType(), "_highlights");
            if (fh?.GetValue(renderer) is System.Collections.IDictionary hl && entity != null)
            {
                object mes = PlanetRenderBridge.Contracts.GetType().GetMethod("GetMeshEffectSystem", Type.EmptyTypes)?.Invoke(PlanetRenderBridge.Contracts, null);
                var create = mes?.GetType().GetMethod("CreateHighlight");
                var keys = new List<object>();
                foreach (var k in hl.Keys) keys.Add(k);
                foreach (var k in keys)
                {
                    object old = hl[k];
                    old?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(old, null);
                    object props = k.GetType().GetField("Item1").GetValue(k);
                    object section = k.GetType().GetField("Item2").GetValue(k);   // MeshSectionId? (boxed or null)
                    object sectionId = section != null ? section.GetType().GetField("Id")?.GetValue(section) : null;
                    if (create != null) hl[k] = create.Invoke(mes, new[] { entity, props, sectionId });
                }
            }
        }
        catch (Exception e) { LastError = "highlight refresh: " + (e.InnerException ?? e).Message; }
        // The game recolours every section by name (colonization state).
        renderer.GetType().GetMethod("UpdateSectorParameters", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(renderer, null);
    }

    private static void DisposeModel(object model)
    {
        try { model?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(model, null); } catch { }
    }

    private static void SetField(object boxed, string name, object value) => FindField(boxed.GetType(), name).SetValue(boxed, value);

    private static FieldInfo FindField(Type t, string name)
    {
        for (; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return f;
        }
        return null;
    }

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
    public static float Hysteresis(string key) { float u = Math.Max(1f, ScreenSize.Y / 1080f); return ShownLately(key) >= 0 ? -3f * u : 3f * u; }

    /// <summary>No map label (or reserved area) placed this frame overlaps the box.</summary>
    public static bool Free(Vector2 min, Vector2 max)
    {
        var box = new BoundingBox2(min, max);
        foreach (var p in _placed) if (p.Intersects(box)) return false;
        return true;
    }
    private static MethodInfo _drawLine, _drawString;
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
            object ui = contracts?.GetType().GetMethod("GetUISystem", Type.EmptyTypes)?.Invoke(contracts, null);
            var mk = ui?.GetType().GetMethod("CreateImmediateMainViewBatch");
            if (mk == null || _cam == null) return false;
            _batch = mk.Invoke(ui, new object[] { 60, "OrbitalMap" });
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

    // ── bodies hide the lines behind them (an orbit line ran across the planet's globe) ──
    private struct Occluder { public Vector2 C; public float R; public double Depth, RWorld; }
    private static readonly List<Occluder> _occluders = new List<Occluder>();

    // Sector areas on screen (outline polygons): orbit lines are not drawn through them. Two lists swapped each frame;
    // the outlines' own lists are pooled (a map frame draws several areas: no per-frame arrays).
    private static List<List<Vector2>> _areas = new List<List<Vector2>>(), _areasPrev = new List<List<Vector2>>();
    private static readonly List<List<Vector2>> _areaPool = new List<List<Vector2>>();
    private static double _areasAt;

    /// <summary>A sector's area on screen (its outline): orbit lines do not cross it.</summary>
    public static void OccludeArea(IList<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return;
        List<Vector2> a;
        if (_areaPool.Count > 0) { a = _areaPool[_areaPool.Count - 1]; _areaPool.RemoveAt(_areaPool.Count - 1); a.Clear(); }
        else a = new List<Vector2>(poly.Count);
        for (int i = 0; i < poly.Count; i++) a.Add(poly[i]);
        _areas.Add(a);
    }


    /// <summary>A screen point inside a polygon (even-odd).</summary>
    public static bool InPolygon(List<Vector2> p, Vector2 s)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Y > s.Y) != (p[j].Y > s.Y) && s.X < (p[j].X - p[i].X) * (s.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
        return inside;
    }

    /// <summary>Inside a sector's area on screen (as drawn last frame; none once the map has not drawn for a moment).</summary>
    public static bool InSectorArea(Vector2 s)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_areasPrev.Count == 0 || now - _areasAt > 0.3) return false;
        foreach (var p in _areasPrev)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].Y > s.Y) != (p[j].Y > s.Y) && s.X < (p[j].X - p[i].X) * (s.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    /// <summary>
    /// A dotted path, the dashes measured along its whole length (the batch's own dashing restarts on
    /// every segment, and a small shape of short segments came out solid).
    /// </summary>
    public static void ScreenDotted(IList<Vector2> pts, bool closed, ColorSRGB color, float width, float dash, float gap)
    {
        if (pts == null || pts.Count < 2 || !(dash > 0) || !(gap > 0)) return;
        float period = dash + gap, phase = 0;
        int n = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
            float len = (b - a).Length();
            if (!(len > 0)) continue;
            Vector2 dir = (b - a) / len;
            float at = 0;
            while (at < len)
            {
                float inPeriod = phase % period;
                if (inPeriod < dash)
                {
                    float on = Math.Min(dash - inPeriod, len - at);
                    ScreenLine(a + dir * at, a + dir * (at + on), color, width);
                    at += on; phase += on;
                }
                else
                {
                    float off = Math.Min(period - inPeriod, len - at);
                    at += off; phase += off;
                }
            }
        }
    }

    /// <summary>A body's globe this frame (world centre and radius): lines behind it are not drawn.</summary>
    public static void Occlude(Vector3D centre, double radius)
    {
        if (_cam == null || !(radius > 0) || !Screen(centre, out var c)) return;
        var wt = _cam.Entity.Data.GetWorldTransform();
        Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward, right = (QuaternionD)wt.Orientation * Vector3D.Right;
        if (!Screen(centre + right * radius, out var e)) return;
        float r = (e - c).Length();
        if (r < 2f) return;
        _occluders.Add(new Occluder { C = c, R = r, Depth = Vector3D.Dot(centre - wt.Position, fwd), RWorld = radius });
    }

    /// <summary>Behind (or inside) a body's globe as seen from the camera.</summary>
    public static bool Occluded(Vector3D world)
    {
        if (_occluders.Count == 0 || _cam == null || !Screen(world, out var s)) return false;
        var wt = _cam.Entity.Data.GetWorldTransform();
        double depth = Vector3D.Dot(world - wt.Position, (QuaternionD)wt.Orientation * Vector3D.Forward);
        foreach (var o in _occluders)
            if ((s - o.C).LengthSquared() < o.R * o.R && depth > o.Depth - o.RWorld * 0.9) return true;
        return false;
    }

    /// <summary>A smooth screen-space line between two world points (px width).</summary>
    public static void Line(Vector3D a, Vector3D b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || Occluded((a + b) * 0.5) || !Screen(a, out var sa) || !Screen(b, out var sb) || !Clip(ref sa, ref sb)) return;
        AddPick(sa, sb);
        Seg(sa, sb, color, width);
    }

    /// <summary>Text in the map's own font at a world point, centred.</summary>
    /// <summary>All map text is drawn this much larger than the sizes the callers ask for (legibility).</summary>
    public static float TextScale = 1.3f;


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

    /// <summary>A world point on screen (false behind the camera).</summary>
    /// <summary>The map camera's orientation (a screen-aligned offset for sizes on screen).</summary>
    public static Vector3D? CameraPosition => _cam != null ? _cam.Entity.Data.GetWorldTransform().Position : (Vector3D?)null;
    public static Quaternion CameraOrientation => _cam != null ? _cam.Entity.Data.GetWorldTransform().Orientation : Quaternion.Identity;
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
        float u = Math.Max(1f, ScreenSize.Y / 1080f);
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
        _lineArgs[0] = a; _lineArgs[1] = b; _lineArgs[2] = color; _lineArgs[3] = width; _lineArgs[4] = _solid; _lineArgs[5] = 1f; _lineArgs[6] = false;
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
        _strArgs[0] = _font; _strArgs[1] = at; _strArgs[2] = color; _strArgs[3] = text; _strArgs[4] = scale;
        _strArgs[5] = _false; _strArgs[6] = null; _strArgs[7] = _zero;
        _drawString.Invoke(_batch, _strArgs);
    }
    static readonly object[] _lineArgs = new object[7];

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
            if (_drawPathM != null) _path = (PathFn)Delegate.CreateDelegate(typeof(PathFn), _batch, _drawPathM);
            if (_drawFillM != null)
            {
                Type g = _drawFillM.GetParameters()[2].ParameterType;
                var d = Delegate.CreateDelegate(typeof(FillFn<>).MakeGenericType(g), _batch, _drawFillM);
                _callFill ??= typeof(MapPipeline).GetMethod(nameof(CallFill), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(g);
                _fill = (Action<QuadraticBezier2[], int, ColorSRGB>)Delegate.CreateDelegate(typeof(Action<QuadraticBezier2[], int, ColorSRGB>), d, _callFill);
            }
            VectorStatus = $"path={_path != null} fill={_fill != null} image={_drawImageM != null}";
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
            _imgArgs[0] = h; _imgArgs[1] = box; _imgArgs[2] = color; _imgArgs[3] = _false; _imgArgs[4] = null; _imgArgs[5] = null;
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
        try { _imgArgs[0] = h; _imgArgs[1] = new BoundingBox2(min, max); _imgArgs[2] = color; _imgArgs[3] = _false; _imgArgs[4] = null; _imgArgs[5] = null; _drawImageM.Invoke(_batch, _imgArgs); return true; }
        catch { return false; }
    }

    /// <summary>An edge arrow pointing along a screen direction (16 steps).</summary>
    public static bool ScreenArrow(Vector2 c, Vector2 dir, float half, ColorSRGB color)
    {
        double th = Math.Atan2(-dir.Y, dir.X);
        int k = ((int)Math.Round(th / (Math.PI / 8)) % 16 + 16) % 16;
        return ScreenIcon("edge" + k, c, half, color);
    }

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

    /// <summary>
    /// A HUD marker for a frame-transferred GPS point: a ringed diamond, its name and distance; off
    /// screen (or behind the camera) it sits on the screen edge in its true direction.
    /// </summary>
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
            float u = Math.Max(1f, size.Y / 1080f);
            float r = 9f * u;
            Vector2 up = new Vector2(0, -r), rt = new Vector2(r, 0);
            var dia = new[] { s + up, s + rt, s - up, s - rt, s + up };
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
            _drawSingle.Invoke(null, new object[] { _gpsSettings, _batch, _lodFull, centre, centre, s, false, edge, marker, distance });
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
