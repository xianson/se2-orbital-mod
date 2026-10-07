using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The map (KSP-like): one continuous zoom at true scale about the body it is about (the one you
/// focus, else yours), drawn through the game's map pipeline (MapPipeline: screen paths, fills, icons,
/// text in the map's font). The system follows the game's chart: Delfos in the centre, a sector per
/// planet, the rest of the sectors orbiting Delfos. A body's own system (moons, its sector) shows once
/// it is big enough on screen.
///  - CleanMap.cs          the draw, the view (anchor, zoom limits, level of detail), curves
///  - CleanMap.Bodies.cs   the star, planets, moons and their orbits; your orbit
///  - CleanMap.Sectors.cs  sector areas, markers, colours, groups
///  - CleanMap.Ui.cs       the list, the path, hints, tooltips, focus, the menu, pins
/// </summary>
public static partial class CleanMap
{
    /// <summary>The terminal's tab row ends here (a fraction of its design cell's height): the map is clipped below it.</summary>
    public const float TabRowBottom = MapLayout.TabRowBottom;
    public static bool Enabled = true;
    public static string Focus = "auto";   // map units (fills most of the view at the map's default zoom)
    public const double SolarRadius = 1.90;
    /// <summary>The system's star, as the game names it.</summary>
    public const string StarName = "Delfos";       // u (camera distance / original max) where the view switches
    /// <summary>
    /// The sector mesh is rebuilt when the view changes, and for motion at most this often (wall
    /// seconds) and only once the sectors have moved (MeshRebuildSimSeconds of game time). Each rebuild
    /// swaps a new model into the game's renderer; doing that twice a second piled up GPU load.
    /// </summary>
    public static double MeshRebuildSeconds = 5.0;
    public static double MeshRebuildSimSeconds = 600.0;
    private static double _lastMeshSimT = double.NaN;

    static readonly ColorSRGB Line = new ColorSRGB(0.60f, 0.72f, 0.88f, 0.28f);
    static readonly ColorSRGB LineSel = new ColorSRGB(1.00f, 0.85f, 0.30f, 0.95f);
    static readonly ColorSRGB You = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);   // you: cyan (gold is 'selected')
    static readonly ColorSRGB Text = new ColorSRGB(0.94f, 0.97f, 1.00f, 1f);
    static readonly ColorSRGB BeltLine = new ColorSRGB(0.75f, 0.68f, 0.55f, 0.22f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.70f, 0.78f, 0.88f, 0.9f);

    static readonly ColorSRGB TargetText = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);
    static readonly ColorSRGB RowHover = new ColorSRGB(0.30f, 0.55f, 0.65f, 0.22f);

    /// <summary>The text, cut with an ellipsis to fit a width (px) at a scale.</summary>
    internal static string Fit(string text, float width, float scale)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // (remembered: a list row's text is cut to its column every frame - a substring and a measure per character
        //  removed - and it rarely changes)
        var key = (text, (int)MathF.Round(width), (int)MathF.Round(scale * 1000f));
        if (_fit.TryGetValue(key, out var done)) return done;
        string r = "";
        if (MapPipeline.MeasureText(text, scale).X <= width) r = text;
        else
            for (int n = text.Length - 1; n > 0; n--)
            {
                string t = text.Substring(0, n).TrimEnd() + "...";
                if (MapPipeline.MeasureText(t, scale).X <= width) { r = t; break; }
            }
        if (!MapPipeline.CanMeasure) return r;   // (an estimate: not kept)
        if (_fit.Count > 512) _fit.Clear();
        _fit[key] = r;
        return r;
    }
    static readonly Dictionary<(string, int, int), string> _fit = new Dictionary<(string, int, int), string>();

    private static double _lastMesh;
    private static string _lastKey;
    public static string Status = "-";
    /// <summary>Where the star is on the map this frame (world), for the game's star model.</summary>
    public static Vector3D? StarWorld;
    /// <summary>A planet's own system has the view (the star's lines step back).</summary>
    private static bool _planetLevel;
    /// <summary>DEV: this frame's view (the body it is about, the zoom).</summary>
    public static string Frame = "-";

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session, object sectorsRenderer, object mapConfig,
                            List<Band> bands, SystemRegistry reg, Vector3D mapPos, Quaternion orient,
                            double u, double t, string playerPlanet, Vector3D playerRel, KeplerianElements? playerOrbit,
                            HashSet<string> globes)
    {
        _session = session;
        bool cam = MapCamera.Distance > 0;
        Vector3D W(Vector3D v) => cam ? MapCamera.ToWorld(v) : mapPos + (QuaternionD)orient * v;
        _wscale = cam ? MapCamera.Scale : 1.0;
        var root = reg.Root;

        // ONE map (KSP): every body at its true place, about the body the map is about (the one you
        // focused, else yours). Its origin follows that body; switching bodies keeps the view still.
        GravityBody anchor = null;
        if (ViewFocus != null) anchor = ViewFocus == StarName || ViewFocus == root?.Name ? root : reg.Find(ViewFocus);
        if (anchor == null && playerPlanet != null) anchor = reg.Find(playerPlanet);
        anchor ??= root;
        if (anchor == null) return;
        if (_anchor != null && _anchor != anchor && cam)
            MapCamera.Shift(-Flat(anchor.OriginInRoot(t).Position - _anchor.OriginInRoot(t).Position));
        _anchor = anchor;
        Vector3D aPos = anchor.OriginInRoot(t).Position;
        Vector3D Map(Vector3D rootPos) => Flat(rootPos - aPos);
        // The closest zoom: the body stays smaller than the camera's distance.
        double aR = anchor.IsRoot ? SystemHost.StarRadius : (reg.FindDefinition(anchor.Name)?.RadiusMeters ?? 6e4);
        // (Only near the body: looking elsewhere, at a sector or a ship out in the system, zoom right in.)
        bool nearBody = !cam || MapCamera.FocusV.Length() < 20 * aR * Sigma;
        GameMap.MinZoom = nearBody ? Math.Max(1e-7, 2.5 * aR * Sigma) : 1e-7;

        // Screen pixels per map unit at the view's centre (the level of detail goes by it).
        _pxPerV = 0;
        if (cam && MapPipeline.ToScreen(W(MapCamera.FocusV), out var f0))
        {
            double step = MapCamera.Distance * 0.1;
            foreach (var ax in new[] { new Vector3D(step, 0, 0), new Vector3D(0, 0, step) })
                if (MapPipeline.ToScreen(W(MapCamera.FocusV + ax), out var f1)) _pxPerV = Math.Max(_pxPerV, (f1 - f0).Length() / step);
        }
        { var sw = _detailPrev; _detailPrev = _detail; _detail = sw; _detail.Clear(); }

        // A body focused (double-click, breadcrumb, menu): glide there and frame it, once re-anchored.
        if (_zoomTo != null && _zoomTo == anchor && cam && _pxPerV > 0)
        {
            MapCamera.PanTo(W(Vector3D.Zero), smooth: true);
            double fitV = anchor.IsRoot ? SolarRadius : FrameOf(anchor, bands, reg) * Sigma;
            MapCamera.ZoomTo(MapCamera.Distance * fitV * _pxPerV / (MapPipeline.ScreenSize.Y * 0.36));
            _zoomTo = null;
        }
        // Opening the map: centred on your body (the camera starts there), framed on your orbit (as KSP).
        if (_fitPending && cam && _pxPerV > 0)
        {
            double far = anchor.IsRoot ? SolarRadius / Sigma : FrameOf(anchor, bands, reg);
            if (!anchor.IsRoot && playerPlanet == anchor.Name && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))
            {
                var o = playerOrbit.Value;
                far = o.IsElliptic ? o.SemiMajorAxis * (1 + o.Eccentricity) : anchor.SoiRadius;
                far = Math.Max(far, 1.6 * aR);
            }
            if (IsFinite(far) && far > 0) MapCamera.ZoomTo(MapCamera.Distance * far * Sigma * _pxPerV / (MapPipeline.ScreenSize.Y * 0.28));
            _fitPending = false;
        }

        Frame = $"anchor {anchor.Name} pxPerV {_pxPerV:G3} d {MapCamera.Distance:G3} min {GameMap.MinZoom:G3} focusV {MapCamera.FocusV.Length():G3} scale {_wscale:G3}";
        _hits.Clear(); _crumbs.Clear(); SwapLens();
        GravityBody focusPlanet = anchor.IsRoot ? null : anchor.Parent != null && !anchor.Parent.IsRoot ? anchor.Parent : anchor;
        string focus = focusPlanet?.Name;
        // The planet's own level (its sectors listed) while its system shows.
        bool sys = focusPlanet == null || !_detailPrev.Contains(focusPlanet.Name);
        _planetLevel = !sys;
        _viewBody = anchor.IsRoot ? null : anchor;
        ViewKey = sys ? "system" : anchor.Name;

        var parts = new List<MapPipeline.Part>();
        var ordered = Ordered(bands);
        _deferred.Clear();
        bool ui = MapPipeline.UiBegin(session, mapConfig);
        try
        {
            RendezvousView.Tab(session, Mouse);
            if (RendezvousView.Active)
            {
                // The Rendezvous tab has the view: the relative plot instead of the map's contents.
                RendezvousView.Draw(session, reg, t, W, Mouse);
                if (ManeuverEditor && ui) ContextMenu(bands, t);
                WarpBar.DrawMap(Mouse);
            }
            else
            {
            // The game's own panels (left column, tab bar, bottom hints): map labels keep off them.
            var scrR = MapPipeline.ScreenSize;
            // (places in the terminal's design cell, MapLayout: right at every screen shape, not only 16:9)
            MapPipeline.Reserve(Vector2.Zero, new Vector2(MapLayout.X(0.255f), scrR.Y));
            MapPipeline.Reserve(Vector2.Zero, new Vector2(scrR.X, MapLayout.Y(TabRowBottom)));
            MapPipeline.Reserve(new Vector2(0, MapLayout.Y(0.93f)), scrR);
            // The system level lists every sector (the star's opened); a planet's level only that planet's.
            long s0 = ModCost.Start();
            DrawList(ordered, b => sys ? b.Home.Host == root.Name || b.Selected : b.Host == focus,
                b => sys || b.Host == focus);
            ModCost.Sec("list").Stop(s0); s0 = ModCost.Start();
            // The title's area is kept free of map labels now; the title itself is drawn after the map, over its lines.
            MapPipeline.Reserve(MapLayout.P(0.26f, 0.151f), new Vector2(MapLayout.X(0.56f), MapLayout.Y(0.151f) + TitleHeight(scrR)));
            // The map itself (orbits, sectors, the plan) is clipped to the open area between the panels.
            MapPipeline.ClipRect = MapLayout.OpenArea;   // (below the terminal's tab row: a label drawn under it read as part of the tabs)

            // Every globe hides the lines behind it.
            foreach (var body in reg.Bodies)
            {
                if (body.IsRoot || !SystemHost.BeaconOf.ContainsKey(body.Name)) continue;
                double br = (reg.FindDefinition(body.Name)?.RadiusMeters ?? 0) * Sigma * _wscale;
                MapPipeline.Occlude(W(Map(body.OriginInRoot(t).Position)), br);
            }
            // The sun and its planets, the belt and the outer ring (about the sun).
            _off = Map(Vector3D.Zero);
            Vector3D starV = _off;
            DrawSolar(parts, bands, reg, t, playerPlanet, globes, v => W(v + starV), playerOrbit);
            ModCost.Sec("solar").Stop(s0); s0 = ModCost.Start();
            // Each planet's and moon's own system: its sectors and moons pop in once it is big enough
            // on screen to read; yours is always drawn (your orbit and where you are).
            foreach (var p in root.Children)
            {
                if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
                Vector3D pv = Map(p.OriginInRoot(t).Position);
                bool pd = Detail(p.Name, FrameOf(p, bands, reg) * Sigma, pv);
                _off = pv;
                DrawSystem(parts, bands, reg, p, t, playerPlanet, playerRel, playerOrbit, globes, v => W(v + pv), pd);
                foreach (var m in p.Children)
                {
                    if (!SystemHost.BeaconOf.ContainsKey(m.Name)) continue;
                    Vector3D mv = Map(m.OriginInRoot(t).Position);
                    bool md = pd && Detail(m.Name, FrameOf(m, bands, reg) * Sigma, mv);
                    if (!md && m.Name != playerPlanet) continue;
                    _off = mv;
                    DrawSystem(parts, bands, reg, m, t, playerPlanet, playerRel, playerOrbit, globes, v => W(v + mv), md);
                }
            }
            _off = Vector3D.Zero;
            ModCost.Sec("systems").Stop(s0); s0 = ModCost.Start();
            foreach (var d in _deferred) d();
            _deferred.Clear();
            ModCost.Sec("deferred").Stop(s0); s0 = ModCost.Start();
            Overlay(r => Map(r), double.PositiveInfinity, W, t, reg);
            ModCost.Sec("overlay").Stop(s0); s0 = ModCost.Start();
            _toMap = (r, tt) => Flat(r - anchor.OriginInRoot(tt).Position);
            _limit = double.PositiveInfinity;

            string selName = null;
            foreach (var bd in bands) if (bd.Selected) selName = bd.Name;
            if (_toMap != null && ManeuverEditor) Maneuvers.MapDraw(_toMap, W, _limit, t, Mouse, selName, anchor.Name);   // editable at every zoom
            ModCost.Sec("trajectory").Stop(s0); s0 = ModCost.Start();
            MapPipeline.ClipRect = null;   // menus, the warp bar, hints: unclipped
            long u0 = ModCost.Start();
            Title(anchor.IsRoot ? null : anchor, playerPlanet, playerOrbit);
            ModCost.Sec("ui.title").Stop(u0); u0 = ModCost.Start();
            // (no UI batch: nothing is drawn - nor any click taken: an invisible menu swallowed the game's clicks and its items
            //  could be hit blind)
            if (ManeuverEditor && ui) ContextMenu(bands, t);
            _lastW = W; _lastSolar = false;
            ModCost.Sec("ui.menu").Stop(u0); u0 = ModCost.Start();
            if (ui) FocusInput(reg, t, W, false);
            BodyTooltip(reg, playerPlanet, playerOrbit);
            ModCost.Sec("ui.focus+tip").Stop(u0); u0 = ModCost.Start();
            if (ui) ListInput(W);
            Hints();
            ModCost.Sec("ui.list+hints").Stop(u0); u0 = ModCost.Start();
            WarpBar.DrawMap(Mouse);
            ModCost.Sec("ui.warpbar").Stop(u0);
            ModCost.Sec("ui").Stop(s0);
            }
        }
        finally { long s1 = ModCost.Start(); MapPipeline.ClipRect = null; if (ui) MapPipeline.UiEnd(); ModCost.Sec("uiend").Stop(s1); }
        long s2 = ModCost.Start();

        // Every sector keeps a (possibly zero-size) part: the game colours parts by sector name and
        // must always find them.
        var have = new HashSet<string>();
        foreach (var pt in parts) have.Add(pt.Name);
        foreach (var bd in bands)
            if (!bd.Virtual && !have.Contains(bd.Name))   // (a virtual one: no game sector to colour)
            {
                var ph = new MapPipeline.Part { Name = bd.Name };
                // A tiny but proper hexagon under the map's centre. (A 1e-6 triangle is degenerate in
                // float: the selected sector's colour then smeared into a huge band across the view,
                // over the game's panels, and it showed in flight.)
                for (int q = 0; q < 6; q++)
                {
                    double a = Math.PI * q / 3;
                    ph.Outline.Add(new Vector3((float)(Math.Cos(a) * 2e-4), -0.002f, (float)(Math.Sin(a) * 2e-4)));
                }
                parts.Add(ph);
            }

        // A world without colonization sectors: no part at all, so the game's placeholder sector ring stayed (the wedges).
        // One tiny hexagon under the map's centre replaces its mesh with nothing visible.
        if (parts.Count == 0)
        {
            var ph = new MapPipeline.Part { Name = "OrbitalEmpty" };
            for (int q = 0; q < 6; q++)
            {
                double a = Math.PI * q / 3;
                ph.Outline.Add(new Vector3((float)(Math.Cos(a) * 2e-4), -0.002f, (float)(Math.Sin(a) * 2e-4)));
            }
            parts.Add(ph);
        }

        // The mesh changes only when the view changes, or every MeshRebuildSeconds (motion).
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string key = "unified";
        bool moved = double.IsNaN(_lastMeshSimT) || Math.Abs(t - _lastMeshSimT) > MeshRebuildSimSeconds;
        if (key != _lastKey || (moved && now - _lastMesh > MeshRebuildSeconds))
        {
            bool ok = MapPipeline.ShowParts(sectorsRenderer, parts);
            _lastKey = key; _lastMesh = now; _lastMeshSimT = t;
            Status = ok ? $"{key} parts={parts.Count}" : $"{key} mesh failed: {MapPipeline.LastError} (parts={parts.Count})";
            // (nothing of ours to show in its place - a world without colonization sectors - the game's own sector mesh
            //  would show through: hidden; closing the map restores it, GameMap.RestoreGame)
            if (!ok && sectorsRenderer != null) PlanetRenderBridge.SetRenderComponentVisible(sectorsRenderer, false);
        }
        ModCost.Sec("parts+mesh").Stop(s2);
    }

    public static void Reset() { _lastKey = null; }

    /// <summary>Map units per metre: one scale for everything (the system to the outer ring is SolarRadius).</summary>
    static double Sigma => SolarRadius / (SectorHomes.SystemOuterAU * SystemHost.AU);
    /// <summary>An ecliptic vector (x, y) in map units (x, 0, z).</summary>
    static Vector3D Flat(Vector3D v) => new Vector3D(v.X * Sigma, v.Z * Sigma, v.Y * Sigma);   // height is the map's up
    /// <summary>The body the map is about (its origin), and a body to frame once the map is about it.</summary>
    private static GravityBody _anchor, _zoomTo;
    /// <summary>World units per map unit this frame (globe sizes), and screen px per map unit at the view's centre.</summary>
    private static double _wscale = 1, _pxPerV;
    /// <summary>The map-unit offset of what is being drawn (its local origin), for the curves' view test.</summary>
    private static Vector3D _off;
    private static HashSet<string> _detail = new HashSet<string>(), _detailPrev = new HashSet<string>();

    /// <summary>A body's own system shows (its sectors, moons and labels) once it is this big on screen.</summary>
    static bool Detail(string name, double fitV, Vector3D centreV)
    {
        if (!(_pxPerV > 0)) return true;
        double px = fitV * _pxPerV;
        bool show = px > (_detailPrev.Contains(name) ? 55 : 80);   // hysteresis: no flicker at the edge
        // Well off the view: nothing of it to draw.
        if (show && MapCamera.Distance > 0 && (centreV - MapCamera.FocusV).Length() > 6 * MapCamera.Distance + fitV) show = false;
        if (show) _detail.Add(name);
        return show;
    }

    /// <summary>A body's system radius (metres): its sectors' farthest apoapsis, at least a few radii.</summary>
    static double FrameOf(GravityBody b, List<Band> bands, SystemRegistry reg)
    {
        if (b.IsRoot) return SolarRadius / Sigma;
        double frame = SectorHomes.HillRadius(b) * 0.2;
        if (!IsFinite(frame)) frame = 0;
        foreach (var bd in bands)   // its own space, its rings, its L1 / L2
            if (bd.Home.Host == b.Name)
                frame = Math.Max(frame, bd.Home.Kind == SectorHomes.Kind.Ring ? bd.Home.Outer : bd.Home.Kind == SectorHomes.Kind.Body ? bd.Home.Outer
                                        : bd.Home.Point <= 2 ? SectorHomes.HillRadius(b) * 1.15 : 0);
        frame = Math.Max(frame, 8 * (reg.FindDefinition(b.Name)?.RadiusMeters ?? 0));
        return frame * 1.15;
    }

    /// <summary>
    /// A smooth curve at any zoom: sampled coarsely, then split where it is long on screen, and only
    /// near the view (zoomed onto a planet, the sun's circles are a straight line through it; a
    /// fixed number of chords missed the planet by a good part of the screen).
    /// </summary>
    /// <summary>Skip curve pieces wholly off the view (harness switch, to compare).</summary>
    public static bool CullCurves = true;

    static void Curve(Func<double, Vector3D> at, Func<Vector3D, Vector3D> W, double a0, double a1, int n, ColorSRGB col, float px, bool depthCue = false, Func<Vector2, bool> skip = null)
    {
        var dim = HudPanel.Alpha(col, col.A / 255f * 0.4f);
        bool runDim = false;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        bool cam = MapCamera.Distance > 0;
        Vector3D f = cam ? MapCamera.FocusV - _off : Vector3D.Zero;
        double reach = cam ? 5 * MapCamera.Distance : double.PositiveInfinity;
        int budget = 3000;   // points per curve: never a runaway (a split that cannot resolve froze the game)
        void Seg(double ta, Vector3D pa, double tb, Vector3D pb, int depth)
        {
            if (--budget < 0) return;
            Vector3D ab = pb - pa; double len = ab.Length();
            double k = len > 0 ? Math.Clamp(Vector3D.Dot(f - pa, ab) / (len * len), 0, 1) : 0;
            if ((pa + ab * k - f).Length() > reach + 0.3 * len) return;   // the arc keeps near its chord
            bool oa = MapPipeline.ToScreen(W(pa), out var sa), ob = MapPipeline.ToScreen(W(pb), out var sb);
            if (oa && ob && CullCurves)
            {
                // Wholly off the view, with room for the arc's bulge off its chord: nothing to split (zoomed
                // in on a planet, the sun's orbits are huge on screen; split to 12 px they cost ~1 ms a frame).
                var scr = MapPipeline.ScreenSize; float m = 0.3f * (sb - sa).Length() + 4f * u;
                if (Math.Max(sa.X, sb.X) < -m || Math.Min(sa.X, sb.X) > scr.X + m || Math.Max(sa.Y, sb.Y) < -m || Math.Min(sa.Y, sb.Y) > scr.Y + m)
                { Flush(); return; }
            }
            if (oa && ob && ((sb - sa).Length() < 12f * u || depth >= 22))
            {
                if (MapPipeline.Occluded(W((pa + pb) * 0.5))) { Flush(); return; }   // behind a body's globe
                if (MapPipeline.InSectorArea(0.5f * (sa + sb))) { Flush(); return; }   // through a sector: not drawn
                if (skip != null && skip(0.5f * (sa + sb))) { Flush(); return; }
                Emit(sa, sb, depthCue && (pa.Y + pb.Y) < 0);
                return;
            }
            // Not on screen at either end (behind the camera, or far off it): a few splits to find where
            // it comes into view, no more; one end on screen: deeper.
            int max = oa && ob ? 22 : oa || ob ? 14 : 7;
            if (depth >= max) return;
            double tm = 0.5 * (ta + tb); Vector3D pm = at(tm);
            Seg(ta, pa, tm, pm, depth + 1); Seg(tm, pm, tb, pb, depth + 1);
        }
        // Consecutive pieces join into runs; each run is one smooth path.
        var run = _curveRun; run.Clear();   // (one list for every curve: ScreenPath draws it at once)
        void Flush() { if (run.Count > 1) MapPipeline.ScreenPath(run, false, runDim ? dim : col, px * u); run.Clear(); }
        void Emit(Vector2 sa, Vector2 sb, bool below)
        {
            if (run.Count > 0 && ((run[run.Count - 1] - sa).LengthSquared() > 0.25f || below != runDim)) { var last = run[run.Count - 1]; Flush(); if ((last - sa).LengthSquared() <= 0.25f) run.Add(last); }
            runDim = below;
            if (run.Count == 0) run.Add(sa);
            run.Add(sb);
        }
        Vector3D prev = at(a0);
        for (int i = 1; i <= n; i++)
        {
            double ti = a0 + (a1 - a0) * i / n; Vector3D pi = at(ti);
            Seg(a0 + (a1 - a0) * (i - 1) / n, prev, ti, pi, 0);
            prev = pi;
        }
        Flush();
    }

    static readonly List<Vector2> _curveRun = new List<Vector2>();
    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    private static Func<Vector3D, double, Vector3D> _toMap;
    private static double _limit;
    public static bool ManeuverEditor = true; private static Vector2 _lastClickAt; private static bool _lastSolar;

    /// <summary>The planet the zoom took you to (zooming in over it); null until a zoom chooses one.</summary>
    public static string ViewFocus;
    /// <summary>What the map shows (the system, or a body's view): menus close when it changes.</summary>
    public static string ViewKey = "";

    /// <summary>Where a body (a planet about the sun) sits in the system view, in map-local units.</summary>
    public static Vector3D SolarLocal(GravityBody b, double t)
        => Flat(b.OriginInRoot(t).Position - (_anchor != null ? _anchor.OriginInRoot(t).Position : Vector3D.Zero));

    /// <summary>The map closed: the next opening starts from where you are.</summary>
    public static void ResetView() { ViewFocus = null; _fitPending = true; _anchor = null; _zoomTo = null; }
    private static bool _fitPending = true;

    /// <summary>The planet the view is about (null: the system view). Only its own things are drawn.</summary>
    private static GravityBody _viewBody;

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
