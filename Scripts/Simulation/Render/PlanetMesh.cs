using System.Reflection;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Materials;
using Keen.VRage.Core.Render.Materials.Templates;
using Keen.VRage.Library.Filesystem;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// A high-detail proxy globe: a runtime cube-sphere textured with the planet's own 8K per-face
/// overlay colour maps (Verdure_cm_px.. / Kemik_Right_cm..), instead of the colonization-map model
/// whose 2K atlas of 20 icosahedron patches looks faceted and seamed up close.
///
/// Built once per planet (per <see cref="Generation"/>), the way MapSectorsMesherComponent builds
/// its runtime mesh (see MapPipeline): one sub-part and mesh section per cube face, each with its
/// own runtime PBREmissive material in the MapPlanet state (the colonization globe's state: it
/// scales the vertices by ScaleCustomData, so the mesh is a UNIT sphere and UpdateProxy's scale is
/// simply the render radius). Optionally a cloud shell sub-part with the planet's map cloud material.
///
/// The face textures are found in the content cache next to the planet's overlay cubemap
/// (PlanetOverlayDefinition.OverlayColorMetal); they are indexed there but referenced by no definition.
/// Which texture goes on which face, and how it is turned, is the <see cref="Faces"/> table:
/// the default is the D3D cubemap convention the voxel code uses (CubemapCoordinates, uncorrected),
/// tunable from the dev harness ("hiresglobe face ...").
///
/// Everything is best effort: a planet without face textures, or any failure, gives null and the
/// proxy keeps the map model. Off by default (<see cref="Enabled"/>).
/// </summary>
public static class PlanetMesh
{
    /// <summary>Master switch; the proxies pick it up on their next update.</summary>
    public static bool Enabled = false;
    /// <summary>A cloud shell with the planet's map cloud material (only with <see cref="Enabled"/>).</summary>
    public static bool Clouds = true;
    /// <summary>Quads per cube-face edge (a power of two keeps the half-float UVs exact).</summary>
    public static int Quads = 64;
    public static float CloudRadius = 1.012f;
    /// <summary>
    /// The cloud material masks clouds by its colonization displacement atlas at the mesh UV; our UVs
    /// are not that atlas's, so the whole shell samples one spot of it: an unused (zero-height) corner
    /// of the Verdure and Kemik atlases, i.e. clouds from the noise alone.
    /// </summary>
    public static Vector2 CloudUv = new Vector2(0.02f, 0.25f);
    /// <summary>Front faces counter-clockwise seen from outside (as PlanetWaterRenderUtils.CreateCube); false flips.</summary>
    public static bool CcwOutside = true;

    /// <summary>Bumped by every change above: proxies swap to the current model on their next update.</summary>
    public static int Generation { get; private set; }

    /// <summary>How one cube face (geometry: +X, -X, +Y, -Y, +Z, -Z) is textured.</summary>
    public sealed class FaceMap
    {
        /// <summary>Which face texture (same order: px, nx, py, ny, pz, nz).</summary>
        public int Tex;
        public bool FlipU, FlipV;
        /// <summary>Quarter turns of the UVs, applied before the flips.</summary>
        public int Rot;
    }

    public static readonly FaceMap[] Faces =
    {
        new FaceMap { Tex = 0 }, new FaceMap { Tex = 1 }, new FaceMap { Tex = 2 },
        new FaceMap { Tex = 3 }, new FaceMap { Tex = 4 }, new FaceMap { Tex = 5 },
    };

