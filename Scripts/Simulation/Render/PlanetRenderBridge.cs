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

    /// <summary>Frames to keep re-queuing hide transitions before freezing the clipmap.</summary>
    public const int HideSettleFrames = 20;

    /// <summary>
    /// Hide or show a planet's terrain, for BOTH the detailed and the low-res clipmap.
    ///
    /// Cell visibility is a queued transition (VoxelCell.SetVisible -> _batchedCellUpdates) that is
    /// only COMMITTED by VoxelClipmap.Update -> EndBatch -> RecordBatchCommit. Update returns early
    /// when clipmap._visible is false. So the stock Visible setter (and flipping _visible first)
    /// never commits the hide for cells that already exist; it only "works" for a planet whose cells
    /// were never built. Verified in game: at 88 km the atmosphere vanished and the terrain stayed.
    ///
    /// Hide therefore takes several frames: queue hides with _visible still true, re-queue for
    /// <see cref="HideSettleFrames"/> frames (catching cells streamed in meanwhile), then set
    /// _visible = false to freeze the clipmap (and have future cells created hidden). Call
    /// <see cref="TickTerrain"/> every frame. Show is immediate: _visible = true, then queue shows.
    /// Tolerates cells that are not attached yet (the stock setter throws on those).
    /// Returns false on any failure so the caller retries next frame.
    /// </summary>
    public static bool SetTerrainVisible(PlanetHandles h, bool visible)
    {
        if (h == null) return false;
        h.HideFramesLeft = visible ? 0 : HideSettleFrames;
        return ApplyTerrain(h, visible, setFlag: visible);
    }

    /// <summary>Advance a pending hide. Cheap no-op when nothing is pending.</summary>
    public static void TickTerrain(PlanetHandles h)
    {
        if (h == null || h.HideFramesLeft <= 0) return;
        h.HideFramesLeft--;
        // Last frame: queue once more AND freeze.
        ApplyTerrain(h, false, setFlag: h.HideFramesLeft == 0);
    }

    private static bool ApplyTerrain(PlanetHandles h, bool visible, bool setFlag)
    {
        if (h?.Terrain == null) return false;
        try
        {
            foreach (object clipmap in h.Clipmaps)
            {
                if (setFlag || visible) SetMember(clipmap, "_visible", visible);

                // Every cell mesh hangs off a render RootEntity from the clipmap's
                // DistributedRootEntityProvider (one root per 1 km block, created on demand under
                // lock(_rootEntities)). Deactivating a root hides everything under it immediately,
                // with no dependence on the clipmap's batch commit. Take the SAME lock Keen takes.
                object provider = GetMember(clipmap, "RootEntityProvider");
                if (!(GetMember(provider, "_rootEntities") is IDictionary roots)) continue;
                var snapshot = new List<object>();
                lock (roots)
                {
                    foreach (object root in roots.Values) snapshot.Add(root);
                }
                foreach (object root in snapshot)
                {
                    if (GetMember(root, "IsValid") is bool ok && !ok) continue;
                    RootMethod(root.GetType(), visible)?.Invoke(root, null);
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

    private static MethodInfo _rootActivate, _rootDeactivate, _rootUpdateTransform;

    private static MethodInfo RootMethod(Type rootType, bool activate)
    {
        _rootActivate ??= rootType.GetMethod("Activate", Type.EmptyTypes);
        _rootDeactivate ??= rootType.GetMethod("Deactivate", Type.EmptyTypes);
        return activate ? _rootActivate : _rootDeactivate;
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

    public static Proxy CreateProxy(PlanetHandles h, Vector3D center) => CreateProxy(h, center, false);

    /// <summary>
    /// A globe from the planet's map-visual model. mapOnly: EntityType.Map, drawn only while the
    /// renderer is in map mode (<see cref="SetDraw3DMap"/>): the orbital map's globes.
    /// </summary>
    public static Proxy CreateProxy(PlanetHandles h, Vector3D center, bool mapOnly)
    {
        if (h == null || !h.HasProxyModel || !ResolveRender()) return null;
        try
        {
            object root = _createRoot.Invoke(_contracts, new object[] { "OrbitalProxyRoot_" + h.Name, new WorldTransform(center), true });

            // Visible | SkipFarPlaneCulling | ForceHighestLOD, as a Default (world-view) entity.
            object flags = Enum.ToObject(_renderFlagsType, 0x1 | 0x10 | 0x20);
            object entityType = mapOnly ? Enum.Parse(_entityTypeType, "Map") : Enum.ToObject(_entityTypeType, 0);
            object model = _createModel.Invoke(_contracts, new object[]
            {
                (mapOnly ? "OrbitalMapGlobe_" : "OrbitalProxy_") + h.Name, h.ProxyModel, RelativeTransform.Identity, root, flags, entityType, null
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
            _rootUpdateTransform ??= p.Root.GetType().GetMethod("UpdateTransform");
            _rootUpdateTransform?.Invoke(p.Root, new object[] { new WorldTransform(center) });

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

    /// <summary>
    /// The renderer's map mode (RenderSystem.SetDraw3DMap): only EntityType.Map models are drawn,
    /// the world is hidden. What the strategic map uses; the orbital map uses it the same way.
    /// </summary>
    public static bool SetDraw3DMap(bool enable)
    {
        try
        {
            if (!ResolveRender()) return false;
            object rs = _contracts.GetType().GetMethod("GetRenderSystem", Type.EmptyTypes)?.Invoke(_contracts, null);
            var m = rs?.GetType().GetMethod("SetDraw3DMap");
            if (m == null) { WarnOnce("draw3dmap", "SetDraw3DMap not found"); return false; }
            m.Invoke(rs, new object[] { enable });
            return true;
        }
        catch (Exception e) { WarnOnce("draw3dmap2", $"SetDraw3DMap failed: {Inner(e)}"); return false; }
    }

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

    /// <summary>
    /// Rewrite a planet's gravity law at runtime: FallOffPower and AffectDistance in the private
    /// GravityGeneratorData (via the protected Component.GetData/SetData, the aero mod's pattern),
    /// then the public AffectDistance so the trigger volume follows. SERVER THREAD ONLY.
    /// Note: GravityGeneratorComponent's [Serializer] writes these values back to the object
    /// builder, so a world SAVED while patched keeps them.
    /// </summary>
    public static bool SetGravityLaw(GravityGeneratorComponent gravity, float falloff, float reach)
    {
        try
        {
            Type dataType = typeof(GravityGeneratorComponent).GetNestedType("GravityGeneratorData", BindingFlags.NonPublic);
            MethodInfo get = null, set = null;
            for (Type t = typeof(GravityGeneratorComponent); t != null && (get == null || set == null); t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(AnyInstance))
                {
                    if (!m.IsGenericMethodDefinition) continue;
                    if (m.Name == "GetData" && m.GetParameters().Length == 0) get = m;
                    if (m.Name == "SetData" && m.GetParameters().Length == 1) set = m;
                }
            }
            if (dataType == null || get == null || set == null)
            {
                WarnOnce("gravity-set", $"gravity data access missing (type={dataType != null} get={get != null} set={set != null})");
                return false;
            }

            object data = get.MakeGenericMethod(dataType).Invoke(gravity, null);
            dataType.GetField("FallOffPower").SetValue(data, falloff);
            dataType.GetField("AffectDistance").SetValue(data, reach);
            set.MakeGenericMethod(dataType).Invoke(gravity, new[] { data });

            gravity.AffectDistance = reach;
            return true;
        }
        catch (Exception e) { Log.Default?.Warning($"[ORBIT] gravity law write failed: {Inner(e)}"); return false; }
    }

    /// <summary>
    /// DEV HARNESS: an ENGINE screenshot (RenderContracts.GetMainTarget().TakeScreenshotAsync), the
    /// path the blueprint tool uses. Rendered by the engine into the game's temp folder, so it works
    /// when desktop capture (GDI CopyFromScreen) fails because the display is asleep.
    /// Returns the absolute path it will be written to, or null.
    /// </summary>
    /// <summary>Engine screenshots leave out the 2D UI (HUD, terminal); 3D labels stay.</summary>
    public static bool ShotWithoutUi = true;

    /// <summary>DEV: the render stats (ms, average/max over the current stats window): GPU, render thread, main thread.</summary>
    public static string FrameStats(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            Assembly render = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "VRage.Render") { render = a; break; }
            Type t = render?.GetType("Keen.VRage.Render.SessionComponents.StatsRecorderSessionComponent");
            if (t == null) return "no stats type";
            object comps = session.SessionComponents;
            MethodInfo tryGet = null;
            foreach (var m in comps.GetType().GetMethods()) if (m.Name == "TryGet" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { tryGet = m; break; }
            object rec = tryGet?.MakeGenericMethod(t).Invoke(comps, null);
            object st = rec?.GetType().GetProperty("ImmediateStats")?.GetValue(rec);
            if (st == null) return "no stats";
            string F(string field)
            {
                FieldInfo f = st.GetType().GetField(field);
                object v = f.GetValue(st);   // a boxed copy: Calculate works on the copy (shares the buffer)
                v.GetType().GetMethod("Calculate").Invoke(v, null);
                double avg = (double)v.GetType().GetProperty("Average").GetValue(v), max = (double)v.GetType().GetProperty("Max").GetValue(v);
                return $"{avg:F1}/{max:F0}";
            }
            return $"gpu {F("GpuTimeWork")} render {F("RenderThreadTimeWork")} main {F("MainThreadTime")} ms (avg/max)";
        }
        catch (Exception e) { return "stats: " + e.Message; }
    }

    /// <summary>
    /// Zoom the colonization map's own camera to a distance: its private CameraData.TargetDistance, and
    /// its CameraNeedsUpdateTag so it eases there itself (the same as its wheel zoom does). The wheel
    /// keeps working from there.
    /// </summary>
    public static bool SetMapTargetDistance(object map, float distance)
    {
        try
        {
            Type mt = map.GetType();
            Type cd = mt.GetNestedType("CameraData", BindingFlags.NonPublic | BindingFlags.Public);
            Type tag = mt.GetNestedType("CameraNeedsUpdateTag", BindingFlags.NonPublic | BindingFlags.Public);
            object data = mt.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(map);
            if (cd == null || tag == null || data == null) return false;
            MethodInfo get = null, set = null;
            foreach (var m in data.GetType().GetMethods())
            {
                if (!m.IsGenericMethodDefinition) continue;
                if (m.Name == "Get" && m.GetParameters().Length == 0) get = m;
                if (m.Name == "Set" && m.GetParameters().Length == 1) set = m;
            }
            if (get == null || set == null) return false;
            object v = get.MakeGenericMethod(cd).Invoke(data, null);
            cd.GetField("TargetDistance").SetValue(v, distance);
            set.MakeGenericMethod(cd).Invoke(data, new[] { v });
            set.MakeGenericMethod(tag).Invoke(data, new[] { Activator.CreateInstance(tag) });
            return true;
        }
        catch (Exception e) { WarnOnce("map-zoom", "map zoom failed: " + Inner(e)); return false; }
    }

    public static string EngineScreenshot(string name)
    {
        try
        {
            if (!ResolveRender()) return null;
            object target = _contracts.GetType().GetMethod("GetMainTarget", Type.EmptyTypes)?.Invoke(_contracts, null);
            if (target == null) { WarnOnce("shot", "GetMainTarget unavailable"); return null; }
            var handle = Keen.VRage.Library.Filesystem.FileSystem.Temp.GetFileHandleWritable(name);
            MethodInfo take = null;
            foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "TakeScreenshotAsync" && m.GetParameters().Length == 5) { take = m; break; }
            if (take == null) { WarnOnce("shot2", "TakeScreenshotAsync not found"); return null; }
            take.Invoke(target, new object[] { handle, null, null, ShotWithoutUi, true });
            // GetAbsolutePath throws until the file exists; the temp root is the game's Temp folder.
            try { return handle.GetAbsolutePath(); }
            catch { return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "Temp", name); }
        }
        catch (Exception e) { Log.Default?.Warning($"[ORBIT] engine screenshot failed: {Inner(e)}"); return null; }
    }

    /// <summary>
    /// DEV HARNESS: switch another mod's static bool at runtime (e.g. AeroMod.AeroSpeedSpike.Enabled,
    /// the aero mod's in-progress experiment that holds every grid at 50 m/s and freezes the main
    /// thread). Runtime only; the other mod's files are untouched. Returns a description.
    /// </summary>
    public static string SetForeignFlag(string typeName, string field, bool value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                FieldInfo f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(bool)) return $"{typeName}.{field} not a static bool";
                f.SetValue(null, value);
                return $"{typeName}.{field}={value} (in {a.GetName().Name})";
            }
            return $"{typeName} not loaded";
        }
        catch (Exception e) { return "failed: " + Inner(e); }
    }

    private static MethodInfo _teleportPlayer;

    /// <summary>
    /// DEV HARNESS: Keen's admin EntityAdmin.TeleportPlayer(session, target, preferredCharacter,
    /// teleportIfInShip, clearMotion). Public and whitelisted, but it returns a NetworkStory from
    /// VRage.Multiplayer, which mod scripts do not reference (CS0012), so it is late-bound here.
    /// Invoking an async method starts its state machine; nothing needs to await the result.
    /// </summary>
    public static bool TeleportPlayer(Keen.VRage.Core.Game.Systems.Session session, WorldTransform target, bool clearMotion = true)
    {
        try
        {
            if (_teleportPlayer == null)
            {
                foreach (MethodInfo m in typeof(Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin)
                             .GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "TeleportPlayer" && m.GetParameters().Length == 5) { _teleportPlayer = m; break; }
                }
            }
            if (_teleportPlayer == null) { WarnOnce("tp", "EntityAdmin.TeleportPlayer not found"); return false; }
            _teleportPlayer.Invoke(null, new object[] { session, target, null, true, clearMotion });
            return true;
        }
        catch (Exception e) { Log.Default?.Warning($"[ORBIT] teleport failed: {Inner(e)}"); return false; }
    }

    /// <summary>Property or field by name, any visibility, walking base types.</summary>
    /// <summary>Call a one-argument bool method by name (for types in assemblies mods cannot reference).</summary>
    internal static bool CallBool(object target, string method, object arg)
    {
        foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != method || m.ReturnType != typeof(bool)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(arg)) return (bool)m.Invoke(target, new[] { arg });
        }
        return false;
    }

    /// <summary>Show or hide a game render component's model (its RenderModelEntity's Visible flag).</summary>
    public static void SetRenderComponentVisible(object renderComponent, bool visible)
    {
        if (renderComponent == null || !ResolveRender()) return;
        // Only on a change, and only on a valid model: the call is replayed on the render thread,
        // where a bad id is fatal (seen in game: a root entity behind a render component crashed it).
        if (_rcVisible.TryGetValue(renderComponent, out bool was) && was == visible) return;
        try
        {
            object model = GetMember(renderComponent, "RenderModelEntity");
            if (model == null || !(GetMember(model, "IsValid") is bool ok) || !ok) return;
            string modelType = model.GetType().Name;
            if (modelType != "ModelEntity") { WarnOnce("rc-type", "render component model is " + modelType + "; not toggled"); return; }
            model.GetType().GetMethod("SetRenderFlagsState")?.Invoke(model, new[] { Enum.ToObject(_renderFlagsType, 0x1), (object)visible });
            _rcVisible[renderComponent] = visible;
        }
        catch (Exception e) { WarnOnce("rc-vis", $"render component visibility failed: {Inner(e)}"); }
    }

    private static readonly Dictionary<object, bool> _rcVisible = new Dictionary<object, bool>();

    /// <summary>
    /// Scale a colonization-map object (MapObjectRenderComponent.UpdateScale, the engine's own path).
    /// multiplier &lt;= 0 restores its configured scale. Used to hide the game's globes safely.
    /// </summary>
    public static void ScaleMapObject(object mapObject, float multiplier)
    {
        if (mapObject == null) return;
        try
        {
            float m = multiplier > 0 ? multiplier : (GetMember(mapObject, "_scale") is float s ? s : 1f);
            mapObject.GetType().GetMethod("UpdateScale")?.Invoke(mapObject, new object[] { m });
        }
        catch (Exception e) { WarnOnce("mapobj-scale", $"map object scale failed: {Inner(e)}"); }
    }

    /// <summary>For the map pipeline: the render assembly, the RenderContracts instance, a type by name.</summary>
    public static Assembly RenderAssembly { get { ResolveRender(); return _renderAsm; } }
    public static object Contracts { get { ResolveRender(); return _contracts; } }
    public static Type RenderOrCoreType(string fullName)
    {
        ResolveRender();
        return _renderAsm?.GetType(fullName) ?? typeof(Keen.VRage.Core.Render.RenderRuntimeDataType).Assembly.GetType(fullName);
    }

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

    internal static object GetMember(object target, string name)
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

    internal static void SetMember(object target, string name, object value)
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
