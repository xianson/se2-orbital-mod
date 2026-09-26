using Keen.Game2.Client.WorldObjects.ColonizationMap;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// ONE map: the colonization chart and the orbital (KSP) map as bands of a single zoom.
/// The game's own sector mesh and globes are hidden; we draw the sectors ourselves (their
/// polygons from SectorComponent.ToPolygon, coloured by colonization state), so they can move.
///
/// Zoom u = camera distance / the game's original max distance (the max is raised so you can
/// keep scrolling out):
///  - u &lt;= 1   CHART: sectors exactly where charted (as the game draws them), each deep sector's
///              live orbital position shown as a marker with an arc back to its cell.
///  - 1..2    the chart comes alive: each sector polygon shrinks into a token and glides to its
///              live orbital position around its planet (the planet systems).
///  - 2.5..3.5 the planet systems glide to their true places around the sun and collapse into
///              badges; Trojan sectors fly to their L4/L5 points; the sun and planet orbits appear.
/// At t = 0 each deep sector's orbital position IS its charted position, so the bands agree.
/// Selection stays the game's (its click collider is at the charted positions).
/// </summary>
public static class UnifiedMap
{
    public static bool Enabled = true;
    /// <summary>Sectors drawn as band sections of their orbits (else: discs).</summary>
    public static bool BandSections = true;
    public static double ZoomOutFactor = 6.0;
    public static string Status = "-";

    private static float _baseMax = -1;
    private static bool _gameHidden, _labelsHidden;

    // Colours: the colonization states, KSP conventions for orbits.
    private static readonly ColorSRGB Locked = new ColorSRGB(0.42f, 0.45f, 0.5f);
    private static readonly ColorSRGB Unlocked = new ColorSRGB(0.30f, 0.62f, 0.95f);
    private static readonly ColorSRGB Colonized = new ColorSRGB(0.35f, 0.9f, 0.5f);
    private static readonly ColorSRGB Selected = new ColorSRGB(1f, 0.85f, 0.2f);
    private static readonly ColorSRGB You = new ColorSRGB(1f, 0.85f, 0.1f);
    private static readonly ColorSRGB Ring = new ColorSRGB(0.4f, 0.5f, 0.65f);
    private static readonly ColorSRGB Sun = new ColorSRGB(1f, 0.85f, 0.4f);
    private static readonly ColorSRGB Text = new ColorSRGB(0.92f, 0.96f, 1f);

    private sealed class SectorInfo
    {
        public SectorComponent Sector;
        public string Host;            // nearest planet
        public bool OwnsPlanet, Trojan;
        public int TrojanSide, TrojanIndex;
        public double R, Theta0, Omega;
        public SectorHomes.Home Home;
    }

