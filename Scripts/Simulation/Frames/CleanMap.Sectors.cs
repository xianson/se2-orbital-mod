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

    /// <summary>
    /// A body's Lagrange points (with its primary), sector or not: a small faint mark and "L4" etc.
    /// Points a sector sits on are left to the sector.
    /// </summary>
    static void LagrangeMarks(List<Band> bands, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, Func<Vector3D, Vector3D> toLocal, GravityBody body, int fromPoint, int toPoint)
    {
        if (body?.Parent == null) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        var col = HudPanel.Alpha(Dim, 0.55f);
        for (int p = fromPoint; p <= toPoint; p++)
        {
            if (bands.Exists(b => b.Home.Kind == SectorHomes.Kind.Lagrange && b.Home.Host == body.Name && b.Home.Point == p)) continue;
            var h = new SectorHomes.Home { Kind = SectorHomes.Kind.Lagrange, Host = body.Name, Point = p };
            Vector3D at = SectorHomes.Where(h, reg, t, out Vector3D centre);
            // Its shape, as a sector's but faint: L3-L5 a lens on the body's orbit, L1 / L2 a small zone.
            // Named as its zone ("Palatine L2"): double-click centres on it, as on a sector.
            string zoneName = $"{SystemHost.DisplayName(body.Name)} L{p}";
            var ghost = new Band { Name = zoneName, Home = h };
            _markerAt[zoneName] = W(toLocal(at));
            if (p >= 3)
            {
                double P = SectorHomes.Period(h, reg);
                SectorArea(W, q => { var p = SectorHomes.Where(h, reg, t + P * q, out var cq); return p - cq; }, v => toLocal(centre + v), ghost, SectorSpan, 0.035, taper: true, faint: true);
            }
            else OwnSector(W, SectorHomes.HillRadius(body) * 0.15 * LocalScale(toLocal), ghost, toLocal(at), faint: true);
            CoreMark(W, toLocal, h, reg, at);
            Vector3D w = W(toLocal(at));
            if (!MapPipeline.ToScreen(w, out var s) || !InOpenArea(s)) continue;
            // Named after its body: a planet's points (with the star) and its moon's (with the planet) share a screen.
            // L3-L5: set off the lens, on its parent's side, clear of its edge.
            Vector2 lab = s + new Vector2(0, 12f * u);
            if (p >= 3 && MapPipeline.ToScreen(W(toLocal(centre)), out var cs) && MapPipeline.ToScreen(W(toLocal(centre + (at - centre) * (1 - 0.035))), out var es))
            {
                Vector2 toC = cs - s;
                if (toC.LengthSquared() > 1f) lab = s + Vector2.Normalize(toC) * ((es - s).Length() + 14f * u);
            }
            MapPipeline.TextScreen(lab, $"{SystemHost.DisplayName(body.Name)} L{p}", col, 0.5f);
        }
    }

    /// <summary>A sector as shown: a body's own sector is named after the body ("Verdure", not "Verdure Sector").</summary>
    public static string Label(Band b) => b.Home?.Kind == SectorHomes.Kind.Body
        ? (b.Home.Future ? b.Name.Replace(" Sector", "") : SystemHost.DisplayName(b.Home.Host))
        : b.Name;

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
        string label = $"{bd.Number}  {Label(bd)}";
        _deferred.Add(() =>
        {
            long d0 = ModCost.Start();
            try { DeferredSector(); } finally { ModCost.Sec("deferred:" + h.Kind + (h.Kind == SectorHomes.Kind.Ring ? ":" + h.Host : "")).Stop(d0); }
        });
        void DeferredSector()
        {
            MapPipeline.PickName = b0.Name;
            switch (h.Kind)
            {
                case SectorHomes.Kind.Ring:
                    long r0 = ModCost.Start();
                    // The ring about its host (the point's offset from its centre, all the way round).
                    SectorArea(W0, q => { var p = SectorHomes.Where(h, reg, t + P * q, out var cq); return p - cq; },
                               v => toLocal(centre + v), b0, 0.5, (h.Outer - h.Inner) / (h.Outer + h.Inner));
                    ModCost.Sec("ring.area").Stop(r0); r0 = ModCost.Start();
                    // Its rocks only once the belt is wide on screen (zoomed in on it: a star's belt spans the
                    // whole system at any zoom, its width does not).
                    if (MapPipeline.ToScreen(W0(toLocal(centre + new Vector3D(h.Inner, 0, 0))), out var rc0) && MapPipeline.ToScreen(W0(toLocal(centre + new Vector3D(h.Outer, 0, 0))), out var rc1)
                        && (rc1 - rc0).Length() >= RoidsMinScreen * MapPipeline.ScreenSize.Y)
                        RoidMarks(W0, toLocal, reg, t, b0);
                    ModCost.Sec("ring.rocks").Stop(r0);
                    MapPipeline.PickName = b0.Name;
                    break;
                case SectorHomes.Kind.Lagrange when h.Point >= 3:
                {
                    // You are in it: only its dotted boundary (the preview draws it, as a planet's SOI), no fill.
                    var pf = FrameHost.PlayerFrame;
                    bool inIt = pf != null && EncounterFrames.SiteOf(pf.Id)?.Sector == b0.Name;
                    if (!inIt) SectorArea(W0, q => { var p = SectorHomes.Where(h, reg, t + P * q, out var cq); return p - cq; }, v => toLocal(centre + v), b0, SectorSpan, 0.035, taper: true);   // a thin lens
                    // Hovered or selected: the triangle (or line) it makes with Delfos and its planet.
                    if (b0.Selected || b0.Name == Hovered)
                    {
                        var body = reg.Find(h.Host);
                        float lu = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
                        var tc = HudPanel.Alpha(b0.Selected ? LineSel : StateColor(b0), 0.5f);
                        if (body?.Parent != null && MapPipeline.ToScreen(W0(mk), out var pm)
                            && MapPipeline.ToScreen(W0(toLocal(body.Parent.OriginInRoot(t).Position)), out var pp)
                            && MapPipeline.ToScreen(W0(toLocal(body.OriginInRoot(t).Position)), out var pb))
                        {
                            MapPipeline.ScreenDashed(pm, pp, tc, 1.2f * lu, lu);
                            MapPipeline.ScreenDashed(pm, pb, tc, 1.2f * lu, lu);
                            MapPipeline.ScreenDashed(pp, pb, HudPanel.Alpha(tc, 0.5f), 1f * lu, lu);
                        }
                    }
                    break;
                }
                case SectorHomes.Kind.Lagrange:   // L1 / L2: a small zone round the point
                {
                    var s = reg.Find(h.Host);
                    double rz = (s != null ? SectorHomes.HillRadius(s) : 1e6) * 0.15 * scale;
                    OwnSector(W0, rz, b0, mk);
                    break;
                }
            }
            if (h.Kind == SectorHomes.Kind.Lagrange && Maneuvers.Lag?.Site?.Sector != b0.Name)   // (the preview draws its own)
                CoreMark(W0, toLocal, h, reg, at);
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
        }
    }

    /// <summary>A belt's rocks show once its width is this much of the screen's height (zoomed in on it).</summary>
    public static float RoidsMinScreen = 0.08f;
    static readonly ColorSRGB RoidColor = new ColorSRGB(0.80f, 0.72f, 0.58f, 0.75f), RoidLive = new ColorSRGB(1.00f, 0.80f, 0.45f, 1f);

    /// <summary>
    /// A belt's asteroid frames (AsteroidFrames): a small ring each where it is now on its own orbit (a cluster's
    /// a little bigger; brighter while its rocks are out), named where there is room. Pointed at like a sector:
    /// hover, click to centre, right-click "Set as target" (closest approach, route).
    /// </summary>
    static void RoidMarks(Func<Vector3D, Vector3D> W, Func<Vector3D, Vector3D> toLocal, SystemRegistry reg, double t, Band belt)
    {
        var roids = AsteroidFrames.Of(belt.Name);
        if (roids.Count == 0) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        // A click on a rock selects it (again: unselects); its orbit is drawn while selected or targeted.
        if (MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open && Hovered != null && roids.Exists(x => x.label == Hovered))
            SelectedRock = SelectedRock == Hovered ? null : Hovered;
        foreach (var (label, home, cluster, live) in roids)
            if ((label == SelectedRock || label == Maneuvers.Target) && Contacts.TrackedRock(label)) RoidPath(W, toLocal, reg, t, home, label == Maneuvers.Target ? TargetText : LineSel);   // (its path once its orbit is known)
        foreach (var (label, home, cluster, live) in roids)
        {
            if (!Contacts.KnownRock(label)) continue;   // not seen yet
            Vector3D w = W(toLocal(SectorHomes.Where(home, reg, t)));
            if (!MapPipeline.ToScreen(w, out var s) || !InOpenArea(s)) continue;
            _markerAt[label] = w;
            bool hot = label == Hovered || label == Maneuvers.Target || label == SelectedRock;
            var c = label == Maneuvers.Target ? TargetText : hot ? LineSel : live ? RoidLive : Quiet(belt) ? HudPanel.Alpha(RoidColor, 0.4f) : RoidColor;
            MapPipeline.PickName = label;
            MapPipeline.ScreenRing(w, (cluster ? 4f : 2.8f) * u, c, (hot ? 2f : 1.4f) * u);
            if (hot || !Quiet(belt)) MapPipeline.TextScreen(s + new Vector2(0, 12f * u), Contacts.TrackedRock(label) ? label : "≈ " + label, c, hot ? 0.6f : 0.5f);
        }
        MapPipeline.PickName = null;
    }

    /// <summary>The rock selected on the map (clicked), or null.</summary>
    public static string SelectedRock;

    /// <summary>A rock's trajectory: one revolution of its own orbit about its host, from where it is now (a path: solid).</summary>
    static void RoidPath(Func<Vector3D, Vector3D> W, Func<Vector3D, Vector3D> toLocal, SystemRegistry reg, double t, SectorHomes.Home home, ColorSRGB col)
    {
        Vector3D p0 = SectorHomes.Where(home, reg, t, out Vector3D c0);
        var host = reg.Find(home.Host) ?? reg.Root;
        double r = (p0 - c0).Length();
        if (host == null || !(r > 0) || !(host.Mu > 0)) return;
        double T = 2 * Math.PI * Math.Sqrt(r * r * r / host.Mu);
        // (about the host where it is now: the path the rock draws round it, as the planets' orbits are drawn)
        Curve(q => { var p = SectorHomes.Where(home, reg, t + T * q, out var cq); return toLocal(c0 + (p - cq)); }, W, 0, 1, 128, col, 1.6f);
    }

    /// <summary>Lagrange zones on screen (name, outline), for double-click: this frame's and last frame's.</summary>
    private static List<(string name, Vector2[] poly)> _lens = new List<(string, Vector2[])>(), _lensPrev = new List<(string, Vector2[])>();
    static void LensHit(string name, IList<Vector2> poly)
    {
        if (string.IsNullOrEmpty(name) || poly == null || poly.Count < 3) return;
        var a = new Vector2[poly.Count]; poly.CopyTo(a, 0); _lens.Add((name, a));
    }
    static void SwapLens() { var s = _lensPrev; _lensPrev = _lens; _lens = s; _lens.Clear(); }

    /// <summary>
    /// A double-click on a Lagrange zone: centre on it and frame it (about half the view), so its inside
    /// (the calm core) reads. False when the click was on no zone.
    /// </summary>
    static bool FocusLens(Vector2 m)
    {
        foreach (var l in _lensPrev)
        {
            if (!MapPipeline.InPolygon(l.poly, m)) continue;
            CentreOn(l.name);
            Vector2 lo = l.poly[0], hi = l.poly[0];
            foreach (var p in l.poly) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            float ext = Math.Max(hi.X - lo.X, hi.Y - lo.Y);
            if (ext > 1f && MapCamera.Distance > 0) MapCamera.ZoomTo(MapCamera.Distance * ext / (0.5 * MapPipeline.ScreenSize.Y));
            return true;
        }
        return false;
    }

    /// <summary>
    /// A Lagrange zone's calm core (no pull at all), once zoomed in enough to see it: a dotted circle in
    /// its orbit plane round the point, named when big.
    /// </summary>
    static void CoreMark(Func<Vector3D, Vector3D> W, Func<Vector3D, Vector3D> toLocal, SectorHomes.Home h, SystemRegistry reg, Vector3D at)
    {
        double core = SectorHomes.LagrangeCore(h, reg);
        if (!(core > 0)) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        Vector3D c = toLocal(at);
        double rl = core * LocalScale(toLocal);
        if (!MapPipeline.ToScreen(W(c), out var cs)) return;
        var pts = new List<Vector2>(64);
        float rmax = 0;
        for (int i = 0; i < 64; i++)
        {
            double a = 2 * Math.PI * i / 64;
            if (!MapPipeline.ToScreen(W(c + new Vector3D(Math.Cos(a) * rl, 0, Math.Sin(a) * rl)), out var sp)) return;
            pts.Add(sp); rmax = Math.Max(rmax, (sp - cs).Length());
        }
        if (rmax < 6f * u) return;   // too small to read yet
        var col = new ColorSRGB(0.55f, 0.85f, 1f, 0.55f);
        MapStyle.Boundary(pts, true, col, MapStyle.Thin(u), u);
        if (rmax > 40f * u) MapPipeline.TextScreen(cs + new Vector2(0, rmax * 0.6f + 10f * u), "Calm core", col, 0.5f);
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
    /// <summary>Map units per metre of a level's toLocal (it is linear).</summary>
    static double LocalScale(Func<Vector3D, Vector3D> toLocal) => (toLocal(new Vector3D(1e6, 0, 0)) - toLocal(Vector3D.Zero)).Length() / 1e6;

    static readonly List<Vector2> _saOuter = new List<Vector2>(97), _saInner = new List<Vector2>(97), _saPoly = new List<Vector2>(194);
    static void SectorArea(Func<Vector3D, Vector3D> W, Func<double, Vector3D> relAt, Func<Vector3D, Vector3D> toLocal, Band b, double span = SectorSpan, double kWidth = 0, bool taper = false, bool faint = false)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        double k = kWidth > 0 ? kWidth : Math.Clamp(b.Home.Size * 0.5 * SystemHost.SectorOrbitScale / Math.Max(1, b.Home.A), 0.03, 0.065);
        bool ring = span >= 0.5;
        if (ring && !(kWidth > 0)) k = Math.Min(k, 0.03);   // a belt: a thin ring
        int n = ring ? 96 : 24;
        // (three lists reused: everything they are handed draws at once or copies - LensHit, OccludeArea)
        var outer = _saOuter; var inner = _saInner; outer.Clear(); inner.Clear();
        for (int i = 0; i <= n; i++)
        {
            Vector3D r = ring ? relAt((double)i / n) : relAt(-span + 2 * span * i / n);
            // Tapered (a Trojan swarm's tadpole): full width in the middle, to a point at the ends.
            double ki = taper ? k * Math.Max(0.08, Math.Sin(Math.PI * i / n)) : k;
            if (!MapPipeline.ToScreen(W(toLocal(r * (1 + ki))), out var so) || !MapPipeline.ToScreen(W(toLocal(r * (1 - ki))), out var si)) return;
            outer.Add(so); inner.Add(si);
        }
        var poly = _saPoly; poly.Clear(); poly.AddRange(outer);
        for (int i = inner.Count - 1; i >= 0; i--) poly.Add(inner[i]);
        if (!ring && b.Home?.Kind == SectorHomes.Kind.Lagrange) LensHit(b.Name, poly);
        var c = b.Selected ? LineSel : StateColor(b);
        // Under the ghost of a sector you will enter (drawn where it will be then): today's sector steps back.
        if (!faint && Maneuvers.UnderGhost(0.5f * (outer[n / 2] + inner[n / 2]), b.Name)) faint = true;
        float fa = faint ? 0.06f : b.Selected ? 0.30f : b.Name == Hovered ? 0.34f : Quiet(b) ? 0.08f : 0.18f;
        // Just a dotted edge, never filled (selected or hovered: a brighter edge); orbit lines stop at it.
        if (!ring) MapPipeline.OccludeArea(poly);   // (a belt is crossed by orbits by nature: they stay)
        var edge = HudPanel.Alpha(c, faint ? 0.3f : b.Selected ? 0.95f : b.Name == Hovered ? 0.9f : Quiet(b) ? 0.3f : 0.6f);
        float ew = b.Selected ? MapStyle.Medium(u) : MapStyle.Thin(u);
        if (ring)
        {
            MapStyle.Boundary(outer, true, edge, ew, u);
            MapStyle.Boundary(inner, true, edge, ew, u);
        }
        else MapStyle.Boundary(poly, true, edge, ew, u);   // round the whole lens
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
            bool dim = Quiet(b) || Maneuvers.UnderGhost(ms, b.Name);
            if (MapPipeline.ScreenIcon(icon, ms, (dim ? 7f : b.Selected ? 11f : 9f) * mu, dim ? HudPanel.Alpha(c, 0.35f) : c)) return;
        }
        if (Quiet(b)) { MapPipeline.ScreenRing(world, 5f, HudPanel.Alpha(c, 0.5f), 1.4f); MapPipeline.ScreenRing(world, 2f, HudPanel.Alpha(c, 0.5f), 2f); return; }
        MapPipeline.ScreenRing(world, 8f, c, 2f);
        MapPipeline.ScreenRing(world, 3.5f, c, 3.5f);
    }
}
