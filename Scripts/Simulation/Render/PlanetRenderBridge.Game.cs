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

/// <summary>The game's own objects, late-bound: a planet's map visual and gravity law, the colonization map's camera,
/// the player teleport, render component visibility, map object scale.</summary>
public static partial class PlanetRenderBridge
{
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

    private static readonly Dictionary<object, float> _origScale = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Scale a colonization-map object (MapObjectRenderComponent.UpdateScale, the engine's own path).
    /// multiplier &lt;= 0 restores its configured scale. Used to hide the game's globes safely.
    /// </summary>
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
}