    /// <summary>Draw everything for this frame. Returns false when the unified map is off.</summary>
    public static bool Draw(Keen.VRage.Core.Game.Systems.Session session, MeshBuilder b, ColonizationMapSessionComponent map,
                            SectorsSessionComponent sectors, Vector3D cam, Vector3D mapPos, Quaternion orient, double t)
    {
        if (!Enabled) { RestoreGame(map); return false; }
        if (_baseMax < 0) _baseMax = map.MaxDistance;
        if (map.MaxDistance < _baseMax * (float)ZoomOutFactor) map.MaxDistance = _baseMax * (float)ZoomOutFactor;
        HideGame(map);
        _labelsHidden = true;

        var reg = SystemHost.Registry;
        Vector3D origin = sectors.MapWorldPosition;
        float scale = sectors.MapWorldScale;
        double dist = (cam - mapPos).Length();
        double u = dist / Math.Max(1e-6, _baseMax);
        double w12 = Smooth(1.0, 2.0, u), w23 = Smooth(2.5, 3.5, u);
        Vector3D C(Vector3D world) => ColonizationMapSessionComponent.WorldToMapPosition(world, mapPos, orient, origin, scale);
        Vector3D Rot(Vector3D v) => (QuaternionD)orient * v;
        double W(double k = 1.0) => dist * 0.0022 * k;   // line width: constant on screen

        IColonizationProgress progress = null;
        try { progress = session.Get<IColonizationProgress>(); } catch { }
        StringIdSel(map, out var selected);

        // ── planets: families, and their place around the sun ──
        var planets = new List<GravityBody>();
        foreach (var body in reg.Root.Children) if (SystemHost.BeaconOf.ContainsKey(body.Name)) planets.Add(body);
        var infos = Classify(sectors, reg, planets);
        if (CleanMap.Enabled)
        {
            var bands = new List<CleanMap.Band>();
            foreach (var si in infos)
            {
                var st = SectorColonizationState.Locked;
                try { if (progress != null) st = progress.GetGlobalProgressFor(si.Sector).State; } catch { }
                bands.Add(new CleanMap.Band
                {
                    Name = si.Sector.Name, Host = si.Host, Home = si.Home,
                    Color = st == SectorColonizationState.Colonized ? Colonized : st == SectorColonizationState.Unlocked ? Unlocked : Locked,
                    Selected = selected.HasValue && selected.Value == Keen.VRage.Library.Utils.StringId.Get(si.Sector.Name),
                });
            }
            string youPlanet = null; Vector3D youRel = default; KeplerianElements? youOrbit = null;
            if (SunDriver.TryObserverCelestial(FrameHost.PlayerPosition, t, out Vector3D ycel))
            {
                double bestD = double.MaxValue;
                foreach (var p in planets) { double d = (ycel - p.OriginInRoot(t).Position).Length(); if (d < bestD) { bestD = d; youPlanet = p.Name; } }
                if (youPlanet != null)
                {
                    youRel = ycel - reg.Find(youPlanet).OriginInRoot(t).Position;
                    var f = FrameHost.PlayerFrame;
                    if (f != null && f.ParentBodyName == youPlanet) youOrbit = f.Elements;
                    else if (f == null && FrameHost.TryGetLocalOrbit(youPlanet, t, out var le)) youOrbit = le;
                }
            }
            var usedGlobes = new HashSet<string>();
            CleanMap.Draw(b, bands, reg, mapPos, orient, dist, u, t, youPlanet, youRel, youOrbit, usedGlobes);
            MapGlobes.End(usedGlobes);
            Status = $"clean u={u:F2} bands={bands.Count} focus={CleanMap.Focus}";
            return true;
        }
        double maxAp = 1;
        foreach (var p in planets) maxAp = Math.Max(maxAp, p.StateInParentAt(t).Position.Length() * 1.05);
        double k3 = 0.42 * dist / maxAp;
        Vector3D sunPos = mapPos;
        Vector3D Solar(Vector3D helio) => sunPos + Rot(new Vector3D(helio.X, helio.Z, helio.Y) * k3);

        var famCenter = new Dictionary<string, Vector3D>();
        var famScale = new Dictionary<string, double>();
        foreach (var p in planets)
        {
            Vector3D chartGlobe = C(SystemHost.BeaconOf[p.Name].Center);
            double rMax = 1;
            foreach (var si in infos)
                if (si.Host == p.Name && si.Home != null && si.Home.Kind == SectorHomes.Kind.Ellipse) rMax = Math.Max(rMax, si.Home.A * (1 + si.Home.E));
                else if (si.Host == p.Name && !si.Trojan && !si.OwnsPlanet && !BandSections) rMax = Math.Max(rMax, si.R);
            double sChart = 1.0 / scale;
            double sFamily = Math.Max(sChart, 0.16 * dist / rMax);
            double sBadge = 0.03 * dist / rMax;
            famCenter[p.Name] = Lerp(chartGlobe, Solar(p.StateInParentAt(t).Position), w23);
            famScale[p.Name] = LerpD(LerpD(sChart, sFamily, w12), sBadge, w23);
            MapView.GlobePos[p.Name] = famCenter[p.Name];
        }
        Vector3D Fam(string planet, Vector3D rel) => famCenter[planet] + Rot(new Vector3D(rel.X, 0, rel.Z) * famScale[planet]);

        // ── the sun and the planets' orbits (fade in with the solar band) ──
        if (w23 > 0.02)
        {
            b.AddSphere(new WorldTransform(sunPos, Quaternion.Identity), dist * 0.022 * w23, Sun, Sun, true);
            b.AddText(sunPos + Rot(new Vector3D(0, dist * 0.03, 0)), "Sun", new ColorSRGB(Sun, (float)w23), 0.6f);
            foreach (var p in planets)
            {
                var el = OrbitalMath.ToElements(p.StateInParentAt(t), reg.Root.Mu, t);
                if (!IsFinite(el.SemiMajorAxis)) continue;
                Polyline(b, OrbitSampler.SamplePath(el, 180), Solar, new ColorSRGB(Ring, (float)w23), W(1.4));
            }
        }

        // ── Trojan groups: torus arcs on the host planet's orbit, 60 degrees ahead (L4) and behind (L5) ──
        if (w23 > 0.05)
            foreach (var p in planets)
            {
                bool any = false; foreach (var si in infos) if (si.Host == p.Name && si.Trojan) any = true;
                if (!any) continue;
                Vector3D hp = p.StateInParentAt(t).Position;
                double a = hp.Length();
                foreach (int side in new[] { 1, -1 })
                    TorusArc(b, hp, a, side * Math.PI / 3, 0.16, a * 0.05, Solar, new ColorSRGB(Unlocked, (float)(0.22 * w23)), new ColorSRGB(Unlocked, (float)w23), W(0.7));
            }

        // ── sectors: discs that become tokens on their orbits ──
        foreach (var si in infos)
        {
            var sc = si.Sector;
            var state = SectorColonizationState.Locked;
            try { if (progress != null) state = progress.GetGlobalProgressFor(sc).State; } catch { }
            ColorSRGB col = state == SectorColonizationState.Colonized ? Colonized : state == SectorColonizationState.Unlocked ? Unlocked : Locked;
            bool sel = selected.HasValue && selected.Value == Keen.VRage.Library.Utils.StringId.Get(sc.Name);

            if (BandSections)
            {
                DrawHome(b, si, reg, col, sel, t, dist, w23, (planet, rel) => Fam(planet, new Vector3D(rel.X, 0, rel.Y)), Solar, orient);
                continue;
            }
            Vector3D cc = C(sc.Area.Center);
            Vector3D live;   // where the sector's region is now, at this zoom
            if (si.OwnsPlanet) live = famCenter[si.Host];
            else if (si.Trojan)
            {
                var host = reg.Find(si.Host);
                Vector3D hp = host.StateInParentAt(t).Position;
                double ang = si.TrojanSide * (Math.PI / 3 + si.TrojanIndex * 0.035);
                Vector3D lp = PlanetBerths.RotateAboutAxis(hp, Vector3D.UnitZ, ang);
                live = Lerp(cc, Solar(lp), w23);
            }
            else
            {
                double th = si.Theta0 + si.Omega * t;
                live = Fam(si.Host, new Vector3D(Math.Cos(th) * si.R, 0, Math.Sin(th) * si.R));
            }

            // A sector is a place on an orbit: a disc with a radius (half its charted size),
            // at its charted centre on the chart, shrinking into a token at its live position.
            double chartR = sc.Area.Size / scale * 0.5;
            double tokenR = dist * (si.OwnsPlanet ? 0.02 : 0.011);
            double discR = LerpD(chartR, tokenR, w12) * (si.Trojan ? 1 : 1 - w23 * 0.6);
            Vector3D centre = Lerp(cc, live, si.Trojan ? w23 : w12);
            if (!BandSections)
            {
                if (!(si.Trojan && w23 > 0.6))
                    Disc(b, centre, orient, discR, new ColorSRGB(col, sel ? 0.42f : 0.24f), sel ? Selected : col, W(sel ? 1.2 : 0.7));
            }
            else if (!si.Trojan)
            {
                // BAND SECTIONS: a sector is a stretch of its orbit around its planet. Its angular
                // span and radial thickness come from its charted size; it sits where the sector is
                // now and turns with its orbit (inner ones faster: the Kepler shear is visible).
                Vector3D P(double ang, double rr) => Fam(si.Host, new Vector3D(Math.Cos(ang) * rr, 0, Math.Sin(ang) * rr));
                if (si.OwnsPlanet)
                {
                    // The planet's own sector: its near space, a full band around it.
                    double rOut = si.R + sc.Area.Size * 0.15, rIn = Math.Max(si.R * 0.35, 1);
                    BandArc(b, P, 0.5 * (rIn + rOut), 0, Math.PI, 0.5 * (rOut - rIn), new ColorSRGB(col, (sel ? 0.26f : 0.14f) * (float)(1 - w23)), new ColorSRGB(sel ? Selected : col, (float)(1 - w23)), W(sel ? 1.0 : 0.6), 96);
                }
                else
                {
                    double th = si.Theta0 + si.Omega * t;
                    double half = Math.Max(0.08, Math.Min(0.6, 0.5 * sc.Area.Size / si.R));
                    double hw = Math.Min(sc.Area.Size * 0.18, si.R * 0.09);
                    float a = (float)(1 - w23 * 0.7);
                    BandArc(b, P, si.R, th, half, hw, new ColorSRGB(col, (sel ? 0.5f : 0.34f) * a), new ColorSRGB(sel ? Selected : col, a), W(sel ? 1.2 : 0.7), 24);
                }
            }

            // Band 1: the live position as a marker with an arc back to the charted cell.
            if (!BandSections && !si.OwnsPlanet && !si.Trojan && w12 < 0.5)
            {
                double th0 = si.Theta0, th = si.Theta0 + si.Omega * t;
                double sweep = Math.Min(th - th0, 2 * Math.PI);
                int n = Math.Max(2, (int)(sweep / (2 * Math.PI) * 96));
                Vector3D prev = Fam(si.Host, new Vector3D(Math.Cos(th0) * si.R, 0, Math.Sin(th0) * si.R));
                var arc = new ColorSRGB(Ring, (float)(1 - 2 * w12));
                for (int i = 1; i <= n; i++)
                {
                    double a = th0 + sweep * i / n;
                    Vector3D p = Fam(si.Host, new Vector3D(Math.Cos(a) * si.R, 0, Math.Sin(a) * si.R));
                    b.AddLine(prev, p, arc, (float)W(0.6), true);
                    prev = p;
                }
                b.AddSphere(new WorldTransform(live, Quaternion.Identity), dist * 0.006, col, col, true);
            }
            // Band 2: the sector's orbit ring.
            if (!si.OwnsPlanet && !si.Trojan && (BandSections ? w23 < 0.9 : w12 > 0.2 && w23 < 0.8))
            {
                var ringCol = new ColorSRGB(sel ? Selected : Ring, (float)((BandSections ? 0.35 : Math.Min(1, (w12 - 0.2) * 2)) * (1 - w23)));
                Vector3D q = Fam(si.Host, new Vector3D(si.R, 0, 0));
                for (int i = 1; i <= 96; i++)
                {
                    double a = 2 * Math.PI * i / 96;
                    Vector3D p = Fam(si.Host, new Vector3D(Math.Cos(a) * si.R, 0, Math.Sin(a) * si.R));
                    b.AddLine(q, p, ringCol, (float)W(sel ? 0.9 : 0.5), true);
                    q = p;
                }
            }
            // Labels: sector names until the solar band; the selected one with its orbit.
            if (BandSections && !si.Trojan && !si.OwnsPlanet)
                centre = Fam(si.Host, new Vector3D(Math.Cos(si.Theta0 + si.Omega * t) * si.R, 0, Math.Sin(si.Theta0 + si.Omega * t) * si.R));
            if (BandSections && si.Trojan && w23 < 0.3) continue;   // far away along the planet's orbit: shown in the solar band
            if (w23 < 0.6 || si.Trojan)
            {
                string label = sc.Name;
                if (sel && !si.OwnsPlanet && !si.Trojan)
                    label += $"   r {si.R / 1e6:F1} Mm  T {2 * Math.PI / si.Omega / 3600:F0} h";
                if (si.Trojan && w23 > 0.3) label += si.TrojanSide > 0 ? " (L4)" : " (L5)";
                b.AddText(centre + Rot(new Vector3D(0, dist * 0.012, 0)), label, sel ? Selected : Text, sel ? 0.55f : 0.42f);
            }
        }

        // ── planet globes, moons, badges ──
        var used = new HashSet<string>();
        foreach (var p in planets)
        {
            var def = reg.FindDefinition(p.Name);
            double chartR = (def?.RadiusMeters ?? 6e4) / scale * PlanetScaleOf(map);
            double globeR = LerpD(chartR, dist * 0.02, Math.Max(w12, w23));
            MapGlobes.Use(p.Name, famCenter[p.Name], globeR, used);
            int n = 0; foreach (var si in infos) if (si.Host == p.Name && !si.OwnsPlanet) n++;
            string badge = w23 > 0.5 ? $"{p.Name}  ({n} sector{(n == 1 ? "" : "s")})" : p.Name;
            b.AddText(famCenter[p.Name] - Rot(new Vector3D(0, 0, globeR * 1.6)), badge, Text, 0.55f);
            foreach (var moon in p.Children)
            {
                if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
                Vector3D mp = Fam(p.Name, moon.StateInParentAt(t).Position);
                double mr = Math.Max(globeR * 0.3, (reg.FindDefinition(moon.Name)?.RadiusMeters ?? 2e4) * famScale[p.Name]);
                MapGlobes.Use(moon.Name, Lerp(mp, famCenter[p.Name] + Vector3D.Normalize(mp - famCenter[p.Name] + new Vector3D(1e-9, 0, 0)) * globeR * 1.8, w23), mr, used);
            }
        }
        MapGlobes.End(used);

        // ── you ──
        if (SunDriver.TryObserverCelestial(FrameHost.PlayerPosition, t, out Vector3D cel))
        {
            GravityBody near = null; double best = double.MaxValue;
            foreach (var p in planets) { double d = (cel - p.OriginInRoot(t).Position).Length(); if (d < best) { best = d; near = p; } }
            if (near != null)
            {
                Vector3D rel = cel - near.OriginInRoot(t).Position;
                Vector3D youPos = Fam(near.Name, rel);
                b.AddSphere(new WorldTransform(youPos, Quaternion.Identity), dist * 0.005, You, You, false);
                b.AddText(youPos + Rot(new Vector3D(0, dist * 0.01, 0)), "you", You, 0.5f);
                // Your orbit about that planet (frame rails, or the local orbit).
                KeplerianElements el = default; bool have = false;
                var f = FrameHost.PlayerFrame;
                if (f != null && f.ParentBodyName == near.Name) { el = f.Elements; have = true; }
                else if (f == null && FrameHost.TryGetLocalOrbit(near.Name, t, out el)) have = true;
                if (have && IsFinite(el.SemiMajorAxis))
                    Polyline(b, OrbitSampler.SamplePath(el, 160, near.SoiRadius), r => Fam(near.Name, r), new ColorSRGB(You, (float)(1 - w23)), W(0.9));
            }
        }

        Status = $"unified u={u:F2} w12={w12:F2} w23={w23:F2} sectors={infos.Count}";
        return true;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static List<SectorInfo> Classify(SectorsSessionComponent sectors, SystemRegistry reg, List<GravityBody> planets)
    {
        var list = new List<SectorInfo>();
        var trojanCount = new Dictionary<string, int>();
        foreach (var sc in sectors.Sectors)
        {
            var si = new SectorInfo { Sector = sc };
            Vector3D c = sc.Area.Center;
            double best = double.MaxValue; Vector3D hc = default;
            foreach (var p in planets)
            {
                Vector3D pc = SystemHost.BeaconOf[p.Name].Center;
                if (sc.Contains(pc)) si.OwnsPlanet = true;
                double d = (new Vector3D(c.X, pc.Y, c.Z) - pc).Length();
                if (d < best) { best = d; si.Host = p.Name; hc = pc; }
            }
            if (si.Host == null) continue;
            si.R = best;
            si.Theta0 = Math.Atan2(c.Z - hc.Z, c.X - hc.X);
            double mu = reg.Find(si.Host).Mu;
            si.Omega = Math.Sqrt(mu / (best * best * best));
            si.Home = SectorHomes.Make(sc.Name, si.Host, best, si.Theta0, sc.Area.Size, 0);
            if (si.OwnsPlanet) si.Home.Kind = SectorHomes.Kind.OwnPlanet;
            if (!si.OwnsPlanet && best > MapView.TrojanThreshold)
            {
                si.Trojan = true;
                trojanCount.TryGetValue(si.Host, out int k);
                trojanCount[si.Host] = k + 1;
                si.TrojanSide = k % 2 == 0 ? 1 : -1;
                si.TrojanIndex = k / 2;
            }
            list.Add(si);
        }
        // Slots within each L-point group (spread the members apart).
        var slots = new Dictionary<string, int>();
        foreach (var si in list)
        {
            if (si.Home == null) continue;
            string key = si.Host + "/" + si.Home.Kind;
            slots.TryGetValue(key, out int k);
            si.Home.Slot = k; slots[key] = k + 1;
            si.Trojan = si.Home.Kind == SectorHomes.Kind.L4 || si.Home.Kind == SectorHomes.Kind.L5;
            if (si.Trojan) si.TrojanSide = si.Home.Kind == SectorHomes.Kind.L4 ? 1 : -1;
            si.TrojanIndex = si.Home.Slot;
        }
        return list;
    }

    private static void HideGame(ColonizationMapSessionComponent map)
    {
        SetGameVisible(map, false);
        _gameHidden = true;
    }

    /// <summary>Put the game's own sector mesh and globes back (unified map off or map closing).</summary>
    public static void RestoreGame(ColonizationMapSessionComponent map)
    {
        if (!_gameHidden || map == null) return;
        SetGameVisible(map, true);
        if (_baseMax > 0) map.MaxDistance = _baseMax;
        _gameHidden = false;
    }

    public static bool HideGameSectors = true;

    private static void SetGameVisible(ColonizationMapSessionComponent map, bool visible)
    {
        if (HideGameSectors || visible)
            PlanetRenderBridge.SetRenderComponentVisible(PlanetRenderBridge.GetMember(map, "SectorsRenderer"), visible);
        // Globes: shrink through the engine's own scale path instead of render flags.
        // The game's own markers ("You are here", GPS): we draw our own.
        try { var mr = PlanetRenderBridge.GetMember(map, "MarkersRenderer"); if (mr != null) PlanetRenderBridge.SetMember(mr, "Enabled", visible); } catch { }
        if (PlanetRenderBridge.GetMember(map, "_discoveredPlanets") is System.Collections.IDictionary planets)
            foreach (var v in planets.Values)
            {
                PlanetRenderBridge.ScaleMapObject(v, visible ? 0f : 1e-4f);
                // The game's globe labels would stay behind at the charted positions: hide them too.
                try { v.GetType().GetMethod("SetLabelVisible")?.Invoke(v, new object[] { visible }); } catch { }
            }
    }

    private static void StringIdSel(ColonizationMapSessionComponent map, out Keen.VRage.Library.Utils.StringId? sel)
    {
        sel = null;
        try
        {
            object renderer = PlanetRenderBridge.GetMember(map, "SectorsRenderer");
            if (renderer != null && PlanetRenderBridge.GetMember(renderer, "SelectedSector") is Keen.VRage.Library.Utils.StringId sid) sel = sid;
        }
        catch { }
    }

    private static double PlanetScaleOf(ColonizationMapSessionComponent map)
    {
        try
        {
            var cfg = PlanetRenderBridge.GetMember(map, "_configuration");
            var v = cfg != null ? PlanetRenderBridge.GetMember(cfg, "PlanetScale") : null;
            if (v is float f && f > 0) return f * 1.03;
        }
        catch { }
        return 5.15;
    }

    /// <summary>A filled disc with an outline, lying in the map plane.</summary>
    private static void Disc(MeshBuilder b, Vector3D c, Quaternion orient, double r, ColorSRGB fill, ColorSRGB edge, double width)
    {
        const int n = 48;
        Vector3D P(int i) { double a = 2 * Math.PI * i / n; return c + (QuaternionD)orient * new Vector3D(Math.Cos(a) * r, 0, Math.Sin(a) * r); }
        Vector3D prev = P(0);
        for (int i = 1; i <= n; i++)
        {
            Vector3D p = P(i);
            b.AddTriangle(c, prev, p, fill, true);
            b.AddLine(prev, p, edge, (float)width, true);
            prev = p;
        }
    }

    /// <summary>
    /// A belt: the stretch of an orbit (radius a, about the sun) from centre angle +/- halfSpan,
    /// drawn as a flat torus band of half-width hw (filled, with its two edges). Angles are
    /// measured from the host planet's current direction <paramref name="hp"/> in the ecliptic.
    /// </summary>
    private static void TorusArc(MeshBuilder b, Vector3D hp, double a, double centre, double halfSpan, double hw,
                                 Func<Vector3D, Vector3D> solar, ColorSRGB fill, ColorSRGB edge, double width)
    {
        const int n = 32;
        double baseAng = Math.Atan2(hp.Y, hp.X);
        Vector3D Q(double ang, double rr) => solar(new Vector3D(Math.Cos(ang) * rr, Math.Sin(ang) * rr, 0));
        for (int i = 0; i < n; i++)
        {
            double a0 = baseAng + centre - halfSpan + 2 * halfSpan * i / n, a1 = baseAng + centre - halfSpan + 2 * halfSpan * (i + 1) / n;
            Vector3D i0 = Q(a0, a - hw), o0 = Q(a0, a + hw), i1 = Q(a1, a - hw), o1 = Q(a1, a + hw);
            b.AddTriangle(i0, o0, o1, fill, true);
            b.AddTriangle(i0, o1, i1, fill, true);
            b.AddLine(i0, i1, edge, (float)width, true);
            b.AddLine(o0, o1, edge, (float)width, true);
        }
    }

    /// <summary>
    /// One sector as a band section of its prescribed motion (SectorHomes): a stretch of its path
    /// around the current position (a fixed slice of TIME, so it stretches where the motion is
    /// fast: Kepler's second law), its whole path faint, and its name.
    /// </summary>
    private static void DrawHome(MeshBuilder b, SectorInfo si, SystemRegistry reg, ColorSRGB col, bool sel, double t, double dist, double w23,
                                 Func<string, Vector3D, Vector3D> fam, Func<Vector3D, Vector3D> solar, Quaternion orient)
    {
        var h = si.Home; var planet = reg.Find(si.Host);
        if (h == null || planet == null) return;
        var edge = sel ? Selected : col;
        double W(double k) => dist * 0.0022 * k;
        Vector3D up = (QuaternionD)orient * new Vector3D(0, dist * 0.012, 0);
        switch (h.Kind)
        {
            case SectorHomes.Kind.OwnPlanet:
            {
                if (w23 > 0.9) return;
                double rOut = si.R + h.Size * 0.15, rIn = Math.Max(si.R * 0.35, 1);
                Vector3D P(double ang, double rr) => fam(si.Host, new Vector3D(Math.Cos(ang) * rr, Math.Sin(ang) * rr, 0));
                BandArc(b, P, 0.5 * (rIn + rOut), 0, Math.PI, 0.5 * (rOut - rIn), new ColorSRGB(col, (sel ? 0.24f : 0.12f) * (float)(1 - w23)), new ColorSRGB(edge, (float)(1 - w23)), W(sel ? 1.0 : 0.6), 96);
                b.AddText(fam(si.Host, new Vector3D(0, -rOut, 0)) + up, si.Sector.Name, sel ? Selected : Text, 0.45f);
                return;
            }
            case SectorHomes.Kind.L4:
            case SectorHomes.Kind.L5:
            {
                if (w23 < 0.3) return;   // millions of km along the planet's orbit: solar band only
                Vector3D pos = solar(SectorHomes.HelioTrojan(h, planet, t));
                b.AddSphere(new WorldTransform(pos, Quaternion.Identity), dist * 0.004, col, edge, true);
                b.AddText(pos + up, $"{si.Sector.Name} ({h.Kind})", sel ? Selected : Text, 0.42f);
                return;
            }
        }
        // Ellipse or L1/L2 loop: path, band section, label.
        double T = SectorHomes.Period(h, planet);
        Vector3D At(double tau) { SectorHomes.Rel(h, planet, tau, out var rel); return rel; }
        Func<Vector3D, Vector3D> map = rel => w23 < 0.5 ? fam(si.Host, rel) : solar(planet.StateInParentAt(t).Position + rel);
        float a = (float)(1 - w23 * 0.6);
        // The whole path, faint.
        Vector3D q = map(At(t));
        for (int i = 1; i <= 96; i++)
        {
            Vector3D p = map(At(t + T * i / 96));
            b.AddLine(q, p, new ColorSRGB(sel ? Selected : Ring, 0.35f * a), (float)W(0.5), true);
            q = p;
        }
        // The band: a slice of time around now, thickened across the path.
        double span = 0.07 * T, hw = Math.Min(h.Size * 0.18, (h.Kind == SectorHomes.Kind.Ellipse ? h.A : SectorHomes.HillRadius(planet)) * 0.08);
        const int n = 20;
        Vector3D prevIn = default, prevOut = default;
        for (int i = 0; i <= n; i++)
        {
            double tau = t - span + 2 * span * i / n;
            Vector3D r0 = At(tau), r1 = At(tau + T * 0.002);
            Vector3D tan = r1 - r0; tan.Z = 0;
            Vector3D nrm = tan.LengthSquared() > 0 ? Vector3D.Normalize(new Vector3D(-tan.Y, tan.X, 0)) : Vector3D.UnitX;
            Vector3D pin = map(r0 - nrm * hw), pout = map(r0 + nrm * hw);
            if (i > 0)
            {
                b.AddTriangle(prevIn, prevOut, pout, new ColorSRGB(col, (sel ? 0.55f : 0.38f) * a), true);
                b.AddTriangle(prevIn, pout, pin, new ColorSRGB(col, (sel ? 0.55f : 0.38f) * a), true);
                b.AddLine(prevIn, pin, new ColorSRGB(edge, a), (float)W(sel ? 1.2 : 0.7), true);
                b.AddLine(prevOut, pout, new ColorSRGB(edge, a), (float)W(sel ? 1.2 : 0.7), true);
            }
            else b.AddLine(pin, pout, new ColorSRGB(edge, a), (float)W(0.7), true);
            prevIn = pin; prevOut = pout;
        }
        b.AddLine(prevIn, prevOut, new ColorSRGB(edge, a), (float)W(0.7), true);
        string tag = h.Kind == SectorHomes.Kind.Ellipse ? "" : $" ({h.Kind})";
        string label = sel ? $"{si.Sector.Name}{tag}  T {T / 3600:F0} h" : si.Sector.Name + tag;
        b.AddText(map(At(t)) + up, label, sel ? Selected : Text, sel ? 0.55f : 0.42f);
    }

    /// <summary>A band section of an orbit: radius a, centre angle, half span, half width (P maps angle, radius to the map).</summary>
    private static void BandArc(MeshBuilder b, Func<double, double, Vector3D> P, double a, double centre, double halfSpan, double hw,
                                ColorSRGB fill, ColorSRGB edge, double width, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double a0 = centre - halfSpan + 2 * halfSpan * i / n, a1 = centre - halfSpan + 2 * halfSpan * (i + 1) / n;
            Vector3D i0 = P(a0, a - hw), o0 = P(a0, a + hw), i1 = P(a1, a - hw), o1 = P(a1, a + hw);
            b.AddTriangle(i0, o0, o1, fill, true);
            b.AddTriangle(i0, o1, i1, fill, true);
            b.AddLine(i0, i1, edge, (float)width, true);
            b.AddLine(o0, o1, edge, (float)width, true);
            if (i == 0) b.AddLine(i0, o0, edge, (float)width, true);
            if (i == n - 1 && halfSpan < Math.PI) b.AddLine(i1, o1, edge, (float)width, true);
        }
    }

