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

    /// <summary>Where a sector lives, as a list group ("Delfos", "Delfos rings", "Kemik Lagrange points", ...).</summary>
    static string Group(Band b)
    {
        string host = b.Home.Future ? b.Name.Replace(" Sector", "") : SystemHost.DisplayName(b.Home.Host);
        return b.Home.Kind switch
        {
            SectorHomes.Kind.Body => host,
            SectorHomes.Kind.Ring => host + (b.Home.Host == SystemHost.Registry?.Root?.Name ? " belts" : " ring"),
            _ => host + " Lagrange points",
        };
    }

    /// <summary>The list: the star first, then each planet outward (its space, its rings, its points), bodies to come last.</summary>
    static int GroupRank(Band b)
    {
        var reg = SystemHost.Registry;
        var host = reg?.Find(b.Home.Host);
        int body;
        if (b.Home.Future) body = 90;
        else if (host == null || host.IsRoot) body = 0;
        else
        {
            body = 1;
            double a = host.StateInParentAt(0).Position.Length();
            foreach (var p in reg.Root.Children) if (SystemHost.BeaconOf.ContainsKey(p.Name) && p.StateInParentAt(0).Position.Length() < a) body++;
        }
        return body * 10 + (int)b.Home.Kind;
    }

    /// <summary>Number the sectors in list order (group, then distance).</summary>
    static List<Band> Ordered(List<Band> bands)
    {
        var list = new List<Band>(bands);
        list.Sort((x, y) => { int g = GroupRank(x).CompareTo(GroupRank(y)); if (g != 0) return g; int p = x.Home.Point.CompareTo(y.Home.Point); return p != 0 ? p : x.Home.A.CompareTo(y.Home.A); });
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
            if (Maneuvers.Base(t, out var body, out var el) && reg != null && !(b.Home.Kind == SectorHomes.Kind.Body && b.Home.Host == body.Name))
            {
                double r1 = el.IsElliptic ? el.SemiMajorAxis : OrbitPropagation.StateAt(el, t).Position.Length();
                double Hohmann(double mu, double ra, double rb, out double tof)
                {
                    double at = (ra + rb) / 2;
                    tof = Math.PI * Math.Sqrt(at * at * at / mu);
                    return Math.Abs(Math.Sqrt(mu / ra) * (Math.Sqrt(2 * rb / (ra + rb)) - 1)) + Math.Abs(Math.Sqrt(mu / rb) * (1 - Math.Sqrt(2 * ra / (ra + rb))));
                }
                double dv = double.NaN, tt = 0;
                var h = b.Home;
                SectorHomes.Where(h, reg, t, out Vector3D centre);
                Vector3D target = SectorHomes.Where(h, reg, t);
                var about = reg.Root.DeepestSoiContaining(target, t) ?? reg.Root;
                if (about == body)   // about your own body: the two-burn transfer to its distance
                    dv = Hohmann(body.Mu, r1, (target - body.OriginInRoot(t).Position).Length(), out tt);
                else if (body.Parent != null && body.Parent.IsRoot)
                {
                    // Out of your planet's sphere, across about the star, and in (a planet's) or matched (the star's).
                    var root = body.Parent;
                    double rB = body.StateInParentAt(t).Position.Length();
                    double rT = (about.IsRoot ? target : about.OriginInRoot(t).Position).Length();
                    double at = (rB + rT) / 2;
                    tt = Math.PI * Math.Sqrt(at * at * at / root.Mu);
                    double vB = Math.Sqrt(root.Mu / rB), vT = Math.Sqrt(root.Mu / rT);
                    double vinfD = Math.Abs(Math.Sqrt(root.Mu * (2 / rB - 1 / at)) - vB), vinfA = Math.Abs(vT - Math.Sqrt(root.Mu * (2 / rT - 1 / at)));
                    double ej = Math.Sqrt(vinfD * vinfD + 2 * body.Mu / r1) - Math.Sqrt(body.Mu / r1);
                    double arr = vinfA;
                    if (!about.IsRoot)
                    {
                        double rc = Math.Max((target - about.OriginInRoot(t).Position).Length(), (reg.FindDefinition(about.Name)?.RadiusMeters ?? 6e4) + 150e3);
                        arr = Math.Sqrt(vinfA * vinfA + 2 * about.Mu / rc) - Math.Sqrt(about.Mu / rc);
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

    static string WherePlace(Band b)
    {
        var h = b.Home;
        switch (h.Kind)
        {
            case SectorHomes.Kind.Body: return h.Future ? $"{h.AU:0.##} AU" : "near space";
            case SectorHomes.Kind.Ring:
                return h.Host == SystemHost.Registry?.Root?.Name ? $"{h.Inner / SystemHost.AU:0.##}-{h.Outer / SystemHost.AU:0.##} AU"
                                                                 : $"{HudPanel.Km(h.Inner)}-{HudPanel.Km(h.Outer)}";
            default: return $"L{h.Point}";
        }
    }

    /// <summary>
    /// A sector drawn by its kind (everything is an orbit), about the origin of the level drawing it:
    ///  - Body: its space, a zone round the body (a body still to come: a marker on its own orbit);
    ///  - Ring: the whole band round its host;
    ///  - Lagrange: L3-L5 a section of its body's orbit about the point; L1 / L2 a small zone round it.
    /// toLocal maps a root position to the level's local map units (scale: map units per metre).
    /// </summary>
    static void DrawSector(Band bd, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, Func<Vector3D, Vector3D> toLocal, double scale)
    {
        var h = bd.Home;
        Vector3D at = SectorHomes.Where(h, reg, t, out Vector3D centre);
        Vector3D mk = toLocal(at), cl = toLocal(centre);
        double P = SectorHomes.Period(h, reg);
        bool marker = true;
        MapPipeline.PickName = bd.Name;
        switch (h.Kind)
        {
            case SectorHomes.Kind.Body when !h.Future:
                OwnSector(W, h.Outer * scale, bd, cl);   // its space, named at its top
                marker = false;
                break;
            case SectorHomes.Kind.Body:   // a body still to come: its orbit, faint
            {
                double r = h.AU * SystemHost.AU * scale;
                Curve(a => cl + new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r), W, 0, 2 * Math.PI, 90, HudPanel.Alpha(Line, 0.6f), 1.1f);
                break;
            }
        }
        MapPipeline.PickName = null;
        var b0 = bd; var W0 = W;
        string label = $"{bd.Number}  {bd.Name}";
        _deferred.Add(() =>
        {
            MapPipeline.PickName = b0.Name;
            switch (h.Kind)
            {
                case SectorHomes.Kind.Ring:
                    // The ring about its host (the point's offset from its centre, all the way round).
                    SectorArea(W0, q => { var p = SectorHomes.Where(h, reg, t + P * q, out var cq); return p - cq; },
                               v => toLocal(centre + v), b0, 0.5, (h.Outer - h.Inner) / (h.Outer + h.Inner));
                    break;
                case SectorHomes.Kind.Lagrange when h.Point >= 3:
                    SectorArea(W0, q => SectorHomes.Where(h, reg, t + P * q) - centre, v => toLocal(centre + v), b0);
                    break;
                case SectorHomes.Kind.Lagrange:   // L1 / L2: a small zone round the point
                {
                    var s = reg.Find(h.Host);
                    double rz = (s != null ? SectorHomes.HillRadius(s) : 1e6) * 0.15 * scale;
                    OwnSector(W0, rz, b0, mk);
                    break;
                }
            }
            if (marker)
            {
                Marker(W0(mk), b0);
                if (MapPipeline.ToScreen(W0(mk), out var ms))
                {
                    float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                    MapPipeline.TextScreen(ms + new Vector2(0, 22f * lu), label, Quiet(b0) ? QuietText : b0.Selected ? LineSel : Text, Quiet(b0) ? 0.72f * 0.85f : 0.72f);
                }
            }
            MapPipeline.PickName = null;
        });
    }

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
