using System.Reflection;
using Keen.Game2.Client.GameSystems.Render;
using Keen.Game2.Simulation.GameSystems.ProceduralGeneration;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Materials;
using Keen.VRage.Core.Render.Materials.Templates;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Injected into the client composition of the procedural volume prefab (the entity that carries a
/// ProceduralVolumeRenderComponent: the planets' rings). Registers the ring with <see cref="PlanetRings"/>.
/// </summary>
public class OrbitalRingComponent : Component, IInSceneListener
{
    void IInSceneListener.OnAddedToScene()
    {
        try { PlanetRings.Register(this); }
        catch (Exception e) { Log.Default?.Warning("[ORBIT] ring register failed: " + PlanetRenderBridge.Inner(e)); }
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        try { PlanetRings.Unregister(this); }
        catch (Exception e) { Log.Default?.Warning("[ORBIT] ring unregister failed: " + PlanetRenderBridge.Inner(e)); }
    }
}

/// <summary>
/// The planets' rings next to their proxies.
///
/// The game draws a ring (ProceduralVolumeRenderComponent) as a volume at the REAL planet's place:
/// with the real planet hidden and a proxy drawn elsewhere, the ring floated beside a tiny globe.
///  - <see cref="HideGameRings"/>: the game's ring model is hidden (its Visible render flag, as the
///    proxies are) whenever its planet's real planet is hidden, and shown again with it.
///  - <see cref="ProxyRings"/>: our own copy of the ring at the proxy, built exactly as the game builds
///    its own (the same post-processed hull mesh, the same AsteroidRing material, a Volumes runtime
///    model, ProceduralFieldCustomData), everything scaled by k = proxy radius / real radius.
///
/// How the ring volume works (AsteroidRingVertex/Pixel.hlsl): outside the volume-processing pass the
/// vertex shader ignores the mesh and draws a box from the custom data (OuterRadius, Height); the
/// volume samples map radius and altitude to the ring texture through the custom data radii. So the
/// custom data carries the exact k (resent on any 0.1% change), while the hull mesh and the material
/// are rebuilt only when k leaves its 10% bucket: the hull is built for the bucket's extremes (outer
/// vertices at its top, inner ones at its bottom) so it always contains the ring.
/// Optics: the extinction coefficient is 3 / AbsorptionLength per metre, so a ring k times smaller
/// is k times thinner optically; AbsorptionLength is scaled by k and ScatteringMagnitude (log10 of
/// the scattering amplitude, also per metre) by -log10(k) to keep its look (<see cref="ScaleOptics"/>).
/// StartDistance (100 m) and FadeDistance (10 km) are left as they are: the volume fades in with
/// smoothstep((viewDistance - Start) / Fade), and a proxy ring is only ever seen from hundreds of km
/// (the proxy stands in only outside the planet's keep / frame sphere), where that is 1.
/// </summary>
public static class PlanetRings
{
    /// <summary>Hide the game's ring while its planet is a proxy. Uses the same render flag toggle as the proxies.</summary>
    public static bool HideGameRings = true;
    /// <summary>Our scaled ring at the proxy. Off until tested in game.</summary>
    public static bool ProxyRings = false;
    public static bool ScaleOptics = true;
    private const double BucketStep = 1.1;

    private sealed class Ring
    {
        public OrbitalRingComponent Owner;
        public ProceduralVolumeRenderComponent Render;
        public string Name;
        public Vector3D Center;
        public Quaternion Orientation;
        public double Inner, Outer, Half;
        /// <summary>What we last set on the game's ring model (null: never touched).</summary>
        public bool? GameShown;

        // The game's mesh, copied when the ring entered the scene (the game keeps it until removal).
        public Vector3[] Pos;
        public object[] Uv;   // boxed HalfVector2
        public int[] Idx;
        public AsteroidRingMaterialDefinition BaseMat;
        public object CustomData;   // boxed ProceduralFieldCustomData
        public string MeshError;

        // Ours.
        public object Root, Model, RuntimeModel;
        public AsteroidRingMaterialDefinition Mat;
        public int Bucket = int.MinValue;
        public double LastK = -1;
        public bool ProxyShown;
        public string Error;
        public double K;
    }

    private static readonly List<Ring> _rings = new List<Ring>();

    internal static void Register(OrbitalRingComponent owner)
    {
        var volume = owner.Entity?.TryGet<ProceduralVolumeComponent>()?.ProceduralVolume;
        if (!(volume is ProceduralRing pr)) return;   // ellipsoid fields: nothing to follow a planet
        var torus = pr.Torus;
        var r = new Ring
        {
            Owner = owner,
            Render = owner.Entity.TryGet<ProceduralVolumeRenderComponent>(),
            Name = owner.Entity.TryGet<ProceduralVolumeComponent>()?.Name ?? "ring",
            Center = torus.WorldTransform.Position,
            Orientation = torus.WorldTransform.Orientation,
            Inner = torus.InnerRadius, Outer = torus.OuterRadius, Half = torus.MinorRadii.Y,
        };
        CaptureMesh(r);
        lock (PlanetRenderBridge.Lock) _rings.Add(r);
        Log.Default?.Info($"[ORBIT] ring '{r.Name}' registered: outer {r.Outer / 1000:F1} km, render={r.Render != null}, mesh={(r.MeshError ?? $"{r.Pos?.Length} verts")}");
    }

    internal static void Unregister(OrbitalRingComponent owner)
    {
        lock (PlanetRenderBridge.Lock)
        {
            for (int i = _rings.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_rings[i].Owner, owner)) continue;
                DisposeProxy(_rings[i]);   // the game's model goes with its entity: not touched
                _rings.RemoveAt(i);
            }
        }
    }

    static bool Near(Ring r, Vector3D planetCenter) => (r.Center - planetCenter).Length() < Math.Max(5000.0, 0.05 * r.Outer);

    /// <summary>
    /// Once per planet frame (PlanetFrameComponent, under PlanetRenderBridge.Lock): the rings round
    /// planetCenter follow the planet's state. proxyOn: a proxy drawn at proxyCenter, k = its radius / the real radius.
    /// </summary>
    public static void Sync(Vector3D planetCenter, bool realShown, bool proxyOn, Vector3D proxyCenter, double k)
    {
        if (_rings.Count == 0) return;
        foreach (var r in _rings)
        {
            if (!Near(r, planetCenter)) continue;
            bool gameVisible = realShown || !HideGameRings;
            if (r.GameShown != gameVisible && (r.GameShown.HasValue || !gameVisible)) SetGameRing(r, gameVisible);

            bool want = ProxyRings && !realShown && proxyOn && k > 0 && !double.IsInfinity(k);
            if (want) UpdateProxy(r, planetCenter, proxyCenter, k);
            else if (r.ProxyShown) { SafeVisible(r, false); }
        }
    }

    /// <summary>The planet leaves (its component is removed): its rings back to the game's own.</summary>
    public static void Release(Vector3D planetCenter)
    {
        foreach (var r in _rings)
        {
            if (!Near(r, planetCenter)) continue;
            if (r.GameShown == false) SetGameRing(r, true);
            DisposeProxy(r);
        }
    }

    /// <summary>Flags changed from the harness: everything to the game's own, the next frame applies the flags again.</summary>
    public static void Reset()
    {
        lock (PlanetRenderBridge.Lock)
            foreach (var r in _rings)
            {
                if (r.GameShown == false) SetGameRing(r, true);
                DisposeProxy(r);
            }
    }

    private static void SetGameRing(Ring r, bool visible)
    {
        try
        {
            // Only a live ModelEntity (the call is replayed on the render thread, where a bad id is fatal).
            object model = PlanetRenderBridge.GetMember(r.Render, "ModelEntity");
            if (model == null || model.GetType().Name != "ModelEntity" || !(PlanetRenderBridge.GetMember(model, "IsValid") is bool ok) || !ok) return;
            PlanetRenderBridge.SetModelVisible(model, visible);
            r.GameShown = visible;
        }
        catch (Exception e) { PlanetRenderBridge.WarnOnce("ring-hide", $"ring '{r.Name}' visibility failed: {PlanetRenderBridge.Inner(e)}"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The proxy ring
    // ─────────────────────────────────────────────────────────────────────────

    private static Type _tRmd, _tVs0, _tSub, _tRuntimeModel;
    private static MethodInfo _createRuntimeModel, _toHandle;

    private static string Resolve()
    {
        if (_tRmd != null) return null;
        var render = PlanetRenderBridge.RenderAssembly;
        if (render == null) return "VRage.Render not loaded";
        _tVs0 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatPositionUV0Packed");
        _tSub = render.GetType("Keen.VRage.Render.Data.IRuntimeMeshData+SubPart");
        _tRuntimeModel = render.GetType("Keen.VRage.Render.Contracts.RuntimeModel");
        var rmd = render.GetType("Keen.VRage.Render.Data.RuntimeMeshData`3");
        foreach (var m in PlanetRenderBridge.Contracts?.GetType().GetMethods() ?? new MethodInfo[0])
            if (m.Name == "CreateRuntimeModel" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType.Name == "IRuntimeMeshData") _createRuntimeModel = m;
        _toHandle = _tRuntimeModel?.GetMethod("op_Implicit", new[] { _tRuntimeModel });
        if (_tVs0 == null || _tSub == null || rmd == null || _createRuntimeModel == null || _toHandle == null) return "runtime mesh types not found";
        var nul = typeof(Keen.VRage.Core.Render.Data.VertexFormatNull);
        _tRmd = rmd.MakeGenericType(_tVs0, nul, nul);
        return null;
    }

    /// <summary>The game's post-processed hull, material and custom data, copied (managed arrays).</summary>
    private static void CaptureMesh(Ring r)
    {
        try
        {
            if (r.Render == null) { r.MeshError = "no ProceduralVolumeRenderComponent"; return; }
            object mesh = PlanetRenderBridge.GetMember(r.Render, "_runtimeMeshData");
            r.CustomData = PlanetRenderBridge.GetMember(r.Render, "_entityCustomData");
            if (mesh == null || r.CustomData == null) { r.MeshError = "no runtime mesh / custom data (no model asset?)"; return; }

            var verts = ToArray(PlanetRenderBridge.GetMember(mesh, "_vertexStream0"));
            var subs = ToArray(PlanetRenderBridge.GetMember(mesh, "_subParts"));
            if (!(PlanetRenderBridge.GetMember(mesh, "_indices") is Buffer<int> idx) || verts == null || subs == null || subs.Length == 0)
            { r.MeshError = "mesh buffers unreadable"; return; }
            r.Idx = idx.ToArray();
            FieldInfo fPos = null, fUv = null;
            r.Pos = new Vector3[verts.Length];
            r.Uv = new object[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                object v = verts.GetValue(i);
                fPos ??= v.GetType().GetField("Position");
                fUv ??= v.GetType().GetField("TexCoord");
                r.Pos[i] = (Vector3)fPos.GetValue(v);
                r.Uv[i] = fUv.GetValue(v);
            }
            r.BaseMat = subs.GetValue(0).GetType().GetField("Material").GetValue(subs.GetValue(0)) as AsteroidRingMaterialDefinition;
            if (r.BaseMat == null) r.MeshError = "ring material is not an AsteroidRing material";
        }
        catch (Exception e) { r.MeshError = "capture: " + PlanetRenderBridge.Inner(e); }
    }

    static Array ToArray(object buffer) => buffer?.GetType().GetMethod("ToArray", Type.EmptyTypes)?.Invoke(buffer, null) as Array;

    private static void UpdateProxy(Ring r, Vector3D planetCenter, Vector3D proxyCenter, double k)
    {
        if (r.MeshError != null) return;
        try
        {
            r.K = k;
            int bucket = (int)Math.Round(Math.Log(k) / Math.Log(BucketStep));
            if (bucket != r.Bucket || r.Model == null)
            {
                DisposeProxy(r);
                string why = Build(r, bucket, planetCenter, proxyCenter, k);
                if (why != null) { r.Error = why; r.MeshError = why; PlanetRenderBridge.WarnOnce("ring-build-" + r.Name, $"proxy ring '{r.Name}': {why}"); DisposeProxy(r); return; }
                r.Bucket = bucket;
                r.Error = null;
            }
            PlanetRenderBridge.UpdateRootTransform(r.Root, new WorldTransform(proxyCenter + (r.Center - planetCenter) * k, r.Orientation));
            if (r.LastK < 0 || Math.Abs(k - r.LastK) > r.LastK * 1e-3)
            {
                PlanetRenderBridge.SetEntityCustomData(r.Model, ScaledCustomData(r.CustomData, (float)k));
                r.LastK = k;
            }
            if (!r.ProxyShown) SafeVisible(r, true);
        }
        catch (Exception e)
        {
            r.Error = PlanetRenderBridge.Inner(e);
            r.MeshError = "update: " + r.Error;   // no retry every frame
            PlanetRenderBridge.WarnOnce("ring-update-" + r.Name, $"proxy ring '{r.Name}' failed (off for it): {r.Error}");
            DisposeProxy(r);
        }
    }

    private static string Build(Ring r, int bucket, Vector3D planetCenter, Vector3D proxyCenter, double k)
    {
        string why = Resolve();
        if (why != null) return why;
        float kMid = (float)Math.Pow(BucketStep, bucket);
        float kLo = (float)Math.Pow(BucketStep, bucket - 0.5), kHi = (float)Math.Pow(BucketStep, bucket + 0.5);

        // Hull: outer vertices at the bucket's top, inner ones (the hole's rim) at its bottom.
        float rMin = float.MaxValue, rMax = 0;
        foreach (var p in r.Pos) { float rr = new Vector2(p.X, p.Z).Length(); rMin = Math.Min(rMin, rr); rMax = Math.Max(rMax, rr); }
        float thr = 0.5f * (rMin + rMax);
        object vs0 = Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(_tVs0), new object[] { Allocator.Heap, "OrbitalProxyRing" });
        MethodInfo add0 = vs0.GetType().GetMethod("Add", new[] { _tVs0 });
        var bb = BoundingBox.CreateInvalid();
        var args = new object[1];
        for (int i = 0; i < r.Pos.Length; i++)
        {
            Vector3 p = r.Pos[i];
            float s = new Vector2(p.X, p.Z).Length() > thr ? kHi : kLo;
            var q = new Vector3(p.X * s, p.Y * kHi, p.Z * s);
            bb.Include(q);
            args[0] = Activator.CreateInstance(_tVs0, new[] { (object)q, r.Uv[i] });
            add0.Invoke(vs0, args);
        }
        var idx = new Buffer<int>(Allocator.Heap, "OrbitalProxyRing");
        foreach (int i in r.Idx) idx.Add(i);

        r.Mat = ScaledMaterial(r.BaseMat, kMid);
        if (r.Mat == null) { idx.Dispose(); return "material creation failed"; }

        object subs = Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(_tSub), new object[] { Allocator.Heap, "OrbitalProxyRing" });
        object sub = Activator.CreateInstance(_tSub);
        _tSub.GetField("Name").SetValue(sub, StringId.Get("OrbitalProxyRing_" + r.Name));
        _tSub.GetField("IndexStart").SetValue(sub, 0);
        _tSub.GetField("IndicesCount").SetValue(sub, idx.Count);
        _tSub.GetField("Material").SetValue(sub, r.Mat);
        subs.GetType().GetMethod("Add", new[] { _tSub }).Invoke(subs, new[] { sub });

        // As the game: RuntimeMeshData(name, counts) then its streams; ours replace the empty ones.
        object rmd = Activator.CreateInstance(_tRmd, new object[] { "OrbitalProxyRing_" + r.Name, 0, 0, -1, -1, Allocator.Heap });
        SetField(rmd, "_vertexStream0", vs0);
        SetField(rmd, "_indices", idx);
        SetField(rmd, "_subParts", subs);
        _tRmd.GetProperty("AABB").SetValue(rmd, bb);

        object contracts = PlanetRenderBridge.Contracts;
        object volumes = Enum.ToObject(_createRuntimeModel.GetParameters()[1].ParameterType, (int)RenderRuntimeDataType.Volumes);
        r.RuntimeModel = _createRuntimeModel.Invoke(contracts, new[] { rmd, volumes, (object)true, (object)false });
        var handle = (ResourceHandle)_toHandle.Invoke(null, new[] { r.RuntimeModel });

        r.Root = PlanetRenderBridge.CreateRootEntity("OrbitalProxyRingRoot_" + r.Name, new WorldTransform(proxyCenter + (r.Center - planetCenter) * k, r.Orientation));
        // Visible | SkipFarPlaneCulling | ForceHighestLOD: the game's own flags for its ring.
        r.Model = PlanetRenderBridge.CreateModelEntity("OrbitalProxyRing_" + r.Name, handle, r.Root, 0x1 | 0x10 | 0x20);
        r.LastK = -1;
        r.ProxyShown = true;
        return r.Model != null ? null : "model entity not created";
    }

    /// <summary>The game's ProceduralFieldCustomData with every length times k (a boxed copy).</summary>
    private static object ScaledCustomData(object data, float k)
    {
        Type t = data.GetType();
        object copy = Activator.CreateInstance(t);
        FieldInfo fRing = t.GetField("Ring"), fField = t.GetField("Field");
        object ring = fRing.GetValue(data);   // boxed copies, edited in place
        foreach (string n in new[] { "InnerRadius", "OuterRadius", "HalfHeight" })
        {
            var f = ring.GetType().GetField(n);
            f.SetValue(ring, (float)f.GetValue(ring) * k);
        }
        object field = fField.GetValue(data);
        var fs = field.GetType().GetField("Scale");
        fs.SetValue(field, (Vector3)fs.GetValue(field) * k);
        fRing.SetValue(copy, ring);
        fField.SetValue(copy, field);
        return copy;
    }

    /// <summary>
    /// A runtime AsteroidRing material with the optics scaled by k, registered with the renderer
    /// (MaterialSystem.AddRuntimeMaterial is internal: reflection). AsteroidRing is not an
    /// IRuntimeMaterial, so RuntimeMaterialHandle cannot hold it; it is removed in DisposeProxy.
    /// </summary>
    private static AsteroidRingMaterialDefinition ScaledMaterial(AsteroidRingMaterialDefinition b, float k)
    {
        var ob = DefinitionHelper.CreateObjectBuilder<AsteroidRingMaterialDefinitionObjectBuilder>();
        ob.DefaultState = b.DefaultState;
        ob.PinTextures = b.PinTextures;
        ob.ColorOpacityTexture = b.ColorOpacityTexture;
        ob.AbsorptionLength = ScaleOptics ? b.AbsorptionLength * k : b.AbsorptionLength;
        ob.ScatteringMagnitude = ScaleOptics ? b.ScatteringMagnitude - (float)Math.Log10(k) : b.ScatteringMagnitude;
        ob.StartDistance = b.StartDistance;
        ob.FadeDistance = b.FadeDistance;
        var mat = RuntimeDefinitionHelper.Create<AsteroidRingMaterialDefinition>(ob, null, keepBuilderGuid: true);
        return MaterialSystemCall("AddRuntimeMaterial", mat) ? mat : null;
    }

    private static bool MaterialSystemCall(string method, MaterialDefinition mat)
    {
        object contracts = PlanetRenderBridge.Contracts;
        object ms = contracts?.GetType().GetMethod("GetMaterialSystem", Type.EmptyTypes)?.Invoke(contracts, null);
        var m = ms?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(MaterialDefinition) }, null);
        if (m == null) { PlanetRenderBridge.WarnOnce("ring-matsys-" + method, "MaterialSystem." + method + " not found"); return false; }
        m.Invoke(ms, new object[] { mat });
        return true;
    }

    private static void SafeVisible(Ring r, bool visible)
    {
        try { PlanetRenderBridge.SetModelVisible(r.Model, visible); r.ProxyShown = visible; }
        catch (Exception e) { PlanetRenderBridge.WarnOnce("ring-vis", "proxy ring visibility failed: " + PlanetRenderBridge.Inner(e)); }
    }

    /// <summary>Ours gone, in the order the renderer needs: entity, root, model, then its material.</summary>
    private static void DisposeProxy(Ring r)
    {
        PlanetRenderBridge.DisposeRender(r.Model);
        PlanetRenderBridge.DisposeRender(r.Root);
        PlanetRenderBridge.DisposeRender(r.RuntimeModel);
        if (r.Mat != null)
        {
            try { MaterialSystemCall("RemoveMaterial", r.Mat); } catch (Exception e) { PlanetRenderBridge.WarnOnce("ring-mat-remove", "ring material remove failed: " + PlanetRenderBridge.Inner(e)); }
        }
        r.Model = r.Root = r.RuntimeModel = null;
        r.Mat = null;
        r.Bucket = int.MinValue;
        r.LastK = -1;
        r.ProxyShown = false;
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

    public static string Status()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"rings: hideGame={HideGameRings} proxyRings={ProxyRings} scaleOptics={ScaleOptics} registered={_rings.Count}\n");
        lock (PlanetRenderBridge.Lock)
            foreach (var r in _rings)
                sb.Append($"  '{r.Name}' outer {r.Outer / 1000:F1} km inner {r.Inner / 1000:F1} km half {r.Half / 1000:F2} km | game ring {(r.GameShown == null ? "untouched" : r.GameShown.Value ? "shown" : "HIDDEN")} | " +
                          $"mesh {(r.MeshError ?? $"{r.Pos?.Length} verts, {r.Idx?.Length} idx, mat {r.BaseMat?.Guid}")} | " +
                          $"proxy {(r.Model != null ? $"k={r.K:G4} bucket {r.Bucket} {(r.ProxyShown ? "shown" : "hidden")}" : "none")} err={r.Error ?? "-"}\n");
        return sb.ToString();
    }
}