    private static readonly string[] FaceNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };

    /// <summary>The MapPlanet material state (VRage Engine Content/Materials/States/MapPlanet.def).</summary>
    private static readonly Guid MapPlanetState = new Guid("8bb9a381-432c-4b96-b31a-50dfaab5ba4f");

    /// <summary>The planets' map cloud materials (ColonizationMap/Models/Planets/*/..CloudsColonization.def), by planet folder.</summary>
    private static readonly Dictionary<string, Guid> CloudMaterials = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
    {
        { "Verdure", new Guid("668536af-9831-4718-bac6-40b58415cf13") },
        { "Kemik", new Guid("518cfe4b-d644-4331-b32a-f40ce4d2de10") },
    };

    public static void SetEnabled(bool on) { Enabled = on; Invalidate(); }
    public static void SetClouds(bool on) { Clouds = on; Invalidate(); }
    public static void SetWinding(bool ccwOutside) { CcwOutside = ccwOutside; Invalidate(); }

    public static string SetFace(int face, int tex, bool flipU, bool flipV, int rot)
    {
        if (face < 0 || face > 5 || tex < 0 || tex > 5) return "face and texture are 0..5";
        Faces[face] = new FaceMap { Tex = tex, FlipU = flipU, FlipV = flipV, Rot = ((rot % 4) + 4) % 4 };
        Invalidate();
        return $"face {face} ({FaceNames[face]}) <- tex {tex} flipU={flipU} flipV={flipV} rot={Faces[face].Rot}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-planet state
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One built model; proxies count their use so a retired one is disposed after the last.</summary>
    public sealed class Built
    {
        public ResourceHandle Handle;
        public object Model;   // boxed RuntimeModel
        public int Users;
        public bool Retired;
    }

    private sealed class Info
    {
        public string Name;
        public bool Discovered;
        public string Folder, Dir;
        public readonly string[] Files = new string[6];
        public readonly ResourceHandle<TextureAsset>?[] Tex = new ResourceHandle<TextureAsset>?[6];
        public readonly PBREmissiveMaterialDefinition[] Mats = new PBREmissiveMaterialDefinition[6];
        public readonly object[] MatHandles = new object[6];   // boxed RuntimeMaterialHandle<T>
        public bool MaterialsOk;
        public MaterialDefinition CloudMat;
        public Built Mesh;
        public int MeshGeneration = -1;
        public int FailedGeneration = -1;
        public string Error;
    }

    private static readonly object _sync = new object();
    private static readonly Dictionary<PlanetRenderBridge.PlanetHandles, Info> _infos =
        new Dictionary<PlanetRenderBridge.PlanetHandles, Info>(ReferenceEqualityComparer.Instance);

    private static void Invalidate()
    {
        lock (_sync)
        {
            Generation++;
            // Current meshes retire; each is disposed once no proxy uses it any more.
            foreach (var info in _infos.Values)
            {
                if (info.Mesh != null) { info.Mesh.Retired = true; DisposeIfUnused(info.Mesh); }
                info.Mesh = null;
                info.Error = null;
            }
        }
    }

    /// <summary>The planet's hi-res globe for this generation (built on first use), or null.</summary>
    public static Built Get(PlanetRenderBridge.PlanetHandles h)
    {
        if (!Enabled || h == null) return null;
        lock (_sync)
        {
            if (!_infos.TryGetValue(h, out var info)) _infos[h] = info = new Info { Name = h.Name };
            if (info.Mesh != null && info.MeshGeneration == Generation) return info.Mesh;
            if (info.FailedGeneration == Generation) return null;   // one attempt per generation
            try
            {
                string why = Prepare(h, info);
                if (why == null) why = Build(info);
                if (why == null) { info.MeshGeneration = Generation; info.Error = null; return info.Mesh; }
                info.Error = why;
            }
            catch (Exception e) { info.Error = "exception: " + PlanetRenderBridge.Inner(e); }
            info.FailedGeneration = Generation;
            PlanetRenderBridge.WarnOnce("hiresglobe-" + h.Name + "-" + Generation, $"hi-res globe for {h.Name}: {info.Error}; keeping the map model");
            return null;
        }
    }

    /// <summary>A proxy started using a model.</summary>
    public static void Acquire(Built b)
    {
        if (b == null) return;
        lock (_sync) b.Users++;
    }

    /// <summary>A proxy stopped using a model.</summary>
    public static void Release(Built b)
    {
        if (b == null) return;
        lock (_sync)
        {
            b.Users--;
            DisposeIfUnused(b);
        }
    }

    private static void DisposeIfUnused(Built b)
    {
        if (!b.Retired || b.Users > 0 || b.Model == null) return;
        try { b.Model.GetType().GetMethod("Dispose", new[] { typeof(bool) })?.Invoke(b.Model, new object[] { false }); }   // delayed: runs in order after the swaps
        catch (Exception e) { PlanetRenderBridge.WarnOnce("hiresglobe-dispose", "hi-res globe dispose failed: " + PlanetRenderBridge.Inner(e)); }
        b.Model = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Textures + materials (once per planet)
    // ─────────────────────────────────────────────────────────────────────────

    private static List<(string dir, string name, ResourceHandle<TextureAsset> handle)> _indexed;

    private static string Prepare(PlanetRenderBridge.PlanetHandles h, Info info)
    {
        if (!info.Discovered)
        {
            info.Discovered = true;
            string why = Discover(h, info);
            if (why != null) { info.Discovered = false; return why; }
        }
        if (!info.MaterialsOk)
        {
            string why = MakeMaterials(info);
            if (why != null) return why;
            info.MaterialsOk = true;
        }
        return null;
    }

    private static string Discover(PlanetRenderBridge.PlanetHandles h, Info info)
    {
        for (int k = 0; k < 6; k++) { info.Tex[k] = null; info.Files[k] = null; }
        if (h.Overlay == null) return "no planet overlay (no terrain definition)";
        object cube = PlanetRenderBridge.GetMember(h.Overlay, "OverlayColorMetal");
        if (!(cube is IResourceHandle rh)) return "overlay has no OverlayColorMetal";
        UInt128 key = rh.Key;
        var bytes = new byte[16];
        BitConverter.GetBytes((ulong)key).CopyTo(bytes, 0);
        BitConverter.GetBytes((ulong)(key >> 64)).CopyTo(bytes, 8);
        var overlayHandle = new ResourceHandle(new Guid(bytes));

        var cache = FileSystem.Instance?.ContentCache;
        if (cache == null) return "no content cache";
        if (!cache.TryTranslateResourceHandle(overlayHandle, out FileHandle overlayFile)) return "overlay cubemap not in the content cache";
        string dir = DirOf(overlayFile.Path);
        info.Dir = dir;
        // The planet's folder: Procedural\VS2_0\Planets\<Folder>\Maps\Overlay.
        string[] parts = dir.Split('\\', '/');
        for (int i = 0; i + 1 < parts.Length; i++) if (parts[i].Equals("Planets", StringComparison.OrdinalIgnoreCase)) info.Folder = parts[i + 1];

        // Every indexed texture, by folder (once: the cache holds tens of thousands of files).
        if (_indexed == null)
        {
            var list = new List<(string, string, ResourceHandle<TextureAsset>)>();
            foreach (var t in cache.GetAssets<TextureAsset>())
            {
                if (!cache.TryTranslateResourceHandle((ResourceHandle)t, out FileHandle f) || f.Path == null) continue;
                if (f.Path.IndexOf("Planets", StringComparison.OrdinalIgnoreCase) < 0) continue;   // only planet folders are ever asked for
                list.Add((DirOf(f.Path), NameOf(f.Path), t));
            }
            _indexed = list;
        }

        int found = 0;
        foreach (var (d, name, handle) in _indexed)
        {
            if (!d.Equals(dir, StringComparison.OrdinalIgnoreCase)) continue;
            int face = FaceOf(name);
            if (face < 0 || info.Tex[face].HasValue) continue;
            info.Tex[face] = handle;
            info.Files[face] = name;
            found++;
        }
        if (found < 6) return $"{found}/6 face textures in {dir}";

        if (info.Folder != null && CloudMaterials.TryGetValue(info.Folder, out Guid cg)
            && DefinitionManager.Instance.TryGetDefinition(cg, out MapPlanetCloudsMaterialDefinition clouds))
            info.CloudMat = clouds;
        return null;
    }

    /// <summary>Face (px, nx, py, ny, pz, nz) of a per-face colour map name, or -1. "Planet_Back_nao", the cubemap itself and the like are not.</summary>
    private static int FaceOf(string fileName)
    {
        string stem = fileName;
        int dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem.Substring(0, dot);
        string[] tokens = stem.ToLowerInvariant().Split('_');
        if (Array.IndexOf(tokens, "cm") < 0 || Array.IndexOf(tokens, "overlay") >= 0) return -1;
        foreach (string t in tokens)
        {
            switch (t)
            {
                case "px": case "right": return 0;
                case "nx": case "left": return 1;
                case "py": case "up": return 2;
                case "ny": case "down": return 3;
                case "pz": case "back": case "backward": return 4;   // Backward is +Z (CubemapFace)
                case "nz": case "front": case "forward": return 5;
            }
        }
        return -1;
    }

    static string DirOf(string path)
    {
        int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i > 0 ? path.Substring(0, i) : "";
    }

    static string NameOf(string path)
    {
        int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i >= 0 ? path.Substring(i + 1) : path;
    }

    /// <summary>
    /// One runtime material per face texture, as CachedRuntimeMaterialsFactory.Create makes them:
    /// a fresh definition from an object builder, in the MapPlanet state, then registered with the
    /// renderer through a RuntimeMaterialHandle (its constructor calls the internal AddRuntimeMaterial).
    /// No normal/extensions maps: their defaults are a flat normal, full AO, no emissive.
    /// </summary>
    private static string MakeMaterials(Info info)
    {
        if (!DefinitionManager.Instance.TryGetDefinition(MapPlanetState, out MaterialStateDefinition state)) return "MapPlanet material state not found";
        Type handleType = PlanetRenderBridge.RenderAssembly?.GetType("Keen.VRage.Render.Materials.RuntimeMaterialHandle`1")?.MakeGenericType(typeof(PBREmissiveMaterialDefinition));
        object contracts = PlanetRenderBridge.Contracts;
        if (handleType == null || contracts == null) return "RuntimeMaterialHandle / RenderContracts not available";
        for (int k = 0; k < 6; k++)
        {
            if (info.Mats[k] != null) continue;
            var ob = DefinitionHelper.CreateObjectBuilder<PBREmissiveMaterialDefinitionObjectBuilder>();
            ob.DefaultState = state;
            ob.ColorMetalTexture = info.Tex[k];
            ob.EmissivityMultiplier = 0;
            var mat = RuntimeDefinitionHelper.Create<PBREmissiveMaterialDefinition>(ob, null, keepBuilderGuid: true);
            mat.SetRuntimeState(state);
            info.MatHandles[k] = Activator.CreateInstance(handleType, new object[] { mat, contracts });
            info.Mats[k] = mat;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mesh
    // ─────────────────────────────────────────────────────────────────────────

    private static Type _tRmd, _tVs0, _tVs1, _tSub, _tRuntimeModel;
    private static object _cullNone;
    private static MethodInfo _createRuntimeModel, _toHandle;

    private static string ResolveMesh()
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
        if (_tVs0 == null || _tVs1 == null || _tSub == null || rmd == null || _createRuntimeModel == null || _toHandle == null)
            return "runtime mesh types not found";
        _tRmd = rmd.MakeGenericType(_tVs0, _tVs1, typeof(Keen.VRage.Core.Render.Data.VertexFormatNull));
        return null;
    }

    private static object NewBuffer(Type elem) =>
        Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(elem), new object[] { Allocator.Heap, "OrbitalPlanetMesh" });

    /// <summary>
    /// Point on the unit cube for face f at face coordinates (s, t) in 0..1: the inverse of
    /// CubemapCoordinates.GetCoordinatesForFaceUncorrected (the D3D cubemap layout).
    /// </summary>
    private static Vector3 CubePoint(int f, float s, float t)
    {
        float a = 2 * s - 1, b = 2 * t - 1;
        switch (f)
        {
            case 0: return new Vector3(1, -b, -a);
            case 1: return new Vector3(-1, -b, a);
            case 2: return new Vector3(a, 1, b);
            case 3: return new Vector3(a, -1, -b);
            case 4: return new Vector3(a, -b, 1);
            default: return new Vector3(-a, -b, -1);
        }
    }

    /// <summary>Texture UV of face coordinates (s, t) through the face's table entry.</summary>
    private static Vector2 FaceUv(FaceMap m, float s, float t)
    {
        for (int r = 0; r < m.Rot; r++) { float o = s; s = t; t = 1 - o; }
        if (m.FlipU) s = 1 - s;
        if (m.FlipV) t = 1 - t;
        return new Vector2(s, t);
    }

    private static string Build(Info info)
    {
        string why = ResolveMesh();
        if (why != null) return why;

        object vs0 = NewBuffer(_tVs0), vs1 = NewBuffer(_tVs1), subs = NewBuffer(_tSub);
        var idx = new Buffer<int>(Allocator.Heap, "OrbitalPlanetMesh");
        var sections = new Buffer<Keen.VRage.Core.Model.Data.MeshData.Section>(Allocator.Heap, "OrbitalPlanetMesh");
        MethodInfo add0 = vs0.GetType().GetMethod("Add", new[] { _tVs0 });
        MethodInfo add1 = vs1.GetType().GetMethod("Add", new[] { _tVs1 });
        MethodInfo addS = subs.GetType().GetMethod("Add", new[] { _tSub });
        var args1 = new object[1];
        int vcount = 0;

        void AddPart(string name, MaterialDefinition material, int start)
        {
            object sub = Activator.CreateInstance(_tSub);
            _tSub.GetField("Name").SetValue(sub, StringId.Get(name));
            _tSub.GetField("IndexStart").SetValue(sub, start);
            _tSub.GetField("IndicesCount").SetValue(sub, idx.Count - start);
            _tSub.GetField("Material").SetValue(sub, material);
            if (_cullNone != null) _tSub.GetField("CullingCone")?.SetValue(sub, _cullNone);
            addS.Invoke(subs, new[] { sub });
            int subIndex = (int)subs.GetType().GetProperty("Count").GetValue(subs) - 1;
            sections.Add(new Keen.VRage.Core.Model.Data.MeshData.Section
            {
                Name = new Keen.VRage.Core.Model.MeshSectionId(StringId.Get(name)),
                Parts = new[] { new Keen.VRage.Core.Model.Data.MeshData.Section.SectionPart { PartIndex = subIndex, IndicesOffset = 0, IndicesCount = idx.Count - start } },
            });
        }

        // One (n+1)^2 grid per face; uv(s, t) -> texture through the table. radius 1 for the ground,
        // CloudRadius for the shell (whose UVs all point at one spot of its displacement atlas).
        void AddFace(int f, int n, float radius, FaceMap map, Vector2? fixedUv)
        {
            int baseV = vcount;
            // Texture-space gradients of this face's (s, t) -> (u, v), for the tangent frame.
            Vector2 u0 = FaceUv(map, 0, 0), uS = FaceUv(map, 1, 0) - u0, uT = FaceUv(map, 0, 1) - u0;
            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    float s = i / (float)n, t = j / (float)n;
                    Vector3 p = Vector3.Normalize(CubePoint(f, s, t));
                    const float e = 1e-3f;
                    Vector3 dS = Vector3.Normalize(CubePoint(f, s + e, t)) - Vector3.Normalize(CubePoint(f, s - e, t));
                    Vector3 dT = Vector3.Normalize(CubePoint(f, s, t + e)) - Vector3.Normalize(CubePoint(f, s, t - e));
                    // dP/du and dP/dv from dP/ds, dP/dt and the (constant) UV gradients.
                    float det = uS.X * uT.Y - uS.Y * uT.X;
                    Vector3 tan = (uT.Y * dS - uS.Y * dT) / det;
                    Vector3 bit = (-uT.X * dS + uS.X * dT) / det;
                    tan = Vector3.Normalize(tan - p * Vector3.Dot(tan, p));
                    float w = Vector3.Dot(Vector3.Cross(p, tan), bit) > 0 ? -1f : 1f;   // as PlanetWaterRenderUtils: n x t = +dP/dv -> w = -1

                    Vector2 uv = fixedUv ?? FaceUv(map, s, t);
                    args1[0] = Activator.CreateInstance(_tVs0, new object[] { p * radius, uv });
                    add0.Invoke(vs0, args1);
                    args1[0] = Activator.CreateInstance(_tVs1, new object[] { p, new Vector4(tan, w) });
                    add1.Invoke(vs1, args1);
                    vcount++;
                }
            }
            // Winding: (b - a) x (c - a) outward for counter-clockwise-from-outside, whatever the face's parametrisation.
            Vector3 c0 = CubePoint(f, 0, 0), cS = CubePoint(f, 1, 0) - c0, cT = CubePoint(f, 0, 1) - c0;
            bool stOutward = Vector3.Dot(Vector3.Cross(cS, cT), CubePoint(f, 0.5f, 0.5f)) > 0;
            bool keep = stOutward == CcwOutside;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = baseV + j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    if (keep) { idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(b); idx.Add(d); idx.Add(c); }
                    else { idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d); }
                }
            }
        }

        int quads = Math.Max(4, Quads);
        for (int f = 0; f < 6; f++)
        {
            FaceMap map = Faces[f];
            var mat = info.Mats[map.Tex];
            if (mat == null) return $"no material for texture {map.Tex}";
            int start = idx.Count;
            AddFace(f, quads, 1f, map, null);
            AddPart("OrbitalGlobeFace" + f, mat, start);
        }
        bool clouds = Clouds && info.CloudMat != null;
        if (clouds)
        {
            int start = idx.Count;
            for (int f = 0; f < 6; f++) AddFace(f, Math.Max(4, quads / 2), CloudRadius, Faces[f], CloudUv);
            AddPart("OrbitalGlobeClouds", info.CloudMat, start);
        }

        float extent = clouds ? CloudRadius : 1f;
        object rmd = Activator.CreateInstance(_tRmd);
        SetField(rmd, "_vertexStream0", vs0);
        SetField(rmd, "_vertexStream1", vs1);
        SetField(rmd, "_vertexStream2", NewBuffer(typeof(Keen.VRage.Core.Render.Data.VertexFormatNull)));
        SetField(rmd, "_indices", idx);
        SetField(rmd, "_subParts", subs);
        SetField(rmd, "_meshSections", sections);
        SetField(rmd, "_bones", NewBuffer(typeof(Keen.VRage.Core.Model.ModelBone)));
        SetField(rmd, "_debugName", "OrbitalPlanetGlobe_" + info.Name);
        _tRmd.GetProperty("AABB").SetValue(rmd, new BoundingBox(new Vector3(-extent), new Vector3(extent)));

        object contracts = PlanetRenderBridge.Contracts;
        object dataType = Enum.ToObject(_createRuntimeModel.GetParameters()[1].ParameterType, (int)RenderRuntimeDataType.UI3D);
        object model = _createRuntimeModel.Invoke(contracts, new[] { rmd, dataType, (object)true, (object)false });
        info.Mesh = new Built { Model = model, Handle = (ResourceHandle)_toHandle.Invoke(null, new[] { model }) };
        return null;
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

    // ─────────────────────────────────────────────────────────────────────────
    // Status (dev harness)
    // ─────────────────────────────────────────────────────────────────────────

    public static string Status()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"hiresglobe enabled={Enabled} clouds={Clouds} quads={Quads} ccwOutside={CcwOutside} gen={Generation} cloudUv=({CloudUv.X:F2},{CloudUv.Y:F2})\n");
        for (int f = 0; f < 6; f++)
            sb.Append($"  face {f} {FaceNames[f]}: tex {Faces[f].Tex} flipU={(Faces[f].FlipU ? 1 : 0)} flipV={(Faces[f].FlipV ? 1 : 0)} rot={Faces[f].Rot}\n");
        lock (_sync)
        {
            if (_infos.Count == 0) sb.Append("  no planet asked yet (turn it on and look at a proxy)\n");
            foreach (var info in _infos.Values)
            {
                sb.Append($"  {info.Name} [{info.Folder ?? "?"}] dir={info.Dir ?? "?"}\n");
                for (int k = 0; k < 6; k++)
                    sb.Append($"    tex {k}: {info.Files[k] ?? "-"} {(info.Tex[k].HasValue ? info.Tex[k].Value.ToString() : "")}\n");
                sb.Append($"    materials={(info.MaterialsOk ? "ok" : "no")} clouds={(info.CloudMat != null ? "material ok" : "none")} " +
                          $"mesh={(info.Mesh != null ? $"ok (gen {info.MeshGeneration}, users {info.Mesh.Users})" : "no")} error={info.Error ?? "-"}\n");
            }
        }
        return sb.ToString();
    }
}