    private static void Polyline(MeshBuilder b, OrbitPath path, Func<Vector3D, Vector3D> map, ColorSRGB color, double width)
    {
        var pts = path.Points;
        if (pts == null || pts.Length < 2) return;
        int n = pts.Length, seg = path.IsClosed ? n : n - 1;
        for (int i = 0; i < seg; i++)
        {
            Vector3D a = map(pts[i]), c = map(pts[(i + 1) % n]);
            if (IsFinite(a) && IsFinite(c)) b.AddLine(a, c, color, (float)width, true);
        }
    }

    private static double Smooth(double a, double b, double x)
    {
        double k = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
        return k * k * (3 - 2 * k);
    }
    private static Vector3D Lerp(Vector3D a, Vector3D b, double k) => a + (b - a) * k;
    private static double LerpD(double a, double b, double k) => a + (b - a) * k;
    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}

/// <summary>The planets' own textured map globes as Map-type models (shared by the map views).</summary>
public static class MapGlobes
{
    private static readonly Dictionary<string, PlanetRenderBridge.Proxy> _globes = new Dictionary<string, PlanetRenderBridge.Proxy>();

    public static void Use(string body, Vector3D center, double radius, HashSet<string> used)
    {
        var h = OrbitalMap.HandlesFor(body);
        if (h == null) return;
        if (!_globes.TryGetValue(body, out var g)) { g = PlanetRenderBridge.CreateProxy(h, center, mapOnly: true); if (g == null) return; _globes[body] = g; }
        PlanetRenderBridge.UpdateProxy(h, g, center, radius);
        PlanetRenderBridge.SetProxyVisible(g, true);
        used.Add(body);
    }

    public static void End(HashSet<string> used)
    {
        foreach (var kv in _globes) if (!used.Contains(kv.Key)) PlanetRenderBridge.SetProxyVisible(kv.Value, false);
    }

    public static void HideAll() => End(new HashSet<string>());
}
