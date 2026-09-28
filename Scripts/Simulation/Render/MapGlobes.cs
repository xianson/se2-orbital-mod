using Keen.VRage.Core;
using SEAerospace;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The planets' own textured map globes as Map-type models (shared by the map views).</summary>
public static class MapGlobes
{
    // ── the planets' render handles, registered by their planet components ──
    private static readonly Dictionary<string, PlanetRenderBridge.PlanetHandles> _handles = new Dictionary<string, PlanetRenderBridge.PlanetHandles>();
    private static readonly List<(PlanetRenderBridge.PlanetHandles h, Vector3D center)> _pendingHandles = new List<(PlanetRenderBridge.PlanetHandles, Vector3D)>();

    /// <summary>A planet's render handles, from its planet component (matched to a body by its cell).</summary>
    public static void Register(PlanetRenderBridge.PlanetHandles h, Vector3D center)
    {
        if (h != null && h.HasProxyModel) _pendingHandles.Add((h, center));
    }

    /// <summary>A body's render handles (matched to its cell centre), or null.</summary>
    public static PlanetRenderBridge.PlanetHandles HandlesFor(string body)
    {
        if (_handles.TryGetValue(body, out var h)) return h;
        if (VoxelBerthRegistry.TryGetCell(body, SystemHost.Registry, out Vector3D cell))
            foreach (var ph in _pendingHandles) if ((ph.center - cell).Length() < 1000) { _handles[body] = ph.h; return ph.h; }
        return null;
    }


    private static readonly Dictionary<string, PlanetRenderBridge.Proxy> _globes = new Dictionary<string, PlanetRenderBridge.Proxy>();

    public static void Use(string body, Vector3D center, double radius, HashSet<string> used)
    {
        var h = HandlesFor(body);
        if (h == null) return;
        // Not over the game's panels (a 3D globe cannot be clipped): only when its centre is in the open area.
        var scr = MapPipeline.ScreenSize;
        // Hidden only once its whole disc is off the open area (by the centre alone, a globe at the edge
        // popped out while half of it was still in view).
        if (MapPipeline.ToScreen(center, out var cs))
        {
            float rpx = 0;
            var q = (QuaternionD)MapPipeline.CameraOrientation;
            if (MapPipeline.ToScreen(center + q * Vector3D.Right * radius, out var es)) rpx = (es - cs).Length();
            if (cs.X + rpx < scr.X * 0.255f || cs.X - rpx > scr.X * 0.775f || cs.Y + rpx < scr.Y * 0.1f || cs.Y - rpx > scr.Y * 0.84f) { if (_globes.TryGetValue(body, out var off)) PlanetRenderBridge.SetProxyVisible(off, false); return; }
        }
        if (!_globes.TryGetValue(body, out var g)) { g = PlanetRenderBridge.CreateProxy(h, center, mapOnly: true); if (g == null) return; _globes[body] = g; }
        PlanetRenderBridge.UpdateProxy(h, g, center, radius);
        PlanetRenderBridge.SetProxyVisible(g, true);
        used.Add(body);
    }

    public static void End(HashSet<string> used)
    {
        foreach (var kv in _globes) if (!used.Contains(kv.Key)) PlanetRenderBridge.SetProxyVisible(kv.Value, false);
    }

    public static void HideAll() => End(new HashSet<string>());
}
