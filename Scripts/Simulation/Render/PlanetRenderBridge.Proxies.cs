using System.Collections;
using System.Reflection;
using System.Globalization;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The proxy globes and their atmospheres.</summary>
public static partial class PlanetRenderBridge
{
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
            bool atmos = ProxyAtmospheres && ProxyAtmospheresWork;
            if (atmos && p.Visible && !p.MapOnly) UpdateAtmosphere(h, p, center, radius);
            else if (!atmos && (p.Atmo != null || p.AtmoNext != null || p.AtmoRetiring.Count > 0)) DisposeAtmosphere(p);   // (switched off: none left behind)
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
    /// <summary>Wanted: the setting Proxy.Atmospheres (the player's, saved). Never written by the code itself.</summary>
    public static bool ProxyAtmospheres = true;
    /// <summary>Working: false after a failure, for the session (2026-10-15: the failure used to clear ProxyAtmospheres
    /// itself - as a setting that would have saved "off" for good).</summary>
    public static bool ProxyAtmospheresWork = true;
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

    // SE2 4.0: CreatePlanetEnvironmentEntity takes one `in PlanetEnvironmentEntityBuildParameters` (a struct whose fields
    // are the old 12 parameters, plus FrameSettingsOverride, left null) instead of 12 parameters. The old order is kept as
    // the argument list here: EnvArgType(i) is the type of argument i, CreateEnvEntity builds the struct from the list.
    static readonly string[] EnvParamFields = { "DebugName", "AtmosphereDefinition", "AtmosphereRadius", "RadiusWithMaxHills", "CloudDefinition",
        "SphereData", "AtmosphereSpherizationData", "SkyboxSpherizationData", "SpherizeRadius", "PlanetCenter", "PlanetOverlayDefinition", "PreloadItems" };
    static FieldInfo[] _envFields;
    static bool ResolveCreateEnv()
    {
        if (_createEnv != null) return true;
        foreach (var m in _contracts.GetType().GetMethods())
        {
            if (m.Name != "CreatePlanetEnvironmentEntity") continue;
            var ps = m.GetParameters();
            if (ps.Length == 12) { _createEnv = m; return true; }
            if (ps.Length == 1 && Plain(ps[0]).IsValueType)
            {
                var t = Plain(ps[0]);
                var fs = new FieldInfo[EnvParamFields.Length];
                for (int i = 0; i < fs.Length; i++) { fs[i] = t.GetField(EnvParamFields[i]); if (fs[i] == null) return false; }
                _envFields = fs; _createEnv = m; return true;
            }
        }
        return false;
    }
    static Type EnvArgType(int i) => _envFields != null ? _envFields[i].FieldType : Plain(_createEnv.GetParameters()[i]);
    static object CreateEnvEntity(object[] args)
    {
        if (_envFields == null) return _createEnv.Invoke(_contracts, args);
        var t = Plain(_createEnv.GetParameters()[0]);
        object box = t.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(t);   // (its own defaults: no frame override, an empty preload list)
        for (int i = 0; i < args.Length; i++) _envFields[i].SetValue(box, args[i]);
        return _createEnv.Invoke(_contracts, new[] { box });
    }

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
                if (!ResolveCreateEnv()) { DisposeAtmosphere(p); ProxyAtmospheresWork = false; WarnOnce("proxy-atmo", "no CreatePlanetEnvironmentEntity: proxy atmospheres off"); return; }
                float F(object o) => (float)(Convert.ToDouble(o) * k);
                p.AtmoNext = CreateEnvEntity(new object[]
                {
                    "OrbitalProxyAtmosphere_" + h.Name,
                    null,                                         // no atmosphere yet (stage 2 gives it)
                    F(h.EnvShowArgs[1]), F(h.EnvShowArgs[2]),
                    null,                                         // no clouds
                    // the planet's spherization, its distances scaled as the proxy is (AtmoSpherize: the atmosphere pulled
                    // down to the ground at a distance as the real one is; off: none - the proxy is already a sphere)
                    AtmoSpherize ? Sphere(h.EnvShowArgs[4], k, EnvArgType(5)) : Activator.CreateInstance(EnvArgType(5)),
                    AtmoSpherize ? Sphere(h.AtmoSphere, k, EnvArgType(6)) : Activator.CreateInstance(EnvArgType(6)),
                    AtmoSpherize ? Sphere(h.SkySphere, k, EnvArgType(7)) : Activator.CreateInstance(EnvArgType(7)),
                    F(h.EnvShowArgs[5]) * AtmoStageShrink, center, null,
                    Array.CreateInstance(EnvArgType(11).GetElementType(), 0),
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
            ProxyAtmospheresWork = false;
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
}
