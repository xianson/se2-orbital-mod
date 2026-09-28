using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The clean map, rendered through the game's own map pipeline (MapPipeline):
///  - band sections are mesh parts NAMED AFTER THEIR SECTORS inside the game's sector model, so
///    the game's sector material draws them (gradient edges) and colours them by colonization
///    state, and its selection highlight works on them;
///  - orbit rings, Lagrange loops and your orbit are smooth screen-space UI lines;
///  - labels are in the map's own font.
/// Two views, fixed in map units (so zooming really zooms, and the mesh only changes when things
/// move): SYSTEM (one planet system, centred; real angles, real ORDER of radii at readable
/// spacing) and SOLAR (sun, planet orbits, Trojan groups), switched by the zoom.
/// </summary>
public static class CleanMap
{
    public static bool Enabled = true;
    public static string Focus = "auto";
    public const double SystemRadius = 0.80;   // map units (fills most of the view at the map's default zoom)
    public const double SolarRadius = 1.90;
    /// <summary>The system's star, as the game names it.</summary>
    public const string StarName = "Delfos";
    public const double SolarZoom = 2.2;       // u (camera distance / original max) where the view switches
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

    public sealed class Band
    {
        public string Name, Host;
        public SectorHomes.Home Home;
        public bool Selected;
        public int Number;
        public Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState State;
    }

    static readonly ColorSRGB StLocked = new ColorSRGB(0.62f, 0.66f, 0.74f, 1f);
    static readonly ColorSRGB StUnlocked = new ColorSRGB(0.35f, 0.68f, 1.00f, 1f);
    static readonly ColorSRGB StColonized = new ColorSRGB(0.40f, 0.92f, 0.55f, 1f);
    static ColorSRGB StateColor(Band b) => b.State == Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState.Colonized ? StColonized
        : b.State == Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState.Unlocked ? StUnlocked : StLocked;

    /// <summary>Where a sector lives, as a list group ("Kemik orbit", "Main belt", ...).</summary>
    static string Group(Band b) => b.Home.Kind switch
    {
        SectorHomes.Kind.OwnPlanet => b.Host,
        SectorHomes.Kind.Ellipse => b.Host + " orbit",
        SectorHomes.Kind.L1 or SectorHomes.Kind.L2 => b.Host + " Lagrange points",
        SectorHomes.Kind.L4 or SectorHomes.Kind.L5 => b.Host + " Trojans",
        SectorHomes.Kind.Belt => "Main belt",
        SectorHomes.Kind.Ring => b.Name == SectorHomes.StarSector ? StarName : b.Home.AU < 0.8 ? "Inner ring" : "Outer ring",
        _ => "Other",
    };

    static int GroupRank(Band b)
    {
        int planet = Array.IndexOf(SystemHost.PlanetOrder, b.Host); if (planet < 0) planet = 9;
        return b.Home.Kind switch
        {
            SectorHomes.Kind.OwnPlanet => planet * 10, SectorHomes.Kind.Ellipse => planet * 10 + 1,
            SectorHomes.Kind.L1 or SectorHomes.Kind.L2 => planet * 10 + 2, SectorHomes.Kind.L4 or SectorHomes.Kind.L5 => planet * 10 + 3,
            SectorHomes.Kind.Belt => 200, SectorHomes.Kind.Ring => b.Name == SectorHomes.StarSector ? 1 : b.Home.AU < 0.8 ? 150 : 300, _ => 400,
        };
    }

    /// <summary>Number the sectors in list order (group, then distance).</summary>
    static List<Band> Ordered(List<Band> bands)
    {
        var list = new List<Band>(bands);
        list.Sort((x, y) => { int g = GroupRank(x).CompareTo(GroupRank(y)); return g != 0 ? g : x.Home.A.CompareTo(y.Home.A); });
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        return list;
    }

    static string StateText(Band b) => b.State == Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState.Colonized ? "colonized"
        : b.State == Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState.Unlocked ? "open" : "locked";

    static string Where(Band b)
    {
        string place = WherePlace(b);
        string est = Estimate(b);
        return est != null ? $"{place}  ·  {est}" : place;
    }

    private static readonly Dictionary<string, (string text, double at)> _est = new Dictionary<string, (string, double)>();

    /// <summary>
    /// A quick guide from your orbit (Hohmann, circular): delta-v and trip time to the sector's home.
    /// Same planet: the two-burn transfer. Another planet: escape + heliocentric transfer + capture at the
    /// home's radius. A heliocentric home (belt, ring, L4/L5): escape + transfer + velocity match. The
    /// Plan route button computes the real thing.
    /// </summary>
    static string Estimate(Band b)
    {
        double now = Wall();
        if (_est.TryGetValue(b.Name, out var c) && now - c.at < 2.0) return c.text;
        string txt = null;
        try
        {
            double t = SystemHost.Now;
            var reg = SystemHost.Registry;
            if (Maneuvers.Base(t, out var body, out var el) && reg != null && b.Home.Kind != SectorHomes.Kind.OwnPlanet)
            {
                double r1 = el.IsElliptic ? el.SemiMajorAxis : OrbitPropagation.StateAt(el, t).Position.Length();
                double Hohmann(double mu, double ra, double rb, out double tof)
                {
                    double at = (ra + rb) / 2;
                    tof = Math.PI * Math.Sqrt(at * at * at / mu);
                    return Math.Abs(Math.Sqrt(mu / ra) * (Math.Sqrt(2 * rb / (ra + rb)) - 1)) + Math.Abs(Math.Sqrt(mu / rb) * (1 - Math.Sqrt(2 * ra / (ra + rb))));
                }
                double dv = double.NaN, tt = 0;
                var host = reg.Find(b.Host);
                bool helioHome = b.Home.Kind == SectorHomes.Kind.Belt || b.Home.Kind == SectorHomes.Kind.Ring || b.Home.Kind == SectorHomes.Kind.L4 || b.Home.Kind == SectorHomes.Kind.L5;
                if (!helioHome && host == body && b.Home.Kind == SectorHomes.Kind.Ellipse)
                    dv = Hohmann(body.Mu, r1, b.Home.A, out tt);
                else if (!helioHome && host == body)   // L1 / L2: out to the Hill radius
                    dv = Hohmann(body.Mu, r1, SectorHomes.HillRadius(body), out tt);
                else if (body.Parent != null && body.Parent.IsRoot && host != null)
                {
                    var root = body.Parent;
                    double rB = body.StateInParentAt(t).Position.Length();
                    double rT = helioHome ? HomeHelioRadius(b, host) : host.StateInParentAt(t).Position.Length();
                    double at = (rB + rT) / 2;
                    tt = Math.PI * Math.Sqrt(at * at * at / root.Mu);
                    double vB = Math.Sqrt(root.Mu / rB), vT = Math.Sqrt(root.Mu / rT);
                    double vinfD = Math.Abs(Math.Sqrt(root.Mu * (2 / rB - 1 / at)) - vB), vinfA = Math.Abs(vT - Math.Sqrt(root.Mu * (2 / rT - 1 / at)));
                    double ej = Math.Sqrt(vinfD * vinfD + 2 * body.Mu / r1) - Math.Sqrt(body.Mu / r1);
                    double arr;
                    if (helioHome) arr = vinfA;
                    else
                    {
                        double rc = b.Home.Kind == SectorHomes.Kind.Ellipse ? b.Home.A : (reg.FindDefinition(host.Name)?.RadiusMeters ?? 6e4) + 150e3;
                        arr = Math.Sqrt(vinfA * vinfA + 2 * host.Mu / rc) - Math.Sqrt(host.Mu / rc);
                    }
                    dv = ej + arr;
                }
                if (!double.IsNaN(dv)) txt = $"~{dv:N0} m/s, {Maneuvers.Clock(tt)}";
            }
        }
        catch { }
        _est[b.Name] = (txt, now);
        return txt;
    }

    static double HomeHelioRadius(Band b, GravityBody host) => b.Home.Kind switch
    {
        SectorHomes.Kind.Belt => (SectorHomes.BeltInnerAU + SectorHomes.BeltOuterAU) * 0.5 * SystemHost.AU,
        SectorHomes.Kind.Ring => b.Home.AU * SystemHost.AU,
        _ => host.StateInParentAt(SystemHost.Now).Position.Length(),   // Trojans share the planet's orbit
    };

    static string WherePlace(Band b) => b.Home.Kind switch
    {
        SectorHomes.Kind.OwnPlanet => "near space",
        SectorHomes.Kind.Ellipse => $"{b.Home.A / 1000:N0} km",
        SectorHomes.Kind.L1 => "L1", SectorHomes.Kind.L2 => "L2", SectorHomes.Kind.L4 => "L4", SectorHomes.Kind.L5 => "L5",
        SectorHomes.Kind.Belt => $"{SectorHomes.BeltInnerAU:F1}-{SectorHomes.BeltOuterAU:F1} AU",
        SectorHomes.Kind.Ring => $"{b.Home.AU:0.##} AU",
        _ => "",
    };

    static string PeriodText(Band b)
    {
        var planet = SystemHost.Registry?.Find(b.Host);
        if (planet == null) return "";
        double T = SectorHomes.Period(b.Home, planet);
        return T > 2 * 86400 ? $"{T / 86400:F1} d" : $"{T / 3600:F0} h";
    }

