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

    /// <summary>
    /// WARM-UP before showing (2026-10-10, "often I see a low quality one" - the user): the terrain was frozen while hidden,
    /// its detail still centred where the camera last was (often a berth 2,000+ km away); shown at once, it rebuilt in view,
    /// coarse first. Warm: unfreeze it (_visible = true: the clipmap updates round the camera again) while its render roots
    /// stay deactivated - call every frame until <see cref="TerrainSettled"/>, keeping the proxy up, then
    /// <see cref="SetTerrainVisible"/>(true).
    /// </summary>
    public static bool WarmTerrain(PlanetHandles h)
    {
        if (h?.Terrain == null) return false;
        h.HideFramesLeft = 0;
        try
        {
            foreach (object clipmap in h.Clipmaps) SetMember(clipmap, "_visible", true);
        }
        catch { return false; }
        return ApplyTerrain(h, false, setFlag: false);   // (roots made since last frame: dark too)
    }

    /// <summary>
    /// The terrain has caught up since the warm-up began (warmFrame 0 starts the count): every clipmap has been through one
    /// whole rebuild round the camera (VoxelClipmap._updateState left Idle and came back to it: a batch done), or had
    /// nothing to rebuild (Idle all along, for 20 frames). Waiting for "idle with nothing pending" never ended for a moving
    /// camera - an orbit re-centres the clipmap continually (it always hit the cap). Unknown (reflection failed): true.
    /// </summary>
    public static bool TerrainSettled(PlanetHandles h, int warmFrame)
    {
        if (h?.Terrain == null) return true;
        try
        {
            if (warmFrame <= 0 || h.WarmBusy == null) { h.WarmBusy = new Dictionary<object, int>(ReferenceEqualityComparer.Instance); }
            bool all = true;
            foreach (object clipmap in h.Clipmaps)
            {
                object st = GetMember(clipmap, "_updateState");
                if (st == null) return true;
                bool idle = st.ToString() == "Idle";
                h.WarmBusy.TryGetValue(clipmap, out int phase);   // 0 not seen busy, 1 busy, 2 a rebuild done
                if (!idle && phase == 0) phase = 1;
                else if (idle && phase == 1) phase = 2;
                h.WarmBusy[clipmap] = phase;
                if (phase != 2 && !(phase == 0 && idle && warmFrame >= 20)) all = false;
            }
            return all;
        }
        catch { return true; }
    }

    /// <summary>The warm-up's state, for the log: per clipmap its phase (done / busy and its state / idle all along).</summary>
    public static string WarmReport(PlanetHandles h)
    {
        if (h?.Terrain == null || h.WarmBusy == null) return "-";
        int done = 0, idle = 0; var busy = new List<string>();
        foreach (object clipmap in h.Clipmaps)
        {
            h.WarmBusy.TryGetValue(clipmap, out int phase);
            if (phase == 2) done++;
            else if (phase == 0) idle++;
            else busy.Add(GetMember(clipmap, "_updateState")?.ToString() ?? "?");
        }
        return $"{done} rebuilt, {idle} idle throughout, {busy.Count} still busy{(busy.Count > 0 ? " (" + string.Join(", ", busy) + ")" : "")}";
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
    /// A globe from the planet's map-visual model, or its hi-res globe while <see cref="PlanetMesh.Enabled"/>
    /// and one can be built. mapOnly: EntityType.Map, drawn only while the renderer is in map mode
    /// (RenderSystem.SetDraw3DMap, which the game's map turns on): the orbital map's globes.
    /// </summary>
    public static Proxy CreateProxy(PlanetHandles h, Vector3D center, bool mapOnly)
    {
        if (h == null || !h.HasProxyModel || !ResolveRender()) return null;
        try
        {
            object root = _createRoot.Invoke(_contracts, new object[] { "OrbitalProxyRoot_" + h.Name, new WorldTransform(center), true });

            // Visible | SkipCulling | SkipFarPlaneCulling | ForceHighestLOD, as a Default (world-view) entity.
            // SkipCulling: the globe is scaled in its shader (ScaleCustomData), so the renderer culls it by
            // the model's own small bounds; scaled up, a planet vanished once its centre left the view.
            object flags = Enum.ToObject(_renderFlagsType, 0x1 | 0x2 | 0x10 | 0x20);
            object entityType = mapOnly ? Enum.Parse(_entityTypeType, "Map") : Enum.ToObject(_entityTypeType, 0);
            PlanetMesh.Built mesh = PlanetMesh.Get(h);
            object model = _createModel.Invoke(_contracts, new object[]
            {
                (mapOnly ? "OrbitalMapGlobe_" : "OrbitalProxy_") + h.Name, mesh != null ? mesh.Handle : h.ProxyModel, RelativeTransform.Identity, root, flags, entityType, null
            });
            PlanetMesh.Acquire(mesh);
            PlanetMesh.SetShown(mesh, true);   // (made visible)

            return new Proxy
            {
                Root = root, Model = model, Visible = true, MapOnly = mapOnly,
                Mesh = mesh, ModelRadius = mesh != null ? 1.0 : h.ProxyModelRadius, MeshGeneration = PlanetMesh.Generation,
            };
        }
        catch (Exception e) { WarnOnce("proxy-create", $"proxy create failed for {h.Name}: {Inner(e)}"); return null; }
    }

    // (UpdateProxy runs for every proxy every frame - the star's, the planets', the map's globes: its argument array and the
    //  transform's box are kept per thread and the box's value written in place (Invoke copies it out for the call and keeps
    //  nothing); a new array and box each call before, 2026-10-14)
    [ThreadStatic] static object[] _xfArgs;
    static object[] TransformArgs(WorldTransform xf)
    {
        var a = _xfArgs ??= new object[1];
        if (a[0] is WorldTransform) System.Runtime.CompilerServices.Unsafe.Unbox<WorldTransform>(a[0]) = xf;
        else a[0] = xf;
        return a;
    }

    public static void UpdateProxy(PlanetHandles h, Proxy p, Vector3D center, double radius, Quaternion? orient = null)
    {
        if (p == null) return;
        if (!GridMembers.Finite(center) || double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0) return;   // never a NaN transform or scale to the renderer
        try
        {
            _rootUpdateTransform ??= p.Root.GetType().GetMethod("UpdateTransform");
            // (turned as the planet is in the viewer's axes: its surface's axes through its spin, into the observer's window)
            _rootUpdateTransform?.Invoke(p.Root, TransformArgs(orient.HasValue ? new WorldTransform(center, orient.Value) : new WorldTransform(center)));

            if (p.MeshGeneration != PlanetMesh.Generation) SwapProxyModel(h, p);
            float scale = (float)(radius / p.ModelRadius);
            if (p.LastScale < 0 || Math.Abs(scale - p.LastScale) > p.LastScale * 1e-3f)
            {
                object data = Activator.CreateInstance(_scaleDataType);
                _scaleField.SetValue(data, scale);
                FindSetEntityCustomData(p.Model.GetType())?.MakeGenericMethod(_scaleDataType).Invoke(p.Model, new[] { data });
                p.LastScale = scale;
            }
            // World proxies only: on the map the globes' atmospheres hid Delfos's star model.
            if (ProxyAtmospheres && p.Visible && !p.MapOnly) UpdateAtmosphere(h, p, center, radius);
            else if (!ProxyAtmospheres && (p.Atmo != null || p.AtmoNext != null || p.AtmoRetiring.Count > 0)) DisposeAtmosphere(p);   // (switched off: none left behind)
        }
        catch (Exception e) { WarnOnce("proxy-update", $"proxy update failed for {h.Name}: {Inner(e)}"); }
    }

    /// <summary>
    /// The hi-res globe was switched or retuned (<see cref="PlanetMesh.Generation"/>): put the current
    /// model into this proxy's entity (ModelEntity.UpdateModel) and resend its scale.
    /// </summary>
    static void SwapProxyModel(PlanetHandles h, Proxy p)
    {
        p.MeshGeneration = PlanetMesh.Generation;
        PlanetMesh.Built mesh = PlanetMesh.Get(h);
        if (ReferenceEquals(mesh, p.Mesh)) return;
        try
        {
            var update = p.Model.GetType().GetMethod("UpdateModel", new[] { typeof(ResourceHandle) });
            if (update == null) { WarnOnce("proxy-swap", "ModelEntity.UpdateModel not found"); return; }
            update.Invoke(p.Model, new object[] { mesh != null ? mesh.Handle : h.ProxyModel });
            PlanetMesh.Acquire(mesh);
            if (p.Visible) { PlanetMesh.SetShown(mesh, true); PlanetMesh.SetShown(p.Mesh, false); }
            PlanetMesh.Release(p.Mesh);
            p.Mesh = mesh;
            p.ModelRadius = mesh != null ? 1.0 : h.ProxyModelRadius;
            p.LastScale = -1f;
        }
        catch (Exception e) { WarnOnce("proxy-swap", $"proxy model swap failed for {h.Name}: {Inner(e)}"); }
    }

    // ── proxy atmospheres ──
    // The game's own atmosphere (RenderContracts.CreatePlanetEnvironmentEntity, as
    // PlanetEnvironmentRenderComponent makes a planet's), with the real planet's definition and its
    // radii scaled by the proxy's size (the definition's heights are fractions of the radius). Its
    // position is fixed at creation: it is made again as soon as the proxy has moved; a size
    // change goes through SetParameters. Any failure turns them off for the session.
    public static bool ProxyAtmospheres = true;
    /// <summary>The proxy atmosphere's inner sphere (the radius with the highest hills) laid on the globe's surface.</summary>
    public static bool AtmoOnHills = false;
    /// <summary>The proxy atmosphere spherized as the planet's (its shell pulled down to the ground at a distance).</summary>
    public static bool AtmoSpherize = true;

    /// <summary>A SpherizationData (boxed) with its distances (start, range) scaled by k; a default one when there is none.</summary>
    static object Sphere(object src, double k, Type t)
    {
        if (src == null) return Activator.CreateInstance(t);
        var copy = Activator.CreateInstance(t);   // (a fresh box: the planet's own is never changed)
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object v = f.GetValue(src);
            if (f.FieldType == typeof(float) && (f.Name.Contains("Start") || f.Name.Contains("Range"))) v = (float)((float)v * k);
            f.SetValue(copy, v);
        }
        return copy;
    }

    /// <summary>A change of AtmoOnHills: the atmospheres made again.</summary>
    public static void ResetAtmospheres() { _atmoGen++; }
    /// <summary>The camera this frame (FrameHost.Tick). (SetParent on a planet environment entity crashed the render thread,
    /// 2026-10-13 - KeyNotFoundException ChildComponent in SceneManager.IChildEntity_SetParent: not used; see the reflection doc.)</summary>
    public static Vector3D CameraPos;
    public static int AtmoRemakes;
    /// <summary>(diagnostics, harness aeroget OrbitalMod.PlanetRenderBridge.AtmoWhy: per proxy, the remakes started in the
    /// last second and the last reason)</summary>
    public static string AtmoWhy = "-";
    static readonly Dictionary<string, (int n, string why)> _atmoWhy = new();
    static double _atmoWhyAt;
    static void AtmoDiag(string name, string why)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_atmoWhy)
        {
            if (now - _atmoWhyAt > 1.0)
            {
                AtmoWhy = string.Join(" | ", _atmoWhy.Select(kv => $"{kv.Key}: {kv.Value.n}/s, {kv.Value.why}"));
                _atmoWhy.Clear(); _atmoWhyAt = now;
            }
            _atmoWhy.TryGetValue(name ?? "?", out var c);
            _atmoWhy[name ?? "?"] = (c.n + 1, why);
        }
    }
    static int _atmoGen;
    private static MethodInfo _createEnv;

    static Type Plain(ParameterInfo pi) => pi.ParameterType.IsByRef ? pi.ParameterType.GetElementType() : pi.ParameterType;

    static void UpdateAtmosphere(PlanetHandles h, Proxy p, Vector3D center, double radius)
    {
        if (h.EnvShowArgs == null || h.EnvShowArgs[0] == null || !(h.SurfaceRadius > 0)) return;
        // The atmosphere's inner sphere is the radius WITH the highest hills: scaled by the surface radius it floated above
        // the smooth globe's edge - a band of ground-level air there, a thick bright ring. AtmoOnHills: its inner sphere on
        // the globe instead (dev switch while compared).
        double inner = AtmoOnHills && h.EnvShowArgs[2] != null ? Convert.ToDouble(h.EnvShowArgs[2]) : h.SurfaceRadius;
        double k = radius / (inner > 0 ? inner : h.SurfaceRadius);
        if (!(k > 0) || double.IsInfinity(k)) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        try
        {
            p.AtmoUpdates++;
            // the handover's later stages: the retired ones given back when due (alongside whatever else happens - a new
            // handover may start meanwhile, so the atmosphere never waits on a retirement to follow its globe), and a staged
            // successor promoted a frame and AtmoStageSeconds after it was made
            if (p.AtmoRetiring.Count > 0) RetireAtmospheres(p, now);
            if (p.AtmoNext != null) { PromoteAtmosphere(h, p, now); return; }
            // Remade as soon as the proxy has moved VISIBLY from where its atmosphere was made: 0.2% of its radius, or
            // AtmoMovePixels on screen at the camera's distance, whichever is more (a throttle in time made it trail the globe
            // in warp; the radius alone remade a far globe - Kemik 2000 km off, swept by the planet's spin - 15 times a
            // second for shifts of 1/40 px, 2026-10-14)
            double camDist = (center - CameraPos).Length();
            double pxRad = 2.0 * Math.Tan(AtmoFovDeg * Math.PI / 360.0) / Math.Max(480.0, MapPipeline.ScreenSize.Y);   // (rad per pixel)
            double tol = Math.Max(radius * 0.002, camDist * pxRad * AtmoMovePixels);
            bool moved = p.Atmo != null && ((center - p.AtmoAt).Length() > tol || p.AtmoGen != _atmoGen);
            if (p.Atmo == null || moved)
            {
                AtmoDiag(h.Name, p.Atmo == null ? "none" : p.AtmoGen != _atmoGen ? "generation" : $"moved {(center - p.AtmoAt).Length():F0} m of r {radius:F0} m (k {k:F3})");
                // STAGE 1: the successor made where the globe is now, with NO atmosphere and its spherize radius a hair smaller
                // than the current's - it joins the renderer's planet list (at the end) and, sorted, lands right behind the
                // current one: this frame draws exactly as the last. WHY in two stages (2026-10-13, the user: the sky flickering
                // close to a planet in orbit; the cause found in the renderer by two independent reviews):
                // SettingsGroup.CreateFrameSettings takes the skybox brightness from PlanetEnvironmentManager.SortedPlanets[0]
                // BEFORE PlanetEnvironmentGroup sorts that list for the frame; AddPlanet appends and RemovePlanet removes, so a
                // remake in one frame (dispose + create) left another planet's environment first for that frame - the sky
                // undimmed (multiplier 1 against ~0.24 at 13 km) for a frame, 6-8 times a second where the proxy drifts fast.
                // (Since 2026-10-11 the proxy carries the planet's skybox spherization: before, the multiplier was 1 anyway.)
                if (_createEnv == null)
                    foreach (var m in _contracts.GetType().GetMethods())
                        if (m.Name == "CreatePlanetEnvironmentEntity" && m.GetParameters().Length == 12) _createEnv = m;
                if (_createEnv == null) { DisposeAtmosphere(p); ProxyAtmospheres = false; WarnOnce("proxy-atmo", "no CreatePlanetEnvironmentEntity: proxy atmospheres off"); return; }
                var ps = _createEnv.GetParameters();
                float F(object o) => (float)(Convert.ToDouble(o) * k);
                p.AtmoNext = _createEnv.Invoke(_contracts, new object[]
                {
                    "OrbitalProxyAtmosphere_" + h.Name,
                    null,                                         // no atmosphere yet (stage 2 gives it)
                    F(h.EnvShowArgs[1]), F(h.EnvShowArgs[2]),
                    null,                                         // no clouds
                    // the planet's spherization, its distances scaled as the proxy is (AtmoSpherize: the atmosphere pulled
                    // down to the ground at a distance as the real one is; off: none - the proxy is already a sphere)
                    AtmoSpherize ? Sphere(h.EnvShowArgs[4], k, Plain(ps[5])) : Activator.CreateInstance(Plain(ps[5])),
                    AtmoSpherize ? Sphere(h.AtmoSphere, k, Plain(ps[6])) : Activator.CreateInstance(Plain(ps[6])),
                    AtmoSpherize ? Sphere(h.SkySphere, k, Plain(ps[7])) : Activator.CreateInstance(Plain(ps[7])),
                    F(h.EnvShowArgs[5]) * AtmoStageShrink, center, null,
                    Array.CreateInstance(Plain(ps[11]).GetElementType(), 0),
                });
                p.AtmoNextAt = center; p.AtmoNextK = k; p.AtmoNextFrame = p.AtmoUpdates; p.AtmoNextWall = now; p.AtmoNextGen = _atmoGen;
                if (!AtmoStaged)
                {
                    // (A/B: the old one-frame swap - made, given its atmosphere, the old disposed, all in this frame)
                    SetEnvParameters(h, p.AtmoNext, k, 1f, true);
                    DisposeEnv(p.Atmo);
                    p.Atmo = p.AtmoNext; p.AtmoNext = null;
                    p.AtmoAt = center; p.AtmoK = k; p.AtmoMadeAt = now; p.AtmoGen = _atmoGen;
                    AtmoRemakes++;
                }
                return;
            }
            if (Math.Abs(k - p.AtmoK) > p.AtmoK * 0.01) { SetEnvParameters(h, p.Atmo, k, 1f, true); p.AtmoK = k; }
        }
        catch (Exception e)
        {
            ProxyAtmospheres = false;
            DisposeAtmosphere(p);
            WarnOnce("proxy-atmo", $"proxy atmosphere failed for {h.Name} (off for this session): {Inner(e)}");
        }
    }

    /// <summary>A proxy atmosphere's parameters at scale k (PlanetEnvironmentEntity.SetParameters, 7 parameters): the planet's
    /// atmosphere, radii and spherization scaled as its globe; the spherize radius x shrink.</summary>
    static void SetEnvParameters(PlanetHandles h, object env, double k, float shrink, bool air)
    {
        _setEnv ??= env.GetType().GetMethod("SetParameters");
        var sp = _setEnv?.GetParameters();
        if (_setEnv == null || sp.Length != 7) throw new InvalidOperationException("PlanetEnvironmentEntity.SetParameters(7) not found");
        float F(object o) => (float)(Convert.ToDouble(o) * k);
        _setEnv.Invoke(env, new object[] { air ? h.EnvShowArgs[0] : null, F(h.EnvShowArgs[1]), F(h.EnvShowArgs[2]), null,
            AtmoSpherize ? Sphere(h.EnvShowArgs[4], k, Plain(sp[4])) : Activator.CreateInstance(Plain(sp[4])), F(h.EnvShowArgs[5]) * shrink, null });
    }
    static MethodInfo _setEnv;

    /// <summary>STAGE 2: the successor given its real atmosphere at its real spherize radius, and the current one stripped of
    /// its air but kept (its spherize radius and skybox spherization unchanged - SetParameters touches neither the skybox
    /// spherization nor the position): whichever of the two the renderer's stale list has first when it takes the sky's
    /// brightness, the brightness is that of an environment where the globe is. Stage 3 waits AtmoRetireSeconds, so only a
    /// renderer that far behind (replaying all three stages before one sort) could still put another planet first.</summary>
    static void PromoteAtmosphere(PlanetHandles h, Proxy p, double now)
    {
        if (p.AtmoUpdates <= p.AtmoNextFrame || now - p.AtmoNextWall < AtmoStageSeconds) return;
        SetEnvParameters(h, p.AtmoNext, p.AtmoNextK, 1f, true);
        if (p.Atmo != null) { SetEnvParameters(h, p.Atmo, p.AtmoK, 1f, false); p.AtmoRetiring.Add((p.Atmo, p.AtmoUpdates, now)); }
        p.Atmo = p.AtmoNext; p.AtmoNext = null;
        p.AtmoAt = p.AtmoNextAt; p.AtmoK = p.AtmoNextK; p.AtmoMadeAt = now; p.AtmoGen = p.AtmoNextGen;
        AtmoRemakes++;
    }

    /// <summary>STAGE 3: the stripped old ones given back, each a frame and AtmoRetireSeconds after its stage 2.</summary>
    static void RetireAtmospheres(Proxy p, double now)
    {
        for (int i = p.AtmoRetiring.Count - 1; i >= 0; i--)
        {
            var r = p.AtmoRetiring[i];
            if (p.AtmoUpdates <= r.upd || now - r.wall < AtmoRetireSeconds) continue;
            DisposeEnv(r.env); p.AtmoRetiring.RemoveAt(i);
        }
    }
    /// <summary>The least wall time from stage 1 to stage 2 (more than a frame the renderer could be behind by: it replays
    /// queued game frames back to back with no sort between), and from stage 2 to stage 3.</summary>
    public static double AtmoStageSeconds = 0.05, AtmoRetireSeconds = 0.25;
    /// <summary>A proxy atmosphere's offset from its globe is remade past this many pixels at the camera's distance (with
    /// AtmoFovDeg, the vertical field of view assumed) - or 0.2% of the radius, whichever is more.</summary>
    public static double AtmoMovePixels = 0.5, AtmoFovDeg = 70;
    /// <summary>The staged handover (off: the old one-frame swap, for A/B - it flashes the sky near a planet).</summary>
    public static bool AtmoStaged = true;
    /// <summary>The staged successor's spherize radius against the current's: just behind it in the renderer's sort (distance
    /// minus spherize radius) - more than a remake's drift (0.2% of the radius), small enough to change nothing seen.</summary>
    public static float AtmoStageShrink = 0.99f;

    static void DisposeEnv(object env)
    {
        if (env == null) return;
        try { env.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(env, null); } catch { }
    }

    static void DisposeAtmosphere(Proxy p)
    {
        if (p == null) return;
        DisposeEnv(p.Atmo); p.Atmo = null;
        DisposeEnv(p.AtmoNext); p.AtmoNext = null;
        foreach (var r in p.AtmoRetiring) DisposeEnv(r.env);
        p.AtmoRetiring.Clear();
    }

    public static void SetProxyVisible(Proxy p, bool visible)
    {
        if (p == null || p.Visible == visible) return;
        if (!visible) DisposeAtmosphere(p);   // never two atmospheres (the real planet's is back)
        try
        {
            p.Model.GetType().GetMethod("SetRenderFlagsState")?.Invoke(p.Model, new[] { Enum.ToObject(_renderFlagsType, 0x1), (object)visible });
            p.Visible = visible;
            PlanetMesh.SetShown(p.Mesh, visible);
        }
        catch (Exception e) { WarnOnce("proxy-vis", $"proxy visibility failed: {Inner(e)}"); }
    }

    public static void DisposeProxy(Proxy p)
    {
        if (p == null) return;
        DisposeAtmosphere(p);
        try
        {
            p.Model?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(p.Model, null);
            p.Root?.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(p.Root, null);
        }
        catch (Exception e) { WarnOnce("proxy-dispose", $"proxy dispose failed: {Inner(e)}"); }
        if (p.Visible) PlanetMesh.SetShown(p.Mesh, false);
        PlanetMesh.Release(p.Mesh);
        p.Mesh = null;
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
            if (st == null)
            {
                // not in this session: every recorder registers with the engine's StatsRecorderEngineComponent
                object engine = typeof(Keen.VRage.Core.VRageCore).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                                ?.GetType().GetProperty("Engine")?.GetValue(typeof(Keen.VRage.Core.VRageCore).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null));
                Type te = render.GetType("Keen.VRage.Render.EngineComponents.StatsRecorderEngineComponent");
                object ec = null;
                if (engine != null && te != null)
                    foreach (var m in engine.GetType().GetMethods())
                        if (m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                        {
                            try { ec = m.MakeGenericMethod(te).Invoke(engine, null); } catch (Exception) { ec = null; }
                            if (ec != null) break;
                        }
                if (ec == null) return "no stats (engine " + (engine == null ? "null" : engine.GetType().Name) + ", recorder component not found)";
                var list = ec?.GetType().GetField("_statsRecorders", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ec) as System.Collections.IEnumerable;
                int nrec = 0;
                if (list != null) foreach (var r in list) { nrec++; st = r?.GetType().GetProperty("ImmediateStats")?.GetValue(r); if (st != null) break; }
                if (st == null) return $"no stats ({nrec} recorders registered)";
            }
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

    /// <summary>DEV: call a private parameterless method (the game's own input handlers).</summary>
    public static bool CallPrivate(object target, string method)
    {
        try
        {
            var m = target?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (m == null) return false;
            m.Invoke(target, null);
            return true;
        }
        catch (Exception e) { WarnOnce("call-" + method, method + " failed: " + Inner(e)); return false; }
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

    /// <summary>
    /// Put the colonization map's zoom back inside [min, max] and stop its zoom easing. Its easing only
    /// ends when it reaches its target; a target outside the (restored) limits never is, and the easing
    /// then ran on after the map closed: re-showing the map's planet labels in flight and moving its
    /// camera controller.
    /// </summary>
    private static Type _mapCd; private static object _mapData; private static MethodInfo _mapGet; private static object _mapFor;
    /// <summary>The map camera's own state: its focus offset from the map entity, and its distance.</summary>
    public static bool TryGetMapCamera(object map, out Vector3 focusOffset, out float distance)
    {
        focusOffset = default; distance = 0;
        try
        {
            if (!ReferenceEquals(map, _mapFor))
            {
                Type mt = map.GetType();
                _mapCd = mt.GetNestedType("CameraData", BindingFlags.NonPublic | BindingFlags.Public);
                _mapData = mt.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(map);
                _mapGet = null;
                foreach (var m in _mapData?.GetType().GetMethods() ?? new MethodInfo[0])
                    if (m.IsGenericMethodDefinition && m.Name == "Get" && m.GetParameters().Length == 0) { _mapGet = m.MakeGenericMethod(_mapCd); break; }
                _mapFor = map;
            }
            if (_mapGet == null) return false;
            object v = _mapGet.Invoke(_mapData, null);
            focusOffset = (Vector3)_mapCd.GetField("FocusPointOffset").GetValue(v);
            distance = (float)_mapCd.GetField("Distance").GetValue(v);
            return distance > 0 && distance < 1e6f;
        }
        catch { _mapFor = null; return false; }
    }

    public static bool SettleMapZoom(object map, float min, float max)
    {
        try
        {
            Type mt = map.GetType();
            Type cd = mt.GetNestedType("CameraData", BindingFlags.NonPublic | BindingFlags.Public);
            Type tag = mt.GetNestedType("CameraNeedsUpdateTag", BindingFlags.NonPublic | BindingFlags.Public);
            object data = mt.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(map);
            if (cd == null || tag == null || data == null) return false;
            MethodInfo get = null, set = null, remove = null;
            foreach (var m in data.GetType().GetMethods())
            {
                if (!m.IsGenericMethodDefinition) continue;
                if (m.Name == "Get" && m.GetParameters().Length == 0) get = m;
                if (m.Name == "Set" && m.GetParameters().Length == 1) set = m;
                if (m.Name == "TryRemove" && m.GetParameters().Length == 0) remove = m;
            }
            if (get == null || set == null) return false;
            object v = get.MakeGenericMethod(cd).Invoke(data, null);
            var fd = cd.GetField("Distance"); var ft = cd.GetField("TargetDistance");
            float d = Math.Clamp((float)fd.GetValue(v), min, max);
            fd.SetValue(v, d); ft.SetValue(v, d);
            set.MakeGenericMethod(cd).Invoke(data, new[] { v });
            remove?.MakeGenericMethod(tag).Invoke(data, null);
            return true;
        }
        catch (Exception e) { WarnOnce("map-settle", "map zoom settle failed: " + Inner(e)); return false; }
    }

    /// <summary>
    /// DEV HARNESS: an ENGINE screenshot (RenderContracts.GetMainTarget().TakeScreenshotAsync), the
    /// path the blueprint tool uses. Rendered by the engine into the game's temp folder, so it works
    /// when desktop capture (GDI CopyFromScreen) fails because the display is asleep.
    /// Returns the absolute path it will be written to, or null.
    /// </summary>
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
    /// <summary>DEV: read (value null) or set a static field or property of another mod's type (bool, int, float,
    /// double, string), e.g. the aero mod's tuning knobs and counters.</summary>
    public static string ForeignValue(string typeName, string member, string value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FieldInfo f = t.GetField(member, S); PropertyInfo p = f == null ? t.GetProperty(member, S) : null;
                Type ft = f?.FieldType ?? p?.PropertyType;
                if (ft == null) return $"{typeName}.{member}: no such static";
                if (value != null)
                {
                    object v = ft == typeof(bool) ? (object)(value == "on" || value == "true" || value == "1")
                             : ft == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(float) ? float.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(double) ? double.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(string) ? value : null;
                    if (v == null) return $"{typeName}.{member}: {ft.Name} not settable";
                    if (f != null) f.SetValue(null, v); else p.SetValue(null, v);
                }
                object now = f != null ? f.GetValue(null) : p.GetValue(null);
                return $"{typeName}.{member}={Convert.ToString(now, CultureInfo.InvariantCulture)}";
            }
            return $"{typeName} not loaded";
        }
        catch (Exception e) { return "failed: " + Inner(e); }
    }

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

    /// <summary>DEV HARNESS: as SetForeignFlag, a static string field (null clears it).</summary>
    public static string SetForeignString(string typeName, string field, string value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                FieldInfo f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(string)) return $"{typeName}.{field} not a static string";
                f.SetValue(null, value);
                return $"{typeName}.{field}={value ?? "null"} (in {a.GetName().Name})";
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
        if (!GridMembers.Finite(target.Position)) return false;
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
    private static readonly Dictionary<object, float> _origScale = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);

    public static void ScaleMapObject(object mapObject, float multiplier)
    {
        if (mapObject == null) return;
        try
        {
            // Only on a live model entity (UpdateScale sends SetEntityCustomData unchecked; a dead or
            // non-model id is fatal on the render thread, as the star's was).
            object model = GetMember(mapObject, "RenderModelEntity");
            if (model == null || !(GetMember(model, "IsValid") is bool ok) || !ok || model.GetType().Name != "ModelEntity") return;
            // Its own scale, remembered before the first change (UpdateScale overwrites _scale, so reading
            // it back to restore re-applied the tiny hide scale).
            if (!_origScale.ContainsKey(mapObject)) _origScale[mapObject] = GetMember(mapObject, "_scale") is float s0 && s0 > 0 ? s0 : 1f;
            float m = multiplier > 0 ? multiplier : _origScale[mapObject];
            if (!(m > 0) || float.IsInfinity(m)) return;
            mapObject.GetType().GetMethod("UpdateScale")?.Invoke(mapObject, new object[] { m });
        }
        catch (Exception e) { WarnOnce("mapobj-scale", $"map object scale failed: {Inner(e)}"); }
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
