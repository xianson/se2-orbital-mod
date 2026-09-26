using System.Reflection;
using Keen.VRage.Core;
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
            if (!_haveOriginal) { _originalModel = fModel.GetValue(sectorsRenderer); _haveOriginal = true; }
            fModel.SetValue(sectorsRenderer, model);
            ApplyModel(sectorsRenderer);
            DisposeModel(_ourModel);
            _ourModel = model;
            LastError = null;
            return true;
        }
        catch (Exception e) { LastError = (e.InnerException ?? e).Message; return false; }
    }

    /// <summary>Colour a section of our model through the renderer's own public SetSectorParameters.</summary>
    public static void ColourSection(object renderer, string section, ColorSRGB main, ColorSRGB gradient)
    {
        try
        {
            renderer?.GetType().GetMethod("SetSectorParameters")?.Invoke(renderer, new object[] { Keen.VRage.Library.Utils.StringId.Get(section), main, gradient });
        }
        catch (Exception e) { LastError = "colour: " + (e.InnerException ?? e).Message; }
    }

    /// <summary>Put the game's own sector model back.</summary>
    public static void Restore(object sectorsRenderer)
    {
        if (sectorsRenderer == null || !_haveOriginal) return;
        try
        {
            FindField(sectorsRenderer.GetType(), "_proceduralSectorModel")?.SetValue(sectorsRenderer, _originalModel);
            ApplyModel(sectorsRenderer);
            DisposeModel(_ourModel);
            _ourModel = null;
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
    private static MethodInfo _drawLine, _drawString;
    private static object _font;
    private static Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent _cam;

    public static bool UiBegin(Keen.VRage.Core.Game.Systems.Session session, object mapConfiguration)
    {
        _batch = null;
        _placed.Clear();
        _picks.Clear();
        try
        {
            _cam = SpecCam.CameraOf(session);
            object contracts = PlanetRenderBridge.Contracts;
            object ui = contracts?.GetType().GetMethod("GetUISystem", Type.EmptyTypes)?.Invoke(contracts, null);
            var mk = ui?.GetType().GetMethod("CreateImmediateMainViewBatch");
            if (mk == null || _cam == null) return false;
            _batch = mk.Invoke(ui, new object[] { 60, "OrbitalMap" });
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
        if (Vector3D.Dot(world - wt.Position, fwd) <= 1e-6) return false;
        s = _cam.WorldToScreenPoint(in world);
        return true;
    }

    /// <summary>A smooth screen-space line between two world points (px width).</summary>
    public static void Line(Vector3D a, Vector3D b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Screen(a, out var sa) || !Screen(b, out var sb)) return;
        AddPick(sa, sb);
        var ps = _drawLine.GetParameters();
        _drawLine.Invoke(_batch, new object[] { sa, sb, color, width, ps[4].DefaultValue, 1f, false });
    }

    /// <summary>Text in the map's own font at a world point, centred.</summary>
    public static void Text(Vector3D at, string text, ColorSRGB color, float scale)
    {
        if (_batch == null || _drawString == null || _font == null || !Screen(at, out var s)) return;
        try
        {
            Vector2 size = Vector2.Zero;
            foreach (var m in _font.GetType().GetMethods())
            {
                if (m.Name != "MeasureString" || m.ReturnType != typeof(Vector2)) continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                var args = new object[ps.Length];
                args[0] = text;
                for (int q = 1; q < ps.Length; q++) args[q] = ps[q].HasDefaultValue ? ps[q].DefaultValue : (ps[q].ParameterType.IsValueType ? Activator.CreateInstance(ps[q].ParameterType) : null);
                try { size = (Vector2)m.Invoke(_font, args) * scale; } catch { }
                if (size.X > 0) break;
            }
            if (size.X <= 0) size = new Vector2(text.Length * 12f * scale, 22f * scale);   // estimate
            // No overlapping labels (first placed wins): zoom in to reveal the rest.
            var box = new BoundingBox2(s - size * 0.5f - new Vector2(4, 2), s + size * 0.5f + new Vector2(4, 2));
            foreach (var placed in _placed) if (placed.Intersects(box)) return;
            _placed.Add(box);
            var shadow = new ColorSRGB(0f, 0f, 0f, 0.8f);
            _drawString.Invoke(_batch, new object[] { _font, s - size * 0.5f + new Vector2(1.5f, 1.5f), shadow, text, scale, false, null, 0f });
            _drawString.Invoke(_batch, new object[] { _font, s - size * 0.5f, color, text, scale, false, null, 0f });
        }
        catch { }
    }

    /// <summary>A fixed-size ring on screen around a world point (a marker for bodies too small to see at true size).</summary>
    public static void ScreenRing(Vector3D at, float radiusPx, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Screen(at, out var c)) return;
        AddPick(c, c);
        var ps = _drawLine.GetParameters();
        const int n = 24;
        Vector2 prev = c + new Vector2(radiusPx, 0);
        for (int i = 1; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector2 p = c + new Vector2((float)(Math.Cos(a) * radiusPx), (float)(Math.Sin(a) * radiusPx));
            _drawLine.Invoke(_batch, new object[] { prev, p, color, width, ps[4].DefaultValue, 1f, false });
            prev = p;
        }
    }

    /// <summary>A fixed-size diamond on screen around a world point (GPS markers on the map).</summary>
    public static void ScreenDiamond(Vector3D at, float r, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null || !Screen(at, out var c)) return;
        var ps = _drawLine.GetParameters();
        var d = new[] { c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), c + new Vector2(0, -r) };
        for (int i = 0; i < 4; i++) _drawLine.Invoke(_batch, new object[] { d[i], d[i + 1], color, width, ps[4].DefaultValue, 1f, false });
    }

    /// <summary>A world point on screen (false behind the camera).</summary>
    public static bool ToScreen(Vector3D world, out Vector2 s) { s = default; return _cam != null && Screen(world, out s); }

    /// <summary>A screen-space line (px).</summary>
    public static void ScreenLine(Vector2 a, Vector2 b, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null) return;
        var ps = _drawLine.GetParameters();
        _drawLine.Invoke(_batch, new object[] { a, b, color, width, ps[4].DefaultValue, 1f, false });
    }

    /// <summary>A screen-space circle (px).</summary>
    public static void ScreenCircle(Vector2 c, float r, ColorSRGB color, float width)
    {
        if (_batch == null || _drawLine == null) return;
        var ps = _drawLine.GetParameters();
        const int n = 20;
        Vector2 prev = c + new Vector2(r, 0);
        for (int i = 1; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector2 p = c + new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r));
            _drawLine.Invoke(_batch, new object[] { prev, p, color, width, ps[4].DefaultValue, 1f, false });
            prev = p;
        }
    }

    /// <summary>Screen resolution in pixels.</summary>
    public static Vector2 ScreenSize => _cam != null ? new Vector2(_cam.Resolution.X, _cam.Resolution.Y) : new Vector2(1920, 1080);

    /// <summary>Left-aligned text at a screen position (px), in the map's font; no collision test.</summary>
    public static void ScreenText(Vector2 at, string text, ColorSRGB color, float scale)
    {
        if (PickName != null && !string.IsNullOrEmpty(text)) { float h = 22f * scale; AddPick(at + new Vector2(0, h * 0.5f), at + new Vector2(text.Length * 15f * scale, h * 0.5f)); }
        if (_batch == null || _drawString == null || _font == null) return;
        try
        {
            var shadow = new ColorSRGB(0f, 0f, 0f, 0.8f);
            _drawString.Invoke(_batch, new object[] { _font, at + new Vector2(1.5f, 1.5f), shadow, text, scale, false, null, 0f });
            _drawString.Invoke(_batch, new object[] { _font, at, color, text, scale, false, null, 0f });
        }
        catch { }
    }

    /// <summary>A small filled dot on screen (concentric rings).</summary>
    public static void ScreenDot(Vector2 c, float r, ColorSRGB color)
    {
        if (_batch == null || _drawLine == null) return;
        var ps = _drawLine.GetParameters();
        for (float rr = 0.8f; rr <= r; rr += 1.2f)
        {
            const int n = 14;
            Vector2 prev = c + new Vector2(rr, 0);
            for (int i = 1; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                Vector2 p = c + new Vector2((float)(Math.Cos(a) * rr), (float)(Math.Sin(a) * rr));
                _drawLine.Invoke(_batch, new object[] { prev, p, color, 1.4f, ps[4].DefaultValue, 1f, false });
                prev = p;
            }
        }
    }

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
            var ps = _drawLine.GetParameters();
            float u = Math.Max(1f, size.Y / 1080f);
            float r = 9f * u;
            Vector2 up = new Vector2(0, -r), rt = new Vector2(r, 0);
            var dia = new[] { s + up, s + rt, s - up, s - rt, s + up };
            for (int i = 0; i < 4; i++) _drawLine.Invoke(_batch, new object[] { dia[i], dia[i + 1], color, 2f * u, ps[4].DefaultValue, 1f, false });
            ScreenDot(s, 2.5f * u, color);
            ScreenText(s + new Vector2(14, -14) * u, name ?? "", color, 0.62f * u);
            ScreenText(s + new Vector2(14, 4) * u, distance + (edge ? "  >" : ""), new ColorSRGB(0.85f, 0.9f, 1f, 0.9f), 0.55f * u);
        }
        catch { }
    }

    // ── the game's own GPS marker look (GPSMarkerHelpers.DrawSingleMarker) ──
    private static object _gpsSettings, _lodFull;
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
            if (_gpsSettings == null && !BuildGpsSettings(session)) return false;
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
        catch (Exception e) { GpsError = (e.InnerException ?? e).Message; _gpsSettings = null; return false; }
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
