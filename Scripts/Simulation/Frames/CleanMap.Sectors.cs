using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The map's sectors: their areas, markers, colours, groups and list order.</summary>
public static partial class CleanMap
{

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

    static string WherePlace(Band b) => b.Home.Kind switch
    {
        SectorHomes.Kind.OwnPlanet => "near space",
        SectorHomes.Kind.Ellipse => $"{b.Home.A / 1000:N0} km",
        SectorHomes.Kind.L1 => "L1", SectorHomes.Kind.L2 => "L2", SectorHomes.Kind.L4 => "L4", SectorHomes.Kind.L5 => "L5",
        SectorHomes.Kind.Belt => $"{SectorHomes.BeltInnerAU:F1}-{SectorHomes.BeltOuterAU:F1} AU",
        SectorHomes.Kind.Ring => $"{b.Home.AU:0.##} AU",
        _ => "",
    };
    /// <summary>Where each sector's marker was drawn (world), for centring on it.</summary>
    private static readonly Dictionary<string, Vector3D> _markerAt = new Dictionary<string, Vector3D>();

    /// <summary>Glide the view to a sector's marker (in this view).</summary>
    public static void CentreOn(string sector)
    {
        if (sector != null && _markerAt.TryGetValue(sector, out var at)) MapCamera.PanTo(at, smooth: true);
    }
    /// <summary>Sector areas, markers and names: drawn after every line, so no orbit runs through a sector.</summary>
    private static readonly List<Action> _deferred = new List<Action>();
    /// <summary>A sector section's reach along its orbit, each side of where it is (fraction of the period).</summary>
    const double SectorSpan = 0.035;

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

    /// <summary>A sector's orbit line colour: its state, faint; the selected sector in gold.</summary>
    /// <summary>The sector under the mouse (from the last frame's pick).</summary>
    public static string Hovered;

    /// <summary>Planning a maneuver: the sectors step back (dimmer orbit, smaller dim label) unless selected or hovered.</summary>
    static bool Planning => ManeuverEditor && Maneuvers.Nodes.Count > 0;
    static readonly ColorSRGB QuietText = new ColorSRGB(0.72f, 0.78f, 0.86f, 0.62f);
    static bool Quiet(Band b) => Planning && !b.Selected && b.Name != Hovered;

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
}
