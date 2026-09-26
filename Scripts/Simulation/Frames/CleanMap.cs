using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The clean map. Two views and a crossfade, nothing else:
///  - SYSTEM (zoomed in): ONE planet system, centred: the planet, its moon, its sectors on
///    nested, evenly spaced, near-circular orbits (one thin line each), each sector a short
///    band section of uniform thickness with its name outside it; the L1/L2 loops at the
///    edge; you and your orbit.
///  - SOLAR (zoomed out): the sun, circular planet orbits, the planets with their sector
///    counts, the Trojan groups as torus arcs.
/// Radii in the system view are compressed (r^0.6) so 3,000 km bands and 38,000 km Lagrange
/// loops both read at one zoom.
/// </summary>
public static class CleanMap
{
    public static bool Enabled = true;
    public static string Focus = "auto";   // "auto" = the planet you are at / the selected sector's host

    static readonly ColorSRGB Line = new ColorSRGB(0.55f, 0.66f, 0.80f);
    static readonly ColorSRGB Locked = new ColorSRGB(0.62f, 0.66f, 0.74f);
    static readonly ColorSRGB Unlocked = new ColorSRGB(0.33f, 0.66f, 1.00f);
    static readonly ColorSRGB Colonized = new ColorSRGB(0.38f, 0.92f, 0.55f);
    static readonly ColorSRGB Selected = new ColorSRGB(1.00f, 0.84f, 0.25f);
    static readonly ColorSRGB You = new ColorSRGB(1.00f, 0.80f, 0.15f);
    static readonly ColorSRGB Text = new ColorSRGB(0.93f, 0.96f, 1.00f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.62f, 0.70f, 0.80f);
    static readonly ColorSRGB SunC = new ColorSRGB(1.00f, 0.86f, 0.45f);

    public sealed class Band
    {
        public string Name, Host;
        public SectorHomes.Home Home;
        public ColorSRGB Color;
        public bool Selected;
        public double DisplayA;   // evenly spaced display radius (ellipse homes)
    }

