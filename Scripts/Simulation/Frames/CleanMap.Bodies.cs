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
        StarWorld = W(Vector3D.Zero);   // the game's star model goes here (GameMap.PlaceStar)
        if (!GameMap.StarPlaced && MapPipeline.ToScreen(W(Vector3D.Zero), out var sunS) && InOpenArea(sunS))
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

        // The planets: orbit line, the globe, and the planet's own sector as a circular section around it.
        foreach (var p in root.Children)
        {
            if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
            var el = OrbitalMath.ToElements(p.StateInParentAt(t), root.Mu, t);
            // Faint while a planet's own system has the view (its sweep across the screen is not the point there).
            if (el.IsElliptic) Curve(nu => S(OrbitSampler.PositionAtTrueAnomaly(el, nu)), W, 0, 2 * Math.PI, 96, _planetLevel ? HudPanel.Alpha(Line, 0.1f) : Line, 1.2f);
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
