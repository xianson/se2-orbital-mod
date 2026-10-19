using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
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
///
/// TARGETING in flight: N takes the marked body or contact nearest the crosshair as your target (again on it: clears
/// it); the target, whatever it is (a ship of yours too), is bracketed where it lies, with its name and distance.
/// 2026-10-19 (fx-detect): N also takes what is not drawn here - a ship the game marks already (a GPS / ownership marker:
/// yours, a contract's), a sector, a rock you have seen - and says why when it takes nothing (CleanMap.SetTarget:
/// "landed on Verdure"; nothing there: how things become targetable).
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
    struct Item { public GPSMarker M; public Vector3D World; public double Surface; public bool Moon, Contact; public string Key; }
    static readonly List<Vector2> _placed = new List<Vector2>();
    static double _statusAt;
    static readonly List<Item> _items = new List<Item>();

    /// <summary>Client tick, from FrameMarkers (the camera's true place known): which bodies to mark, and where.</summary>
    public static void Collect(WorldTransform camera, Vector3D camModel, Chart camChart, double t)
    {
        _items.Clear(); _tgtOn = false; _pick.Clear();
        _cam = camera.Position; _camModel = camModel; _camChart = camChart; _camT = t; _camOk = true;
        var reg = SystemHost.Registry;
        if (!Enabled || reg == null || MapView.Visible || OrbitHud.Walking) { Status = "off"; return; }
        // the target, wherever it is (bracketed in Draw)
        string tk = Maneuvers.Target;
        if (tk != null && Targets.RootAt(tk, t, out var troot))
        {
            Vector3D d = troot.Position - camModel; double dist = d.Length();
            if (dist > 1 && !double.IsNaN(dist))
            {
                _tgtWorld = camera.Position + camChart.FromInertial(d) * (System.Math.Min(dist, 5e4) / dist);
                _tgtDist = dist - Targets.Radius(tk); _tgtName = Targets.Name(tk); _tgtOn = true;
            }
        }
        int hidden = 0;
        var regBodies = reg.Bodies;   // (by index: a foreach over the IReadOnlyList boxed an enumerator every frame)
        for (int bi = 0; bi < regBodies.Count; bi++)
        {
            var b = regBodies[bi];
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
            _items.Add(new Item { M = m, World = camera.Position + dw * (System.Math.Min(dist, 5e4) / dist), Surface = dist - radius, Moon = b.Parent != null && !b.Parent.IsRoot, Key = b.Name });
        }
        int bodies = _items.Count;
        CollectContacts(camera, camModel, camChart, t, FrameMarkers.GameMarked(_session));
        bool origin = CollectOrigin(camera);
        if (FrameMarkers.StatusDue(ref _statusAt)) Status = $"bodies: {bodies} marked, {hidden} too close to need one; contacts: {_items.Count - bodies - (origin ? 1 : 0)} marked{(origin ? "; frame origin marked" : "")}";
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
        Vector3D at; int grids = 0;
        lock (ServerFrames.FramesLock)
        {
            at = f.BerthCenter;   // (the frame's centre in this world: the berth)
            // (a ship with rotors / hinges counts once: its subgrids are joined to the frame's anchor - the published view's
            //  AnchorJoined; the anchor itself counts)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && !(ServerFrames.View(id, out var v) && v.AnchorJoined)) grids++;
        }
        // (only your own grid in the frame - 2026-10-14, the user: nothing to hold station by, so no marker)
        if (grids <= 1) return false;
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

    /// <summary>Contacts this close are marked whether pinned or not (m).</summary>
    public const double NearbyMeters = 50000;

    /// <summary>
    /// The ships and stations to mark (one per group - a frame - where they truly are): your target, what you pinned in the
    /// Contacts tab, and the contacts you know of within <see cref="NearbyMeters"/>; your own ships only when pinned or
    /// targeted. Everything else is in the Contacts tab, not on the HUD.
    /// </summary>
    static void CollectContacts(WorldTransform camera, Vector3D camModel, Chart camChart, double t, List<Vector3D> gameMarked)
    {
        string target = Maneuvers.Target;
        foreach (var e in Targets.ShipsCached())
        {
            if (!Targets.GridOf(e.Key, out long id) || !ServerFrames.View(id, out var g)) continue;
            bool targeted = target != null && Targets.SameShip(target, id);
            bool pinned = ContactsTab.Pinned.Contains(e.Key);
            // (your own ships: the game marks them - N takes them where they are)
            if (e.Yours && !targeted && !pinned) { _pick.Add((e.Key, g.Position)); continue; }
            // the game marks it already (a contract's station, an antenna's broadcast): not twice - but N still takes it
            Vector3D wpos = g.Position; bool marked = false;
            foreach (var gm in gameMarked) if ((gm - wpos).LengthSquared() < GameMarkRadius * GameMarkRadius) { marked = true; break; }
            if (marked) { _pick.Add((e.Key, wpos)); continue; }
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(g.Position, t, out m, out _, out _);
            if (!ok) continue;
            Vector3D d = m - camModel; double dist = d.Length();
            if (!(dist > ContactMinDistance)) continue;
            if (!targeted && !pinned && !(dist <= NearbyMeters)) continue;
            // detected, not yet tracked: '≈' and a rough distance (a bearing and a rough range, no orbit yet)
            bool tracked = e.Tracked;
            string name = ContactsTab.Names.TryGetValue(e.Key, out var nn) ? nn : e.Name;
            var mk = ContactOf(id, tracked ? name : Approx(id, name));
            if (mk == null) continue;
            _items.Add(new Item { M = mk, World = camera.Position + camChart.FromInertial(d) * (System.Math.Min(dist, 5e4) / dist),
                                  Surface = tracked ? dist : SEAerospace.Sensing.Tracking.Rough(dist), Contact = true, Key = e.Key });
        }
    }

    public static bool Any => _items.Count > 0 || _tgtOn;
    public static void Clear() { _items.Clear(); Status = "no camera place"; }

    /// <summary>Inside an open HUD batch.</summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        var size = MapPipeline.ScreenSize;
        float mx = size.X * 0.06f, my = size.Y * 0.08f, u = MapPipeline.UiScale(size.Y);
        // Planets before moons, then nearest first; one whose marker would land on an earlier one's is left
        // out (two bodies in one direction printed their names over each other). Far off, a planet and its
        // moon are one place: the planet names it.
        // (contacts first: a station in front of a planet is what you are looking for)
        _items.Sort((a, b) => a.Contact != b.Contact ? (a.Contact ? -1 : 1) : a.Moon != b.Moon ? (a.Moon ? 1 : -1) : a.Surface.CompareTo(b.Surface));
        var placed = _placed; placed.Clear();
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
        // The target: brackets, its name and distance.
        if (_tgtOn && MapPipeline.ToScreen(_tgtWorld, out var ts) && ts.X >= 0 && ts.Y >= 0 && ts.X <= size.X && ts.Y <= size.Y)
        {
            float r = 18f * u, a = 7f * u;
            for (int i = 0; i < 4; i++)
            {
                float sx = (i & 1) == 0 ? -1f : 1f, sy = (i & 2) == 0 ? -1f : 1f;
                var k = ts + new Vector2(sx * r, sy * r);
                MapPipeline.ScreenLine(k, k - new Vector2(sx * a, 0), TargetColor, 2f * u);
                MapPipeline.ScreenLine(k, k - new Vector2(0, sy * a), TargetColor, 2f * u);
            }
            MapPipeline.TextScreen(ts + new Vector2(0, r + 12f * u), TargetText(), TargetColor, (0.45f) * MapPipeline.MapText);
        }
        TargetKey(session, size);
    }

    static Vector3D _tgtWorld; static bool _tgtOn; static double _tgtDist; static string _tgtName;
    // (the target's line made again only when what it shows changes: its name, or its distance at the precision HudPanel.Km
    //  prints it - whole km from 10 km, tenths under; it was two new strings every frame while a target was set)
    static string _tgtText, _tgtTextName; static long _tgtTextKey = long.MinValue;
    static string TargetText()
    {
        double m = System.Math.Max(0, _tgtDist);
        long key = m >= 1e4 ? (long)System.Math.Round(m / 1000) * 2 : (long)System.Math.Round(m / 100) * 2 + 1;
        if (_tgtText == null || key != _tgtTextKey || _tgtName != _tgtTextName)
        {
            _tgtText = $"Target  {_tgtName}  {HudPanel.Km(m)}";
            _tgtTextKey = key; _tgtTextName = _tgtName;
        }
        return _tgtText;
    }
    static readonly ColorSRGB TargetColor = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);

    /// <summary>N in flight: target the marked body or contact nearest the crosshair (within ~12% of the screen); on the
    /// target itself, clear it.</summary>
    static void TargetKey(Keen.VRage.Core.Game.Systems.Session session, Vector2 size)
    {
        bool n = MapInput.KeyPressed(KeyboardInputs.N) | MapInput.DevKeys.Remove("hudn");
        if (!n || MapView.Visible || GameUi.TopScreenNeedingInput(session) != null) return;
        Vector2 c = size * 0.5f;
        float best = size.Y * 0.12f; string key = null, name = null;
        foreach (var it in _items)
        {
            if (it.Key == null || !MapPipeline.ToScreen(it.World, out var s)) continue;
            float d = (s - c).Length();
            if (d < best) { best = d; key = it.Key; name = it.M?.Name; }
        }
        // what is not drawn here: ships the game marks, sectors, rocks you have seen (found only when N is pressed)
        foreach (var (k, w) in _pick)
            if (MapPipeline.ToScreen(w, out var s)) { float d = (s - c).Length(); if (d < best) { best = d; key = k; } }
        if (_camOk)
        {
            var secs = _secScratch; secs.Clear();
            foreach (var st in EncounterFrames.Sites) if (st.Anchor && st.Sector != null && secs.Add(st.Sector)) Consider(st.Sector, c, ref best, ref key);
            secs.Clear();
            foreach (var b in RingRocks.Belts())
                for (int i = 0; i < b.R.Length; i++) { string label = Targets.RingRockLabel(b, i); if (Contacts.KnownRock(label)) Consider(label, c, ref best, ref key); }
            foreach (var (belt, label, home, cluster) in AsteroidFrames.All()) if (Contacts.KnownRock(label)) Consider(label, c, ref best, ref key);
        }
        if (_tgtOn && MapPipeline.ToScreen(_tgtWorld, out var ts) && (ts - c).Length() <= best && key == null) key = Maneuvers.Target;
        if (key == null) { Messages.Say(SEAerospace.Flight.MessageKinds.Target, SEAerospace.Sensing.Reasons.Text(SEAerospace.Sensing.Reasons.Why.Nothing, flight: true)); return; }
        if (key == Maneuvers.Target) CleanMap.SetTarget(null);
        else CleanMap.SetTarget(key);   // (says why not when it cannot: landed)
    }

    /// <summary>N's candidates beyond the drawn markers: where this key's target is now, on the screen, nearer the crosshair.</summary>
    static void Consider(string key, Vector2 c, ref float best, ref string pick)
    {
        if (!Targets.RootAt(key, _camT, out var root)) return;
        Vector3D d = root.Position - _camModel; double dist = d.Length();
        if (!(dist > 1)) return;
        Vector3D w = _cam + _camChart.FromInertial(d) * (System.Math.Min(dist, 5e4) / dist);
        if (!MapPipeline.ToScreen(w, out var s)) return;
        float dd = (s - c).Length();
        if (dd < best) { best = dd; pick = key; }
    }

    // (N's extra candidates: the game-marked and your own ships of this frame; the camera of the last Collect)
    static readonly List<(string key, Vector3D world)> _pick = new List<(string, Vector3D)>();
    static readonly HashSet<string> _secScratch = new HashSet<string>();
    static Vector3D _cam, _camModel; static Chart _camChart; static double _camT; static bool _camOk;

    static readonly Dictionary<long, GPSMarker> _contacts = new Dictionary<long, GPSMarker>();
    // (an untracked contact's "≈ name", made again only when its name changes - it was a new string every frame for each)
    static readonly Dictionary<long, (string name, string approx)> _approx = new Dictionary<long, (string, string)>();
    static string Approx(long group, string name)
    {
        if (_approx.TryGetValue(group, out var a) && a.name == name) return a.approx;
        if (_approx.Count > 1024) _approx.Clear();   // (bounded)
        string s = "≈ " + name;
        _approx[group] = (name, s);
        return s;
    }
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