    public static void Draw(MeshBuilder b, List<Band> bands, SystemRegistry reg, Vector3D mapPos, Quaternion orient,
                            double dist, double u, double t, string playerPlanet, Vector3D playerRel,
                            KeplerianElements? playerOrbit, HashSet<string> globes)
    {
        double wSolar = Smooth(2.2, 2.8, u);
        Vector3D Rot(Vector3D v) => (QuaternionD)orient * v;
        double W(double k) => dist * 0.0020 * k;
        Vector3D up = Rot(new Vector3D(0, dist * 0.014, 0));

        // ── which system ──
        string focus = Focus != "auto" ? Focus : null;
        if (focus == null) foreach (var bd in bands) if (bd.Selected && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) focus = bd.Host;
        focus ??= playerPlanet;
        var planet = focus != null ? reg.Find(focus) : null;

        // ── SYSTEM view ──
        if (planet != null && wSolar < 0.99)
        {
            float a = (float)(1 - wSolar);
            var mine = new List<Band>();
            foreach (var bd in bands) if (bd.Host == focus) mine.Add(bd);
            // Evenly spaced display radii for the ellipse homes, in order of their real size.
            var ell = mine.FindAll(x => x.Home.Kind == SectorHomes.Kind.Ellipse);
            ell.Sort((x, y) => x.Home.A.CompareTo(y.Home.A));
            double rH = SectorHomes.HillRadius(planet);
            double rMax = rH * 1.25;
            double fit = 0.78 * dist;
            // A system diagram: real angles and real ORDER of radii, readable spacing.
            double lo = 0.28, hi = 0.73;
            double R(double r)
            {
                if (r >= rH * 0.8) return fit * 0.92 * (r / rH);                       // Lagrange region
                if (ell.Count == 0) return fit * lo;
                // piecewise-linear through the sorted ellipse radii
                if (r <= ell[0].Home.A) return fit * lo * Math.Max(0, r) / ell[0].Home.A;
                for (int i = 1; i < ell.Count; i++)
                    if (r <= ell[i].Home.A)
                    {
                        double f = (r - ell[i - 1].Home.A) / (ell[i].Home.A - ell[i - 1].Home.A);
                        return fit * (lo + (hi - lo) * ((i - 1) + f) / Math.Max(1, ell.Count - 1));
                    }
                double last = ell[ell.Count - 1].Home.A;
                return fit * (hi + (0.92 - hi) * Math.Min(1, (r - last) / (rH * 0.8 - last)));
            }
            Vector3D P(double ang, double r) => mapPos + Rot(new Vector3D(Math.Cos(ang) * R(r), 0, Math.Sin(ang) * R(r)));
            Vector3D Pv(Vector3D rel) => P(Math.Atan2(rel.Y, rel.X), Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y));

            // Planet, and its SOI as the frame of the view.
            var def = reg.FindDefinition(planet.Name);
            double planetR = fit * 0.07;
            MapGlobes.Use(planet.Name, mapPos, planetR, globes);
            b.AddText(mapPos - Rot(new Vector3D(0, 0, planetR * 1.8)), planet.Name, new ColorSRGB(Text, a), 0.8f);

            // Moons.
            foreach (var moon in planet.Children)
            {
                if (!SystemHost.BeaconOf.ContainsKey(moon.Name)) continue;
                double mr = moon.StateInParentAt(t).Position.Length();
                Circle(b, mapPos, orient, fit * 0.16, new ColorSRGB(Line, 0.35f * a), W(0.5));
                Vector3D mp = moon.StateInParentAt(t).Position;
                double ang = Math.Atan2(mp.Y, mp.X), rr = fit * 0.16;
                Vector3D mpos = mapPos + Rot(new Vector3D(Math.Cos(ang) * rr, 0, Math.Sin(ang) * rr));
                MapGlobes.Use(moon.Name, mpos, planetR * 0.35, globes);
            }

            // Sector orbits: one thin line each, then the band sections, then the names.
            for (int i = 0; i < ell.Count; i++)
            {
                var bd = ell[i];
                bd.DisplayA = bd.Home.A;
                Circle(b, mapPos, orient, R(bd.DisplayA), new ColorSRGB(bd.Selected ? Selected : Line, (bd.Selected ? 0.8f : 0.45f) * a), W(bd.Selected ? 1.1 : 0.7));
            }
            foreach (var bd in mine)
            {
                var h = bd.Home;
                var col = new ColorSRGB(bd.Selected ? Selected : bd.Color, a);
                switch (h.Kind)
                {
                    case SectorHomes.Kind.OwnPlanet:
                        // The planet's own sector: a soft halo around it.
                        Ring(b, mapPos, orient, planetR * 1.15, fit * 0.21, new ColorSRGB(bd.Color, 0.10f * a), new ColorSRGB(col, 0.5f * a), W(0.5));
                        b.AddText(mapPos + Rot(new Vector3D(0, 0, fit * 0.21)), bd.Name, new ColorSRGB(Dim, a), 0.5f);
                        break;
                    case SectorHomes.Kind.Ellipse:
                    {
                        SectorHomes.Rel(h, planet, t, out var rel);
                        double ang = Math.Atan2(rel.Y, rel.X);
                        double rr = R(bd.DisplayA), hw = fit * 0.026, span = 0.26;
                        BandSection(b, mapPos, orient, rr, ang, span, hw, new ColorSRGB(col, 0.85f * a), new ColorSRGB(Text, a), W(0.9));
                        Vector3D lp = mapPos + Rot(new Vector3D(Math.Cos(ang) * (rr + hw * 2.4), 0, Math.Sin(ang) * (rr + hw * 2.4)));
                        b.AddText(lp, bd.Name, new ColorSRGB(bd.Selected ? Selected : Text, a), bd.Selected ? 0.66f : 0.62f);
                        break;
                    }
                    case SectorHomes.Kind.L1:
                    case SectorHomes.Kind.L2:
                    {
                        // The loop around the Lagrange point, and the sector on it.
                        Vector3D prev = default;
                        double T = SectorHomes.Period(h, planet);
                        for (int i = 0; i <= 72; i++)
                        {
                            SectorHomes.Rel(h, planet, t + T * i / 72, out var r);
                            Vector3D p = Pv(r);
                            if (i > 0) b.AddLine(prev, p, new ColorSRGB(Line, 0.4f * a), (float)W(0.5), true);
                            prev = p;
                        }
                        SectorHomes.Rel(h, planet, t, out var now);
                        Vector3D np = Pv(now);
                        b.AddSphere(new WorldTransform(np, Quaternion.Identity), fit * 0.016, col, col, true);
                        b.AddText(np + up, $"{bd.Name}  ({h.Kind})", new ColorSRGB(bd.Selected ? Selected : Text, a), 0.56f);
                        break;
                    }
                }
            }

            // You.
            if (playerPlanet == focus)
            {
                if (playerOrbit.HasValue && IsFinite(playerOrbit.Value.SemiMajorAxis))
                {
                    var path = OrbitSampler.SamplePath(playerOrbit.Value, 128, planet.SoiRadius);
                    var pts = path.Points;
                    if (pts != null)
                        for (int i = 0; i + 1 < pts.Length || (path.IsClosed && i < pts.Length); i++)
                            b.AddLine(Pv(pts[i]), Pv(pts[(i + 1) % pts.Length]), new ColorSRGB(You, 0.8f * a), (float)W(0.8), true);
                }
                Vector3D yp = Pv(playerRel);
                b.AddSphere(new WorldTransform(yp, Quaternion.Identity), fit * 0.012, You, You, false);
                b.AddText(yp + up, "you", new ColorSRGB(You, a), 0.56f);
            }
        }

