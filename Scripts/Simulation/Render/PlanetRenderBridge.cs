using System.Collections;
using System.Reflection;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE ONLY FILE THAT USES REFLECTION. Everything else in the mod is whitelisted API.
///
/// Mod scripts compile against a fixed set of engine assemblies. Planet terrain rendering
/// (VRage.Voxels.Client) and the render service (VRage.Render) are not in that set, so the
/// calls below are late-bound. Reflection is only allowed today because the whitelist admits all
/// of CoreLib; if Keen bans it, this file stops compiling and nothing else should. If Keen instead
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
public static class PlanetRenderBridge
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

        /// <summary>VoxelPlanetRenderComponent. Non-null means terrain can be hidden.</summary>
        public object Terrain;
        /// <summary>Its VoxelClipmaps: the detailed one and the low-res one that draws the planet from afar.</summary>
        public List<object> Clipmaps = new List<object>();

        public object Environment;
        public MethodInfo EnvSetParameters;
        public object[] EnvShowArgs;
        public object[] EnvHideArgs;

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
            object overlay = GetMember(GetMember(h.Terrain, "Definition"), "PlanetOverlay");
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

        // Proxy model: the model asset inside the planet's map visual prefab.
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

        return h;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Real planet visibility
    // ─────────────────────────────────────────────────────────────────────────

    private static int _terrainFailures;

    /// <summary>
    /// Hide or show a planet's terrain. Does what VoxelClipmap.Visible's setter does, for BOTH the
    /// detailed and the low-res clipmap, but tolerates cells that are not attached yet: the stock
    /// setter throws (NullReference on VoxelCell._voxel) on those and then never records the flag.
    ///  1. clipmap._visible = visible — new cells inherit it (VoxelClipmapRing.AddCell), and a
    ///     hidden clipmap stops its LOD update (VoxelClipmap.Update early-out).
    ///  2. every attached cell gets SetVisible(visible) — a queued transition, applied by the
    ///     PostProcessCells job, which is not gated on visibility.
    /// Returns false on any failure so the caller retries next frame.
    /// </summary>
    public static bool SetTerrainVisible(PlanetHandles h, bool visible)
    {
        if (h?.Terrain == null) return false;
        try
        {
            foreach (object clipmap in h.Clipmaps)
            {
                SetMember(clipmap, "_visible", visible);
                if (!(GetMember(clipmap, "Rings") is IEnumerable rings)) continue;
                foreach (object ring in rings)
                {
                    if (!(GetMember(ring, "Cells") is IDictionary cells)) continue;
                    // Copy first: clipmap jobs may add/remove cells while we walk.
                    var snapshot = new List<object>();
                    foreach (object cellData in cells.Values) snapshot.Add(cellData);
                    foreach (object cellData in snapshot)
                    {
                        object cell = GetMember(cellData, "Cell");
                        if (cell == null || GetMember(cell, "_voxel") == null) continue;
                        cell.GetType().GetMethod("SetVisible", new[] { typeof(bool) })?.Invoke(cell, new object[] { visible });
                    }
                }
            }
            return true;
        }
        catch (Exception e)
        {
            if (++_terrainFailures <= 5) Log.Default?.Warning($"[ORBIT] terrain visibility failed ({_terrainFailures}): {Inner(e)}");
            return false;
        }
    }

    public static bool SetAtmosphereVisible(PlanetHandles h, bool visible)
    {
        if (h?.EnvShowArgs == null) return false;
        try
        {
            h.EnvSetParameters.Invoke(h.Environment, visible ? h.EnvShowArgs : h.EnvHideArgs);
            return true;
        }
        catch (Exception e) { WarnOnce("atmo-set", $"SetParameters failed: {Inner(e)}"); return false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Proxy globes
    // ─────────────────────────────────────────────────────────────────────────

    public static Proxy CreateProxy(PlanetHandles h, Vector3D center)
    {
        if (h == null || !h.HasProxyModel || !ResolveRender()) return null;
        try
        {
            object root = _createRoot.Invoke(_contracts, new object[] { "OrbitalProxyRoot_" + h.Name, new WorldTransform(center), true });

            // Visible | SkipFarPlaneCulling | ForceHighestLOD, as a Default (world-view) entity.
            object flags = Enum.ToObject(_renderFlagsType, 0x1 | 0x10 | 0x20);
            object entityType = Enum.ToObject(_entityTypeType, 0);
            object model = _createModel.Invoke(_contracts, new object[]
            {
                "OrbitalProxy_" + h.Name, h.ProxyModel, RelativeTransform.Identity, root, flags, entityType, null
            });

            return new Proxy { Root = root, Model = model, Visible = true };
        }
        catch (Exception e) { WarnOnce("proxy-create", $"proxy create failed for {h.Name}: {Inner(e)}"); return null; }
    }

    public static void UpdateProxy(PlanetHandles h, Proxy p, Vector3D center, double radius)
    {
        if (p == null) return;
        try
        {
            p.Root.GetType().GetMethod("UpdateTransform")?.Invoke(p.Root, new object[] { new WorldTransform(center) });

            float scale = (float)(radius / h.ProxyModelRadius);
            if (p.LastScale < 0 || Math.Abs(scale - p.LastScale) > p.LastScale * 1e-3f)
            {
                object data = Activator.CreateInstance(_scaleDataType);
                _scaleField.SetValue(data, scale);
                FindSetEntityCustomData(p.Model.GetType())?.MakeGenericMethod(_scaleDataType).Invoke(p.Model, new[] { data });
                p.LastScale = scale;
            }
        }
        catch (Exception e) { WarnOnce("proxy-update", $"proxy update failed for {h.Name}: {Inner(e)}"); }
    }

    public static void SetProxyVisible(Proxy p, bool visible)
    {
        if (p == null || p.Visible == visible) return;
        try
        {
            p.Model.GetType().GetMethod("SetRenderFlagsState")?.Invoke(p.Model, new[] { Enum.ToObject(_renderFlagsType, 0x1), (object)visible });
            p.Visible = visible;
        }
        catch (Exception e) { WarnOnce("proxy-vis", $"proxy visibility failed: {Inner(e)}"); }
    }

    public static void DisposeProxy(Proxy p)
    {
        if (p == null) return;
        try
        {
            p.Model?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(p.Model, null);
            p.Root?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(p.Root, null);
        }
        catch (Exception e) { WarnOnce("proxy-dispose", $"proxy dispose failed: {Inner(e)}"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// The planet's colonization-map visual. Private field on the server-side
    /// DiscoverablePlanetComponent; the public route, GetDiscoverable(ClientId), needs
    /// VRage.Multiplayer, which mod scripts do not reference.
    /// </summary>
    public static PrefabDefinition GetMapVisualPrefab(DiscoverablePlanetComponent discoverable)
    {
        try { return GetMember(discoverable, "_mapVisualPrefab") as PrefabDefinition; }
        catch (Exception e) { WarnOnce("mapvisual", $"map visual lookup failed: {Inner(e)}"); return null; }
    }

    /// <summary>
    /// The planet's actual gravity law, g(d) = g0 · (r0 / d)^p (GravityGeneratorComponent,
    /// CalculateGravitationalAccelerationMagnitude). g0 is public; r0 (AccelerationDistance) and
    /// p (FallOffPower) live in the private GravityGeneratorData, so this invokes the component's
    /// own protected [Serializer] into a fresh public object builder and reads them from there.
    /// </summary>
    public static bool TryGetGravityLaw(GravityGeneratorComponent gravity, out double g0, out double r0, out double falloff)
    {
        g0 = gravity?.GravitationalAcceleration ?? 0;
        r0 = 0;
        falloff = 0;
        if (gravity == null) return false;
        try
        {
            MethodInfo serialize = null;
            for (Type t = gravity.GetType(); t != null && serialize == null; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(AnyInstance))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "Serialize" && ps.Length == 1 && ps[0].ParameterType == typeof(GravityGeneratorObjectBuilder))
                    {
                        serialize = m;
                        break;
                    }
                }
            }
            if (serialize == null) { WarnOnce("gravity-law", "GravityGeneratorComponent.Serialize not found"); return false; }

            var ob = new GravityGeneratorObjectBuilder();
            serialize.Invoke(gravity, new object[] { ob });
            r0 = ob.AccelerationDistance;
            falloff = ob.FallOffPower;
            return r0 > 0;
        }
        catch (Exception e) { WarnOnce("gravity-law", $"gravity law read failed: {Inner(e)}"); return false; }
    }

    /// <summary>Property or field by name, any visibility, walking base types.</summary>
    private static object GetMember(object target, string name)
    {
        if (target == null) return null;
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name, AnyInstance);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
            FieldInfo f = t.GetField(name, AnyInstance);
            if (f != null) return f.GetValue(target);
        }
        return null;
    }

    private static void SetMember(object target, string name, object value)
    {
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, AnyInstance);
            if (f != null) { f.SetValue(target, value); return; }
            PropertyInfo p = t.GetProperty(name, AnyInstance);
            if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
        }
        throw new MissingMemberException(target.GetType().Name, name);
    }

    private static Type FindType(string assemblyName, string typeName)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name == assemblyName) return a.GetType(typeName);
        }
        return null;
    }

    private static string Inner(Exception e) => (e.InnerException ?? e).Message;

    private static void WarnOnce(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key)) return;
        }
        Log.Default?.Warning($"[ORBIT] {message}");
    }
}
