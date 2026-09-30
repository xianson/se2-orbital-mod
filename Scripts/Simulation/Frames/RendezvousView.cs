using System.Reflection;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE MAP'S RENDEZVOUS TAB. A third tab after the game's Colonization and GPS (the game's tabs are fixed:
/// this one is drawn on their row, in their look, and switches theirs off while it is open). It shows your
/// planned path relative to your target, in the target's own curvilinear frame about the lowest body you
/// both go round (a planet for a rock, a station or its moon; the star for another planet's things): the
/// target fixed, NASA axes (ahead to the left, away from the body up), its merge and split zones dotted.
/// Your maneuver nodes are on that path and edited there exactly as on the map (the same editor: click the
/// path to add, drag the handles, right-click for exact values); the path answers as you pull.
/// Display only: nothing here ever places or tunes a burn.
/// </summary>
public static class RendezvousView
{
    public static bool Active;
    public static string Status = "-";

    // The plot in map space: its origin (the target) and plane (facing the camera), and its scale (map units per metre).
    private static Vector3D _origin, _right, _up;
    private static double _k;
    private static string _fitFor;
    private static GravityBody _common;

    static readonly ColorSRGB Axis = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.35f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.7f);
    static readonly ColorSRGB TargetCol = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);

    // ───────────────────────────── the tab ─────────────────────────────

    /// <summary>Our sub screen's id: the Map category, so the game keeps the map open on it.</summary>
    public const string TabId = "Map/Rendezvous";
    private static object _term;          // the terminal view model we added our tab to
    private static object _ourSub;        // our TerminalSubScreen in it
    private static bool _injectFailed;
    public static string TabStatus = "-";

    /// <summary>
    /// A REAL tab: the terminal's tab row is its list of sub screens, and the map's two are entries of the
    /// Map category. A third entry there ("Map/Rendezvous", headed "Rendezvous") is drawn, selected and
    /// highlighted by the game itself, the map kept open as it switches (any Map sub screen keeps it). Its
    /// content is the Colonization tab's (the left panels, and the game's own enter / leave / close of the
    /// map), so only what the map draws changes. Added once per terminal it opens with.
    /// Should that fail, a tab drawn on the row in the game's look stands in (<see cref="DrawnTab"/>).
    /// </summary>
    public static void Tab(Keen.VRage.Core.Game.Systems.Session session, Vector2 mouse)
    {
        object term = null;
        try { term = GameUi.TopScreenObject(session); } catch { }
        if (term != null && term.GetType().Name == "TerminalScreenViewModel")
        {
            if (!ReferenceEquals(term, _term)) { _term = term; _ourSub = null; _injectFailed = false; Inject(term); }
            if (_ourSub != null)
            {
                bool now = ReferenceEquals(Prop(term, "SelectedSubScreen"), _ourSub);
                if (now && !Active) _fitFor = null;
                Active = now;
                return;
            }
        }
        if (_injectFailed || term == null) DrawnTab(session, mouse);
    }

    static object Prop(object o, string name)
    {
        try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); } catch { return null; }
    }

    static void Inject(object term)
    {
        try
        {
            var subsProp = term.GetType().GetProperty("SubScreens");
            if (!(subsProp?.GetValue(term) is System.Collections.IList subs)) { Fail("no sub screen list"); return; }
            object gps = null, col = null;
            foreach (var sub in subs)
            {
                string id = Prop(Prop(sub, "Id"), "Value") as string;
                if (id == TabId) { _ourSub = sub; TabStatus = "tab already there"; return; }
                if (id == "Map/GPS") gps = sub;
                if (id == "Map/Colonization") col = sub;
            }
            if (gps == null || col == null)
            {
                var ids = new List<string>();
                foreach (var sub in subs) ids.Add((Prop(Prop(sub, "Id"), "Value") as string) ?? Prop(sub, "Id")?.ToString() ?? "?");
                Fail("no map sub screens among " + string.Join(", ", ids));
                return;
            }
            Type subType = gps.GetType();
            ConstructorInfo ctor = null;
            foreach (var c in subType.GetConstructors()) if (c.GetParameters().Length == 6) ctor = c;
            if (ctor == null) { Fail("no sub screen constructor"); return; }
            var ps = ctor.GetParameters();
            object idV = Activator.CreateInstance(ps[1].ParameterType, TabId);
            object loc = ps[2].ParameterType.GetMethod("FromString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)?.Invoke(null, new object[] { "Rendezvous" });
            if (loc == null) { Fail("no LocKey.FromString"); return; }
            object colContent = Prop(col, "Content");   // the Colonization tab's content (created as the game would)
            Func<object, object> factory = _ => colContent ?? Prop(col, "Content");
            object ours = ctor.Invoke(new object[] { Prop(col, "Category"), idV, loc, true, null, factory });
            // the Colonization tab's control hints (zoom, move)
            try { subType.GetProperty("ControlHints")?.SetValue(ours, Prop(col, "ControlHints")); } catch { }
            var arr = Array.CreateInstance(subType, subs.Count + 1);
            int i = 0;
            foreach (var sub in subs) arr.SetValue(sub, i++);
            arr.SetValue(ours, i);
            subsProp.GetSetMethod(true).Invoke(term, new object[] { arr });
            // tell the tab row
            MethodInfo raise = null;
            for (var t = term.GetType(); t != null && raise == null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "OnPropertyChanged" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) { raise = m; break; }
            raise?.Invoke(term, new object[] { "SubScreens" });
            _ourSub = ours;
            TabStatus = raise != null ? "tab added" : "tab added (row not told)";
        }
        catch (Exception e) { Fail((e.InnerException ?? e).Message); }
    }

    static void Fail(string why) { _injectFailed = true; TabStatus = "tab not added: " + why; }

    /// <summary>Select the tab (as a click on it): harness.</summary>
    public static void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        _fitFor = null;
        if (_term != null && _ourSub != null) { try { _term.GetType().GetProperty("SelectedSubScreen")?.SetValue(_term, _ourSub); } catch { } }
        else Active = true;
    }

    /// <summary>The map closed: our state only (the terminal is the game's to close; the next one gets its own tab).</summary>
    public static void Reset() { Active = false; _fitFor = null; }

    /// <summary>Back to the Colonization tab (harness).</summary>
    public static void Close()
    {
        if (Active && _term != null && _ourSub != null && ReferenceEquals(Prop(_term, "SelectedSubScreen"), _ourSub))
        {
            try
            {
                if (Prop(_term, "SubScreens") is System.Collections.IList subs)
                    foreach (var sub in subs) if (Prop(Prop(sub, "Id"), "Value") as string == "Map/Colonization") { _term.GetType().GetProperty("SelectedSubScreen")?.SetValue(_term, sub); break; }
            }
            catch { }
        }
        Active = false;
        _fitFor = null;
    }

    static readonly ColorSRGB TabText = new ColorSRGB(179 / 255f, 190 / 255f, 196 / 255f, 1f);
    static readonly ColorSRGB TabOrange = new ColorSRGB(212 / 255f, 145 / 255f, 38 / 255f, 1f);
    static readonly ColorSRGB TabEdge = new ColorSRGB(150 / 255f, 108 / 255f, 48 / 255f, 1f);
    static readonly ColorSRGB TabFill = new ColorSRGB(8 / 255f, 22 / 255f, 28 / 255f, 0.92f);

    /// <summary>
    /// The stand-in: a tab drawn on the game's row in its look (measured at 720p: 35 px high from y 72, text
    /// padded 12 px, 8 px between tabs, after GPS). Only when the real one could not be added.
    /// </summary>
    static void DrawnTab(Keen.VRage.Core.Game.Systems.Session session, Vector2 mouse)
    {
        var scr = MapPipeline.ScreenSize;
        float s = scr.Y / 720f;
        const string label = "Rendezvous";
        Vector2 m1 = MapPipeline.MeasureText(label, 1f);
        float scale = m1.Y > 0 ? 23f * s / m1.Y : 1f;
        Vector2 size = MapPipeline.MeasureText(label, scale);
        float x0 = 263f * s, y0 = 72f * s, h = 35f * s;
        var min = new Vector2(x0, y0);
        var max = new Vector2(x0 + size.X + 24f * s, y0 + h);
        bool hover = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
        if (hover && MapInput.LeftReleased && !MapCamera.DragEnded) { Active = !Active; _fitFor = null; }
        if (Active)
        {
            MapPipeline.ScreenFill(Rounded(min, max, 5f * s), TabFill);
            MapPipeline.ScreenPath(Rounded(min, max, 5f * s), true, TabEdge, 1f * s);
            MapPipeline.ScreenRect(new Vector2(min.X + 3f * s, min.Y), new Vector2(max.X - 3f * s, min.Y + 4f * s), TabOrange);
        }
        var tc = Active ? new ColorSRGB(1f, 1f, 1f, 1f) : hover ? new ColorSRGB(0.9f, 0.94f, 0.97f, 1f) : TabText;
        MapPipeline.ScreenText(new Vector2(x0 + 12f * s, y0 + (h - size.Y) * 0.5f), label, tc, scale);
        MapPipeline.Reserve(min, max);
    }

    static List<Vector2> Rounded(Vector2 min, Vector2 max, float r)
    {
        var pts = new List<Vector2>(24);
        void Arc(Vector2 c, double a0)
        {
            for (int i = 0; i <= 4; i++) { double a = a0 + Math.PI / 2 * i / 4; pts.Add(c + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r)); }
        }
        Arc(new Vector2(max.X - r, min.Y + r), -Math.PI / 2);
        Arc(new Vector2(max.X - r, max.Y - r), 0);
        Arc(new Vector2(min.X + r, max.Y - r), Math.PI / 2);
        Arc(new Vector2(min.X + r, min.Y + r), Math.PI);
        return pts;
    }

    // ───────────────────────────── the view ─────────────────────────────

    /// <summary>The target's state about its parent at t (a sector's site, or a body), or false.</summary>
    static bool TargetAt(string target, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        var reg = SystemHost.Registry;
        var body = reg?.Find(target);
        if (body != null && body.Parent != null) { parent = body.Parent; rel = body.StateInParentAt(tk); return true; }
        EncounterFrames.Site site = null;
        foreach (var st in EncounterFrames.Sites) if (st.Sector == target && (site == null || st.Anchor)) site = st;
        return site != null && EncounterFrames.Ephemeris(site, tk, out parent, out rel);
    }

    /// <summary>The lowest body both go round.</summary>
    static GravityBody Common(GravityBody a, GravityBody b)
    {
        var up = new HashSet<GravityBody>();
        for (var x = a; x != null; x = x.Parent) up.Add(x);
        for (var y = b; y != null; y = y.Parent) if (up.Contains(y)) return y;
        return SystemHost.Registry?.Root;
    }

    /// <summary>A sun-centred position at tk in the target's curvilinear frame about the common body: (along, radial, cross) in metres.</summary>
    static bool Rel(string target, GravityBody c, Vector3D rootPos, double tk, out Vector3D q)
    {
        q = default;
        if (!TargetAt(target, tk, out var tp, out var trel)) return false;
        var cs = c.OriginInRoot(tk);
        var ps = tp.OriginInRoot(tk);
        Vector3D rt = ps.Position + trel.Position - cs.Position;
        Vector3D vt = ps.Velocity + trel.Velocity - cs.Velocity;
        Vector3D ry = rootPos - cs.Position;
        double rtl = rt.Length();
        Vector3D n = Vector3D.Cross(rt, vt);
        if (rtl <= 0 || n.LengthSquared() <= 0) return false;
        Vector3D R = rt / rtl, N = Vector3D.Normalize(n), T = Vector3D.Cross(N, R);
        double cross = Vector3D.Dot(ry, N);
        Vector3D inPlane = ry - N * cross;
        double along = rtl * Math.Atan2(Vector3D.Dot(inPlane, T), Vector3D.Dot(inPlane, R));
        double radial = inPlane.Length() - rtl;
        q = new Vector3D(along, radial, cross);
        return true;
    }

    /// <summary>
    /// The view, inside the map's UI batch (instead of the map's own contents). W: map space to world
    /// (the map camera's: its wheel zooms, its drag pans).
    /// </summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, Vector2 mouse)
    {
        var scr = MapPipeline.ScreenSize;
        float u = Math.Max(1f, scr.Y / 1080f);
        MapPipeline.ClipRect = new BoundingBox2(new Vector2(scr.X * 0.255f, scr.Y * 0.1f), new Vector2(scr.X * 0.775f, scr.Y * 0.84f));
        var head = new Vector2(scr.X * 0.265f, scr.Y * 0.158f);
        string target = Maneuvers.Target;
        if (target == null)
        {
            MapPipeline.ScreenText(head, "No target", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f);
            MapPipeline.ScreenText(head + new Vector2(0, 34f * u), "Right-click a sector on the map: Set as target", Dim, 0.8f);
            Status = "no target";
            return;
        }
        if (!Maneuvers.Trajectory(t, out var legs, out _) || legs.Count == 0 || !TargetAt(target, t, out var tpar, out _))
        {
            MapPipeline.ScreenText(head, $"Rendezvous · {target}", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f);
            MapPipeline.ScreenText(head + new Vector2(0, 34f * u), "No path to compare yet", Dim, 0.8f);
            Status = "no path";
            return;
        }
        var c = Common(legs[0].Body, tpar);
        if (c == null) return;

        // The plot's plane: through where the map is looking, facing the camera (upright as you orbit the view).
        Vector3D camPos = MapPipeline.CameraPosition ?? W(MapCamera.FocusV);
        var cq = (QuaternionD)MapPipeline.CameraOrientation;
        Vector3D Loc(Vector3D world) => MapCamera.FromWorld(world);
        string key = target + "|" + c.Name;
        if (_fitFor != key || _common != c)
        {
            // Fit: your path's reach from the target on screen at about a third of its height.
            _fitFor = key; _common = c;
            _origin = MapCamera.FocusV;
            double ext = 20000;
            foreach (var l in legs)
            {
                double t0 = Math.Max(l.T0, t), t1 = Math.Min(l.T1, t + 6 * 3600);
                for (int i = 0; i <= 48 && t1 > t0; i++)
                {
                    double tk = t0 + (t1 - t0) * i / 48;
                    if (Rel(target, c, Maneuvers.RootAt(l, tk), tk, out var q)) ext = Math.Max(ext, Math.Max(Math.Abs(q.X), Math.Abs(q.Y)));
                }
            }
            // map units per screen pixel at the origin, from a unit step along the camera's right
            Vector3D rw = cq * Vector3D.Right, uw = cq * Vector3D.Up;
            Vector3D oW = W(_origin);
            _right = Vector3D.Normalize(Loc(oW + rw) - Loc(oW));
            _up = Vector3D.Normalize(Loc(oW + uw) - Loc(oW));
            // (a small step: a whole map unit can be far off the screen)
            double step = Math.Max(1e-9, MapCamera.Distance * 0.05), px = 0;
            if (MapPipeline.ToScreen(W(_origin), out var s0) && MapPipeline.ToScreen(W(_origin + _right * step), out var s1)) px = (s1 - s0).Length() / step;
            _k = px > 0 ? scr.Y * 0.33 / (ext * px) : 1e-9;
        }
        else
        {
            // keep facing the camera as it turns
            Vector3D oW = W(_origin);
            _right = Vector3D.Normalize(Loc(oW + cq * Vector3D.Right) - Loc(oW));
            _up = Vector3D.Normalize(Loc(oW + cq * Vector3D.Up) - Loc(oW));
        }
        // NASA axes: behind to the right (ahead to the left), away from the body up.
        Vector3D P(double along, double radial) => _origin + _right * (-along * _k) + _up * (radial * _k);
        Func<Vector3D, double, Vector3D> toMap = (root, tk) => Rel(target, c, root, tk, out var q) ? P(q.X, q.Y) : _origin;

        // Axes through the target, its merge and split zones, the target itself.
        if (MapPipeline.ToScreen(W(_origin), out var os))
        {
            var clip = MapPipeline.ClipRect.Value;
            MapPipeline.ScreenLine(new Vector2(clip.Min.X, os.Y), new Vector2(clip.Max.X, os.Y), Axis, 1f * u);
            MapPipeline.ScreenLine(new Vector2(os.X, clip.Min.Y), new Vector2(os.X, clip.Max.Y), Axis, 1f * u);
            MapPipeline.ScreenText(new Vector2(clip.Min.X + 8f * u, os.Y - 22f * u), "ahead", Dim, 0.6f);
            MapPipeline.ScreenText(new Vector2(clip.Max.X - 70f * u, os.Y - 22f * u), "behind", Dim, 0.6f);
            MapPipeline.ScreenText(new Vector2(os.X + 8f * u, clip.Min.Y + 6f * u), $"away from {SystemHost.DisplayName(c.Name)}", Dim, 0.6f);
            MapPipeline.ScreenText(new Vector2(os.X + 8f * u, clip.Max.Y - 24f * u), $"toward {SystemHost.DisplayName(c.Name)}", Dim, 0.6f);
            foreach (var (rz, name) in new[] { (ServerFrames.CaptureEnterRadius, "arrive"), ((double)ServerFrames.SlotRadius, "leave") })
                if (MapPipeline.ToScreen(W(P(rz, 0)), out var rs))
                {
                    float rpx = Math.Abs(rs.X - os.X);
                    if (rpx > 6f * u && rpx < scr.Y) { MapStyle.BoundaryCircle(os, rpx, HudPanel.Alpha(TargetCol, 0.5f), MapStyle.Thin(u), u); if (rpx > 40f * u) MapPipeline.ScreenText(os + new Vector2(rpx * 0.71f + 4f * u, -rpx * 0.71f - 18f * u), $"{name} {HudPanel.Km(rz)}", HudPanel.Alpha(TargetCol, 0.7f), 0.5f); }
                }
            float h = 5f * u;
            MapPipeline.ScreenFill(new List<Vector2> { os + new Vector2(-h, -h), os + new Vector2(h, -h), os + new Vector2(h, h), os + new Vector2(-h, h) }, TargetCol);
            MapPipeline.ScreenText(os + new Vector2(10f * u, 6f * u), target, TargetCol, 0.7f);
        }

        // Your path and nodes: the map's own editor, drawn through the relative frame (no ghost pinning: every arc at its true time).
        Maneuvers.MapDraw(toMap, W, double.PositiveInfinity, t, mouse, null, c.Name, allLive: true);

        MapPipeline.ClipRect = null;
        MapPipeline.ScreenText(head, $"Rendezvous · {target} · about {SystemHost.DisplayName(c.Name)}", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f);
        string line = Maneuvers.TargetLine(t);
        if (line != null) MapPipeline.ScreenText(head + new Vector2(0, 34f * u), line, Dim, 0.8f);
        MapPipeline.ScreenText(new Vector2(scr.X * 0.265f, scr.Y * 0.9f), "Click the path: add a maneuver   ·   Drag a handle: change the burn   ·   Right-click: options   ·   Wheel: zoom   ·   Drag: pan", Dim, 0.74f);
        Status = $"target {target} about {c.Name}, k {_k:G3}";
    }
}