        // ── SOLAR view ──
        if (wSolar > 0.01)
        {
            float a = (float)wSolar;
            var root = reg.Root;
            double maxR = 1;
            foreach (var p in root.Children) if (SystemHost.BeaconOf.ContainsKey(p.Name)) maxR = Math.Max(maxR, p.StateInParentAt(t).Position.Length());
            double k = 0.40 * dist / maxR;
            Vector3D S(Vector3D helio) => mapPos + Rot(new Vector3D(helio.X, 0, helio.Y) * k);
            b.AddSphere(new WorldTransform(mapPos, Quaternion.Identity), dist * 0.018, SunC, SunC, true);
            foreach (var p in root.Children)
            {
                if (!SystemHost.BeaconOf.ContainsKey(p.Name)) continue;
                Vector3D hp = p.StateInParentAt(t).Position;
                Circle(b, mapPos, orient, hp.Length() * k, new ColorSRGB(Line, 0.45f * a), W(0.7));
                int n = 0; foreach (var bd in bands) if (bd.Host == p.Name && bd.Home.Kind != SectorHomes.Kind.OwnPlanet) n++;
                MapGlobes.Use(p.Name, S(hp), dist * 0.022, globes);
                b.AddText(S(hp) + Rot(new Vector3D(0, dist * 0.04, 0)), $"{p.Name}   {n} sector{(n == 1 ? "" : "s")}", new ColorSRGB(Text, a), 0.62f);
                // Trojan groups: torus arcs 60 degrees ahead and behind.
                foreach (int side in new[] { 1, -1 })
                {
                    var names = new List<string>();
                    foreach (var bd in bands)
                        if (bd.Host == p.Name && ((side > 0 && bd.Home.Kind == SectorHomes.Kind.L4) || (side < 0 && bd.Home.Kind == SectorHomes.Kind.L5))) names.Add(bd.Name);
                    if (names.Count == 0) continue;
                    double baseAng = Math.Atan2(hp.Y, hp.X) + side * Math.PI / 3, r = hp.Length() * k;
                    BandSection(b, mapPos, orient, r, baseAng, 0.16, r * 0.045, new ColorSRGB(Unlocked, 0.30f * a), new ColorSRGB(Unlocked, a), W(0.8));
                    Vector3D lp = mapPos + Rot(new Vector3D(Math.Cos(baseAng) * r * 1.12, 0, Math.Sin(baseAng) * r * 1.12));
                    b.AddText(lp, $"{(side > 0 ? "L4" : "L5")}: {string.Join(", ", names)}", new ColorSRGB(Dim, a), 0.5f);
                }
            }
        }
    }

    // ── primitives (all in the map plane) ──

    static void Circle(MeshBuilder b, Vector3D c, Quaternion o, double r, ColorSRGB col, double w)
    {
        const int n = 128;
        Vector3D prev = c + (QuaternionD)o * new Vector3D(r, 0, 0);
        for (int i = 1; i <= n; i++)
        {
            double ang = 2 * Math.PI * i / n;
            Vector3D p = c + (QuaternionD)o * new Vector3D(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);
            b.AddLine(prev, p, col, (float)w, true);
            prev = p;
        }
    }

    static void Ring(MeshBuilder b, Vector3D c, Quaternion o, double r0, double r1, ColorSRGB fill, ColorSRGB edge, double w)
        => BandSection(b, c, o, 0.5 * (r0 + r1), 0, Math.PI, 0.5 * (r1 - r0), fill, edge, w);

    /// <summary>A band section: radius r, centre angle, half span (rad), half width; filled, outlined.</summary>
    static void BandSection(MeshBuilder b, Vector3D c, Quaternion o, double r, double centre, double half, double hw,
                            ColorSRGB fill, ColorSRGB edge, double w)
    {
        int n = Math.Max(8, (int)(half / Math.PI * 64));
        Vector3D Q(double ang, double rr) => c + (QuaternionD)o * new Vector3D(Math.Cos(ang) * rr, 0, Math.Sin(ang) * rr);
        for (int i = 0; i < n; i++)
        {
            double a0 = centre - half + 2 * half * i / n, a1 = centre - half + 2 * half * (i + 1) / n;
            Vector3D i0 = Q(a0, r - hw), o0 = Q(a0, r + hw), i1 = Q(a1, r - hw), o1 = Q(a1, r + hw);
            b.AddTriangle(i0, o0, o1, fill, true);
            b.AddTriangle(i0, o1, i1, fill, true);
            b.AddLine(i0, i1, edge, (float)w, true);
            b.AddLine(o0, o1, edge, (float)w, true);
        }
        if (half < Math.PI)
        {
            b.AddLine(Q(centre - half, r - hw), Q(centre - half, r + hw), edge, (float)w, true);
            b.AddLine(Q(centre + half, r - hw), Q(centre + half, r + hw), edge, (float)w, true);
        }
    }

    static double Smooth(double a, double b, double x) { double k = Math.Max(0, Math.Min(1, (x - a) / (b - a))); return k * k * (3 - 2 * k); }
    static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
