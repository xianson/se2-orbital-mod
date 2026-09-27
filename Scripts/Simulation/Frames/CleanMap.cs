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
    public const double SystemRadius = 0.50;   // map units (fills most of the view at the map's default zoom)
    public const double SolarRadius = 1.90;
    public const double SolarZoom = 2.2;       // u (camera distance / original max) where the view switches
    public static double MeshRebuildSeconds = 0.5;

    static readonly ColorSRGB Line = new ColorSRGB(0.60f, 0.72f, 0.88f, 0.28f);
    static readonly ColorSRGB LineSel = new ColorSRGB(1.00f, 0.85f, 0.30f, 0.95f);
    static readonly ColorSRGB You = new ColorSRGB(1.00f, 0.80f, 0.15f, 1f);
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
    static void DrawList(List<Band> ordered, Func<Band, bool> expanded)
    {
        Vector2 scr = MapPipeline.ScreenSize;
        float x = scr.X * 0.775f, y = scr.Y * 0.27f, line = scr.Y * 0.0275f, scale = 0.78f;
        string group = null;
        foreach (var b in ordered)
        {
            string g = Group(b);
            if (g != group)
            {
                if (group != null) y += line * 0.35f;
                int count = ordered.FindAll(o => Group(o) == g).Count;
                bool open = expanded(b);
                MapPipeline.ScreenText(new Vector2(x, y), open ? g.ToUpperInvariant() : $"{g.ToUpperInvariant()}  ({count})", Dim, scale * 0.85f);
                y += line;
                group = g;
            }
            if (!expanded(b)) continue;
            var c = b.Selected ? LineSel : Text;
            MapPipeline.PickName = b.Name;
            MapPipeline.ScreenText(new Vector2(x, y), b.Number.ToString(), Dim, scale);
            MapPipeline.ScreenDot(new Vector2(x + scr.Y * 0.030f, y + line * 0.42f), 4.5f, StateColor(b));
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.045f, y), b.Name, c, scale);
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.19f, y), Where(b), Dim, scale * 0.85f);
            MapPipeline.PickName = null;
            y += line;
        }
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
        bool solar = u >= SolarZoom;
        string focus = Focus != "auto" ? Focus : null;
        if (focus == null) foreach (var bd in bands) if (bd.Selected && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) focus = bd.Host;
        focus ??= playerPlanet;
        var planet = focus != null ? reg.Find(focus) : null;

        var parts = new List<MapPipeline.Part>();
        var ordered = Ordered(bands);
        bool ui = MapPipeline.UiBegin(session, mapConfig);
        try
        {
            DrawList(ordered, b => solar
                ? b.Home.Kind == SectorHomes.Kind.Belt || b.Home.Kind == SectorHomes.Kind.Ring || b.Selected
                : b.Host == focus && b.Home.Kind != SectorHomes.Kind.Belt && b.Home.Kind != SectorHomes.Kind.Ring);
            if (!solar && planet != null) DrawSystem(parts, bands, reg, planet, t, playerPlanet, playerRel, playerOrbit, globes, W);
            else DrawSolar(parts, bands, reg, t, playerPlanet, globes, W);
            string selName = null;
            foreach (var bd in bands) if (bd.Selected) selName = bd.Name;
            if (_toMap != null && ManeuverEditor) Maneuvers.MapDraw(_toMap, W, _limit, t, Mouse, selName, solar ? null : planet?.Name);
            if (ManeuverEditor) ContextMenu(bands, t);
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
                ph.Outline.Add(new Vector3(0, -0.001f, 0)); ph.Outline.Add(new Vector3(1e-6f, -0.001f, 0)); ph.Outline.Add(new Vector3(0, -0.001f, 1e-6f));
                parts.Add(ph);
            }

        // The mesh changes only when the view changes, or every MeshRebuildSeconds (motion).
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string key = solar ? "solar" : "sys:" + focus;
        if (key != _lastKey || now - _lastMesh > MeshRebuildSeconds)
        {
            bool ok = MapPipeline.ShowParts(sectorsRenderer, parts);
            if (ok)
            {
                MapPipeline.ColourSection(sectorsRenderer, SunPart, new ColorSRGB(1f, 0.78f, 0.35f, 0.95f), new ColorSRGB(1f, 0.93f, 0.6f, 1f));
                MapPipeline.ColourSection(sectorsRenderer, BeltPart, new ColorSRGB(0.10f, 0.09f, 0.08f, 0.12f), new ColorSRGB(0.40f, 0.36f, 0.30f, 0.30f));
            }
            _lastKey = key; _lastMesh = now;
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
        double scaleSys = fit / (frame * 1.15);
        double R(double r) => Math.Max(0, r) * scaleSys;
        Vector3D L(double ang, double r) => new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
        Vector3D Lv(Vector3D rel) => L(Math.Atan2(rel.Y, rel.X), R(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y)));

        // Planet and moons.
        var pdef = reg.FindDefinition(planet.Name);
        double planetR = (pdef?.RadiusMeters ?? 6e4) * scaleSys;      // true size
        MapGlobes.Use(planet.Name, W(Vector3D.Zero), planetR, globes);
        BodyRing(W, Vector3D.Zero, planetR, 9f, Text, 1.5f);
        MapPipeline.Text(W(new Vector3D(0, 0, fit * 0.05)), planet.Name, Text, 0.9f);
        foreach (var moon in planet.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
            Vector3D mp = moon.StateInParentAt(t).Position;
            Vector3D ml = Lv(mp);
            MapGlobes.Use(moon.Name, W(ml), (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, globes);
            BodyRing(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 5f, Dim, 1.2f);
            MapPipeline.Text(W(ml + new Vector3D(0, 0, fit * 0.035)), moon.Name, Dim, 0.6f);
        }

        foreach (var bd in mine)
        {
            var h = bd.Home;
            MapPipeline.PickName = bd.Name;
            switch (h.Kind)
            {
                case SectorHomes.Kind.OwnPlanet:
                    Circle(W, R(si_own_outer(h)), OrbitColor(bd), bd.Selected ? 2.4f : 1.8f);
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
        }

        Vector3D porg = planet.OriginInRoot(t).Position;
        Overlay(r => Lv(r - porg), fit * 1.02, W, t, reg);
        _toMap = (r, tt) => Lv(r - planet.OriginInRoot(tt).Position);
        _limit = fit * 1.02;

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
            MapPipeline.Text(W(yl), "+", You, 1.2f);
            MapPipeline.Text(W(yl + new Vector3D(0, 0, fit * 0.05)), "you", You, 0.7f);
        }
    }

    // ───────────────────────────── solar view ─────────────────────────────

    private static void DrawSolar(List<MapPipeline.Part> parts, List<Band> bands, SystemRegistry reg, double t, string playerPlanet,
                                  HashSet<string> globes, Func<Vector3D, Vector3D> W)
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
        MapPipeline.Text(W(new Vector3D(0, 0, SolarRadius * 0.085)), "Sun", Text, 0.85f);

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
            int n = 0; foreach (var bd in bands) if (bd.Host == p.Name && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) n++;
            MapPipeline.Text(W(c + new Vector3D(0, 0, SolarRadius * 0.045)), p.Name, p.Name == playerPlanet ? You : Text, 0.9f);
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
            items.Add(new MapMenu.Item("Remove maneuver", () => Maneuvers.Delete(node, false)));
        }
        else if (!double.IsNaN(ht))
        {
            double T = ht;
            title = "Trajectory  ·  in " + Maneuvers.Clock(T - t);
            items.Add(new MapMenu.Item("Add maneuver", () => Maneuvers.AddNodeAt(T)));
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
                if (parent == null) continue;
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
                    if (havePrev && inView) MapPipeline.Line(W(prev), W(p), new ColorSRGB(EncColor.R, EncColor.G, EncColor.B, 0.55f), 1.4f);
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
    private static void Pin(Vector3D p, double limit, Func<Vector3D, Vector3D> W, string label, ColorSRGB c, bool diamond = false)
    {
        double r = Math.Sqrt(p.X * p.X + p.Z * p.Z);
        bool edge = r > limit;
        if (edge) p = p * (limit / r);
        Vector3D w = W(p);
        if (diamond) MapPipeline.ScreenDiamond(w, 6f, c, 1.8f);
        else { MapPipeline.ScreenRing(w, 6f, c, 1.8f); MapPipeline.ScreenRing(w, 2.5f, c, 2.5f); }
        MapPipeline.Text(W(p * (edge ? 1.07 : 1.0) + new Vector3D(0, 0, limit * 0.045)), edge ? label + " >" : label, c, 0.6f);
    }

    public const string SunPart = "OrbitalSun", BeltPart = "OrbitalBelt";
    // The planet's own sector at true size: from its charted distance, +/- half its charted size.
    private static double si_own_inner(SectorHomes.Home h) => Math.Max(0, h.A - h.Size * 0.5 * SystemHost.OrbitScale);
    private static double si_own_outer(SectorHomes.Home h) => h.A + h.Size * 0.5 * SystemHost.OrbitScale;
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
        if (Quiet(b)) { var q = StateColor(b); return new ColorSRGB(q.R, q.G, q.B, 0.40f); }
        if (b.Selected) return LineSel;
        if (b.Name == Hovered) { var h = StateColor(b); return new ColorSRGB(Math.Min(1f, h.R + 0.25f), Math.Min(1f, h.G + 0.25f), Math.Min(1f, h.B + 0.25f), 1f); }
        var c = StateColor(b);
        return new ColorSRGB(c.R, c.G, c.B, 0.75f);
    }

    /// <summary>Where a sector is now on its orbit: a ringed dot in its state colour.</summary>
    static void Marker(Vector3D world, Band b)
    {
        var c = b.Selected ? LineSel : StateColor(b);
        if (Quiet(b)) { MapPipeline.ScreenRing(world, 5f, new ColorSRGB(c.R, c.G, c.B, 0.5f), 1.4f); MapPipeline.ScreenRing(world, 2f, new ColorSRGB(c.R, c.G, c.B, 0.5f), 2f); return; }
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
