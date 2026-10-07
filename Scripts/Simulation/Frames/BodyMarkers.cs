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
///
/// CONTACTS too (what Contacts knows: a station, a ship, a wreck): one marker per group (a frame), named by
/// its site or its grid, in amber with a diamond; these keep the game's edge arrow (a few at most, and
/// where a station lies off the screen is the point); none within 1 km (you see it) nor for your own grids.
///
/// Riding a frame, its ORIGIN as well (CollectOrigin): where the relative pull is nothing.
/// </summary>
public static class BodyMarkers
{
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>A body whose disc is wider than this (radians, its angular radius) has no marker.</summary>
    public const double MaxAngularRadius = 5.0 * System.Math.PI / 180.0;

    static readonly ColorSRGB Color = new ColorSRGB(0.78f, 0.84f, 0.93f, 1f);
    static readonly ColorSRGB ContactColor = new ColorSRGB(1.00f, 0.72f, 0.30f, 1f);
    public const double ContactMinDistance = 1000;
    /// <summary>A contact within this of one of the game's own markers is left to the game's (m).</summary>
    public const double GameMarkRadius = 5000;
    internal static Keen.VRage.Core.Game.Systems.Session _session;
    static readonly Dictionary<string, GPSMarker> _markers = new Dictionary<string, GPSMarker>();
    struct Item { public GPSMarker M; public Vector3D World; public double Surface; public bool Moon, Contact; }
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
        int bodies = _items.Count;
        CollectContacts(camera, camModel, camChart, t, FrameMarkers.GameMarked(_session));
        bool origin = CollectOrigin(camera);
        Status = $"bodies: {bodies} marked, {hidden} too close to need one; contacts: {_items.Count - bodies - (origin ? 1 : 0)} marked{(origin ? "; frame origin marked" : "")}";
    }

    /// <summary>
    /// FRAME ORIGIN: riding a frame (not its anchor), where its centre is - the point that goes round on the frame's own
    /// orbit, where the relative pull is nothing. It grows with your distance from it, and holding station against it
    /// costs fuel or power with it (StationKeepCharge): the marker shows where holding is free. Kept, like a contact,
    /// at the screen's edge when off it; none within 50 m.
    /// </summary>
    static bool CollectOrigin(WorldTransform camera)
    {
        var f = FrameHost.PlayerFrame;
        if (!ShowOrigin || f == null || FrameHost.RiderFrame != f.Id) return false;
        Vector3D at; lock (ServerFrames.FramesLock) at = f.BerthCenter;   // (the frame's centre in this world: the berth)
        double dist = (at - camera.Position).Length();
        if (!(dist > 50)) return false;
        if (_origin == null)
        {
            object h = MapIcons.Handle("ring");
            if (h == null) return false;
            _origin = new GPSMarker(null, "Frame origin", "", Vector3D.Zero, (ResourceHandle<Keen.VRage.Core.Render.TextureAsset>)(ResourceHandle)h, OriginColor, true, false);
        }
        _items.Add(new Item { M = _origin, World = at, Surface = dist, Contact = true });
        return true;
    }
    public static bool ShowOrigin = true;
    static GPSMarker _origin;
    static readonly ColorSRGB OriginColor = new ColorSRGB(0.55f, 0.95f, 0.75f, 1f);

    /// <summary>The contacts you know of, one per group (frame), where they truly are.</summary>
    static void CollectContacts(WorldTransform camera, Vector3D camModel, Chart camChart, double t, List<Vector3D> gameMarked)
    {
        var seen = new HashSet<long>();
        var pf = FrameHost.PlayerFrame;
        foreach (long id in Contacts.KnownGrids())
        {
            if (!ServerFrames.View(id, out var g)) continue;   // (the server's snapshot, not its entity)
            string name = g.Name; long group = id;
            lock (ServerFrames.FramesLock)
            {
                var f = SystemHost.Frames?.FindByMember(id);
                if (f != null)
                {
                    group = -f.Id;
                    var site = EncounterFrames.SiteOf(f.Id);
                    if (site?.Label != null) name = site.Label;
                }
            }
            if (!seen.Add(group)) continue;
            // the game marks it already (a contract's station, an antenna's broadcast): not twice
            Vector3D wpos = g.Position; bool marked = false;
            foreach (var gm in gameMarked) if ((gm - wpos).LengthSquared() < GameMarkRadius * GameMarkRadius) { marked = true; break; }
            if (marked) continue;
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(g.Position, t, out m, out _, out _);
            if (!ok) continue;
            Vector3D d = m - camModel; double dist = d.Length();
            if (!(dist > ContactMinDistance)) continue;
            // detected, not yet tracked: '≈' and a rough distance (a bearing and a rough range, no orbit yet)
            bool tracked = Contacts.TrackedGrid(id);
            var mk = ContactOf(group, tracked ? name : "≈ " + name);
            if (mk == null) continue;
            _items.Add(new Item { M = mk, World = camera.Position + camChart.FromInertial(d) * (System.Math.Min(dist, 5e4) / dist),
                                  Surface = tracked ? dist : SEAerospace.Sensing.Tracking.Rough(dist), Contact = true });
        }
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
        // (contacts first: a station in front of a planet is what you are looking for)
        _items.Sort((a, b) => a.Contact != b.Contact ? (a.Contact ? -1 : 1) : a.Moon != b.Moon ? (a.Moon ? 1 : -1) : a.Surface.CompareTo(b.Surface));
        var placed = new List<Vector2>();
        foreach (var it in _items)
        {
            // Bodies on the screen only (the game's own clamp would pin every body to an edge); contacts may
            // sit at the edge, pointing the way (the game clamps them).
            bool on = MapPipeline.ToScreen(it.World, out var s) && s.X >= mx && s.Y >= my && s.X <= size.X - mx && s.Y <= size.Y - my;
            if (!on)
            {
                if (it.Contact) MapPipeline.GameMarker(session, it.M, it.World, it.Surface);
                continue;
            }
            bool clash = false;
            foreach (var q in placed) if (System.Math.Abs(q.X - s.X) < 150f * u && System.Math.Abs(q.Y - s.Y) < 44f * u) { clash = true; break; }
            if (clash) continue;
            placed.Add(s);
            MapPipeline.GameMarker(session, it.M, it.World, it.Surface);
        }
    }

    static readonly Dictionary<long, GPSMarker> _contacts = new Dictionary<long, GPSMarker>();
    static GPSMarker ContactOf(long group, string name)
    {
        if (_contacts.TryGetValue(group, out var m)) { if (m.Name != name) m.Name = name; return m; }
        object h = MapIcons.Handle("contact");
        if (h == null) return null;
        var icon = (ResourceHandle<Keen.VRage.Core.Render.TextureAsset>)(ResourceHandle)h;
        m = new GPSMarker(null, name, "", Vector3D.Zero, icon, ContactColor, true, false);
        _contacts[group] = m;
        return m;
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
