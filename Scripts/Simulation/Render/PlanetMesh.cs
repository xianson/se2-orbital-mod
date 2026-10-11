using System.Reflection;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Materials;
using Keen.VRage.Core.Render.Materials.Templates;
using Keen.VRage.Library.Filesystem;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// A high-detail proxy globe: a runtime cube-sphere textured with the planet's OWN overlay maps - the
/// colour and normal cubemaps the real planet draws with from orbit (PlanetOverlayDefinition.
/// OverlayColorMetal / OverlayNormalAO) - instead of the colonization-map model whose 2K atlas of 20
/// icosahedron patches looks faceted and seamed up close.
///
/// Built once per planet (per <see cref="Generation"/>), the way MapSectorsMesherComponent builds
/// its runtime mesh (see MapPipeline): one sub-part and mesh section per cube face, each with its
/// own runtime PBREmissive material in the MapPlanet state (the colonization globe's state: it
/// scales the vertices by ScaleCustomData, so the mesh is a UNIT sphere and UpdateProxy's scale is
/// simply the render radius). No cloud shell: the map cloud
/// material only masks by its colonization atlas, so on this mesh it drew noise (removed 2026-10-11).
///
/// The textures are the overlay cubemaps' faces re-packed for that material by tools/globes/bake_globes.py
/// (Assets/PlanetGlobes/&lt;Folder&gt;, compiled with the mod's content, found in the content cache):
///  - &lt;Folder&gt;_globe_cm_&lt;face&gt;: the colour, bit for bit, with a zero alpha. The material reads metalness
///    from that alpha; the game's own per-face colour maps carry BC1's alpha 1 - a fully metal, tan,
///    mirror-like globe - where the terrain's overlay path forces metal 0.
///  - &lt;Folder&gt;_globe_nr_&lt;face&gt;: the overlay's OBJECT-space normals turned into this mesh's tangent space
///    (AddFace: T along +s, B along +t), roughness 1 (the overlay path's constant).
/// Which texture goes on which face, and how it is turned, is the <see cref="Faces"/> table: the default
/// is the D3D cubemap convention the voxel code samples the overlay by (CubemapCoordinates, uncorrected),
/// tunable from the dev harness ("hiresglobe face ..."); the normal bake assumes that default.
///
/// Everything is best effort: a planet without baked textures, or any failure, gives null and the
/// proxy keeps the map model.
/// </summary>
public static class PlanetMesh
{
    /// <summary>Master switch; the proxies pick it up on their next update.</summary>
    public static bool Enabled = true;
    /// <summary>Quads per cube-face edge (a power of two keeps the half-float UVs exact).</summary>
    public static int Quads = 64;
    /// <summary>
    /// Front faces counter-clockwise seen from outside; false: clockwise. The MapPlanet state culls back faces,
    /// and in game (tools/harness/globeshot.sh, 2026-10-10) counter-clockwise showed the far hemisphere's
    /// INSIDE - lit on the side away from the sun - and clockwise matches the real planet's terminator.
    /// </summary>
    public static bool CcwOutside = false;
    /// <summary>The baked normal maps (else the material's flat default). Dev switch.</summary>
    public static bool NormalMaps = true;
    /// <summary>Multiplies the tangent's w (the bitangent's sign). Dev switch, to check the normal bake's convention.</summary>
    public static float TangentSign = 1f;

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