    /// <summary>The sector list: grouped, numbered, state dot, name, where; on the right, below the game's index box.</summary>
    static readonly ColorSRGB PanelFill = new ColorSRGB(0.02f, 0.05f, 0.07f, 0.78f);
    static readonly ColorSRGB TargetText = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);
    static readonly ColorSRGB RowHover = new ColorSRGB(0.30f, 0.55f, 0.65f, 0.22f);

    /// <summary>The list's screen area (last frame): a click there goes to a row.</summary>
    private static BoundingBox2 _listBox;
    /// <summary>Where each sector's marker was drawn (world), for centring on it.</summary>
    private static readonly Dictionary<string, Vector3D> _markerAt = new Dictionary<string, Vector3D>();

    /// <summary>
    /// The sector list, as a panel in the game's style: group headers, one row per sector (number,
    /// state dot, name, where), the hovered row lit, the selected one marked with a bar. A click on a
    /// row selects it (the game's own click) and the map glides to it.
    /// </summary>
    static void DrawList(List<Band> ordered, Func<Band, bool> expanded, Func<Band, bool> listed)
    {
        ordered = ordered.FindAll(b => listed(b));
        Vector2 scr = MapPipeline.ScreenSize;
        float k = MapPipeline.TextScale;   // rows and columns grow with the text
        float x = scr.X * 0.782f, y = scr.Y * 0.27f, line = scr.Y * 0.0275f * k, scale = 0.78f;
        float x0 = scr.X * 0.777f, x1 = scr.X * 0.975f;
        // Lay out first (the panel goes behind), then draw.
        var rows = new List<(Band b, bool header, string text, float y)>();
        string group = null;
        foreach (var b in ordered)
        {
            string g = Group(b);
            if (g != group)
            {
                if (group != null) y += line * 0.35f;
                int count = ordered.FindAll(o => Group(o) == g).Count;
                rows.Add((b, true, expanded(b) ? g.ToUpperInvariant() : $"{g.ToUpperInvariant()}  ({count})", y));
                y += line;
                group = g;
            }
            if (!expanded(b)) continue;
            rows.Add((b, false, null, y));
            y += line;
        }
        if (rows.Count == 0) return;
        float top = rows[0].y - line * 0.4f, bottom = y + line * 0.2f;
        MapPipeline.ScreenRect(new Vector2(x0, top), new Vector2(x1, bottom), PanelFill);
        _listBox = new BoundingBox2(new Vector2(x0, top), new Vector2(x1, bottom));
        foreach (var r in rows)
        {
            if (r.header) { MapPipeline.ScreenText(new Vector2(x, r.y), r.text, Dim, scale * 0.85f); continue; }
            var b = r.b;
            var rowMin = new Vector2(x0, r.y - line * 0.12f); var rowMax = new Vector2(x1, r.y + line * 0.88f);
            if (b.Name == Hovered) MapPipeline.ScreenRect(rowMin, rowMax, RowHover);
            if (b.Selected) MapPipeline.ScreenRect(rowMin, new Vector2(x0 + 3f, rowMax.Y), LineSel);
            var c = b.Selected ? LineSel : Text;
            MapPipeline.PickName = b.Name;
            MapPipeline.ScreenText(new Vector2(x, r.y), b.Number.ToString(), Dim, scale);
            MapPipeline.ScreenDot(new Vector2(x + scr.Y * 0.030f * k, r.y + line * 0.42f), 4.5f, StateColor(b));
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.045f * k, r.y), b.Name == Maneuvers.Target ? b.Name + "  · target" : b.Name, b.Name == Maneuvers.Target ? TargetText : c, scale);
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.19f * k, r.y), Fit(Where(b), x1 - (x + scr.Y * 0.19f * k) - 6f, scale * 0.85f), Dim, scale * 0.85f);
            MapPipeline.PickName = null;
        }
        MapPipeline.Reserve(new Vector2(x0, top), new Vector2(scr.X, bottom));
    }

    /// <summary>The text, cut with an ellipsis to fit a width (px) at a scale.</summary>
    static string Fit(string text, float width, float scale)
    {
        if (string.IsNullOrEmpty(text) || MapPipeline.MeasureText(text, scale).X <= width) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string t = text.Substring(0, n).TrimEnd() + "...";
            if (MapPipeline.MeasureText(t, scale).X <= width) return t;
        }
        return "";
    }

    /// <summary>A click on a list row: glide the map to that sector (the game selects it on the same click).</summary>
    static void ListInput(Func<Vector3D, Vector3D> W)
    {
        if (!MapInput.LeftReleased || MapCamera.DragEnded || Hovered == null) return;
        if (_listBox.Contains(Mouse) != ContainmentType.Contains) return;
        CentreOn(Hovered);
    }

    /// <summary>Glide the view to a sector's marker (in this view).</summary>
    public static void CentreOn(string sector)
    {
        if (sector != null && _markerAt.TryGetValue(sector, out var at)) MapCamera.PanTo(at, smooth: true);
    }

    /// <summary>
    /// A line of the controls, in the map's open area above the game's own hint bar: what the mouse can
    /// do where it is (on a maneuver, on the path, or on the map).
    /// </summary>
    static void Hints()
    {
        var scr = MapPipeline.ScreenSize;
        string h;
        if (Maneuvers.OnGizmo) h = "Drag a handle to change the burn   \u00b7   Drag the node along the path   \u00b7   Right-click: options";
        else if (!double.IsNaN(Maneuvers.HoverT)) h = "Click: add a maneuver here   \u00b7   Right-click: options   \u00b7   Drag: pan";
        else h = "Drag: pan   \u00b7   Right-drag: orbit   \u00b7   Wheel: zoom   \u00b7   Double-click a body: focus   \u00b7   Right-click: menu   \u00b7   .  ,  /  warp";
        MapPipeline.ScreenText(new Vector2(scr.X * 0.265f, scr.Y * 0.9f), h, Dim, 0.74f);
    }

    private static double _lastMesh;
    private static string _lastKey;
    public static string Status = "-";
    /// <summary>Where the star is on the map this frame (world), for the game's star model.</summary>
    public static Vector3D? StarWorld;
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
        UnifiedMap.MinZoom = Math.Max(1e-7, 2.5 * aR * Sigma);

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

        Frame = $"anchor {anchor.Name} pxPerV {_pxPerV:G3} d {MapCamera.Distance:G3} min {UnifiedMap.MinZoom:G3} focusV {MapCamera.FocusV.Length():G3} scale {_wscale:G3}";
        _hits.Clear(); _crumbs.Clear();
        GravityBody focusPlanet = anchor.IsRoot ? null : anchor.Parent != null && !anchor.Parent.IsRoot ? anchor.Parent : anchor;
        string focus = focusPlanet?.Name;
        // The planet's own level (its sectors listed) while its system shows.
        bool sys = focusPlanet == null || !_detailPrev.Contains(focusPlanet.Name);
        _viewBody = anchor.IsRoot ? null : anchor;
        ViewKey = sys ? "system" : anchor.Name;

        var parts = new List<MapPipeline.Part>();
        var ordered = Ordered(bands);
        _deferred.Clear();
        bool ui = MapPipeline.UiBegin(session, mapConfig);
        try
        {
            // The game's own panels (left column, tab bar, bottom hints): map labels keep off them.
            var scrR = MapPipeline.ScreenSize;
            MapPipeline.Reserve(Vector2.Zero, new Vector2(scrR.X * 0.255f, scrR.Y));
            MapPipeline.Reserve(Vector2.Zero, new Vector2(scrR.X, scrR.Y * 0.1f));
            MapPipeline.Reserve(new Vector2(0, scrR.Y * 0.93f), scrR);
            DrawList(ordered, b => sys
                ? b.Home.Kind == SectorHomes.Kind.Belt || b.Home.Kind == SectorHomes.Kind.Ring || b.Selected
                : b.Host == focus && b.Home.Kind != SectorHomes.Kind.Belt && b.Home.Kind != SectorHomes.Kind.Ring,
                // A planet's level lists that planet's sectors only; the system level lists them all.
                b => sys || (b.Host == focus && b.Home.Kind != SectorHomes.Kind.Belt && b.Home.Kind != SectorHomes.Kind.Ring));
            // The title's area is kept free of map labels now; the title itself is drawn after the map, over its lines.
            MapPipeline.Reserve(new Vector2(scrR.X * 0.26f, scrR.Y * 0.118f), new Vector2(scrR.X * 0.56f, scrR.Y * 0.118f + TitleHeight(scrR)));
            // The map itself (orbits, sectors, the plan) is clipped to the open area between the panels.
            MapPipeline.ClipRect = new BoundingBox2(new Vector2(scrR.X * 0.255f, scrR.Y * 0.1f), new Vector2(scrR.X * 0.775f, scrR.Y * 0.84f));

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
            foreach (var d in _deferred) d();
            _deferred.Clear();
            Overlay(r => Map(r), double.PositiveInfinity, W, t, reg);
            _toMap = (r, tt) => Flat(r - anchor.OriginInRoot(tt).Position);
            _limit = double.PositiveInfinity;

            string selName = null;
            foreach (var bd in bands) if (bd.Selected) selName = bd.Name;
            if (_toMap != null && ManeuverEditor) Maneuvers.MapDraw(_toMap, W, _limit, t, Mouse, selName, anchor.Name);   // editable at every zoom
            MapPipeline.ClipRect = null;   // menus, the warp bar, hints: unclipped
            Title(anchor.IsRoot ? null : anchor, playerPlanet, playerOrbit);
            if (ManeuverEditor) ContextMenu(bands, t);
            _lastW = W; _lastSolar = false;
            FocusInput(reg, t, W, false);
            BodyTooltip(reg, playerPlanet, playerOrbit);
            ListInput(W);
            Hints();
            WarpBar.DrawMap(Mouse);
        }
        finally { MapPipeline.ClipRect = null; if (ui) MapPipeline.UiEnd(); }

        // Every sector keeps a (possibly zero-size) part: the game colours parts by sector name and
        // must always find them.
        var have = new HashSet<string>();
        foreach (var pt in parts) have.Add(pt.Name);
        foreach (var bd in bands)
            if (!have.Contains(bd.Name))
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

        // The mesh changes only when the view changes, or every MeshRebuildSeconds (motion).
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string key = "unified";
        bool moved = double.IsNaN(_lastMeshSimT) || Math.Abs(t - _lastMeshSimT) > MeshRebuildSimSeconds;
        if (key != _lastKey || (moved && now - _lastMesh > MeshRebuildSeconds))
        {
            bool ok = MapPipeline.ShowParts(sectorsRenderer, parts);
            _lastKey = key; _lastMesh = now; _lastMeshSimT = t;
            Status = ok ? $"{key} parts={parts.Count}" : $"{key} mesh failed: {MapPipeline.LastError}";
        }
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
    /// <summary>Sector areas, markers and names: drawn after every line, so no orbit runs through a sector.</summary>
    private static readonly List<Action> _deferred = new List<Action>();
    /// <summary>A sector section's reach along its orbit, each side of where it is (fraction of the period).</summary>
    const double SectorSpan = 0.035;

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
        foreach (var bd in bands)
            if (bd.Host == b.Name && bd.Home.Kind == SectorHomes.Kind.Ellipse) frame = Math.Max(frame, bd.Home.A * (1 + bd.Home.E));
        frame = Math.Max(frame, 8 * (reg.FindDefinition(b.Name)?.RadiusMeters ?? 0));
        return frame * 1.15;
    }

    /// <summary>
    /// A smooth curve at any zoom: sampled coarsely, then split where it is long on screen, and only
    /// near the view (zoomed onto a planet, the sun's circles are a straight line through it; a
    /// fixed number of chords missed the planet by a good part of the screen).
    /// </summary>
    static void Curve(Func<double, Vector3D> at, Func<Vector3D, Vector3D> W, double a0, double a1, int n, ColorSRGB col, float px, bool depthCue = false)
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
            if (oa && ob && ((sb - sa).Length() < 12f * u || depth >= 22))
            {
                if (MapPipeline.Occluded(W((pa + pb) * 0.5))) { Flush(); return; }   // behind a body's globe
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
        var run = new List<Vector2>();
        void Flush() { if (run.Count > 1) MapPipeline.ScreenPath(run, false, runDim ? dim : col, px * u); run = new List<Vector2>(); }
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

    /// <summary>A sector's orbit line: faint (bright, a planet's many orbits were a tangle); gold when selected.</summary>
    static ColorSRGB OrbitLine(Band b)
    {
        if (b.Selected) return LineSel;
        var c = StateColor(b);
        if (b.Name == Hovered) return HudPanel.Alpha(c, 0.8f);
        return HudPanel.Alpha(c, Quiet(b) ? 0.10f : 0.22f);
    }

    /// <summary>
    /// The sector itself: an area of its orbit (a band section about where it is now), filled in its
    /// state colour and outlined; its orbit carries it round.
    /// </summary>
    static void SectorArea(Func<Vector3D, Vector3D> W, Func<double, Vector3D> relAt, Func<Vector3D, Vector3D> toLocal, Band b, double span = SectorSpan, double kWidth = 0)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        double k = kWidth > 0 ? kWidth : Math.Clamp(b.Home.Size * 0.5 * SystemHost.SectorOrbitScale / Math.Max(1, b.Home.A), 0.03, 0.065);
        bool ring = span >= 0.5;
        if (ring && !(kWidth > 0)) k = Math.Min(k, 0.03);   // a belt: a thin ring
        int n = ring ? 96 : 24;
        var outer = new List<Vector2>(n + 1); var inner = new List<Vector2>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            Vector3D r = ring ? relAt((double)i / n) : relAt(-span + 2 * span * i / n);
            if (!MapPipeline.ToScreen(W(toLocal(r * (1 + k))), out var so) || !MapPipeline.ToScreen(W(toLocal(r * (1 - k))), out var si)) return;
            outer.Add(so); inner.Add(si);
        }
        var poly = new List<Vector2>(outer);
        for (int i = inner.Count - 1; i >= 0; i--) poly.Add(inner[i]);
        var c = b.Selected ? LineSel : StateColor(b);
        float fa = b.Selected ? 0.30f : b.Name == Hovered ? 0.34f : Quiet(b) ? 0.08f : 0.18f;
        // A dark base first: the lines under the sector are hidden, not showing through it.
        MapPipeline.ScreenFill(poly, new ColorSRGB(0.04f, 0.06f, 0.09f, 0.82f));
        MapPipeline.ScreenFill(poly, HudPanel.Alpha(c, fa));
        var edge = HudPanel.Alpha(c, b.Selected ? 0.95f : Quiet(b) ? 0.3f : 0.6f);
        float ew = (b.Selected ? 1.8f : 1.2f) * u;
        MapPipeline.ScreenPath(outer, ring, edge, ew);
        MapPipeline.ScreenPath(inner, ring, edge, ew);
        if (!ring)
        {
            MapPipeline.ScreenLine(outer[0], inner[0], edge, ew);
            MapPipeline.ScreenLine(outer[n], inner[n], edge, ew);
        }
    }

    /// <summary>Height above or below the planet's plane: a dashed drop line to the plane and a dot at its foot.</summary>
    static void DropLine(Func<Vector3D, Vector3D> W, Vector3D p, ColorSRGB c)
    {
        if (!MapPipeline.ToScreen(W(p), out var a) || !MapPipeline.ToScreen(W(new Vector3D(p.X, 0, p.Z)), out var f)) return;
        if ((a - f).Length() < 6f) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        MapPipeline.ScreenDashed(a, f, HudPanel.Alpha(c, 0.45f), 1.2f * u, u);
        MapPipeline.ScreenDisc(f, 2.2f * u, HudPanel.Alpha(c, 0.6f));
    }

    // ───────────────────────────── system view ─────────────────────────────

    private static void DrawSystem(List<MapPipeline.Part> parts, List<Band> bands, SystemRegistry reg, GravityBody planet, double t,
                                   string playerPlanet, Vector3D playerRel, KeplerianElements? playerOrbit, HashSet<string> globes,
                                   Func<Vector3D, Vector3D> W, bool detail)
    {
        // True scale (the one map's): the system's size on the map is its real size.
        double scaleSys = Sigma;
        double fit = FrameOf(planet, bands, reg) * Sigma;
        var mine = bands.FindAll(b => b.Host == planet.Name);
        double rH = SectorHomes.HillRadius(planet);
        double R(double r) => Math.Max(0, r) * scaleSys;
        Vector3D L(double ang, double r) => new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
        Vector3D Lv(Vector3D rel) => new Vector3D(rel.X * scaleSys, rel.Z * scaleSys, rel.Y * scaleSys);   // 3D: height is up

        // (The body itself is drawn by the level above: the sun's planets, a planet's moons.)
        // Its moons, and its sectors: only when its system is big enough on screen.
        if (detail)
        foreach (var moon in planet.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
            Vector3D mp = moon.StateInParentAt(t).Position;
            Vector3D ml = Lv(mp);
            {
                var mel = OrbitalMath.ToElements(moon.StateInParentAt(t), planet.Mu, t);
                if (mel.IsElliptic) Curve(nu => Lv(OrbitSampler.PositionAtTrueAnomaly(mel, nu)), W, 0, 2 * Math.PI, 64, HudPanel.Alpha(Dim, 0.35f), 1.1f, depthCue: true);
            }
            if (!MapPipeline.ToScreen(W(ml), out var mls) || !InOpenArea(mls)) continue;   // off view: not over the game's panels
            MapGlobes.Use(moon.Name, W(ml), (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys * _wscale, globes);
            BodyDot(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, moon.Name == playerPlanet ? You : Dim);
            Hit(moon, W(ml), W(ml + new Vector3D((reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 0, 0)), 5f);
            BodyLabel(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 5f, moon.Name, Dim, 0.6f);
        }

        if (detail)
        foreach (var bd in mine)
        {
            var h = bd.Home;
            MapPipeline.PickName = bd.Name;
            switch (h.Kind)
            {
                case SectorHomes.Kind.OwnPlanet:
                    OwnSector(W, R(si_own_outer(h)), bd);
                    break;
                case SectorHomes.Kind.Ellipse:
                {
                    SectorHomes.Rel(h, planet, t, out var rel);
                    double T = SectorHomes.Period(h, planet);
                    Vector3D RelAt(double q) { SectorHomes.Rel(h, planet, t + T * q, out var rq); return rq; }
                    // Its orbit (not through the sector itself), dimmer where it dips below the plane.
                    if (!h.Belt) Curve(q => Lv(RelAt(q)), W, SectorSpan, 1 - SectorSpan, 60, OrbitLine(bd), bd.Selected ? 2f : 1.2f, depthCue: true);
                    Vector3D mk = Lv(rel);
                    var b0 = bd; var W0 = W;
                    string label = $"{bd.Number}  {bd.Name}";
                    _deferred.Add(() =>
                    {
                        MapPipeline.PickName = b0.Name;
                        DropLine(W0, mk, StateColor(b0));
                        SectorArea(W0, RelAt, Lv, b0, h.Belt ? 0.5 : SectorSpan);
                        Marker(W0(mk), b0);
                        if (MapPipeline.ToScreen(W0(mk), out var ms))
                        {
                            float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                            MapPipeline.TextScreen(ms + new Vector2(0, 22f * lu), label, Quiet(b0) ? QuietText : b0.Selected ? LineSel : Text, Quiet(b0) ? 0.72f * 0.85f : 0.72f);
                        }
                        MapPipeline.PickName = null;
                    });
                    break;
                }
                case SectorHomes.Kind.L1:
                case SectorHomes.Kind.L2:
                {
                    // The loop, drawn small around its Lagrange point (the radius remap would distort it).
                    double T = SectorHomes.Period(h, planet);
                    var sp = planet.StateInParentAt(t);
                    Vector3D radial = Vector3D.Normalize(new Vector3D(sp.Position.X, sp.Position.Y, 0));
                    Vector3D centreRel = radial * (h.Kind == SectorHomes.Kind.L1 ? -rH : rH);
                    Vector3D centreL = Lv(centreRel);
                    double loopScale = scaleSys;
                    SectorHomes.Rel(h, planet, t, out var now);
                    Vector3D dn = now - centreRel;
                    Vector3D nl = centreL + new Vector3D(dn.X, 0, dn.Y) * loopScale;
                    double nlLen = Math.Sqrt(nl.X * nl.X + nl.Z * nl.Z);
                    if (false)
                    {
                        // Beyond the frame: pinned to the edge in its true direction, with its true distance.
                        Vector3D edgeP = nl * (fit * 1.02 / nlLen);
                        MapPipeline.ScreenRing(W(edgeP), 5f, Dim, 1.2f);
                        MapPipeline.Text(W(edgeP * 1.07), $"{bd.Number} >", Quiet(bd) ? QuietText : bd.Selected ? LineSel : Dim, Quiet(bd) ? 0.62f * 0.85f : 0.62f);
                        break;
                    }
                    double ang = Math.Atan2(nl.Z, nl.X), rr = Math.Sqrt(nl.X * nl.X + nl.Z * nl.Z);
                    Vector3D lv = default;
                    for (int q = 0; q <= 96; q++)
                    {
                        SectorHomes.Rel(h, planet, t + T * q / 96, out var rq);
                        Vector3D d = rq - centreRel;
                        Vector3D lp = W(centreL + new Vector3D(d.X, 0, d.Y) * loopScale);
                        if (q > 0) MapPipeline.Line(lv, lp, OrbitColor(bd), bd.Selected ? 2.4f : 1.8f);
                        lv = lp;
                    }
                    Marker(W(nl), bd);
                    MapPipeline.Text(W(nl + new Vector3D(0, 0, fit * 0.05)), $"{bd.Number}  {bd.Name}", Quiet(bd) ? QuietText : bd.Selected ? LineSel : Text, Quiet(bd) ? 0.72f * 0.85f : 0.72f);
                    break;
                }
            }
            MapPipeline.PickName = null;   // only the sector's own lines and marker pick it
        }


        MapPipeline.PickName = null;
        // You.
        if (playerPlanet == planet.Name)
        {
            if (!Planning && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))   // planning: the planner draws it
            {
                var path = OrbitSampler.SamplePath(playerOrbit.Value, 128, planet.SoiRadius);
                var pts = path.Points;
                if (pts != null)
                    for (int i = 0; i < pts.Length - (path.IsClosed ? 0 : 1); i++)
                        MapPipeline.Line(W(Lv(pts[i])), W(Lv(pts[(i + 1) % pts.Length])), You, 2f);
            }
            Vector3D yl = Lv(playerRel);
            float yu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            if (!(MapPipeline.ToScreen(W(yl), out var yic) && MapPipeline.ScreenIcon("PlayerIcon", yic, 10f * yu, You))) MapPipeline.Text(W(yl), "+", You, 1.2f);
            // Beside the mark on screen (a map-space offset lands far away when zoomed in).
            if (MapPipeline.ToScreen(W(yl), out var ys) && InOpenArea(ys))
            {
                float uu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            }
            // Pe / Ap on your orbit (as KSP), plan or not; after 'you', which has the first claim.
            if (!Planning && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis)
                && (!(_pxPerV > 0) || playerOrbit.Value.PeriapsisRadius * Sigma * _pxPerV > 30))
            {
                var po = playerOrbit.Value; double pr = reg.FindDefinition(planet.Name)?.RadiusMeters ?? 0;
                float u2 = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                if (MapPipeline.ToScreen(W(Lv(OrbitSampler.PositionAtTrueAnomaly(po, 0))), out var pes) && InOpenArea(pes))
                    HudPanel.TagAt(pes, "Pe " + HudPanel.Km(po.PeriapsisRadius - pr), You, u2);
                if (po.IsElliptic && po.Eccentricity > 0.002 && MapPipeline.ToScreen(W(Lv(OrbitSampler.PositionAtTrueAnomaly(po, Math.PI))), out var aps) && InOpenArea(aps))
                    HudPanel.TagAt(aps, "Ap " + HudPanel.Km(po.SemiMajorAxis * (1 + po.Eccentricity) - pr), You, u2);
            }
        }
    }

    // ───────────────────────────── solar view ─────────────────────────────

    private static void DrawSolar(List<MapPipeline.Part> parts, List<Band> bands, SystemRegistry reg, double t, string playerPlanet,
                                  HashSet<string> globes, Func<Vector3D, Vector3D> W, KeplerianElements? playerOrbit = null)
    {
        var root = reg.Root;
        double outer = SectorHomes.SystemOuterAU * SystemHost.AU;
        // Display radius: compressed (r^0.55) so the inner planets are not crammed against the sun;
        // order and angles are true, the physics stays proportional.
        double Rs(double r) => SolarRadius * Math.Max(0, r) / outer;   // strictly proportional
        Vector3D S(Vector3D helio) { double r = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y); double f = r > 0 ? Rs(r) / r : 0; return new Vector3D(helio.X * f, 0, helio.Y * f); }

        // The sun: a warm disc (its own section, coloured below), and its name.
        StarWorld = W(Vector3D.Zero);   // the game's star model goes here (UnifiedMap.PlaceStar)
        if (!UnifiedMap.StarPlaced && MapPipeline.ToScreen(W(Vector3D.Zero), out var sunS) && InOpenArea(sunS))
        {
            float su = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            float srpx = MapPipeline.ToScreen(W(new Vector3D(SystemHost.StarRadius * SolarRadius / outer, 0, 0)), out var sunE) ? (sunE - sunS).Length() : 0f;
            float sr = Math.Clamp(srpx, 5f * su, 80f * su);
            // Delfos, a red dwarf: a deep red-orange disc in a soft red glow.
            MapPipeline.ScreenDisc(sunS, sr * 2.2f, new ColorSRGB(1f, 0.30f, 0.12f, 0.10f));
            MapPipeline.ScreenDisc(sunS, sr * 1.5f, new ColorSRGB(1f, 0.38f, 0.16f, 0.22f));
            MapPipeline.ScreenDisc(sunS, sr, new ColorSRGB(1f, 0.46f, 0.22f, 1f));
            MapPipeline.ScreenDisc(sunS, sr * 0.55f, new ColorSRGB(1f, 0.62f, 0.38f, 1f));
        }
        Hit(root, W(Vector3D.Zero), W(new Vector3D(SystemHost.StarRadius * SolarRadius / outer, 0, 0)), 10f);
        BodyLabel(W, Vector3D.Zero, SystemHost.StarRadius * SolarRadius / outer, 10f, StarName, Text, 0.85f);

        // The belt: a torus of its own, and its sectors as band sections on it.
        double b0 = Rs(SectorHomes.BeltInnerAU * SystemHost.AU), b1 = Rs(SectorHomes.BeltOuterAU * SystemHost.AU);
        // The belt: just its two edges, faint (a filled torus dominated the view).
        if (bands.Exists(x => x.Home.Kind == SectorHomes.Kind.Belt)) { Circle(W, b0, BeltLine, 1f); Circle(W, b1, BeltLine, 1f); }
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Belt) continue;
            MapPipeline.PickName = bd.Name;
            Vector3D hp = SectorHomes.HelioBelt(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            // (No orbit line: only the planets and moons have one; a line per sector ringed the star in circles.)
            {
                var bs = bd; var h0 = bd.Home; var W0 = W; double Tb = 2 * Math.PI * Math.Sqrt(Math.Pow(hp.Length(), 3) / root.Mu);
                Vector3D mk = new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
                string label = $"{bd.Number}  {bd.Name}";
                _deferred.Add(() =>
                {
                    MapPipeline.PickName = bs.Name;
                    SectorArea(W0, q => SectorHomes.HelioBelt(h0, root.Mu, t + Tb * q), S, bs);
                    Marker(W0(mk), bs);
                    if (MapPipeline.ToScreen(W0(mk), out var ms))
                    {
                        float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                        MapPipeline.TextScreen(ms + new Vector2(0, 22f * lu), label, Quiet(bs) ? QuietText : bs.Selected ? LineSel : Text, Quiet(bs) ? 0.72f * 0.85f : 0.72f);
                    }
                    MapPipeline.PickName = null;
                });
            }
            MapPipeline.PickName = null;
        }

        // Sectors with their own orbit (a planet-like ring): the full orbit line and the band section.
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Ring) continue;
            MapPipeline.PickName = bd.Name;
            Vector3D hp = SectorHomes.HelioRing(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            Curve(a => new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r), W, ang + 2 * Math.PI * SectorSpan, ang + 2 * Math.PI * (1 - SectorSpan), 90, OrbitLine(bd), bd.Selected ? 2f : 1.2f);
            {
                var bs = bd; var h0 = bd.Home; var W0 = W; double Tb = 2 * Math.PI * Math.Sqrt(Math.Pow(hp.Length(), 3) / root.Mu);
                Vector3D mk = new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
                string label = $"{bd.Number}  {bd.Name}";
                _deferred.Add(() =>
                {
                    MapPipeline.PickName = bs.Name;
                    if (bs.Name == SectorHomes.StarSector)
                        // Delfos's own: a zone round the star (not an orbit), open in the middle for the star.
                        SectorArea(W0, q => new Vector3D(Math.Cos(2 * Math.PI * q), Math.Sin(2 * Math.PI * q), 0) * (SectorHomes.StarZoneAU[0] + SectorHomes.StarZoneAU[1]) * 0.5 * SystemHost.AU,
                                   S, bs, 0.5, (SectorHomes.StarZoneAU[1] - SectorHomes.StarZoneAU[0]) / (SectorHomes.StarZoneAU[0] + SectorHomes.StarZoneAU[1]));
                    else SectorArea(W0, q => SectorHomes.HelioRing(h0, root.Mu, t + Tb * q), S, bs, h0.Belt ? 0.5 : SectorSpan);
                    Marker(W0(mk), bs);
                    if (MapPipeline.ToScreen(W0(mk), out var ms))
                    {
                        float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                        MapPipeline.TextScreen(ms + new Vector2(0, 22f * lu), label, Quiet(bs) ? QuietText : bs.Selected ? LineSel : Text, Quiet(bs) ? 0.8f * 0.85f : 0.8f);
                    }
                    MapPipeline.PickName = null;
                });
            }
            MapPipeline.PickName = null;
        }

        // Trojans: a section of their planet's orbit, 60 degrees ahead (L4) or behind (L5).
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.L4 && bd.Home.Kind != SectorHomes.Kind.L5) continue;
            var tp = reg.Find(bd.Host);
            if (tp == null || tp.Parent == null) continue;
            var bs = bd; var h0 = bd.Home; var W0 = W;
            double ap = tp.StateInParentAt(t).Position.Length();
            double Tp = 2 * Math.PI * Math.Sqrt(ap * ap * ap / root.Mu);
            Vector3D mk = S(SectorHomes.HelioTrojan(h0, tp, t));
            string label = $"{bd.Number}  {bd.Name}";
            _deferred.Add(() =>
            {
                MapPipeline.PickName = bs.Name;
                SectorArea(W0, q => SectorHomes.HelioTrojan(h0, tp, t + Tp * q), S, bs);
                Marker(W0(mk), bs);
                if (MapPipeline.ToScreen(W0(mk), out var ms))
                {
                    float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                    MapPipeline.TextScreen(ms + new Vector2(0, 22f * lu), label, Quiet(bs) ? QuietText : bs.Selected ? LineSel : Text, Quiet(bs) ? 0.72f * 0.85f : 0.72f);
                }
                MapPipeline.PickName = null;
            });
        }

        // The planets: orbit line, the globe, and the planet's own sector as a circular section around it.
        foreach (var p in root.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
            var el = OrbitalMath.ToElements(p.StateInParentAt(t), root.Mu, t);
            if (el.IsElliptic) Curve(nu => S(OrbitSampler.PositionAtTrueAnomaly(el, nu)), W, 0, 2 * Math.PI, 96, Line, 1.2f);
            Vector3D hp = p.StateInParentAt(t).Position;
            Vector3D c = S(hp);
            var own = bands.Find(b => b.Host == p.Name && b.Home.Kind == SectorHomes.Kind.OwnPlanet);
            if (own != null)
            {
                // The planet's own sector at true size is far below a pixel here: the ring marker stands for it.
            }
            MapGlobes.Use(p.Name, W(c), (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer * _wscale, globes);   // true size
            BodyDot(W, c, (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, p.Name == playerPlanet ? You : Text);
            Hit(p, W(c), W(c + new Vector3D((reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, 0, 0)), 8f);
            int n = 0; foreach (var bd in bands) if (bd.Host == p.Name && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) n++;
            BodyLabel(W, c, (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, 8f, p.Name, p.Name == playerPlanet ? You : Text, 0.9f);
        }
        // You, orbiting the star: your orbit and where you are (planning: the planner draws it).
        if (playerPlanet == root.Name && playerOrbit.HasValue && !Planning)
        {
            var path = OrbitSampler.SamplePath(playerOrbit.Value, 256);
            var pts = path.Points;
            if (pts != null)
                for (int i = 0; i < pts.Length - (path.IsClosed ? 0 : 1); i++)
                    MapPipeline.Line(W(S(pts[i])), W(S(pts[(i + 1) % pts.Length])), You, 2f);
            var now = OrbitPropagation.StateAt(playerOrbit.Value, t);
            if (MapPipeline.ToScreen(W(S(now.Position)), out var ys) && InOpenArea(ys))
            {
                float uu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                if (!MapPipeline.ScreenIcon("PlayerIcon", ys, 10f * uu, You)) MapPipeline.ScreenText(ys - new Vector2(6f * uu, 12f * uu), "+", You, 1.2f);
            }
        }
        MapPipeline.PickName = null;
    }

    // ───────────────────────────── encounters and GPS (both views) ─────────────────────────────

    private static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>
    /// Right click on the map: what is under the mouse decides the menu. A node: edit its axes exactly,
    /// delete it (or it and the later ones). The trajectory: add a maneuver there. A sector (orbit,
    /// marker, list row): plan a route to it. Anywhere with a route: clear it.
    /// </summary>
    private static void ContextMenu(List<Band> bands, double t)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        bool rp = MapInput.RightPressed;
        if (MapMenu.Draw(Mouse, MapInput.LeftPressed, rp, u)) { Maneuvers.ClaimsMouse = true; return; }
        if (MapMenu.Open) { Maneuvers.ClaimsMouse = true; return; }
        if (!rp) return;
        var items = new List<MapMenu.Item>();
        string title = null;
        bool hasNodes = Maneuvers.Nodes.Count > 0;
        var node = Maneuvers.HoverNode;
        double ht = Maneuvers.HoverT;
        var session = _session;
        if (node != null)
        {
            title = "Maneuver  ·  in " + Maneuvers.Clock(node.T - t);
            Maneuvers.Selected = node;
            // Warp to it (as KSP): top warp; warp stops by itself ahead of the burn.
            if (node.T - t > Maneuvers.WarpLead + 10) items.Add(new MapMenu.Item("Warp to maneuver", () => WarpControl.SetLevel(WarpControl.Levels.Length - 1), FrameHost.PlayerFrame != null));
            items.Add(new MapMenu.Item(node.Auto ? "Auto-burn: on" : "Auto-burn: off", () => node.Auto = !node.Auto));
            items.Add(new MapMenu.Item("Remove maneuver", () => Maneuvers.Delete(node, false)));
        }
        else if (!double.IsNaN(ht))
        {
            double T = ht;
            title = "Trajectory  ·  in " + Maneuvers.Clock(T - t);
            items.Add(new MapMenu.Item("Add maneuver", () => Maneuvers.AddNodeAt(T)));
            // Warp until the ship gets there (as KSP); other stops (burns, spheres) still come first.
            if (T - t > 30) items.Add(new MapMenu.Item("Warp here", () => { SystemHost.WarpStopAt = T; WarpControl.SetLevel(WarpControl.Levels.Length - 1); }, FrameHost.PlayerFrame != null));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (Hovered != null)
        {
            string sec = Hovered;
            title = sec;
            items.Add(new MapMenu.Item("Centre on " + sec, () => CentreOn(sec)));
            items.Add(Maneuvers.Target == sec ? new MapMenu.Item("Clear target", () => Maneuvers.Target = null)
                                              : new MapMenu.Item("Set as target", () => Maneuvers.Target = sec));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (hasNodes)
            items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        if (items.Count > 0) { MapMenu.Show(Mouse + new Vector2(4f * u, 4f * u), title, items); Maneuvers.ClaimsMouse = true; }
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    private static Func<Vector3D, double, Vector3D> _toMap;
    private static double _limit;
    /// <summary>The mouse (screen px) for this frame's editors.</summary>
    public static Vector2 Mouse;
    public static bool ManeuverEditor = true;
    static readonly ColorSRGB EncColor = new ColorSRGB(1.00f, 0.55f, 0.25f, 0.85f);

    /// <summary>
    /// Encounter frames (procedural spawns, authored sites; sector anchors are the sector orbits already):
    /// their orbit and where they are now. GPS markers: at their true place (frame-transferred), pinned to
    /// the edge when beyond the view. toLocal maps a sun-centred model position to the map.
    /// </summary>
    /// <summary>
    /// Where you are looking and where you are, at the top of the map's open area: "Delfos › Kemik",
    /// then your ship's situation (the orbit's Pe / Ap, or on the ground).
    /// </summary>
    // ---- focus (as KSP: double-click a body, or click the breadcrumb) ----

    private static readonly List<(GravityBody b, Vector2 s, float r)> _hits = new List<(GravityBody, Vector2, float)>();
    private static readonly List<(GravityBody b, BoundingBox2 box)> _crumbs = new List<(GravityBody, BoundingBox2)>();
    private static double _lastClick; private static Vector2 _lastClickAt;
    /// <summary>A body being flown to: the zoom-in switch opens it, whatever is under the cursor.</summary>
    private static GravityBody _glide;
    private static bool _glideToStar;

    /// <summary>A body drawn this frame, clickable within its drawn size (or its ring when smaller).</summary>
    static void Hit(GravityBody b, Vector3D centre, Vector3D edge, float ringPx)
    {
        if (!MapPipeline.ToScreen(centre, out var sc)) return;
        float r = MapPipeline.ToScreen(edge, out var se) ? (se - sc).Length() : 0f;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        _hits.Add((b, sc, Math.Max(r, ringPx * u) + 8f * u));
    }

    private static Func<Vector3D, Vector3D> _lastW; private static bool _lastSolar;
    public static string ClickDebug = "";

    /// <summary>DEV: focus a body as a double-click on it would.</summary>
    public static string DevFocus(string name)
    {
        var reg = SystemHost.Registry; var b = reg?.Find(name) ?? (name == StarName ? reg?.Root : null);
        if (b == null || _lastW == null) return "no body / map not drawn";
        FocusOn(b, reg, SystemHost.Now, _lastW, _lastSolar);
        return "focus " + name;
    }

    /// <summary>
    /// Hovering a body: a small card by the cursor with what it is, its size and reach, and how to
    /// focus it (double-click), so the map can be learnt by pointing at things.
    /// </summary>
    static void BodyTooltip(SystemRegistry reg, string playerPlanet, KeplerianElements? playerOrbit)
    {
        if (MapCamera.Dragging || MapMenu.Open || Maneuvers.OnGizmo || !InOpenArea(Mouse)) return;
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        if (best == null) return;
        var def = reg.FindDefinition(best.Name);
        string kind = best.Parent == null ? "Star" : best.Parent.Parent == null ? "Planet" : $"Moon of {best.Parent.Name}";
        var lines = new List<string> { $"{SystemHost.DisplayName(best.Name)}  ·  {kind}" };
        if (def != null && def.RadiusMeters > 0) lines.Add($"Radius {HudPanel.Km(def.RadiusMeters)}" + (best.Parent != null && !double.IsInfinity(best.SoiRadius) ? $"  ·  reach {HudPanel.Km(best.SoiRadius)}" : ""));
        if (best.Name == playerPlanet && playerOrbit.HasValue)
        {
            var o = playerOrbit.Value; double r = def?.RadiusMeters ?? 0;
            lines.Add($"You: Pe {HudPanel.Km(o.PeriapsisRadius - r)}" + (o.IsElliptic ? $"  Ap {HudPanel.Km(o.SemiMajorAxis * (1 + o.Eccentricity) - r)}" : "  escape"));
        }
        bool viewing = _viewBody == best || (_viewBody == null && best.Parent == null);
        lines.Add(viewing ? "Double-click: centre" : "Double-click: focus");
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        float w = 0, lh = 0;
        foreach (var l in lines) { var m = MapPipeline.MeasureText(l, 0.8f); w = Math.Max(w, m.X); lh = Math.Max(lh, m.Y); }
        var at = Mouse + new Vector2(18f * u, 14f * u);
        var size = new Vector2(w + 16f * u, lines.Count * lh + 10f * u);
        var scr = MapPipeline.ScreenSize;
        if (at.X + size.X > scr.X * 0.77f) at.X = Mouse.X - 18f * u - size.X;
        if (at.Y + size.Y > scr.Y * 0.9f) at.Y = Mouse.Y - 14f * u - size.Y;
        MapPipeline.ScreenRect(at, at + size, PanelFill);
        MapPipeline.ScreenRect(at, new Vector2(at.X + 2f * u, at.Y + size.Y), LineSel);
        for (int i = 0; i < lines.Count; i++)
            MapPipeline.ScreenText(at + new Vector2(8f * u, 5f * u + i * lh), lines[i], i == 0 ? Text : i == lines.Count - 1 ? Dim : Text, i == 0 ? 0.85f : 0.8f);
    }

    static void FocusInput(SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, bool solar)
    {
        if (MapInput.LeftReleased)
        {
            float nd = float.MaxValue; string nn = null;
            foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d < nd) { nd = d; nn = h.b.Name + $"(r{h.r:F0})"; } }
            ClickDebug = $"release at {Mouse} drag={MapCamera.DragEnded} gizmo={Maneuvers.OnGizmo} menu={MapMenu.Open} hits={_hits.Count} nearest {nn} {nd:F0}px";
        }
        if (!MapInput.LeftReleased || MapCamera.DragEnded || Maneuvers.OnGizmo || MapMenu.Open) return;
        foreach (var c in _crumbs)
            if (c.box.Contains(Mouse) == ContainmentType.Contains) { FocusOn(c.b, reg, t, W, solar); return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        bool dbl = now - _lastClick < 0.4 && (Mouse - _lastClickAt).Length() < 10f;
        _lastClick = dbl ? 0 : now; _lastClickAt = Mouse;
        if (!dbl) return;
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        if (best != null) FocusOn(best, reg, t, W, solar);
    }

    /// <summary>Fly the view to a body: the star (system view), a planet or a moon (its own view).</summary>
    public static void FocusOn(GravityBody b, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, bool solar)
    {
        // The map becomes about that body (the view stays put), then glides to it and frames its system.
        ViewFocus = b.IsRoot ? StarName : b.Name;
        _zoomTo = b;
    }

    /// <summary>A breadcrumb part's size: the font's measure, but never under ~8 px a character per unit
    /// scale (the measure comes out short at high resolutions, and parts ran together).</summary>
    static Vector2 CrumbSize(string text)
    {
        return MapPipeline.MeasureText(text, 1.05f);   // (now never short)
    }

    private static Vector2 _titleAt;

    /// <summary>The title block's height: the path, your situation, and the burn and target lines when there are.</summary>
    static float TitleHeight(Vector2 sc)
    {
        return sc.Y * 0.045f;   // the path line only
    }

    static void Title(GravityBody view, string playerPlanet, KeplerianElements? orbit)
    {
        var scr = MapPipeline.ScreenSize;
        var at = new Vector2(scr.X * 0.265f, scr.Y * 0.125f);
        // The breadcrumb: each part a click target (up to the star, down to what is in view).
        var chain = new List<GravityBody>();
        for (var b = view; b != null; b = b.Parent) chain.Insert(0, b);
        if (chain.Count == 0 && SystemHost.Registry?.Root != null) chain.Add(SystemHost.Registry.Root);
        float x = at.X;
        for (int i = 0; i < chain.Count; i++)
        {
            string name = chain[i].Parent == null ? StarName : chain[i].Name;
            var size = CrumbSize(name);
            var box = new BoundingBox2(new Vector2(x, at.Y), new Vector2(x + size.X, at.Y + size.Y));
            bool last = i == chain.Count - 1;
            bool hot = !last && box.Contains(Mouse) == ContainmentType.Contains;
            MapPipeline.ScreenText(new Vector2(x, at.Y), name, hot ? LineSel : last ? Text : Dim, 1.05f);
            if (!last) _crumbs.Add((chain[i], box));
            x += size.X;
            if (!last)
            {
                float gap = 10f * Math.Max(1f, scr.Y / 1080f);   // the font drops leading spaces: gaps in pixels
                x += gap; MapPipeline.ScreenText(new Vector2(x, at.Y), ">", Dim, 1.05f); x += CrumbSize(">").X + gap;
            }
        }
        MapPipeline.Reserve(at - new Vector2(4, 4), at + new Vector2(scr.X * 0.3f, scr.Y * 0.035f));
        _titleAt = at;
        // (Only the path: the title box and the you / burn / target lines were removed on request.)
    }

    /// <summary>
    /// How far below a body (map-local units) its name goes: just clear of the globe as drawn (its true
    /// size, or its minimum ring when smaller), whatever the zoom. Fallback when it cannot be measured.
    /// </summary>
    static double LabelGap(Func<Vector3D, Vector3D> W, Vector3D c, double r, float ringPx, double fallback)
    {
        if (!(r > 0) || !MapPipeline.ToScreen(W(c), out var sc) || !MapPipeline.ToScreen(W(c + new Vector3D(r, 0, 0)), out var se)) return fallback;
        double rpx = (se - sc).Length();
        if (rpx < 0.2) return fallback;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        double perPx = r / rpx;
        return (Math.Max(rpx, ringPx * u) + 16 * u) * perPx;
    }

    /// <summary>The map's open area on screen (between the game's panels, above the warp bar).</summary>
    public static bool InOpenArea(Vector2 s)
    {
        var scr = MapPipeline.ScreenSize;
        return s.X >= scr.X * 0.255f && s.X <= scr.X * 0.775f && s.Y >= scr.Y * 0.1f && s.Y <= scr.Y * 0.84f;
    }

    /// <summary>
    /// A body's name centred just below it on screen: below its drawn disc (largest screen extent of
    /// its radius, so a tilted view cannot tuck it under the globe) or its ring when that is bigger.
    /// </summary>
    static void BodyLabel(Func<Vector3D, Vector3D> W, Vector3D c, double r, float ringPx, string name, ColorSRGB col, float scale)
    {
        if (!MapPipeline.ToScreen(W(c), out var sc)) return;
        float rpx = 0;
        foreach (var ax in new[] { new Vector3D(r, 0, 0), new Vector3D(0, r, 0), new Vector3D(0, 0, r) })
            if (MapPipeline.ToScreen(W(c + ax), out var se)) rpx = Math.Max(rpx, (se - sc).Length());
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        float h = MapPipeline.MeasureText(name, scale).Y;
        MapPipeline.TextScreen(sc + new Vector2(0, Math.Max(rpx, ringPx * u) + 6f * u + h * 0.5f), name, col, scale);
    }

    static string Km(double m) => Math.Abs(m) >= 10000 ? $"{m / 1000:N0} km" : $"{m / 1000:F1} km";

    /// <summary>The planet the zoom took you to (zooming in over it); null until a zoom chooses one.</summary>
    public static string ViewFocus;
    /// <summary>What the map shows (the system, or a body's view): menus close when it changes.</summary>
    public static string ViewKey = "";
    private static bool? _wasSolar;

    /// <summary>
    /// Zooming keeps your place: out of a planet's view, the system view opens centred on that planet;
    /// into the system view, the planet nearest the middle of the view opens (centred).
    /// </summary>
    private static void ZoomTransition(bool solar, GravityBody planet, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, Vector3D mapPos)
    {
        bool? was = _wasSolar; _wasSolar = solar;
        // Opening the map: the view centres on what it shows (the planet, or the star), as KSP does.
        if (was == null) { if (MapCamera.Focus.HasValue) MapCamera.PanTo(W(Vector3D.Zero)); else _wasSolar = null; return; }
        if (was.Value == solar || reg?.Root == null) return;
        if (solar)
        {
            var top = planet; while (top?.Parent != null && top.Parent.Parent != null) top = top.Parent;
            if (_glideToStar) { _glideToStar = false; MapCamera.PanTo(W(Vector3D.Zero)); }
            else if (top != null && top.Parent != null) MapCamera.PanTo(W(SolarLocal(top, t)));
        }
        else
        {
            if (!MapCamera.Focus.HasValue) return;
            if (_glide != null) { ViewFocus = _glide.Name; _glide = null; MapCamera.PanTo(W(Vector3D.Zero)); return; }
            Vector3D f = MapCamera.Focus.Value;
            GravityBody best = null; double bd = double.MaxValue;
            // The planet under the cursor (within 60 px), else the one nearest the middle of the view.
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            double bpx = 60 * u;
            foreach (var b in reg.Root.Children)
                if (MapPipeline.ToScreen(W(SolarLocal(b, t)), out var sp) && (sp - Mouse).Length() < bpx) { bpx = (sp - Mouse).Length(); best = b; }
            if (best == null)
                foreach (var b in reg.Root.Children)
                {
                    double d = (W(SolarLocal(b, t)) - f).Length();
                    if (d < bd) { bd = d; best = b; }
                }
            if (best != null) ViewFocus = best.Name;
            MapCamera.PanTo(W(Vector3D.Zero));
        }
    }

    /// <summary>Where a body (a planet about the sun) sits in the system view, in map-local units.</summary>
    public static Vector3D SolarLocal(GravityBody b, double t)
        => Flat(b.OriginInRoot(t).Position - (_anchor != null ? _anchor.OriginInRoot(t).Position : Vector3D.Zero));

    /// <summary>The map closed: the next opening starts from where you are.</summary>
    public static void ResetView() { ViewFocus = null; _wasSolar = null; _fitPending = true; _anchor = null; _zoomTo = null; }
    private static bool _fitPending = true;

    /// <summary>The planet the view is about (null: the system view). Only its own things are drawn.</summary>
    private static GravityBody _viewBody;
    static bool InView(GravityBody b) { if (_viewBody == null) return true; for (; b != null; b = b.Parent) if (b == _viewBody) return true; return false; }

    private static void Overlay(Func<Vector3D, Vector3D> toLocal, double limit, Func<Vector3D, Vector3D> W, double t, SystemRegistry reg)
    {
        long player = FrameHost.PlayerId;
        lock (ServerFrames.FramesLock)
        {
            if (SystemHost.Frames == null) return;
            foreach (var f in SystemHost.Frames.Frames)
            {
                if (!f.IsEncounter || f.HasMember(player)) continue;
                var site = EncounterFrames.SiteOf(f.Id);
                if (site != null && site.Anchor) continue;
                var parent = reg.Find(f.ParentBodyName);
                if (parent == null || !(parent.IsRoot || _detail.Contains(parent.Name))) continue;
                Vector3D porg = parent.OriginInRoot(t).Position;
                var el = f.Elements;
                double T = el.IsElliptic && IsFinite(el.Period) ? el.Period : 6 * 3600.0;
                Vector3D prev = default; bool havePrev = false;
                for (int k = 0; k <= 120; k++)
                {
                    var st = OrbitPropagation.StateAt(el, t + T * k / 120);
                    if (!IsFinite(st.Position.X) || !IsFinite(st.Position.Y)) { havePrev = false; continue; }
                    Vector3D p = toLocal(porg + st.Position);
                    bool inView = Math.Sqrt(p.X * p.X + p.Z * p.Z) <= limit * 1.3;
                    if (havePrev && inView) MapPipeline.Line(W(prev), W(p), HudPanel.Alpha(EncColor, 0.55f), 1.4f);
                    prev = p; havePrev = inView;
                }
                var now = OrbitPropagation.StateAt(el, t);
                if (!IsFinite(now.Position.X) || !IsFinite(now.Position.Y)) continue;
                Vector3D here = toLocal(porg + now.Position);
                int npc = 0; foreach (long id in f.Members) if (EncounterFrames.IsNpcId(id)) npc++;
                string label = site != null ? site.Label : $"Encounter ({f.Members.Count})";
                Pin(here, limit, W, label, EncColor);
            }
        }

        // GPS markers at their true place.
        if (_session == null) return;
        foreach (var (name, color, model) in FrameMarkers.MapMarkers(_session, t))
            Pin(toLocal(model), limit, W, name, color, diamond: true);
    }

    /// <summary>
    /// A marker (an encounter, a station): where it is when in the map's open area; otherwise an arrow at
    /// the open area's edge in its direction, with its name ("Vallation Station >" sat in the title row).
    /// </summary>
    private static void Pin(Vector3D p, double limit, Func<Vector3D, Vector3D> W, string label, ColorSRGB c, bool diamond = false)
    {
        if (!MapPipeline.ToScreen(W(p), out var s)) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        if (InOpenArea(s))
        {
            if (diamond) MapPipeline.ScreenDiamond(W(p), 6f, c, 1.8f);
            else { MapPipeline.ScreenRing(W(p), 6f, c, 1.8f); MapPipeline.ScreenRing(W(p), 2.5f, c, 2.5f); }
            MapPipeline.TextScreen(s + new Vector2(0, 16f * u), label, c, 0.6f);
            return;
        }
        // The open area's centre, and where the ray to the marker leaves it.
        var scr = MapPipeline.ScreenSize;
        Vector2 min = new Vector2(scr.X * 0.265f, scr.Y * 0.2f), max = new Vector2(scr.X * 0.765f, scr.Y * 0.82f);
        Vector2 c0 = (min + max) * 0.5f, d = s - c0;
        if (d.LengthSquared() < 1e-6f) return;
        float k = Math.Min(Math.Abs((d.X > 0 ? max.X - c0.X : c0.X - min.X) / (Math.Abs(d.X) + 1e-6f)), Math.Abs((d.Y > 0 ? max.Y - c0.Y : c0.Y - min.Y) / (Math.Abs(d.Y) + 1e-6f)));
        Vector2 e = c0 + d * k, n = Vector2.Normalize(d), side = new Vector2(-n.Y, n.X);
        float a = 9f * u;
        // The arrow only with its name (a bare arrow says nothing).
        if (!MapPipeline.TextScreen(e - n * (a + 14f * u), label, c, 0.6f)) return;
        if (!MapPipeline.ScreenArrow(e - n * a * 0.5f, n, a * 0.75f, c))
        {
            MapPipeline.ScreenLine(e, e - n * a + side * a * 0.6f, c, 1.8f * u);
            MapPipeline.ScreenLine(e, e - n * a - side * a * 0.6f, c, 1.8f * u);
        }
    }

    public const string SunPart = "OrbitalSun", BeltPart = "OrbitalBelt";
    // The planet's own sector at true size: from its charted distance, +/- half its charted size.
    private static double si_own_inner(SectorHomes.Home h) => Math.Max(0, h.A - h.Size * 0.5 * SystemHost.SectorOrbitScale);
    /// <summary>
    /// A planet's own sector (its near space): not an orbit, so a faint dashed boundary (gold when
    /// selected), with the sector's name centred above the whole area.
    /// </summary>
    static void OwnSector(Func<Vector3D, Vector3D> W, double r, Band b)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        var col = b.Selected ? HudPanel.Alpha(LineSel, 0.85f) : b.Name == Hovered ? HudPanel.Alpha(StateColor(b), 0.8f) : HudPanel.Alpha(StateColor(b), 0.45f);
        const int n = 64;
        Vector2 prev = default, top = default; bool have = false, haveTop = false;
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector3D p = W(new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r));
            if (!MapPipeline.ToScreen(p, out var s)) { have = false; continue; }
            if (have) MapPipeline.ScreenDashed(prev, s, col, (b.Selected ? 1.8f : 1.3f) * u);
            if (!haveTop || s.Y < top.Y) { top = s; haveTop = true; }
            prev = s; have = true;
        }
        // Only inside the map's open area (not over the game's tab bar or the title).
        var scr = MapPipeline.ScreenSize;
        if (!haveTop || top.Y < scr.Y * 0.2f || top.X < scr.X * 0.26f || top.X > scr.X * 0.77f) return;
        var sz = MapPipeline.MeasureText(b.Name, 0.8f);
        MapPipeline.TextScreen(new Vector2(top.X, top.Y - sz.Y * 0.5f - 4f * u), b.Name, b.Selected ? LineSel : Dim, 0.8f);   // clear of the title lines
    }

    private static double si_own_outer(SectorHomes.Home h) => h.A + h.Size * 0.5 * SystemHost.SectorOrbitScale;
    public const int BeltSegments = 48;

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// A band section (annular sector) as a mesh part: centre angle, half span, inner/outer radius.
    /// Triangulated as a strip with inner, middle and outer rows; the gradient coordinate is 0 on
    /// both edges and the half-width in the middle (as the game's sectors: distance to the edge),
    /// so one band, even a full ring, has one clean glowing edge. r0 = 0 gives a disc.
    /// </summary>
    private static MapPipeline.Part Annulus(string name, double centre, double half, double r0, double r1)
    {
        var part = new MapPipeline.Part { Name = name };
        int n = Math.Max(6, (int)(half / Math.PI * 96));
        double hw = 0.5 * (r1 - r0), rm = 0.5 * (r0 + r1);
        Vector3 P(double a, double r) => new Vector3((float)(Math.Cos(a) * r), 0, (float)(Math.Sin(a) * r));
        void Tri(Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc)
        { part.TriVerts.Add(a); part.TriUvs.Add(ua); part.TriVerts.Add(b); part.TriUvs.Add(ub); part.TriVerts.Add(c); part.TriUvs.Add(uc); }
        var edge = new Vector2(0, 0);
        var mid = new Vector2((float)hw, 1f);
        for (int i = 0; i < n; i++)
        {
            double a0 = centre - half + 2 * half * i / n, a1 = centre - half + 2 * half * (i + 1) / n;
            if (r0 <= 1e-9)
            {
                // Disc: a fan from the centre; the centre carries the full radius as "distance to the edge".
                Tri(Vector3.Zero, new Vector2((float)r1, 1f), P(a0, r1), edge, P(a1, r1), edge);
                continue;
            }
            Vector3 i0 = P(a0, r0), i1 = P(a1, r0), m0 = P(a0, rm), m1 = P(a1, rm), o0 = P(a0, r1), o1 = P(a1, r1);
            Tri(i0, edge, m0, mid, m1, mid); Tri(i0, edge, m1, mid, i1, edge);
            Tri(m0, mid, o0, edge, o1, edge); Tri(m0, mid, o1, edge, m1, mid);
        }
        return part;
    }

    /// <summary>A sector's orbit line colour: its state, faint; the selected sector in gold.</summary>
    /// <summary>The sector under the mouse (from the last frame's pick).</summary>
    public static string Hovered;

    /// <summary>Planning a maneuver: the sectors step back (dimmer orbit, smaller dim label) unless selected or hovered.</summary>
    static bool Planning => ManeuverEditor && Maneuvers.Nodes.Count > 0;
    static readonly ColorSRGB QuietText = new ColorSRGB(0.72f, 0.78f, 0.86f, 0.62f);
    static bool Quiet(Band b) => Planning && !b.Selected && b.Name != Hovered;

    static ColorSRGB OrbitColor(Band b)
    {
        if (Quiet(b)) { var q = StateColor(b); return HudPanel.Alpha(q, 0.40f); }
        if (b.Selected) return LineSel;
        if (b.Name == Hovered) { var h = StateColor(b); return new ColorSRGB((byte)Math.Min(255, h.R + 64), (byte)Math.Min(255, h.G + 64), (byte)Math.Min(255, h.B + 64), (byte)255); }
        var c = StateColor(b);
        return HudPanel.Alpha(c, 0.75f);
    }

    /// <summary>Where a sector is now on its orbit: a ringed dot in its state colour.</summary>
    static void Marker(Vector3D world, Band b)
    {
        _markerAt[b.Name] = world;
        var c = b.Selected ? LineSel : StateColor(b);
        // The colonization map's own sector icons (locked, or the default), tinted by state.
        if (MapPipeline.ToScreen(world, out var ms))
        {
            float mu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            string icon = b.State == Keen.Game2.Simulation.GameSystems.Colonization.SectorColonizationState.Locked ? "LockedIcon" : "DefaultIcon";
            if (MapPipeline.ScreenIcon(icon, ms, (Quiet(b) ? 7f : b.Selected ? 11f : 9f) * mu, Quiet(b) ? HudPanel.Alpha(c, 0.5f) : c)) return;
        }
        if (Quiet(b)) { MapPipeline.ScreenRing(world, 5f, HudPanel.Alpha(c, 0.5f), 1.4f); MapPipeline.ScreenRing(world, 2f, HudPanel.Alpha(c, 0.5f), 2f); return; }
        MapPipeline.ScreenRing(world, 8f, c, 2f);
        MapPipeline.ScreenRing(world, 3.5f, c, 3.5f);
    }

    /// <summary>
    /// A body's marker ring: padded round the body's true size on the map, never smaller than the
    /// fixed marker (minPx) so a far-off body still reads.
    /// </summary>
    /// <summary>A body too small to see as a globe: a small filled dot (KSP), in its colour.</summary>
    private static void BodyDot(Func<Vector3D, Vector3D> W, Vector3D centre, double radiusLocal, ColorSRGB col)
    {
        if (!MapPipeline.ToScreen(W(centre), out var sc) || !InOpenArea(sc)) return;
        float rpx = MapPipeline.ToScreen(W(centre + new Vector3D(radiusLocal, 0, 0)), out var se) ? (se - sc).Length() : 0f;
        float uu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        if (rpx < 4f * uu) MapPipeline.ScreenDisc(sc, 4f * uu, col);
    }

    private static void BodyRing(Func<Vector3D, Vector3D> W, Vector3D centre, double radiusLocal, float minPx, ColorSRGB col, float width)
    {
        Vector3D wc = W(centre);
        float rpx = 0;
        if (MapPipeline.ToScreen(wc, out var sc))
            foreach (var ax in new[] { new Vector3D(radiusLocal, 0, 0), new Vector3D(0, 0, radiusLocal) })
                if (MapPipeline.ToScreen(W(centre + ax), out var se)) rpx = Math.Max(rpx, (se - sc).Length());
        // Only where the globe is too small to see: a flat ring on a big globe cut across its disc.
        float uu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        if (rpx < minPx * uu * 1.2f) MapPipeline.ScreenRing(wc, minPx * uu, col, width);
    }

    private static void Circle(Func<Vector3D, Vector3D> W, double r, ColorSRGB col, float px)
        => Curve(a => new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r), W, 0, 2 * Math.PI, 96, col, px);

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
