using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core;
using Keen.VRage.Library.Utils;
using SEAerospace;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// PLANET AND MOON MARKERS in flight: each body where it truly is, named, with how far its surface is,
/// drawn by the game's own GPS marker code (its icon box, font and distance), with a planet icon of ours
/// and a quiet colour of their own so they never pass for your GPS points. Kept out of the way:
///  - a body that already fills a good part of the view (disc over ~10° across) has none: you see it;
///  - off the screen, none (no edge arrows: the edges stay for your GPS points and contracts);
///  - not over the map (it names them), nor walking about (as the orbit card).
/// The markers are the mod's own objects, never added to your GPS list.
/// </summary>
public static class BodyMarkers
{
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>A body whose disc is wider than this (radians, its angular radius) has no marker.</summary>
    public const double MaxAngularRadius = 5.0 * System.Math.PI / 180.0;

    static readonly ColorSRGB Color = new ColorSRGB(0.78f, 0.84f, 0.93f, 1f);
    static readonly Dictionary<string, GPSMarker> _markers = new Dictionary<string, GPSMarker>();
    struct Item { public GPSMarker M; public Vector3D World; public double Surface; public bool Moon; }
    static readonly List<Item> _items = new List<Item>();

    /// <summary>Client tick, from FrameMarkers (the camera's true place known): which bodies to mark, and where.</summary>
    public static void Collect(WorldTransform camera, Vector3D camModel, Chart camChart, double t)
    {
        _items.Clear();
        var reg = SystemHost.Registry;
        if (!Enabled || reg == null || MapView.Visible || OrbitHud.Walking) { Status = "off"; return; }
        int hidden = 0;
        foreach (var b in reg.Bodies)
        {
            if (b.IsRoot || !SystemHost.BeaconOf.ContainsKey(b.Name)) continue;
            double radius = reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
            Vector3D d = b.OriginInRoot(t).Position - camModel;
            double dist = d.Length();
            if (!(dist > radius) || double.IsNaN(dist)) { hidden++; continue; }
            if (radius > 0 && System.Math.Asin(radius / dist) > MaxAngularRadius) { hidden++; continue; }
            Vector3D dw = camChart.FromInertial(d);
            var m = MarkerOf(b.Name);
            if (m == null) continue;
            // At a comfortable depth in the true direction (only the direction projects).
            _items.Add(new Item { M = m, World = camera.Position + dw * (System.Math.Min(dist, 5e4) / dist), Surface = dist - radius, Moon = b.Parent != null && !b.Parent.IsRoot });
        }
        Status = $"bodies: {_items.Count} marked, {hidden} too close to need one";
    }

    public static bool Any => _items.Count > 0;
    public static void Clear() { _items.Clear(); Status = "no camera place"; }

    /// <summary>Inside an open HUD batch.</summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        var size = MapPipeline.ScreenSize;
        float mx = size.X * 0.06f, my = size.Y * 0.08f, u = System.Math.Max(1f, size.Y / 1080f);
        // Planets before moons, then nearest first; one whose marker would land on an earlier one's is left
        // out (two bodies in one direction printed their names over each other). Far off, a planet and its
        // moon are one place: the planet names it.
        _items.Sort((a, b) => a.Moon != b.Moon ? (a.Moon ? 1 : -1) : a.Surface.CompareTo(b.Surface));
        var placed = new List<Vector2>();
        foreach (var it in _items)
        {
            // On the screen only (the game's own clamp would pin every body to an edge).
            if (!MapPipeline.ToScreen(it.World, out var s) || s.X < mx || s.Y < my || s.X > size.X - mx || s.Y > size.Y - my) continue;
            bool clash = false;
            foreach (var q in placed) if (System.Math.Abs(q.X - s.X) < 150f * u && System.Math.Abs(q.Y - s.Y) < 44f * u) { clash = true; break; }
            if (clash) continue;
            placed.Add(s);
            MapPipeline.GameMarker(session, it.M, it.World, it.Surface);
        }
    }

    static GPSMarker MarkerOf(string body)
    {
        if (_markers.TryGetValue(body, out var m)) return m;
        object h = MapIcons.Handle("body");
        if (h == null) return null;
        var icon = (ResourceHandle<Keen.VRage.Core.Render.TextureAsset>)(ResourceHandle)h;
        m = new GPSMarker(null, SystemHost.DisplayName(body), "", Vector3D.Zero, icon, Color, true, false);
        _markers[body] = m;
        return m;
    }
}
