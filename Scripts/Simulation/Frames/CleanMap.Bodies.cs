using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The map's bodies: the star, the planets and their orbits, each planet's and moon's own system.</summary>
public static partial class CleanMap
{

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
                if (mel.IsElliptic) Curve(nu => Lv(OrbitSampler.PositionAtTrueAnomaly(mel, nu)), W, 0, 2 * Math.PI, 64, HudPanel.Alpha(Dim, 0.35f), 1.0f, depthCue: true);
            }
            if (!MapPipeline.ToScreen(W(ml), out var mls) || !InOpenArea(mls)) continue;   // off view: not over the game's panels
            MapGlobes.Use(moon.Name, W(ml), (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys * _wscale, globes);
            BodyDot(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, moon.Name == playerPlanet ? You : Dim);
            Hit(moon, W(ml), W(ml + new Vector3D((reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 0, 0)), 5f);
            BodyLabel(W, ml, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * scaleSys, 5f, moon.Name, Dim, 0.6f);
        }

        // Its sectors (its own space, its rings, its L1 / L2): only when its system is big enough on screen.
        if (detail)
        {
            Vector3D pRoot = planet.OriginInRoot(t).Position;
            // Its L3-L5 sectors lie far out on its orbit: when off the view, an arrow at the edge names them.
            if (planet.Name == _anchor?.Name)
                foreach (var bd in bands)
                    if (bd.Home.Kind == SectorHomes.Kind.Lagrange && bd.Home.Host == planet.Name && bd.Home.Point >= 3)
                    {
                        Vector3D lp = Lv(SectorHomes.Where(bd.Home, reg, t) - pRoot);
                        if (MapPipeline.ToScreen(W(lp), out var ls) && InOpenArea(ls)) continue;   // on the view: drawn as itself
                        Pin(lp, double.PositiveInfinity, W, $"{Label(bd)}  ·  L{bd.Home.Point}", bd.Selected ? LineSel : StateColor(bd));
                    }
            // Its L1 / L2 (with its primary), and each moon's L1-L5 (with it), sector or not.
            LagrangeMarks(bands, reg, t, W, r => Lv(r - pRoot), planet, 1, 2);
            foreach (var moon in planet.Children)
                if (SystemHost.BeaconOf.ContainsKey(moon.Name)) LagrangeMarks(bands, reg, t, W, r => Lv(r - pRoot), moon, 1, 5);
            foreach (var bd in bands)
            {
                var h = bd.Home;
                bool mineHere = h.Host == planet.Name && (h.Kind != SectorHomes.Kind.Lagrange || h.Point <= 2);
                if (mineHere) DrawSector(bd, reg, t, W, r => Lv(r - pRoot), Sigma);
            }
        }

        MapPipeline.PickName = null;
        // You.
        if (playerPlanet == planet.Name)
        {
            if (!Planning && !Maneuvers.LagActive && !OrbitHud.Walking && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))   // planning (or a Lagrange sector ahead): the planner draws it
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
            if (!Planning && !OrbitHud.Walking && playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis)
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
        // Delfos: our own globe of the star's map model (as the planets), at its true size, always (no
        // scaling up or down with zoom). The game's star object is parked out of view.
        StarWorld = null;
        {
            float su = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            double rWorld = SystemHost.StarRadius * SolarRadius / outer * _wscale;
            MapGlobes.Use(root.Name, W(Vector3D.Zero), rWorld, globes);
            if (!globes.Contains(root.Name) && MapPipeline.ToScreen(W(Vector3D.Zero), out var sunS) && InOpenArea(sunS))
            {
                // No model: a red disc of the same true size (a dot at least, so it is never lost).
                float rpx = MapPipeline.ToScreen(W(new Vector3D(SystemHost.StarRadius * SolarRadius / outer, 0, 0)), out var se) ? (se - sunS).Length() : 0f;
                MapPipeline.ScreenDisc(sunS, Math.Max(3f * su, rpx), new ColorSRGB(1f, 0.46f, 0.22f, 1f));
            }
        }
        Hit(root, W(Vector3D.Zero), W(new Vector3D(SystemHost.StarRadius * SolarRadius / outer, 0, 0)), 36f);
        BodyLabel(W, Vector3D.Zero, SystemHost.StarRadius * SolarRadius / outer, 36f, StarName, Text, 0.85f);

        // Every planet's L3 / L4 / L5 (with Delfos), sector or not.
        foreach (var p in root.Children)
            if (SystemHost.BeaconOf.ContainsKey(p.Name)) LagrangeMarks(bands, reg, t, W, S, p, 3, 5);
        // The star's sectors: its own space, its belts, the planets' L3 / L4 / L5, bodies still to come.
        foreach (var bd in bands)
        {
            var h = bd.Home;
            bool star = h.Host == root.Name || (h.Kind == SectorHomes.Kind.Lagrange && h.Point >= 3);
            if (star) DrawSector(bd, reg, t, W, S, SolarRadius / outer);
        }

        // The planets: orbit line, the globe, and the planet's own sector as a circular section around it.
        foreach (var p in root.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
            var el = OrbitalMath.ToElements(p.StateInParentAt(t), root.Mu, t);
            // Not inside the planet's own space (the dotted circle round it): its system sits clear of its orbit line.
            Func<Vector2, bool> skip = null;
            {
                var own = bands.Find(bb => bb.Home.Kind == SectorHomes.Kind.Body && !bb.Home.Future && bb.Home.Host == p.Name);
                double rOwn = own != null ? own.Home.Outer : p.SoiRadius;
                Vector3D pc = S(p.StateInParentAt(t).Position);
                if (IsFinite(rOwn) && rOwn > 0 && MapPipeline.ToScreen(W(pc), out var psc) && MapPipeline.ToScreen(W(pc + new Vector3D(Rs(rOwn), 0, 0)), out var pse))
                {
                    float rr = (pse - psc).Length();
                    skip = sp => (sp - psc).LengthSquared() < rr * rr;
                }
            }
            // Faint while a planet's own system has the view (its sweep across the screen is not the point there).
            if (el.IsElliptic) Curve(nu => S(OrbitSampler.PositionAtTrueAnomaly(el, nu)), W, 0, 2 * Math.PI, 96, _planetLevel ? HudPanel.Alpha(Line, 0.1f) : Line, 1.0f, skip: skip);
            Vector3D hp = p.StateInParentAt(t).Position;
            Vector3D c = S(hp);
            MapGlobes.Use(p.Name, W(c), (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer * _wscale, globes);   // true size
            // Its ring round the globe, flat in the map's plane (the rings lie on the equators, in the ecliptic).
            if (globes.Contains(p.Name))
            {
                if (VoxelBerthRegistry.TryGetCell(p.Name, reg, out Vector3D pcell))
                    PlanetRings.Map(pcell, reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4, W(c),
                        (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer * _wscale, W(c + new Vector3D(0, 1, 0)) - W(c));
            }
            BodyDot(W, c, (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, p.Name == playerPlanet ? You : Text);
            Hit(p, W(c), W(c + new Vector3D((reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, 0, 0)), 8f);
            BodyLabel(W, c, (reg.FindDefinition(p.Name)?.RadiusMeters ?? 6e4) * SolarRadius / outer, 8f, p.Name, p.Name == playerPlanet ? You : Text, 0.9f);
        }
        // You, orbiting the star: your orbit and where you are (planning: the planner draws it).
        if (playerPlanet == root.Name && playerOrbit.HasValue && !Planning)
        {
            var path = OrbitSampler.SamplePath(playerOrbit.Value, 256);
            var pts = path.Points;
            if (pts != null && !Maneuvers.LagActive && !OrbitHud.Walking)
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
    /// <summary>
    /// A planet's own sector (its near space): not an orbit, so a faint dashed boundary (gold when
    /// selected), with the sector's name centred above the whole area.
    /// </summary>
    static void OwnSector(Func<Vector3D, Vector3D> W, double r, Band b, Vector3D centre = default, bool faint = false)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        var col = faint ? HudPanel.Alpha(Dim, 0.3f) : b.Selected ? HudPanel.Alpha(LineSel, 0.85f) : b.Name == Hovered ? HudPanel.Alpha(StateColor(b), 0.8f) : HudPanel.Alpha(StateColor(b), 0.45f);
        const int n = 64;
        Vector2 top = default; bool haveTop = false;
        var ring = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector3D p = W(centre + new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r));
            if (!MapPipeline.ToScreen(p, out var s)) { ring.Clear(); break; }
            ring.Add(s);
            if (!haveTop || s.Y < top.Y) { top = s; haveTop = true; }
        }
        if (ring.Count == n)
        {
            MapStyle.Boundary(ring, true, col, b.Selected ? MapStyle.Medium(u) : MapStyle.Thin(u), u);
            // Its area stops orbit lines (a body's own space: its moons' orbits run through it by nature).
            if (b.Home?.Kind != SectorHomes.Kind.Body) MapPipeline.OccludeArea(ring);
            if (b.Home?.Kind == SectorHomes.Kind.Lagrange) LensHit(b.Name, ring);
        }
        // Only inside the map's open area (not over the game's tab bar or the title).
        var scr = MapPipeline.ScreenSize;
        if (!haveTop || top.Y < scr.Y * 0.2f || top.X < scr.X * 0.26f || top.X > scr.X * 0.77f) return;
        if (b.Home?.Kind == SectorHomes.Kind.Body || faint) return;   // the body's own label names it (a faint zone: its mark does)
        string nm = Label(b);
        var sz = MapPipeline.MeasureText(nm, 0.8f);
        MapPipeline.TextScreen(new Vector2(top.X, top.Y - sz.Y * 0.5f - 4f * u), nm, b.Selected ? LineSel : Dim, 0.8f);   // clear of the title lines
    }

    private static double si_own_outer(SectorHomes.Home h) => h.A + h.Size * 0.5 * SystemHost.SectorOrbitScale;

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

    private static void Circle(Func<Vector3D, Vector3D> W, double r, ColorSRGB col, float px)
        => Curve(a => new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r), W, 0, 2 * Math.PI, 96, col, px);
}
