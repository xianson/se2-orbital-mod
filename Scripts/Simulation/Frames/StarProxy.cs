using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Delfos in the sky in flight: its own globe (the map's star model), at its true direction and angular
/// size, placed as the planets' proxies are (PlanetBerths.ProjectProxy). The game's sun is not Delfos: it
/// keeps its own place and day cycle (SunDriver is off by default), so its glare is never drawn on Delfos.
/// </summary>
public static class StarProxy
{
    public static bool Enabled = true;
    public static string Status = "-";
    static PlanetRenderBridge.PlanetHandles _h;
    static PlanetRenderBridge.Proxy _proxy;
    static bool _tried;

    /// <summary>Client tick, after the observer is published.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        var reg = SystemHost.Registry;
        if (!Enabled || !SystemHost.Built || reg?.Root == null || !FrameHost.Observer.HasValue) { Hide("off"); return; }
        if (_h == null)
        {
            if (_tried) { Status = "no star model"; return; }
            var map = MapView.Map(session);
            if (map == null) { Status = "no map yet"; return; }
            _tried = true;
            var cfg = PlanetRenderBridge.GetMember(map, "_configuration");
            if (PlanetRenderBridge.GetMember(cfg, "StarVisualPrefab") is Keen.VRage.Core.Game.Definitions.PrefabDefinition sp)
                _h = PlanetRenderBridge.ResolveModelOnly(sp, reg.Root.Name);
            if (_h == null || !_h.HasProxyModel) { _h = null; Status = "no star model"; return; }
        }
        if (MapView.Visible) { Hide("map open"); return; }
        var pp = SEAerospace.PlanetBerths.ProjectProxy(camera.Position, reg.Root.OriginInRoot(t).Position, FrameHost.Observer.Value, SystemHost.StarRadius, t);
        if (pp.RenderRadius <= 0) { Hide("not placed"); return; }
        if (_proxy == null)
        {
            _proxy = PlanetRenderBridge.CreateProxy(_h, pp.RenderPos);
            Log.Default?.Info($"[ORBIT] {reg.Root.Name}: flight proxy {(_proxy != null ? "created" : "FAILED")}");
            if (_proxy == null) { Status = "proxy failed"; return; }
        }
        PlanetRenderBridge.UpdateProxy(_h, _proxy, pp.RenderPos, pp.RenderRadius);
        PlanetRenderBridge.SetProxyVisible(_proxy, true);
        Status = $"shown {pp.TrueDistance / 1000:F0} km, {2 * System.Math.Atan(SystemHost.StarRadius / System.Math.Max(1, pp.TrueDistance)) * 180 / System.Math.PI:F1} deg";
    }

    static void Hide(string why)
    {
        if (_proxy != null) PlanetRenderBridge.SetProxyVisible(_proxy, false);
        Status = why;
    }

    /// <summary>The direction to Delfos from the camera (unit), for the harness's camera.</summary>
    public static Vector3D? Direction(WorldTransform camera, double t)
    {
        var reg = SystemHost.Registry;
        if (reg?.Root == null || !FrameHost.Observer.HasValue) return null;
        var pp = SEAerospace.PlanetBerths.ProjectProxy(camera.Position, reg.Root.OriginInRoot(t).Position, FrameHost.Observer.Value, SystemHost.StarRadius, t);
        var d = pp.RenderPos - camera.Position;
        return d.LengthSquared() > 1 ? Vector3D.Normalize(d) : (Vector3D?)null;
    }
}