    public static void SetEnabled(bool on) { Enabled = on; Invalidate(); }
    public static void SetWinding(bool ccwOutside) { CcwOutside = ccwOutside; Invalidate(); }
    public static void SetNormals(bool on, float tangentSign) { NormalMaps = on; TangentSign = tangentSign; Invalidate(); }

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
        /// <summary>Its textures, pinned (a boxed ResourcePin) while a visible proxy shows it: <see cref="SetShown"/>.</summary>
        public List<ResourceHandle<TextureAsset>> Textures = new List<ResourceHandle<TextureAsset>>();
        public int Shown;
        public object Pin;
    }

    /// <summary>
    /// Pin the globes' textures while shown. The streamer otherwise picks a model texture's mip from its distance
    /// alone at a fixed 1024 texels per metre (ManagedTexturePrioritizerComponent.CollectStandards / ApplyStandardMaterials):
    /// a planet-sized proxy only ever gets the tail mips - the blotchy globe. The game pins its own terrain textures
    /// the same way (TriplanarMaterialsComponent: RenderContracts.CreateTextureResourcePin).
    /// </summary>
    public static bool PinTextures = true;
    /// <summary>ResourcePinType: 1 ObeyQuality (full size down to the texture-quality preset's skipped mips), 2 IgnoreQuality.</summary>
    public static int PinType = 1;

    private sealed class Info
    {
        public string Name;
        public bool Discovered;
        public string Folder, Dir;
        public readonly string[] Files = new string[6];
        public readonly ResourceHandle<TextureAsset>?[] Tex = new ResourceHandle<TextureAsset>?[6];
        public readonly ResourceHandle<TextureAsset>?[] NTex = new ResourceHandle<TextureAsset>?[6];   // normal + roughness
        public readonly PBREmissiveMaterialDefinition[] Mats = new PBREmissiveMaterialDefinition[12];   // 0-5 with the normal maps, 6-11 without
        public readonly object[] MatHandles = new object[12];   // boxed RuntimeMaterialHandle<T>
        public bool MaterialsOk;
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

    /// <summary>A proxy showing this model became visible (true) or hidden / let go of it (false).</summary>
    public static void SetShown(Built b, bool shown)
    {
        if (b == null) return;
        lock (_sync)
        {
            b.Shown = Math.Max(0, b.Shown + (shown ? 1 : -1));
            if (b.Shown > 0 && b.Pin == null && PinTextures && !b.Retired) b.Pin = MakePin(b);
            else if (b.Shown == 0) Unpin(b);
        }
    }

    private static object MakePin(Built b)
    {
        if (b.Textures.Count == 0) return null;
        try
        {
            object contracts = PlanetRenderBridge.Contracts;
            MethodInfo m = contracts?.GetType().GetMethod("CreateTextureResourcePin");
            if (m == null) { PlanetRenderBridge.WarnOnce("hiresglobe-pin", "RenderContracts.CreateTextureResourcePin not found: globe textures not pinned"); return null; }
            var ps = m.GetParameters();
            var handles = new Buffer<ResourceHandle<TextureAsset>>(Allocator.Heap, "OrbitalGlobePin");
            foreach (var t in b.Textures) handles.Add(t);
            return m.Invoke(contracts, new object[] { handles, Enum.ToObject(ps[1].ParameterType, PinType), Enum.ToObject(ps[2].ParameterType, 1 /* Texture2D */), "OrbitalGlobe" });
        }
        catch (Exception e) { PlanetRenderBridge.WarnOnce("hiresglobe-pin", "globe texture pin failed: " + PlanetRenderBridge.Inner(e)); return null; }
    }

    private static void Unpin(Built b)
    {
        if (b.Pin == null) return;
        try { (b.Pin as IDisposable)?.Dispose(); }
        catch (Exception e) { PlanetRenderBridge.WarnOnce("hiresglobe-unpin", "globe texture unpin failed: " + PlanetRenderBridge.Inner(e)); }
        b.Pin = null;
    }

    private static void DisposeIfUnused(Built b)
    {
        if (b.Retired) Unpin(b);
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
        for (int k = 0; k < 6; k++) { info.Tex[k] = null; info.NTex[k] = null; info.Files[k] = null; }
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
                if (f.Path.IndexOf("PlanetGlobes", StringComparison.OrdinalIgnoreCase) < 0) continue;   // only the baked globes are ever asked for
                list.Add((DirOf(f.Path), NameOf(f.Path), t));
            }
            _indexed = list;
        }

        // The baked faces: ..\PlanetGlobes\<Folder>\<Folder>_globe_cm_px.dds (and _nr_).
        if (info.Folder == null) return "no planet folder in the overlay's path " + dir;
        string prefix = info.Folder + "_globe_";
        int found = 0;
        foreach (var (d, name, handle) in _indexed)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length < prefix.Length + 5) continue;
            if (!NameOf(d).Equals(info.Folder, StringComparison.OrdinalIgnoreCase)) continue;
            string kind = name.Substring(prefix.Length, 2).ToLowerInvariant();
            int face = Array.IndexOf(FaceKeys, name.Substring(prefix.Length + 3, 2).ToLowerInvariant());
            if (face < 0) continue;
            if (kind == "cm" && !info.Tex[face].HasValue) { info.Tex[face] = handle; info.Files[face] = name; found++; }
            else if (kind == "nr") info.NTex[face] = handle;
        }
        if (found < 6) return $"{found}/6 baked globe faces for {info.Folder} (tools/globes/bake_globes.py {info.Folder}, then rebuild the content)";

        return null;
    }

    /// <summary>The baked faces' suffixes, in the cubemap's face order (+X, -X, +Y, -Y, +Z, -Z).</summary>
    private static readonly string[] FaceKeys = { "px", "nx", "py", "ny", "pz", "nz" };

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
    /// The baked normal + roughness map; no extensions map (its default: full AO, no emissive).
    /// </summary>
    private static string MakeMaterials(Info info)
    {
        if (!DefinitionManager.Instance.TryGetDefinition(MapPlanetState, out MaterialStateDefinition state)) return "MapPlanet material state not found";
        Type handleType = PlanetRenderBridge.RenderAssembly?.GetType("Keen.VRage.Render.Materials.RuntimeMaterialHandle`1")?.MakeGenericType(typeof(PBREmissiveMaterialDefinition));
        object contracts = PlanetRenderBridge.Contracts;
        if (handleType == null || contracts == null) return "RuntimeMaterialHandle / RenderContracts not available";
        for (int k = 0; k < 12; k++)
        {
            if (info.Mats[k] != null) continue;
            var ob = DefinitionHelper.CreateObjectBuilder<PBREmissiveMaterialDefinitionObjectBuilder>();
            ob.DefaultState = state;
            ob.ColorMetalTexture = info.Tex[k % 6];
            ob.NormalRoughnessTexture = k < 6 ? info.NTex[k] : null;
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

        // One (n+1)^2 grid per face on the unit sphere; uv(s, t) -> texture through the table.
        void AddFace(int f, int n, FaceMap map)
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

                    Vector2 uv = FaceUv(map, s, t);
                    args1[0] = Activator.CreateInstance(_tVs0, new object[] { p, uv });
                    add0.Invoke(vs0, args1);
                    args1[0] = Activator.CreateInstance(_tVs1, new object[] { p, new Vector4(tan, w * TangentSign) });
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
            var mat = info.Mats[map.Tex + (NormalMaps ? 0 : 6)];
            if (mat == null) return $"no material for texture {map.Tex}";
            int start = idx.Count;
            AddFace(f, quads, map);
            AddPart("OrbitalGlobeFace" + f, mat, start);
        }
        const float extent = 1f;
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
        for (int k = 0; k < 6; k++)
        {
            if (info.Tex[k].HasValue) info.Mesh.Textures.Add(info.Tex[k].Value);
            if (NormalMaps && info.NTex[k].HasValue) info.Mesh.Textures.Add(info.NTex[k].Value);
        }
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
        sb.Append($"hiresglobe enabled={Enabled} pin={(PinTextures ? (PinType == 2 ? "ignore-quality" : "obey-quality") : "off")} normals={NormalMaps} tsign={TangentSign} quads={Quads} ccwOutside={CcwOutside} gen={Generation}\n");
        for (int f = 0; f < 6; f++)
            sb.Append($"  face {f} {FaceNames[f]}: tex {Faces[f].Tex} flipU={(Faces[f].FlipU ? 1 : 0)} flipV={(Faces[f].FlipV ? 1 : 0)} rot={Faces[f].Rot}\n");
        lock (_sync)
        {
            if (_infos.Count == 0) sb.Append("  no planet asked yet (turn it on and look at a proxy)\n");
            foreach (var info in _infos.Values)
            {
                sb.Append($"  {info.Name} [{info.Folder ?? "?"}] dir={info.Dir ?? "?"}\n");
                for (int k = 0; k < 6; k++)
                    sb.Append($"    tex {k}: {info.Files[k] ?? "-"} {(info.Tex[k].HasValue ? info.Tex[k].Value.ToString() : "")} normal={(info.NTex[k].HasValue ? "yes" : "no")}\n");
                sb.Append($"    materials={(info.MaterialsOk ? "ok" : "no")} " +
                          $"mesh={(info.Mesh != null ? $"ok (gen {info.MeshGeneration}, users {info.Mesh.Users}, shown {info.Mesh.Shown}, pinned {(info.Mesh.Pin != null ? info.Mesh.Textures.Count : 0)})" : "no")} error={info.Error ?? "-"}\n");
            }
        }
        return sb.ToString();
    }
}
