using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Materials;
using Keen.VRage.Core.Render.Materials.Templates;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// A planet's ring on the map, from the game's own ring texture. The game's ring is a volume, and the
/// renderer skips every volume pass in map mode (SceneDrawSystem.ExecuteVolumetricPasses returns when
/// Is3DMapEnabled), so on the map it can never draw. The same texture (ColorOpacity: colour in RGB,
/// opacity in A, across the ring from inner to outer edge) is laid here on a flat annulus with the
/// engine's transparent material (PBRTransparent, two-sided): colour from its RGB, opacity from its A.
/// A Map entity, so it draws only with the map. Built at the map's scale (a model entity has no scale),
/// rebuilt when the zoom moves that scale by more than <see cref="Rebuild"/>.
/// </summary>
public static class MapRingMesh
{
    public static bool Enabled = true;
    /// <summary>Rebuild the mesh when the scale moves by more than this fraction.</summary>
    public const double Rebuild = 0.02;
    /// <summary>Across the ring (the texture's bands) and round it.</summary>
    public static int Radial = 24, Around = 160;
    /// <summary>DEV: the material's specular colour alpha (the pass's own opacity term) and fresnel.</summary>
    public static float SpecA = 0f, Fresnel = 1f;
    /// <summary>
    /// The ring's light on the map (the pass's colouring: hue and saturation replaced, value shifted): with no
    /// opacity term (SpecA 0) the ring adds its light over what is behind it, as the volume's scattering does,
    /// so a dimmer value is a thinner-looking ring.
    /// </summary>
    public static float Dim = -0.45f, Hue = 0.1f, Sat = 0.12f, Tint = 1f;
    /// <summary>Where through the ring's height its texture is read (0.5: the mid-plane, densest; toward 0 or 1: thinner): its opacity on the map.</summary>
    public static float Depth = 0.5f;
    public static string Status = "-";

    // PBRTransparent, two-sided (Content/Materials/States/PBRTransparentTwoSided.def).
    private static readonly Guid TransparentState = new Guid("5c12f1bd-f4c8-4626-86b5-0778e0613762");

    private sealed class Ring
    {
        public PBRTransparentMaterialDefinition Mat;
        public object MatHandle, Root, Model, RuntimeModel;
        public double K = -1;
        public bool Shown, Colored;
        public string Error;
    }
    private static readonly Dictionary<string, Ring> _rings = new Dictionary<string, Ring>();
    private static readonly HashSet<string> _used = new HashSet<string>();

    /// <summary>Place (building as needed) a ring: its texture material, radii (m), centre and orientation (+Y its normal), scale k.</summary>
    public static void Place(string name, AsteroidRingMaterialDefinition baseMat, double inner, double outer, Vector3D at, Quaternion orient, double k)
    {
        if (!Enabled || baseMat == null || !(k > 0) || !(outer > inner)) return;
        if (!_rings.TryGetValue(name, out var r)) _rings[name] = r = new Ring();
        _used.Add(name);
        if (r.Error != null) return;
        try
        {
            if (r.Mat == null && (r.Error = MakeMaterial(r, baseMat)) != null) { Status = $"map ring '{name}': {r.Error}"; return; }
            if (r.Model == null || Math.Abs(k - r.K) > r.K * Rebuild)
            {
                DisposeModel(r);
                if ((r.Error = Build(r, name, inner * k, outer * k, at, orient)) != null) { Status = $"map ring '{name}': {r.Error}"; DisposeModel(r); return; }
                r.K = k;
            }
            PlanetRenderBridge.UpdateRootTransform(r.Root, new WorldTransform(at, orient));
            if (!r.Colored) r.Colored = Colour(r.Model);
            if (!r.Shown) { PlanetRenderBridge.SetModelVisible(r.Model, true); r.Shown = true; }
            Status = $"map ring '{name}': k={k:G4} shown";
        }
        catch (Exception e) { r.Error = PlanetRenderBridge.Inner(e); Status = $"map ring '{name}' failed: {r.Error}"; DisposeModel(r); }
    }

    /// <summary>After a map frame: the rings not placed in it are hidden.</summary>
    public static void End()
    {
        foreach (var kv in _rings)
            if (!_used.Contains(kv.Key) && kv.Value.Shown && kv.Value.Model != null)
            {
                try { PlanetRenderBridge.SetModelVisible(kv.Value.Model, false); } catch { }
                kv.Value.Shown = false;
            }
        _used.Clear();
    }

    /// <summary>Everything rebuilt on the next placement (harness: after a material knob changed).</summary>
    public static void Reset()
    {
        foreach (var r in _rings.Values)
        {
            DisposeModel(r);
            if (r.Mat != null) try { PlanetRings.MaterialSystemCall("RemoveMaterial", r.Mat); } catch { }
        }
        _rings.Clear();   // (the materials go with their handles' owners: rebuilt fresh)
    }

