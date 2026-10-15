using System.Collections;
using System.Reflection;
using System.Globalization;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE RENDER-SIDE REFLECTION, and the shared reflection helpers the rest of the mod uses (GetMember, SetMember,
/// FindType, CallAdd, WarnOnce). Every use, here and elsewhere, is listed in docs/REFLECTION_AND_WHITELIST.md.
///
/// Mod scripts compile against a fixed set of engine assemblies. Planet terrain rendering
/// (VRage.Voxels.Client) and the render service (VRage.Render) are not in that set, so the
/// calls below are late-bound. Reflection is only allowed today because the whitelist admits all
/// of CoreLib. If Keen instead
/// adds VRage.Voxels.Client + VRage.Render to the mod compile references, every call here can
/// become a direct call.
///
/// What it does, and the engine code it mirrors (SE2 2.4.0.77):
///  - Hide/show terrain: VoxelRenderComponent.SetVisible(bool). Keen's GroundTruthDebugTool
///    "HidePlanet" toggle does exactly this. Hidden clipmaps stop updating; new cells inherit it.
///  - Hide/show atmosphere + clouds: PlanetEnvironmentEntity.SetParameters with the atmosphere
///    and cloud definitions nulled, then restored. UNVERIFIED that null hides them cleanly.
///  - Proxy globe: the planet's own colonization-map visual (DiscoverablePlanetComponent's
///    MapVisualPrefab). The map draws it as EntityType.Map, which the culling shader hides outside
///    the map. We create our own render model from the same model asset as EntityType.Default,
///    and size it with ScaleCustomData, which the map-planet material scales vertices by.
///  - Not handled: planetary water (its model entity is a private field) and flora.
///
/// Every call is best effort: failures are logged once and reported as false, never thrown.
/// </summary>
public static partial class PlanetRenderBridge
{
    /// <summary>Serializes all render-side calls. Planet jobs may run on worker threads.</summary>
    public static readonly object Lock = new object();

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Per-planet late-bound handles, resolved once on the client.</summary>
    public sealed class PlanetHandles
    {
        public string Name;
        public double Radius;
        /// <summary>Radius the proxy is drawn at: midway between base and max-hills radius.</summary>
        public double SurfaceRadius;

        /// <summary>VoxelPlanetRenderComponent. Non-null means terrain can be hidden.</summary>
        public object Terrain;
        /// <summary>Its VoxelClipmaps: the detailed one and the low-res one that draws the planet from afar.</summary>
        public List<object> Clipmaps = new List<object>();
        /// <summary>Frames left in a pending hide (see SetTerrainVisible).</summary>
        public int HideFramesLeft;

        public object Environment;
        public MethodInfo EnvSetParameters;
        public object[] EnvShowArgs;
        /// <summary>The warm-up's per-clipmap progress (TerrainSettled).</summary>
        public Dictionary<object, int> WarmBusy;
        /// <summary>The planet's atmosphere and sky spherization (SpherizationData, boxed), for the proxy's atmosphere.</summary>
        public object AtmoSphere, SkySphere;
        public object[] EnvHideArgs;
        /// <summary>The terrain's PlanetOverlayDefinition (its cubemaps locate the hi-res globe's face textures), or null.</summary>
        public object Overlay;

        public bool HasProxyModel;
        public ResourceHandle ProxyModel;
        /// <summary>Native bounding radius of the proxy model; ScaleCustomData multiplies it.</summary>
        public double ProxyModelRadius = 1.0;
    }

    /// <summary>One client-local proxy globe: a render root plus a model under it.</summary>
    public sealed class Proxy
    {
        public object Root;
        public object Model;
        public bool Visible;
        public float LastScale = -1f;
        /// <summary>A map globe, else a world proxy.</summary>
        public bool MapOnly;
        /// <summary>Its atmosphere (the game's PlanetEnvironmentEntity, boxed), where it was made, and at what scale.</summary>
        public object Atmo;
        public Vector3D AtmoAt;
        public double AtmoK, AtmoMadeAt;
        public int AtmoGen;
        /// <summary>The successor staged a frame ahead (UpdateAtmosphere's two-stage handover): where, at what scale, in
        /// which client frame, for which generation.</summary>
        public object AtmoNext; public Vector3D AtmoNextAt; public double AtmoNextK, AtmoNextWall; public long AtmoNextFrame; public int AtmoNextGen;
        /// <summary>Those being retired (stripped of their air at stage 2, disposed at stage 3): env, at which update, when.</summary>
        public readonly List<(object env, long upd, double wall)> AtmoRetiring = new List<(object env, long upd, double wall)>();
        /// <summary>Its atmosphere's updates (UpdateAtmosphere: once a frame while it is shown) - the handover's frame count.</summary>
        public long AtmoUpdates;
        /// <summary>The hi-res globe it draws (<see cref="PlanetMesh"/>), else the map model; the model's radius; for which PlanetMesh.Generation.</summary>
        public PlanetMesh.Built Mesh;
        public double ModelRadius = 1.0;
        public int MeshGeneration;
    }

    // ── Render service (resolved once) ──
    private static bool _renderResolved;
    private static bool _renderOk;
    private static object _contracts;
    private static MethodInfo _createRoot;
    private static MethodInfo _createModel;
    private static Type _renderFlagsType;
    private static Type _entityTypeType;
    private static Type _scaleDataType;
    private static FieldInfo _scaleField;

    private static readonly HashSet<string> _warned = new HashSet<string>();

    // ─────────────────────────────────────────────────────────────────────────
    // Resolve
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve everything needed to hide/show and proxy one planet. Never returns null; check
    /// <see cref="PlanetHandles.Terrain"/> and <see cref="PlanetHandles.HasProxyModel"/>.
    /// </summary>
    /// <summary>Handles with only a proxy model, from a map visual prefab (the star's: no terrain, no atmosphere).</summary>
    public static PlanetHandles ResolveModelOnly(PrefabDefinition mapVisual, string name)
    {
        var h = new PlanetHandles { Name = name };
        ResolveModel(h, mapVisual, name);
        return h;
    }

    public static PlanetHandles Resolve(PlanetEnvironmentRenderComponent env, PrefabDefinition mapVisual, string name)
    {
        var h = new PlanetHandles { Name = name };

        // Terrain + planet shape.
        object planet = null;
        try
        {
            h.Terrain = GetMember(env, "_voxelPlanetRender");
            foreach (string field in new[] { "_clipmap", "_lowResClipmap" })
            {
                object clipmap = GetMember(h.Terrain, field);
                if (clipmap != null) h.Clipmaps.Add(clipmap);
            }
            if (h.Clipmaps.Count == 0) h.Terrain = null;

            planet = GetMember(env, "_planet");
            h.Radius = planet != null ? Convert.ToDouble(GetMember(planet, "Radius")) : 0;
            // Proxy size: the visible limb sits between the base radius and the max-hills radius;
            // the base radius alone drew the globe ~5% small next to the real planet (screenshots 02/03).
            object maxHills = GetMember(planet, "RadiusWithMaxHills");
            h.SurfaceRadius = maxHills != null ? 0.5 * (h.Radius + Convert.ToDouble(maxHills)) : h.Radius;
        }
        catch (Exception e) { WarnOnce("terrain", $"terrain resolve failed: {e.Message}"); h.Terrain = null; }

        // Atmosphere + clouds: capture the exact arguments PlanetEnvironmentRenderComponent used.
        try
        {
            h.Environment = GetMember(env, "PlanetEnvironmentEntity");
            h.EnvSetParameters = h.Environment?.GetType().GetMethod("SetParameters");

            object atmosphere = GetMember(env, "AtmosphereDefinition");
            object clouds = GetMember(env, "CloudDefinition");
            object envDefinition = GetMember(env, "_planetEnvRenderDefinition");
            object spherization = GetMember(envDefinition, "Spherization");
            // An extension method (SpherizationOverrideHelper, VRage.Render), not an instance method.
            object sphereData = null;
            Type helper = spherization?.GetType().Assembly.GetType("Keen.VRage.Render.Materials.SpherizationOverrideHelper")
                          ?? FindType("VRage.Render", "Keen.VRage.Render.Materials.SpherizationOverrideHelper");
            MethodInfo getData = helper?.GetMethod("GetSpherizationData", BindingFlags.Public | BindingFlags.Static);
            if (getData != null) sphereData = getData.Invoke(null, new[] { spherization });
            // (the atmosphere's and the sky's own: at a distance the game pulls the atmosphere's shell down to the ground -
            //  the proxy's atmosphere needs them too, or it shows its full height: a thick glow)
            h.AtmoSphere = helper?.GetMethod("GetAtmosphereSpherization", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { spherization });
            h.SkySphere = helper?.GetMethod("GetSkyboxSpherization", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { spherization });
            object overlay = GetMember(GetMember(h.Terrain, "Definition"), "PlanetOverlay");
            h.Overlay = overlay;
            object atmosphereRadius = GetMember(planet, "AtmosphereRadius");
            object radiusWithMaxHills = GetMember(planet, "RadiusWithMaxHills");
            object spherizeRadius = GetMember(planet, "SpherizeRadius");

            if (h.EnvSetParameters != null && sphereData != null && atmosphereRadius != null)
            {
                h.EnvShowArgs = new[] { atmosphere, atmosphereRadius, radiusWithMaxHills, clouds, sphereData, spherizeRadius, overlay };
                h.EnvHideArgs = new[] { null, atmosphereRadius, radiusWithMaxHills, null, sphereData, spherizeRadius, overlay };
            }
            else
            {
                WarnOnce("atmo-args", $"atmosphere handles incomplete for {name} (entity={h.Environment != null} setParams={h.EnvSetParameters != null} sphere={sphereData != null})");
            }
        }
        catch (Exception e) { WarnOnce("atmo", $"atmosphere resolve failed: {e.Message}"); h.EnvShowArgs = h.EnvHideArgs = null; }

        ResolveModel(h, mapVisual, name);
        return h;
    }

    /// <summary>Proxy model: the model asset inside a map visual prefab.</summary>
    static void ResolveModel(PlanetHandles h, PrefabDefinition mapVisual, string name)
    {
        try
        {
            var prefab = mapVisual;
            if (prefab == null)
            {
                WarnOnce("prefab-" + name, $"{name} has no map visual prefab; no proxy");
            }
            else if (GetMember(prefab.Composition, "Definitions") is IEnumerable definitions)
            {
                foreach (object definition in definitions)
                {
                    if (definition == null) continue;
                    if (GetMember(definition, "Model") is ResourceHandle<ModelAsset> model)
                    {
                        h.ProxyModel = model;
                        h.HasProxyModel = true;

                        ResourceHandle plain = model;
                        if (plain.TryGetMetadata<ResourceHandle, AssetModelMetadata>(out var metadata)
                            && metadata.BoundingSphere.Radius > 1e-4f)
                        {
                            h.ProxyModelRadius = metadata.BoundingSphere.Radius;
                        }
                        break;
                    }
                }
                if (!h.HasProxyModel) WarnOnce("model-" + name, $"{name} map visual has no model definition");
            }
        }
        catch (Exception e) { WarnOnce("model", $"proxy model resolve failed: {e.Message}"); h.HasProxyModel = false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────────────────────

    private static Assembly _renderAsm;

    private static bool ResolveRender()
    {
        if (_renderResolved) return _renderOk;
        _renderResolved = true;
        try
        {
            Assembly render = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "VRage.Render") { render = a; break; }
            }
            if (render == null) { WarnOnce("render-asm", "VRage.Render not loaded (dedicated server?)"); return false; }
            _renderAsm = render;

            Type engine = render.GetType("Keen.VRage.Render.EngineComponents.RenderEngineComponent");
            object instance = engine?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _contracts = instance?.GetType().GetProperty("RenderContracts")?.GetValue(instance);
            if (_contracts == null) { WarnOnce("render-contracts", "RenderContracts not available"); return false; }

            foreach (MethodInfo m in _contracts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "CreateRootEntity" && m.GetParameters().Length == 3) _createRoot = m;
                if (m.Name == "CreateModelEntity" && m.GetParameters().Length == 7) _createModel = m;
            }

            _renderFlagsType = render.GetType("Keen.VRage.Render.Data.RenderFlags");
            _entityTypeType = render.GetType("Keen.VRage.Render.Data.EntityType");
            _scaleDataType = render.GetType("Keen.VRage.Render.Materials.Templates.ScaleCustomData");
            _scaleField = _scaleDataType?.GetField("Scale");

            _renderOk = _createRoot != null && _createModel != null && _renderFlagsType != null
                        && _entityTypeType != null && _scaleField != null;
            Log.Default?.Info($"[ORBIT] render bridge: root={_createRoot != null} model={_createModel != null} " +
                              $"flags={_renderFlagsType != null} etype={_entityTypeType != null} scale={_scaleField != null}");
            return _renderOk;
        }
        catch (Exception e) { WarnOnce("render", $"render resolve failed: {Inner(e)}"); return false; }
    }

    // ── for other render-side helpers (PlanetRings): the entity calls above, by boxed handle ──

    /// <summary>A render root (RenderContracts.CreateRootEntity), or null.</summary>
    internal static object CreateRootEntity(string name, WorldTransform at)
    {
        if (!ResolveRender()) return null;
        return _createRoot.Invoke(_contracts, new object[] { name, at, true });
    }

    /// <summary>A Default-type model entity under a root (RenderContracts.CreateModelEntity), flags as RenderFlags bits.</summary>
    internal static object CreateModelEntity(string name, ResourceHandle model, object root, int flags, bool mapOnly = false)
    {
        if (!ResolveRender()) return null;
        return _createModel.Invoke(_contracts, new object[]
        {
            name, model, RelativeTransform.Identity, root, Enum.ToObject(_renderFlagsType, flags),
            mapOnly ? Enum.Parse(_entityTypeType, "Map") : Enum.ToObject(_entityTypeType, 0), null
        });
    }

    /// <summary>A model entity's Visible render flag (as <see cref="SetProxyVisible"/>).</summary>
    internal static void SetModelVisible(object model, bool visible)
    {
        if (model == null || !ResolveRender()) return;
        model.GetType().GetMethod("SetRenderFlagsState")?.Invoke(model, new[] { Enum.ToObject(_renderFlagsType, 0x1), (object)visible });
    }

    /// <summary>A model entity's entity custom data (boxed struct of the material state's EntityDataType).</summary>
    internal static bool SetEntityCustomData(object model, object data)
    {
        var m = model == null || data == null ? null : FindSetEntityCustomData(model.GetType());
        if (m == null) return false;
        m.MakeGenericMethod(data.GetType()).Invoke(model, new[] { data });
        return true;
    }

    internal static void UpdateRootTransform(object root, WorldTransform at)
    {
        if (root == null) return;
        _rootUpdateTransform ??= root.GetType().GetMethod("UpdateTransform");
        _rootUpdateTransform?.Invoke(root, new object[] { at });
    }

    internal static void DisposeRender(object renderObject)
    {
        if (renderObject == null) return;
        try { renderObject.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(renderObject, null); }
        catch (Exception e) { WarnOnce("render-dispose", "render object dispose failed: " + Inner(e)); }
    }

    private static readonly Dictionary<Type, MethodInfo> _setCustomData = new Dictionary<Type, MethodInfo>();

    /// <summary>ModelEntity.SetEntityCustomData&lt;T&gt;(in T) — the by-ref generic overload.</summary>
    private static MethodInfo FindSetEntityCustomData(Type modelType)
    {
        if (_setCustomData.TryGetValue(modelType, out var cached)) return cached;
        MethodInfo found = null;
        foreach (MethodInfo m in modelType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "SetEntityCustomData" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.IsByRef) { found = m; break; }
        }
        _setCustomData[modelType] = found;
        return found;
    }

    /// <summary>For the map pipeline: the render assembly, the RenderContracts instance, a type by name.</summary>
    public static Assembly RenderAssembly { get { ResolveRender(); return _renderAsm; } }
    public static object Contracts { get { ResolveRender(); return _contracts; } }

    /// <summary>list.Add(item) on a collection type mod code cannot reference.</summary>
    internal static bool CallAdd(object list, object item)
    {
        try
        {
            var m = list?.GetType().GetMethod("Add", new[] { item.GetType() }) ?? list?.GetType().GetMethod("Add");
            if (m == null) return false;
            m.Invoke(list, new[] { item });
            return true;
        }
        catch (Exception e) { Log.Default?.Info("[ORBIT] CallAdd: " + (e.InnerException ?? e).Message); return false; }
    }

    /// <summary>list.Remove(item) on a collection type mod code cannot reference.</summary>
    internal static bool CallRemove(object list, object item)
    {
        try
        {
            var m = list?.GetType().GetMethod("Remove", new[] { item.GetType() }) ?? list?.GetType().GetMethod("Remove");
            if (m == null) return false;
            m.Invoke(list, new[] { item });
            return true;
        }
        catch (Exception e) { Log.Default?.Info("[ORBIT] CallRemove: " + (e.InnerException ?? e).Message); return false; }
    }

    // Member lookups cached per (type, name), misses included: GetMember/SetMember run ~35 times a map frame, and a lookup
    // walked the type chain with GetProperty/GetField each time.
    sealed class MemberSlot { public PropertyInfo P; public FieldInfo F; }
    static readonly Dictionary<(Type, string), MemberSlot> _members = new Dictionary<(Type, string), MemberSlot>();
    // (reads and writes in their own tables: the write key was "=" + name, a new string on every SetMember - ~35 a map frame)
    static readonly Dictionary<(Type, string), MemberSlot> _writeMembers = new Dictionary<(Type, string), MemberSlot>();
    static MemberSlot Slot(Type type, string name, bool forWrite)
    {
        var key = (type, name);
        var table = forWrite ? _writeMembers : _members;
        lock (_members)
        {
            if (table.TryGetValue(key, out var slot)) return slot;
            slot = new MemberSlot();
            for (Type t = type; t != null && slot.P == null && slot.F == null; t = t.BaseType)
            {
                if (forWrite)
                {
                    var f = t.GetField(name, AnyInstance);
                    if (f != null) { slot.F = f; break; }
                    var p = t.GetProperty(name, AnyInstance);
                    if (p != null && p.CanWrite) slot.P = p;
                }
                else
                {
                    var p = t.GetProperty(name, AnyInstance);
                    if (p != null && p.GetIndexParameters().Length == 0) { slot.P = p; break; }
                    var f = t.GetField(name, AnyInstance);
                    if (f != null) slot.F = f;
                }
            }
            table[key] = slot;
            return slot;
        }
    }

    internal static object GetMember(object target, string name)
    {
        if (target == null) return null;
        var slot = Slot(target.GetType(), name, false);
        return slot.P != null ? slot.P.GetValue(target) : slot.F?.GetValue(target);
    }

    /// <summary>Sets a member; false (logged once) when there is none. It threw - ~200 ms a frame after a game patch renamed
    /// one the map sets every frame.</summary>
    internal static bool SetMember(object target, string name, object value)
    {
        if (target == null) return false;
        var slot = Slot(target.GetType(), name, true);
        if (slot.F != null) { slot.F.SetValue(target, value); return true; }
        if (slot.P != null) { slot.P.SetValue(target, value); return true; }
        WarnOnce("setmember-" + target.GetType().Name + "." + name, $"no writable member {target.GetType().Name}.{name}");
        return false;
    }

    internal static Type FindType(string assemblyName, string typeName)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name == assemblyName) return a.GetType(typeName);
        }
        return null;
    }

    internal static string Inner(Exception e) => (e.InnerException ?? e).Message;

    internal static void WarnOnce(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key)) return;
        }
        Log.Default?.Warning($"[ORBIT] {message}");
    }
}
