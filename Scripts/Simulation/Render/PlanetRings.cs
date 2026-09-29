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
/// are rebuilt only when k leaves a small band. The hull must never reach outside the custom-data box
/// (half-sides OuterRadius, Height): where it did (a hull built for a 10% band's top, a circle 5%
/// wider than the box), the volume passes disagreed and drew four dark opaque arcs round the ring,
/// one past each side of the box (seen in game). The game's own hull is a polygon inscribed in
/// OuterRadius, inside its box. So ours is built uniformly at kBuilt = k / (1 + Margin), and rebuilt
/// as soon as k falls below kBuilt (the box would shrink inside the hull) or rises above
/// kBuilt (1 + Margin)^2: the hull always lies inside the box, and trims at most ~4% off the ring's rim.
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
    public static bool ProxyRings = true;   // verified in game (Verdure, Kemik): on
    public static bool ScaleOptics = true;
    /// <summary>Hull scale below the exact k (see the summary); the band is twice this wide.</summary>
    private const double Margin = 0.02;

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

        /// <summary>As the game laid it (round the chart's +Y): the map's plane is that chart's, so the map copy lies so.</summary>
        public Quaternion GameOrientation;
        // Ours: at the proxy (the map's is MapRingMesh: volumes never draw in map mode).
        public readonly Inst P = new Inst();
    }

    /// <summary>One copy of a ring we draw (its model, its scaled material).</summary>
    private sealed class Inst
    {
        public object Root, Model, RuntimeModel;
        public AsteroidRingMaterialDefinition Mat;
        public double KBuilt = -1, LastK = -1, K;
        public bool Shown;
        public string Error;
    }

    private static readonly List<Ring> _rings = new List<Ring>();

    internal static void Register(OrbitalRingComponent owner)
    {
        var volume = owner.Entity?.TryGet<ProceduralVolumeComponent>()?.ProceduralVolume;
        if (!(volume is ProceduralRing pr)) return;   // ellipsoid fields: nothing to follow a planet
        var torus = pr.Torus;
        Quaternion orient = Equatorial(owner.Entity, torus.WorldTransform.Orientation);
        var r = new Ring
        {
            Owner = owner,
            Render = owner.Entity.TryGet<ProceduralVolumeRenderComponent>(),
            Name = owner.Entity.TryGet<ProceduralVolumeComponent>()?.Name ?? "ring",
            Center = torus.WorldTransform.Position,
            Orientation = orient,
            GameOrientation = torus.WorldTransform.Orientation,
            Inner = torus.InnerRadius, Outer = torus.OuterRadius, Half = torus.MinorRadii.Y,
        };
        CaptureMesh(r);
        lock (PlanetRenderBridge.Lock) _rings.Add(r);
        Log.Default?.Info($"[ORBIT] ring '{r.Name}' registered: outer {r.Outer / 1000:F1} km, render={r.Render != null}, mesh={(r.MeshError ?? $"{r.Pos?.Length} verts")}");
    }

    /// <summary>
    /// The game lays its rings round world +Y; our planets spin about their model axis (+Z, the orbit
    /// plane's normal, with world axes == model axes). A ring off the spin axis is turned by the rotating
    /// surface chart (a different place on every transfer) and stands across the ecliptic, where its
    /// sector is drawn flat. So the ring is laid on its planet's equator: the entity is turned, about the
    /// planet's centre, from the game's normal onto the spin axis. Out: the ring's new orientation.
    /// </summary>
    static Quaternion Equatorial(Entity e, Quaternion orient)
    {
        try
        {
            if (!EquatorialRings || e == null) return orient;
            Vector3 n = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orient));
            Vector3 axis = Vector3.UnitZ;   // (every planet's spin axis in the model's canonical plane)
            if (Vector3.Dot(n, axis) > 0.99999f) return orient;
            Quaternion turned = Quaternion.Normalize(Quaternion.Concatenate(orient, Quaternion.CreateFromTwoVectors(n, axis)));
            var wt = e.Data.GetWorldTransform();
            e.Data.Set(new WorldTransform(wt.Position, turned));
            Log.Default?.Info($"[ORBIT] ring laid on its planet's equator: normal {n} -> {axis}");
            return turned;
        }
        catch (Exception ex) { Log.Default?.Warning("[ORBIT] ring turn failed: " + PlanetRenderBridge.Inner(ex)); return orient; }
    }

    /// <summary>Lay the game's rings on their planets' equators (see <see cref="Equatorial"/>).</summary>
    public static bool EquatorialRings = true;

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
            if (want) UpdateProxy(r, r.P, proxyCenter + (r.Center - planetCenter) * k, k, r.Orientation);
            else if (r.P.Shown) { SafeVisible(r.P, false); }
        }
    }

    /// <summary>
    /// The map's globe of a planet (MapGlobes): its rings on it, at the globe's scale (k = globe radius / the
    /// planet's radius), lying in the map's plane (up: the map's up at the globe), laid as the game laid them
    /// round its chart's +Y (the map's plane is that chart's). Drawn by <see cref="MapRingMesh"/>: the renderer
    /// skips every volume in map mode, so the game's own ring can never draw there.
    /// </summary>
    public static void Map(Vector3D cell, double planetRadius, Vector3D globe, double globeRadius, Vector3D up)
    {
        if (!MapRings || _rings.Count == 0 || planetRadius <= 0 || !(globeRadius > 0) || up.LengthSquared() < 1e-12) return;
        lock (PlanetRenderBridge.Lock)
            foreach (var r in _rings)
            {
                if (!Near(r, cell)) continue;
                double k = globeRadius / planetRadius;
                Vector3 n0 = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, r.GameOrientation));
                Quaternion orient = Quaternion.Normalize(Quaternion.Concatenate(r.GameOrientation, Quaternion.CreateFromTwoVectors(n0, (Vector3)Vector3D.Normalize(up))));
                MapRingMesh.Place(r.Name, r.BaseMat, r.Inner, r.Outer, globe, orient, k);
            }
    }

    /// <summary>The rings known (world centre, inner / outer radius, half-thickness): the server's tori, else the client's ring entities.</summary>
    public static List<(Vector3D C, double In, double Out, double Half)> Known()
    {
        var l = new List<(Vector3D, double, double, double)>();
        lock (AsteroidBridge.Rings) l.AddRange(AsteroidBridge.Rings);
        if (l.Count == 0) lock (PlanetRenderBridge.Lock) foreach (var r in _rings) l.Add((r.Center, r.Inner, r.Outer, r.Half));
        return l;
    }

    /// <summary>After a map frame: the map rings not placed in it are hidden.</summary>
    public static void MapEnd() => MapRingMesh.End();

    /// <summary>Rings on the map's globes.</summary>
    public static bool MapRings = true;

    /// <summary>The planet leaves (its component is removed): its rings back to the game's own.</summary>
    public static void Release(Vector3D planetCenter)
    {
        foreach (var r in _rings)
        {
            if (!Near(r, planetCenter)) continue;
            if (r.GameShown == false) SetGameRing(r, true);
            DisposeInst(r.P);
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

    private static void UpdateProxy(Ring r, Inst x, Vector3D at, double k, Quaternion orient)
    {
        if (r.MeshError != null) return;
        try
        {
            x.K = k;
            // Hull inside the custom-data box: rebuilt as soon as k drops below the hull's scale.
            if (x.Model == null || k < x.KBuilt || k > x.KBuilt * (1 + Margin) * (1 + Margin))
            {
                DisposeInst(x);
                double kBuilt = k / (1 + Margin);
                string why = Build(r, x, kBuilt, k, at, orient);
                if (why != null) { x.Error = why; r.MeshError = why; PlanetRenderBridge.WarnOnce("ring-build-" + r.Name, $"proxy ring '{r.Name}': {why}"); DisposeInst(x); return; }
                x.KBuilt = kBuilt;
                x.Error = null;
            }
            PlanetRenderBridge.UpdateRootTransform(x.Root, new WorldTransform(at, orient));
            if (x.LastK < 0 || Math.Abs(k - x.LastK) > x.LastK * 1e-3)
            {
                PlanetRenderBridge.SetEntityCustomData(x.Model, ScaledCustomData(r.CustomData, (float)k));
                x.LastK = k;
            }
            if (!x.Shown) SafeVisible(x, true);
        }
        catch (Exception e)
        {
            x.Error = PlanetRenderBridge.Inner(e);
            r.MeshError = "update: " + x.Error;   // no retry every frame
            PlanetRenderBridge.WarnOnce("ring-update-" + r.Name, $"proxy ring '{r.Name}' failed (off for it): {x.Error}");
            DisposeInst(x);
        }
    }

    /// <summary>Hull at kHull (uniformly: the game's hull scaled, inside the box at any k &gt;= kHull), material at k.</summary>
    private static string Build(Ring r, Inst x, double kHull, double k, Vector3D at, Quaternion orient)
    {
        string why = Resolve();
        if (why != null) return why;
        float s = (float)kHull;
        object vs0 = Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(_tVs0), new object[] { Allocator.Heap, "OrbitalProxyRing" });
        MethodInfo add0 = vs0.GetType().GetMethod("Add", new[] { _tVs0 });
        var bb = BoundingBox.CreateInvalid();
        var args = new object[1];
        for (int i = 0; i < r.Pos.Length; i++)
        {
            var q = r.Pos[i] * s;
            bb.Include(q);
            args[0] = Activator.CreateInstance(_tVs0, new[] { (object)q, r.Uv[i] });
            add0.Invoke(vs0, args);
        }
        var idx = new Buffer<int>(Allocator.Heap, "OrbitalProxyRing");
        foreach (int i in r.Idx) idx.Add(i);

        x.Mat = ScaledMaterial(r.BaseMat, (float)k);
        if (x.Mat == null) { idx.Dispose(); return "material creation failed"; }

        object subs = Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(_tSub), new object[] { Allocator.Heap, "OrbitalProxyRing" });
        object sub = Activator.CreateInstance(_tSub);
        _tSub.GetField("Name").SetValue(sub, StringId.Get("OrbitalProxyRing_" + r.Name));
        _tSub.GetField("IndexStart").SetValue(sub, 0);
        _tSub.GetField("IndicesCount").SetValue(sub, idx.Count);
        _tSub.GetField("Material").SetValue(sub, x.Mat);
        subs.GetType().GetMethod("Add", new[] { _tSub }).Invoke(subs, new[] { sub });

        // As the game: RuntimeMeshData(name, counts) then its streams; ours replace the empty ones.
        object rmd = Activator.CreateInstance(_tRmd, new object[] { "OrbitalProxyRing_" + r.Name, 0, 0, -1, -1, Allocator.Heap });
        SetField(rmd, "_vertexStream0", vs0);
        SetField(rmd, "_indices", idx);
        SetField(rmd, "_subParts", subs);
        _tRmd.GetProperty("AABB").SetValue(rmd, bb);

        object contracts = PlanetRenderBridge.Contracts;
        object volumes = Enum.ToObject(_createRuntimeModel.GetParameters()[1].ParameterType, (int)RenderRuntimeDataType.Volumes);
        x.RuntimeModel = _createRuntimeModel.Invoke(contracts, new[] { rmd, volumes, (object)true, (object)false });
        var handle = (ResourceHandle)_toHandle.Invoke(null, new[] { x.RuntimeModel });

        const string tag = "OrbitalProxyRing";
        x.Root = PlanetRenderBridge.CreateRootEntity(tag + "Root_" + r.Name, new WorldTransform(at, orient));
        // Visible | SkipFarPlaneCulling | ForceHighestLOD: the game's own flags for its ring.
        x.Model = PlanetRenderBridge.CreateModelEntity(tag + "_" + r.Name, handle, x.Root, 0x1 | 0x10 | 0x20);
        x.LastK = -1;
        x.Shown = true;
        return x.Model != null ? null : "model entity not created";
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

    internal static bool MaterialSystemCall(string method, MaterialDefinition mat)
    {
        object contracts = PlanetRenderBridge.Contracts;
        object ms = contracts?.GetType().GetMethod("GetMaterialSystem", Type.EmptyTypes)?.Invoke(contracts, null);
        var m = ms?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(MaterialDefinition) }, null);
        if (m == null) { PlanetRenderBridge.WarnOnce("ring-matsys-" + method, "MaterialSystem." + method + " not found"); return false; }
        m.Invoke(ms, new object[] { mat });
        return true;
    }

    private static void SafeVisible(Inst x, bool visible)
    {
        try { PlanetRenderBridge.SetModelVisible(x.Model, visible); x.Shown = visible; }
        catch (Exception e) { PlanetRenderBridge.WarnOnce("ring-vis", "proxy ring visibility failed: " + PlanetRenderBridge.Inner(e)); }
    }

    /// <summary>Ours gone, in the order the renderer needs: entity, root, model, then its material.</summary>
    private static void DisposeProxy(Ring r) => DisposeInst(r.P);

    private static void DisposeInst(Inst x)
    {
        PlanetRenderBridge.DisposeRender(x.Model);
        PlanetRenderBridge.DisposeRender(x.Root);
        PlanetRenderBridge.DisposeRender(x.RuntimeModel);
        if (x.Mat != null)
        {
            try { MaterialSystemCall("RemoveMaterial", x.Mat); } catch (Exception e) { PlanetRenderBridge.WarnOnce("ring-mat-remove", "ring material remove failed: " + PlanetRenderBridge.Inner(e)); }
        }
        x.Model = x.Root = x.RuntimeModel = null;
        x.Mat = null;
        x.KBuilt = -1;
        x.LastK = -1;
        x.Shown = false;
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
                          $"proxy {(r.P.Model != null ? $"k={r.P.K:G4} hull k={r.P.KBuilt:G4} {(r.P.Shown ? "shown" : "hidden")}" : "none")} err={r.P.Error ?? "-"}\n");
        return sb.ToString();
    }
}