    private static Type _tColoring;

    /// <summary>The model's colouring (ColoringCustomData, in VRage.Render: by reflection).</summary>
    private static bool Colour(object model)
    {
        _tColoring ??= PlanetRenderBridge.RenderAssembly?.GetType("Keen.VRage.Render.Materials.Templates.ColoringCustomData");
        if (_tColoring == null || model == null) return true;   // (nothing to do it with: left as it is)
        object d = Activator.CreateInstance(_tColoring);
        // (the value is stored shifted by -0.55, HSVDeltaColoring: the shift here is the shader's)
        _tColoring.GetField("ColoringHSV").SetValue(d, new ColorHSV(Hue, Sat, 0.55f + Dim, Tint));
        var fFlags = _tColoring.GetField("ColoringFlags");
        fFlags.SetValue(d, Enum.ToObject(fFlags.FieldType, 1));   // IgnoreColorMask: the whole ring
        return PlanetRenderBridge.SetEntityCustomData(model, d);
    }

    private static string MakeMaterial(Ring r, AsteroidRingMaterialDefinition baseMat)
    {
        if (!DefinitionManager.Instance.TryGetDefinition(TransparentState, out MaterialStateDefinition state)) return "PBRTransparent material state not found";
        var ob = DefinitionHelper.CreateObjectBuilder<PBRTransparentMaterialDefinitionObjectBuilder>();
        ob.DefaultState = state;
        ob.ColorMetalTexture = baseMat.ColorOpacityTexture;   // colour (RGB)
        ob.ExtensionsTexture = baseMat.ColorOpacityTexture;   // opacity (A)
        ob.EmissivityMultiplier = 0;
        ob.SpecularBaseColor = new ColorSRGB(0f, 0f, 0f, SpecA);
        ob.FresnelTightness = Fresnel;
        var mat = RuntimeDefinitionHelper.Create<PBRTransparentMaterialDefinition>(ob, null, keepBuilderGuid: true);
        mat.SetRuntimeState(state);
        // (not an IRuntimeMaterial, so no RuntimeMaterialHandle: registered as the proxy rings' materials are)
        if (!PlanetRings.MaterialSystemCall("AddRuntimeMaterial", mat)) return "material registration failed";
        r.Mat = mat;
        return null;
    }

    private static Type _tRmd, _tVs0, _tVs1, _tSub, _tRuntimeModel;
    private static object _cullNone;
    private static MethodInfo _createRuntimeModel, _toHandle;

    private static string Resolve()
    {
        if (_tRmd != null) return null;
        var render = PlanetRenderBridge.RenderAssembly;
        if (render == null) return "VRage.Render not loaded";
        _tVs0 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatPositionUV0Packed");
        _tVs1 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatNormalTangentPacked");
        _tSub = render.GetType("Keen.VRage.Render.Data.IRuntimeMeshData+SubPart");
        _tRuntimeModel = render.GetType("Keen.VRage.Render.Contracts.RuntimeModel");
        _cullNone = render.GetType("Keen.VRage.Render.Data.BackfaceCullingCone")?.GetProperty("CullNone", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var rmd = render.GetType("Keen.VRage.Render.Data.RuntimeMeshData`3");
        foreach (var m in PlanetRenderBridge.Contracts?.GetType().GetMethods() ?? new MethodInfo[0])
            if (m.Name == "CreateRuntimeModel" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType.Name == "IRuntimeMeshData") _createRuntimeModel = m;
        _toHandle = _tRuntimeModel?.GetMethod("op_Implicit", new[] { _tRuntimeModel });
        if (_tVs0 == null || _tVs1 == null || _tSub == null || rmd == null || _createRuntimeModel == null || _toHandle == null) return "runtime mesh types not found";
        _tRmd = rmd.MakeGenericType(_tVs0, _tVs1, typeof(Keen.VRage.Core.Render.Data.VertexFormatNull));
        return null;
    }

    private static object NewBuffer(Type elem) =>
        Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(elem), new object[] { Allocator.Heap, "OrbitalMapRing" });

