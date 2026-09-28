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
        SectorHomes.Kind.Ring => "Outer ring",
        _ => "Other",
    };

    static int GroupRank(Band b)
    {
        int planet = Array.IndexOf(SystemHost.PlanetOrder, b.Host); if (planet < 0) planet = 9;
        return b.Home.Kind switch
        {
            SectorHomes.Kind.OwnPlanet => planet * 10, SectorHomes.Kind.Ellipse => planet * 10 + 1,
            SectorHomes.Kind.L1 or SectorHomes.Kind.L2 => planet * 10 + 2, SectorHomes.Kind.L4 or SectorHomes.Kind.L5 => planet * 10 + 3,
            SectorHomes.Kind.Belt => 200, SectorHomes.Kind.Ring => 300, _ => 400,
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
        SectorHomes.Kind.Ring => SectorHomes.RingAU * SystemHost.AU,
        _ => host.StateInParentAt(SystemHost.Now).Position.Length(),   // Trojans share the planet's orbit
    };

    static string WherePlace(Band b) => b.Home.Kind switch
    {
        SectorHomes.Kind.OwnPlanet => "near space",
        SectorHomes.Kind.Ellipse => $"{b.Home.A / 1000:N0} km",
        SectorHomes.Kind.L1 => "L1", SectorHomes.Kind.L2 => "L2", SectorHomes.Kind.L4 => "L4", SectorHomes.Kind.L5 => "L5",
        SectorHomes.Kind.Belt => $"{SectorHomes.BeltInnerAU:F1}-{SectorHomes.BeltOuterAU:F1} AU",
        SectorHomes.Kind.Ring => $"{SectorHomes.RingAU:F1} AU",
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
        MapPipeline.ScreenText(new Vector2(scr.X * 0.265f, scr.Y * 0.905f), h, Dim, 0.62f);
    }

    private static double _lastMesh;
    private static string _lastKey;
    public static string Status = "-";

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session, object sectorsRenderer, object mapConfig,
                            List<Band> bands, SystemRegistry reg, Vector3D mapPos, Quaternion orient,
                            double u, double t, string playerPlanet, Vector3D playerRel, KeplerianElements? playerOrbit,
                            HashSet<string> globes)
    {
        Vector3D W(Vector3D local) => mapPos + (QuaternionD)orient * local;
        _session = session;
        // Hysteresis: a zoom resting near the switch must not flicker between the views.
        bool solar = _wasSolar == true ? u >= SolarZoom * 0.9 : u >= SolarZoom * 1.1;
        string focus = Focus != "auto" ? Focus : ViewFocus;
        if (focus == null) foreach (var bd in bands) if (bd.Selected && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) focus = bd.Host;
        focus ??= playerPlanet;
        var planet = focus != null ? reg.Find(focus) : null;
        ZoomTransition(solar, planet, reg, t, W, mapPos);
        _hits.Clear(); _crumbs.Clear();
        if (!solar && ViewFocus != null) planet = reg.Find(ViewFocus) ?? planet;
        focus = planet?.Name ?? focus;
        _viewBody = !solar && planet != null && planet.Parent != null ? planet : null;

        var parts = new List<MapPipeline.Part>();
        var ordered = Ordered(bands);
        bool ui = MapPipeline.UiBegin(session, mapConfig);
        try
        {
            // The game's own panels (left column, tab bar, bottom hints): map labels keep off them.
            var scrR = MapPipeline.ScreenSize;
            MapPipeline.Reserve(Vector2.Zero, new Vector2(scrR.X * 0.255f, scrR.Y));
            MapPipeline.Reserve(Vector2.Zero, new Vector2(scrR.X, scrR.Y * 0.1f));
            MapPipeline.Reserve(new Vector2(0, scrR.Y * 0.93f), scrR);
            // The system layout: zoomed out, or about the star itself (orbiting it).
            bool sys = solar || planet == null || planet.Parent == null;
            DrawList(ordered, b => sys
                ? b.Home.Kind == SectorHomes.Kind.Belt || b.Home.Kind == SectorHomes.Kind.Ring || b.Selected
                : b.Host == focus && b.Home.Kind != SectorHomes.Kind.Belt && b.Home.Kind != SectorHomes.Kind.Ring,
                // A planet's view lists that planet's sectors only; the system view lists them all.
                b => sys || (b.Host == focus && b.Home.Kind != SectorHomes.Kind.Belt && b.Home.Kind != SectorHomes.Kind.Ring));
            Title(sys ? null : planet, playerPlanet, playerOrbit);
            if (!solar && planet != null && planet.Parent != null) DrawSystem(parts, bands, reg, planet, t, playerPlanet, playerRel, playerOrbit, globes, W);
            else DrawSolar(parts, bands, reg, t, playerPlanet, globes, W, playerOrbit);
            string selName = null;
            foreach (var bd in bands) if (bd.Selected) selName = bd.Name;
            if (_toMap != null && ManeuverEditor) Maneuvers.MapDraw(_toMap, W, _limit, t, Mouse, selName, sys ? reg.Root?.Name : planet?.Name);   // editable in every view
            if (ManeuverEditor) ContextMenu(bands, t);
            _lastW = W; _lastSolar = solar;
            FocusInput(reg, t, W, solar);
            BodyTooltip(reg, playerPlanet, playerOrbit);
            ListInput(W);
            Hints();
            WarpBar.DrawMap(Mouse);
        }
        finally { if (ui) MapPipeline.UiEnd(); }

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
        string key = solar ? "solar" : "sys:" + focus;
        bool moved = double.IsNaN(_lastMeshSimT) || Math.Abs(t - _lastMeshSimT) > MeshRebuildSimSeconds;
        if (key != _lastKey || (moved && now - _lastMesh > MeshRebuildSeconds))
        {
            bool ok = MapPipeline.ShowParts(sectorsRenderer, parts);
            if (ok)
            {
                MapPipeline.ColourSection(sectorsRenderer, SunPart, new ColorSRGB(1f, 0.78f, 0.35f, 0.95f), new ColorSRGB(1f, 0.93f, 0.6f, 1f));
                MapPipeline.ColourSection(sectorsRenderer, BeltPart, new ColorSRGB(0.10f, 0.09f, 0.08f, 0.12f), new ColorSRGB(0.40f, 0.36f, 0.30f, 0.30f));
            }
            _lastKey = key; _lastMesh = now; _lastMeshSimT = t;
            Status = ok ? $"{key} parts={parts.Count}" : $"{key} mesh failed: {MapPipeline.LastError}";
        }
    }

    public static void Reset() { _lastKey = null; }

    // ───────────────────────────── system view ─────────────────────────────

    private static void DrawSystem(List<MapPipeline.Part> parts, List<Band> bands, SystemRegistry reg, GravityBody planet, double t,
                                   string playerPlanet, Vector3D playerRel, KeplerianElements? playerOrbit, HashSet<string> globes,
                                   Func<Vector3D, Vector3D> W)
    {
        double fit = SystemRadius;
        var mine = bands.FindAll(b => b.Host == planet.Name);
        var ell = mine.FindAll(b => b.Home.Kind == SectorHomes.Kind.Ellipse);
        ell.Sort((x, y) => x.Home.A.CompareTo(y.Home.A));
        double rH = SectorHomes.HillRadius(planet);
        // Strictly proportional: one linear scale, framed on the planet's sectors (farthest
        // apoapsis); anything beyond the frame (the L1/L2 sectors) is pinned to the edge.
        double frame = rH * 0.2;
        foreach (var b in ell) frame = Math.Max(frame, b.Home.A * (1 + b.Home.E));
        // A moon's reach is small next to its own size: frame at least a few radii, or it fills the view.
        frame = Math.Max(frame, 8 * (reg.FindDefinition(planet.Name)?.RadiusMeters ?? 0));
        double scaleSys = fit / (frame * 1.15);
        double R(double r) => Math.Max(0, r) * scaleSys;
        Vector3D L(double ang, double r) => new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
        Vector3D Lv(Vector3D rel) => L(Math.Atan2(rel.Y, rel.X), R(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y)));

        // Planet and moons.
        var pdef = reg.FindDefinition(planet.Name);
        double planetR = (pdef?.RadiusMeters ?? 6e4) * scaleSys;      // true size
        MapGlobes.Use(planet.Name, W(Vector3D.Zero), planetR, globes);
        BodyRing(W, Vector3D.Zero, planetR, 9f, Text, 1.5f);
        Hit(planet, W(Vector3D.Zero), W(new Vector3D(planetR, 0, 0)), 9f);
        BodyLabel(W, Vector3D.Zero, planetR, 9f, SystemHost.DisplayName(planet.Name), Text, 0.9f);
        foreach (var moon in planet.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
            Vector3D mp = moon.StateInParentAt(t).Position;
            Vector3D ml = Lv(mp);
            if (!MapPipeline.ToScreen(W(ml), out var mls) || !InOpenArea(mls)) continue;   // off view: not over the game's panels
            MapGlobes.Use(moon.Name, W(ml), (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, globes);
            BodyRing(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 5f, Dim, 1.2f);
            Hit(moon, W(ml), W(ml + new Vector3D((reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 0, 0)), 5f);
            BodyLabel(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 5f, moon.Name, Dim, 0.6f);
        }

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
                    double ang = Math.Atan2(rel.Y, rel.X);
                    double rr = R(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y));   // true current distance
                    // Its true ellipse, every sector: the orbit is the sector.
                    double T = SectorHomes.Period(h, planet);
                    Vector3D pv = default;
                    for (int q = 0; q <= 160; q++)
                    {
                        SectorHomes.Rel(h, planet, t + T * q / 160, out var rq);
                        Vector3D pp = W(Lv(rq));
                        if (q > 0) MapPipeline.Line(pv, pp, OrbitColor(bd), bd.Selected ? 2.4f : 1.8f);
                        pv = pp;
                    }
                    Marker(W(L(ang, rr)), bd);
                    MapPipeline.Text(W(L(ang, rr + fit * 0.055)), $"{bd.Number}  {bd.Name}", Quiet(bd) ? QuietText : bd.Selected ? LineSel : Text, Quiet(bd) ? 0.72f * 0.85f : 0.72f);
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
                    if (nlLen > fit * 1.02)
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

        Vector3D porg = planet.OriginInRoot(t).Position;
        Overlay(r => Lv(r - porg), fit * 1.02, W, t, reg);
        _toMap = (r, tt) => Lv(r - planet.OriginInRoot(tt).Position);
        _limit = fit * 1.02;

        MapPipeline.PickName = null;
        // You.
        if (playerPlanet == planet.Name)
        {
            // Opening the map: framed on your orbit when it is small on screen (as KSP), so a low orbit
            // and its tags are not crammed into a few pixels round the planet.
            if (_fitPending && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis) && MapCamera.Distance > 0)
            {
                _fitPending = false;
                var o = playerOrbit.Value;
                double far = o.IsElliptic ? o.SemiMajorAxis * (1 + o.Eccentricity) : planet.SoiRadius;
                if (MapPipeline.ToScreen(W(Vector3D.Zero), out var c0) && MapPipeline.ToScreen(W(L(0, R(far))), out var c1))
                {
                    float px = (c1 - c0).Length(), want = MapPipeline.ScreenSize.Y * 0.28f;
                    if (px > 1 && px < want * 0.6f) MapCamera.ZoomTo(MapCamera.Distance * px / want);
                }
            }
            if (!Planning && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))   // planning: the planner draws it
            {
                var path = OrbitSampler.SamplePath(playerOrbit.Value, 128, planet.SoiRadius);
                var pts = path.Points;
                if (pts != null)
                    for (int i = 0; i < pts.Length - (path.IsClosed ? 0 : 1); i++)
                        MapPipeline.Line(W(Lv(pts[i])), W(Lv(pts[(i + 1) % pts.Length])), You, 2f);
            }
            Vector3D yl = Lv(playerRel);
            MapPipeline.Text(W(yl), "+", You, 1.2f);
            // Beside the mark on screen (a map-space offset lands far away when zoomed in).
            if (MapPipeline.ToScreen(W(yl), out var ys) && InOpenArea(ys))
            {
                float uu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                HudPanel.TagAt(ys + new Vector2(12f * uu, 0), "you", You, uu, diamond: false);
            }
        }
    }

    // ───────────────────────────── solar view ─────────────────────────────

    private static void DrawSolar(List<MapPipeline.Part> parts, List<Band> bands, SystemRegistry reg, double t, string playerPlanet,
                                  HashSet<string> globes, Func<Vector3D, Vector3D> W, KeplerianElements? playerOrbit = null)
    {
        var root = reg.Root;
        double outer = SectorHomes.RingAU * 1.05 * SystemHost.AU;
        // Display radius: compressed (r^0.55) so the inner planets are not crammed against the sun;
        // order and angles are true, the physics stays proportional.
        double Rs(double r) => SolarRadius * Math.Max(0, r) / outer;   // strictly proportional
        Vector3D S(Vector3D helio) { double r = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y); double f = r > 0 ? Rs(r) / r : 0; return new Vector3D(helio.X * f, 0, helio.Y * f); }

        // The sun: a warm disc (its own section, coloured below), and its name.
        parts.Add(Annulus(SunPart, 0, Math.PI, 0, Math.Max(SystemHost.StarRadius * SolarRadius / outer, SolarRadius * 0.004)));   // true size
        BodyRing(W, Vector3D.Zero, SystemHost.StarRadius * SolarRadius / outer, 10f, new ColorSRGB(1f, 0.85f, 0.4f, 0.9f), 1.5f);
        Hit(root, W(Vector3D.Zero), W(new Vector3D(SystemHost.StarRadius * SolarRadius / outer, 0, 0)), 10f);
        BodyLabel(W, Vector3D.Zero, SystemHost.StarRadius * SolarRadius / outer, 10f, StarName, Text, 0.85f);

        // The belt: a torus of its own, and its sectors as band sections on it.
        double b0 = Rs(SectorHomes.BeltInnerAU * SystemHost.AU), b1 = Rs(SectorHomes.BeltOuterAU * SystemHost.AU);
        // The belt: just its two edges, faint (a filled torus dominated the view).
        Circle(W, b0, BeltLine, 1f);
        Circle(W, b1, BeltLine, 1f);
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Belt) continue;
            MapPipeline.PickName = bd.Name;
            Vector3D hp = SectorHomes.HelioBelt(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            Circle(W, r, OrbitColor(bd), bd.Selected ? 2.4f : 1.8f);
            Marker(W(new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r)), bd);
            MapPipeline.Text(W(new Vector3D(Math.Cos(ang) * (r + SolarRadius * 0.06), 0, Math.Sin(ang) * (r + SolarRadius * 0.06))), $"{bd.Number}  {bd.Name}", Quiet(bd) ? QuietText : bd.Selected ? LineSel : Text, Quiet(bd) ? 0.72f * 0.85f : 0.72f);
            MapPipeline.PickName = null;
        }

        // Sectors with their own orbit (a planet-like ring): the full orbit line and the band section.
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Ring) continue;
            MapPipeline.PickName = bd.Name;
            Vector3D hp = SectorHomes.HelioRing(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            Circle(W, r, OrbitColor(bd), bd.Selected ? 2.4f : 1.8f);
            Marker(W(new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r)), bd);
            MapPipeline.Text(W(new Vector3D(Math.Cos(ang) * (r + SolarRadius * 0.06), 0, Math.Sin(ang) * (r + SolarRadius * 0.06))), $"{bd.Number}  {bd.Name}", Quiet(bd) ? QuietText : bd.Selected ? LineSel : Text, Quiet(bd) ? 0.8f * 0.85f : 0.8f);
            MapPipeline.PickName = null;
        }

        // The planets: orbit line, the globe, and the planet's own sector as a circular section around it.
        foreach (var p in root.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
            var el = OrbitalMath.ToElements(p.StateInParentAt(t), root.Mu, t);
            var path = OrbitSampler.SamplePath(el, 160);
            if (path.Points != null)
                for (int i = 0; i < path.Points.Length; i++)
                    MapPipeline.Line(W(S(path.Points[i])), W(S(path.Points[(i + 1) % path.Points.Length])), Line, 1.2f);
            Vector3D hp = p.StateInParentAt(t).Position;
            Vector3D c = S(hp);
            var own = bands.Find(b => b.Host == p.Name && b.Home.Kind == SectorHomes.Kind.OwnPlanet);
            if (own != null)
            {
                // The planet's own sector at true size is far below a pixel here: the ring marker stands for it.
            }
            MapGlobes.Use(p.Name, W(c), (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, globes);   // true size
            BodyRing(W, c, (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, 8f, p.Name == playerPlanet ? You : Text, 1.5f);
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
                MapPipeline.ScreenText(ys - new Vector2(6f * uu, 12f * uu), "+", You, 1.2f);
                HudPanel.TagAt(ys + new Vector2(12f * uu, 0), "you", You, uu, diamond: false);
            }
        }
        MapPipeline.PickName = null;
        Overlay(r => S(r), SolarRadius * 1.02, W, t, reg);
        _toMap = (r, tt) => S(r);
        _limit = SolarRadius * 1.02;
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
        double baseMax = Math.Max(1e-3, UnifiedMap.BaseMax);
        if (b.Parent == null)
        {
            if (solar) MapCamera.PanTo(W(Vector3D.Zero), smooth: true);
            else { _glideToStar = true; MapCamera.ZoomTo(SolarZoom * 1.3 * baseMax); }
            return;
        }
        if (solar)
        {
            var top = b; while (top.Parent != null && top.Parent.Parent != null) top = top.Parent;
            _glide = b;
            MapCamera.PanTo(W(SolarLocal(top, t)), smooth: true);
            MapCamera.ZoomTo(SolarZoom * 0.5 * baseMax);
            return;
        }
        bool same = ViewFocus == b.Name || (_viewBody != null && _viewBody == b);
        ViewFocus = b.Name;
        MapCamera.PanTo(W(Vector3D.Zero), smooth: same);
        if (!same && UnifiedMap.DefaultDistance > 0) MapCamera.ZoomTo(UnifiedMap.DefaultDistance);   // another body: its view at the usual zoom
    }

    /// <summary>A breadcrumb part's size: the font's measure, but never under ~8 px a character per unit
    /// scale (the measure comes out short at high resolutions, and parts ran together).</summary>
    static Vector2 CrumbSize(string text)
    {
        return MapPipeline.MeasureText(text, 1.05f);   // (now never short)
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
        MapPipeline.Reserve(at - new Vector2(4, 4), at + new Vector2(scr.X * 0.3f, scr.Y * 0.09f));
        string you;
        if (FrameHost.PlayerFrame == null && FrameHost.Grounded && playerPlanet != null) you = $"You: on {playerPlanet}";
        else if (orbit.HasValue && playerPlanet != null)
        {
            var o = orbit.Value;
            double r = SystemHost.Registry?.FindDefinition(playerPlanet)?.RadiusMeters ?? 0;
            string pe = Km(o.PeriapsisRadius - r);
            string ap = o.IsElliptic ? Km(o.SemiMajorAxis * (1 + o.Eccentricity) - r) : "escape";
            you = $"You: orbiting {SystemHost.DisplayName(playerPlanet)}   Pe {pe}   Ap {ap}";
        }
        else you = playerPlanet != null ? $"You: near {SystemHost.DisplayName(playerPlanet)}" : "";
        if (you.Length > 0) MapPipeline.ScreenText(at + new Vector2(0, scr.Y * 0.034f), you, You, 0.78f);
        string burn = Maneuvers.BurnLine;
        if (burn != null) MapPipeline.ScreenText(at + new Vector2(0, scr.Y * 0.062f), burn, Dim, 0.78f);
        string tgt = Maneuvers.TargetLine(SystemHost.Now);
        if (tgt != null) MapPipeline.ScreenText(at + new Vector2(0, scr.Y * (burn != null ? 0.09f : 0.062f)), tgt, TargetText, 0.78f);
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

    /// <summary>
    /// A body's name centred just below it on screen: below its drawn disc (largest screen extent of
    /// its radius, so a tilted view cannot tuck it under the globe) or its ring when that is bigger.
    /// </summary>
    /// <summary>The map's open area on screen (between the game's panels, above the warp bar).</summary>
    public static bool InOpenArea(Vector2 s)
    {
        var scr = MapPipeline.ScreenSize;
        return s.X >= scr.X * 0.255f && s.X <= scr.X * 0.775f && s.Y >= scr.Y * 0.1f && s.Y <= scr.Y * 0.84f;
    }

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
    {
        double outer = SectorHomes.RingAU * 1.05 * SystemHost.AU;
        Vector3D h = b.OriginInRoot(t).Position;
        double r = Math.Sqrt(h.X * h.X + h.Y * h.Y);
        double f = r > 0 ? SolarRadius * r / outer / r : 0;
        return new Vector3D(h.X * f, 0, h.Y * f);
    }

    /// <summary>The map closed: the next opening starts from where you are.</summary>
    public static void ResetView() { ViewFocus = null; _wasSolar = null; _fitPending = true; }
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
                if (parent == null || !InView(parent)) continue;
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

    /// <summary>A marker and label at a map point, or pinned to the view's edge with an arrow.</summary>
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
        MapPipeline.ScreenLine(e, e - n * a + side * a * 0.6f, c, 1.8f * u);
        MapPipeline.ScreenLine(e, e - n * a - side * a * 0.6f, c, 1.8f * u);
        MapPipeline.TextScreen(e - n * (a + 14f * u), label, c, 0.6f);
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
        if (Quiet(b)) { MapPipeline.ScreenRing(world, 5f, HudPanel.Alpha(c, 0.5f), 1.4f); MapPipeline.ScreenRing(world, 2f, HudPanel.Alpha(c, 0.5f), 2f); return; }
        MapPipeline.ScreenRing(world, 8f, c, 2f);
        MapPipeline.ScreenRing(world, 3.5f, c, 3.5f);
    }

    /// <summary>
    /// A body's marker ring: padded round the body's true size on the map, never smaller than the
    /// fixed marker (minPx) so a far-off body still reads.
    /// </summary>
    private static void BodyRing(Func<Vector3D, Vector3D> W, Vector3D centre, double radiusLocal, float minPx, ColorSRGB col, float width)
    {
        Vector3D wc = W(centre);
        float rpx = 0;
        if (MapPipeline.ToScreen(wc, out var sc))
            foreach (var ax in new[] { new Vector3D(radiusLocal, 0, 0), new Vector3D(0, 0, radiusLocal) })
                if (MapPipeline.ToScreen(W(centre + ax), out var se)) rpx = Math.Max(rpx, (se - sc).Length());
        MapPipeline.ScreenRing(wc, Math.Max(rpx + 1.5f, minPx), col, width);   // snug on the limb
    }

    private static void Circle(Func<Vector3D, Vector3D> W, double r, ColorSRGB col, float px)
    {
        const int n = 128;
        Vector3D prev = W(new Vector3D(r, 0, 0));
        for (int i = 1; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector3D p = W(new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r));
            MapPipeline.Line(prev, p, col, px);
            prev = p;
        }
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
