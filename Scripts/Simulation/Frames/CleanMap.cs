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
        bool solar = u >= SolarZoom;
        string focus = Focus != "auto" ? Focus : null;
        if (focus == null) foreach (var bd in bands) if (bd.Selected && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) focus = bd.Host;
        focus ??= playerPlanet;
        var planet = focus != null ? reg.Find(focus) : null;

        var parts = new List<MapPipeline.Part>();
        bool ui = MapPipeline.UiBegin(session, mapConfig);
        try
        {
            if (!solar && planet != null) DrawSystem(parts, bands, reg, planet, t, playerPlanet, playerRel, playerOrbit, globes, W);
            else DrawSolar(parts, bands, reg, t, playerPlanet, globes, W);
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
        MapPipeline.ScreenRing(W(Vector3D.Zero), 9f, Text, 1.5f);
        MapPipeline.Text(W(new Vector3D(0, 0, fit * 0.05)), planet.Name, Text, 0.9f);
        foreach (var moon in planet.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
            Vector3D mp = moon.StateInParentAt(t).Position;
            Vector3D ml = Lv(mp);
            MapGlobes.Use(moon.Name, W(ml), (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, globes);
            MapPipeline.ScreenRing(W(ml), 5f, Dim, 1.2f);
            MapPipeline.Text(W(ml + new Vector3D(0, 0, fit * 0.035)), moon.Name, Dim, 0.6f);
        }

        foreach (var bd in mine)
        {
            var h = bd.Home;
            switch (h.Kind)
            {
                case SectorHomes.Kind.OwnPlanet:
                    parts.Add(Annulus(bd.Name, 0, Math.PI, R(si_own_inner(h)), R(si_own_outer(h))));
                    break;
                case SectorHomes.Kind.Ellipse:
                {
                    SectorHomes.Rel(h, planet, t, out var rel);
                    double ang = Math.Atan2(rel.Y, rel.X);
                    double rr = R(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y));   // true current distance
                    if (bd.Selected)
                    {
                        // Its true ellipse.
                        double T = SectorHomes.Period(h, planet);
                        Vector3D pv = default;
                        for (int q = 0; q <= 128; q++)
                        {
                            SectorHomes.Rel(h, planet, t + T * q / 128, out var rq);
                            Vector3D pp = W(Lv(rq));
                            if (q > 0) MapPipeline.Line(pv, pp, LineSel, 2f);
                            pv = pp;
                        }
                    }
                    parts.Add(Annulus(bd.Name, ang, 0.20, rr - fit * 0.028, rr + fit * 0.028));
                    MapPipeline.Text(W(L(ang, rr + fit * 0.075)), bd.Name, bd.Selected ? LineSel : Text, 0.72f);
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
                        MapPipeline.Text(W(edgeP * 1.08), $"{bd.Name}  {Math.Sqrt(now.X * now.X + now.Y * now.Y) / 1e6:F1} Mm  >", bd.Selected ? LineSel : Dim, 0.62f);
                        break;
                    }
                    double ang = Math.Atan2(nl.Z, nl.X), rr = Math.Sqrt(nl.X * nl.X + nl.Z * nl.Z);
                    var dot = Annulus(bd.Name, 0, Math.PI, 0, fit * 0.022);
                    for (int q = 0; q < dot.TriVerts.Count; q++) dot.TriVerts[q] += (Vector3)nl;
                    parts.Add(dot);
                    MapPipeline.Text(W(nl + new Vector3D(0, 0, fit * 0.06)), bd.Name, bd.Selected ? LineSel : Text, 0.72f);
                    break;
                }
            }
        }

        // You.
        if (playerPlanet == planet.Name)
        {
            if (playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))
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
        MapPipeline.ScreenRing(W(Vector3D.Zero), 10f, new ColorSRGB(1f, 0.85f, 0.4f, 0.9f), 1.5f);
        MapPipeline.Text(W(new Vector3D(0, 0, SolarRadius * 0.085)), "Sun", Text, 0.85f);

        // The belt: a torus of its own, and its sectors as band sections on it.
        double b0 = Rs(SectorHomes.BeltInnerAU * SystemHost.AU), b1 = Rs(SectorHomes.BeltOuterAU * SystemHost.AU);
        // The belt: just its two edges, faint (a filled torus dominated the view).
        Circle(W, b0, BeltLine, 1f);
        Circle(W, b1, BeltLine, 1f);
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Belt) continue;
            Vector3D hp = SectorHomes.HelioBelt(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            parts.Add(Annulus(bd.Name, ang, 0.07, r - SolarRadius * 0.035, r + SolarRadius * 0.035));
            MapPipeline.Text(W(new Vector3D(Math.Cos(ang) * (r + SolarRadius * 0.085), 0, Math.Sin(ang) * (r + SolarRadius * 0.085))), bd.Name, bd.Selected ? LineSel : Text, 0.72f);
        }

        // Sectors with their own orbit (a planet-like ring): the full orbit line and the band section.
        foreach (var bd in bands)
        {
            if (bd.Home.Kind != SectorHomes.Kind.Ring) continue;
            Vector3D hp = SectorHomes.HelioRing(bd.Home, root.Mu, t);
            double ang = Math.Atan2(hp.Y, hp.X), r = Rs(hp.Length());
            Circle(W, r, bd.Selected ? LineSel : Line, 1.2f);
            parts.Add(Annulus(bd.Name, ang, 0.09, r - SolarRadius * 0.04, r + SolarRadius * 0.04));
            MapPipeline.Text(W(new Vector3D(Math.Cos(ang) * (r + SolarRadius * 0.09), 0, Math.Sin(ang) * (r + SolarRadius * 0.09))), bd.Name, bd.Selected ? LineSel : Text, 0.8f);
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
            MapPipeline.ScreenRing(W(c), 8f, p.Name == playerPlanet ? You : Text, 1.5f);
            int n = 0; foreach (var bd in bands) if (bd.Host == p.Name && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) n++;
            MapPipeline.Text(W(c + new Vector3D(0, 0, SolarRadius * 0.045)), p.Name, p.Name == playerPlanet ? You : Text, 0.9f);
        }
    }

    public const string SunPart = "OrbitalSun", BeltPart = "OrbitalBelt";
    // The planet's own sector at true size: from its charted distance, +/- half its charted size.
    private static double si_own_inner(SectorHomes.Home h) => Math.Max(0, h.A - h.Size * 0.5);
    private static double si_own_outer(SectorHomes.Home h) => h.A + h.Size * 0.5;
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
