using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The sector model: the map's sector mesh built and swapped into the game's sector renderer (restored on close).</summary>
public static partial class MapPipeline
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
}