    /// <summary>The annulus in the model's XZ plane (+Y up), u across the ring (0 inner, 1 outer) as the ring's texture, v at its mid-plane.</summary>
    private static string Build(Ring r, string name, double inner, double outer, Vector3D at, Quaternion orient)
    {
        string why = Resolve();
        if (why != null) return why;
        object vs0 = NewBuffer(_tVs0), vs1 = NewBuffer(_tVs1), subs = NewBuffer(_tSub);
        var idx = new Buffer<int>(Allocator.Heap, "OrbitalMapRing");
        var sections = new Buffer<Keen.VRage.Core.Model.Data.MeshData.Section>(Allocator.Heap, "OrbitalMapRing");
        MethodInfo add0 = vs0.GetType().GetMethod("Add", new[] { _tVs0 });
        MethodInfo add1 = vs1.GetType().GetMethod("Add", new[] { _tVs1 });
        var a1 = new object[1];
        int nr = Math.Max(2, Radial), na = Math.Max(16, Around);
        for (int j = 0; j <= na; j++)
        {
            double th = 2 * Math.PI * j / na;
            var dir = new Vector3((float)Math.Cos(th), 0, (float)Math.Sin(th));
            for (int i = 0; i <= nr; i++)
            {
                float u = i / (float)nr;
                float rad = (float)(inner + (outer - inner) * u);
                a1[0] = Activator.CreateInstance(_tVs0, new object[] { dir * rad, new Vector2(u, Depth) });
                add0.Invoke(vs0, a1);
                a1[0] = Activator.CreateInstance(_tVs1, new object[] { Vector3.UnitY, new Vector4(dir, 1f) });   // tangent: along u (outward)
                add1.Invoke(vs1, a1);
            }
        }
        for (int j = 0; j < na; j++)
            for (int i = 0; i < nr; i++)
            {
                int a = j * (nr + 1) + i, b = a + 1, c = a + nr + 1, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        object sub = Activator.CreateInstance(_tSub);
        _tSub.GetField("Name").SetValue(sub, StringId.Get("OrbitalMapRing_" + name));
        _tSub.GetField("IndexStart").SetValue(sub, 0);
        _tSub.GetField("IndicesCount").SetValue(sub, idx.Count);
        _tSub.GetField("Material").SetValue(sub, r.Mat);
        if (_cullNone != null) _tSub.GetField("CullingCone")?.SetValue(sub, _cullNone);
        subs.GetType().GetMethod("Add", new[] { _tSub }).Invoke(subs, new[] { sub });
        sections.Add(new Keen.VRage.Core.Model.Data.MeshData.Section
        {
            Name = new Keen.VRage.Core.Model.MeshSectionId(StringId.Get("OrbitalMapRing_" + name)),
            Parts = new[] { new Keen.VRage.Core.Model.Data.MeshData.Section.SectionPart { PartIndex = 0, IndicesOffset = 0, IndicesCount = idx.Count } },
        });
        object rmd = Activator.CreateInstance(_tRmd);
        SetField(rmd, "_vertexStream0", vs0);
        SetField(rmd, "_vertexStream1", vs1);
        SetField(rmd, "_vertexStream2", NewBuffer(typeof(Keen.VRage.Core.Render.Data.VertexFormatNull)));
        SetField(rmd, "_indices", idx);
        SetField(rmd, "_subParts", subs);
        SetField(rmd, "_meshSections", sections);
        SetField(rmd, "_bones", NewBuffer(typeof(Keen.VRage.Core.Model.ModelBone)));
        SetField(rmd, "_debugName", "OrbitalMapRing_" + name);
        float e = (float)outer;
        _tRmd.GetProperty("AABB").SetValue(rmd, new BoundingBox(new Vector3(-e, -e * 0.01f, -e), new Vector3(e, e * 0.01f, e)));

        object contracts = PlanetRenderBridge.Contracts;
        object dataType = Enum.ToObject(_createRuntimeModel.GetParameters()[1].ParameterType, (int)RenderRuntimeDataType.UI3D);
        r.RuntimeModel = _createRuntimeModel.Invoke(contracts, new[] { rmd, dataType, (object)true, (object)false });
        var handle = (ResourceHandle)_toHandle.Invoke(null, new[] { r.RuntimeModel });
        r.Root = PlanetRenderBridge.CreateRootEntity("OrbitalMapRingRoot_" + name, new WorldTransform(at, orient));
        // Visible | SkipCulling | SkipFarPlaneCulling | ForceHighestLOD, a Map entity (as the map's globes).
        r.Model = PlanetRenderBridge.CreateModelEntity("OrbitalMapRing_" + name, handle, r.Root, 0x1 | 0x2 | 0x10 | 0x20, mapOnly: true);
        r.Shown = true;
        return r.Model != null ? null : "model entity not created";
    }

    private static void DisposeModel(Ring r)
    {
        PlanetRenderBridge.DisposeRender(r.Model);
        PlanetRenderBridge.DisposeRender(r.Root);
        PlanetRenderBridge.DisposeRender(r.RuntimeModel);
        r.Model = r.Root = r.RuntimeModel = null;
        r.K = -1;
        r.Shown = false;
    }

    private static void SetField(object boxed, string name, object value)
    {
        for (Type t = boxed.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) { f.SetValue(boxed, value); return; }
        }
        throw new MissingFieldException(boxed.GetType().Name, name);
    }
}
