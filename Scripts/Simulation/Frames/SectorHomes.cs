using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Where each colonization sector lives in the solar system. Everything is an orbit, and a sector is
/// one of three kinds:
///  - BODY:     a body's own space (the star, a planet; a body still to come has its own orbit). It
///              rides the body.
///  - RING:     a band on an orbit round a body (a belt round Delfos, Verdure's ring).
///  - LAGRANGE: a point co-orbiting with a body: L1 / L2 inside and outside it (its Hill radius), L3
///              opposite, L4 / L5 sixty degrees ahead and behind on its orbit.
/// The game's chart gives the sectors; this table says what each one is. Positions are in the
/// system's root (sun-centred) frame, metres; the ecliptic is (x, y), height z.
/// </summary>
public static class SectorHomes
{
    public enum Kind { Body, Ring, Lagrange }

    public sealed class Home
    {
        public string Sector;
        public Kind Kind;
        /// <summary>Body: the body (the root for the star). Ring: the body it rings. Lagrange: the smaller body of the pair.</summary>
        public string Host;
        /// <summary>Lagrange: 1..5.</summary>
        public int Point;
        /// <summary>Ring: inner and outer radius about its host (m). Body: its zone's radius.</summary>
        public double Inner, Outer;
        /// <summary>Ring: its plane (rad) about the host's ecliptic.</summary>
        public double Tilt, Node;
        /// <summary>Ring, a body still to come: where on its orbit its site is at t = 0 (rad).</summary>
        public double Phase;
        /// <summary>A body still to come (Byblos): its own circular orbit about the star (AU).</summary>
        public bool Future;
        public double AU;
        /// <summary>The charted size (m).</summary>
        public double Size;
        /// <summary>For ordering: a ring's mid radius, else 0.</summary>
        public double A => Kind == Kind.Ring ? 0.5 * (Inner + Outer) : Future ? AU * SystemHost.AU : 0;
    }

    /// <summary>The star's own sector on the game's chart (the centre cell, round Delfos).</summary>
    public const string StarSector = "Delfos Sector";
    /// <summary>The system's extent on the map (AU): out past the outer belt.</summary>
    public const double SystemOuterAU = 1.45;
    /// <summary>Verdure's ring (the game's own ring round Verdure): radii in Verdure radii until the game's torus is read.</summary>
    public const double VerdureRingInnerR = 2.2, VerdureRingOuterR = 3.4;

    /// <summary>
    /// The sectors as orbits. Lagrange points by where the game's chart puts each sector against its
    /// planet (L4 ahead, L5 behind, L3 opposite).
    /// </summary>
    public static Home For(string sector, string nearestPlanet, double chartBearing, double size, SystemRegistry reg)
    {
        var h = new Home { Sector = sector, Size = size };
        string root = reg?.Root?.Name;
        double AUm = SystemHost.AU;
        void Body(string host) { h.Kind = Kind.Body; h.Host = host; }
        void Ring(string host, double inner, double outer) { h.Kind = Kind.Ring; h.Host = host; h.Inner = inner; h.Outer = outer; h.Phase = chartBearing; }
        void L(string host, int p) { h.Kind = Kind.Lagrange; h.Host = host; h.Point = p; }
        double Rp(string body) => reg?.FindDefinition(body)?.RadiusMeters ?? 6e4;
        switch (sector)
        {
            case "Delfos Sector": Body(root); h.Outer = 0.3 * AUm; break;
            case "Verdure Sector": Body("Verdure"); break;
            case "Kemik Sector": Body("Kemik"); break;
            case "Byblos Sector": Body(root); h.Future = true; h.AU = 0.88; h.Phase = chartBearing; break;   // a planet still to come
            case "Zarkon": Ring(root, 0.72 * AUm, 0.80 * AUm); break;         // a belt between Kemik (0.53) and Verdure (1.0), clear of both
            case "Pyrethra": Ring(root, 1.25 * AUm, 1.35 * AUm); break;       // the outer belt, beyond Verdure
            case "Oblivara": Ring("Verdure", VerdureRingInnerR * Rp("Verdure"), VerdureRingOuterR * Rp("Verdure")); break;   // Verdure's ring
            case "Echelon": L("Kemik", 4); break;
            case "Nadirae": L("Kemik", 5); break;
            case "Tarnyx": L("Kemik", 3); break;
            case "Axionis": L("Kemik", 2); break;
            case "Vantaris": L("Verdure", 4); break;
            case "Helionis": L("Verdure", 5); break;
            case "Trinarc": L("Verdure", 3); break;
            case "Cygnark": L("Verdure", 2); break;
            default: Body(nearestPlanet ?? root); break;   // (another world's sectors: its nearest planet's space)
        }
        // A planet's own space: a share of its sphere of influence, clear of the planet.
        if (h.Kind == Kind.Body && !h.Future && h.Outer <= 0)
        {
            var b = reg?.Find(h.Host);
            h.Outer = b != null && !double.IsInfinity(b.SoiRadius) ? Math.Max(0.15 * b.SoiRadius, 2.5 * Rp(h.Host)) : 5e5;
        }
        return h;
    }

    /// <summary>Hill radius: the L1/L2 distance from the planet.</summary>
    public static double HillRadius(GravityBody planet) => planet.Parent == null ? double.PositiveInfinity :
        planet.StateInParentAt(0).Position.Length() * Math.Pow(planet.Mu / (3 * planet.Parent.Mu), 1.0 / 3.0);

    /// <summary>
    /// Where the sector is at t (its site's point; root frame), and the body its motion is about
    /// (centre: a body's, a ring's host, an L1/L2's planet; the star for L3-L5 and a body to come).
    /// </summary>
    public static Vector3D Where(Home h, SystemRegistry reg, double t, out Vector3D centre)
    {
        var root = reg.Root;
        centre = Vector3D.Zero;
        switch (h.Kind)
        {
            case Kind.Body:
                if (h.Future) return Circular(root.Mu, h.AU * SystemHost.AU, h.Phase, 0, 0, t);
                {
                    var b = reg.Find(h.Host) ?? root;
                    centre = b.OriginInRoot(t).Position;
                    return centre;
                }
            case Kind.Ring:
            {
                var b = reg.Find(h.Host) ?? root;
                centre = b.OriginInRoot(t).Position;
                return centre + Circular(b.Mu, 0.5 * (h.Inner + h.Outer), h.Phase, h.Tilt, h.Node, t);
            }
            case Kind.Lagrange:
            {
                var s = reg.Find(h.Host);
                if (s == null || s.Parent == null) return Vector3D.Zero;
                Vector3D po = s.Parent.OriginInRoot(t).Position;
                centre = h.Point <= 2 ? s.OriginInRoot(t).Position : po;
                return po + LagrangeRel(s, h.Point, t);
            }
        }
        return Vector3D.Zero;
    }

    public static Vector3D Where(Home h, SystemRegistry reg, double t) => Where(h, reg, t, out _);

    /// <summary>
    /// A body's Lagrange point with its primary, from their gravity (mu). L4 / L5 are the equilateral
    /// points; L1-L3 the balance, in the frame turning with the body, of the primary's pull, the body's,
    /// the indirect term (the model keeps the primary still) and the turning, as fractions of the body's
    /// present distance. Relative to the primary; the points breathe with an eccentric orbit.
    /// </summary>
    public static Vector3D LagrangeRel(GravityBody s, int point, double t)
    {
        var st = s.StateInParentAt(t);
        Vector3D ps = st.Position, hv = Vector3D.Cross(ps, st.Velocity);
        double r = ps.Length();
        if (!(r > 0) || hv.LengthSquared() < 1e-12) return ps;
        Vector3D e1 = ps / r, n = Vector3D.Normalize(hv), e2 = Vector3D.Cross(n, e1);
        // L4 / L5: the equilateral points, exactly, at the body's present distance (so in the elliptic
        // problem too; a balance at the present turning rate sat ~460 km off Kemik's, and motion round
        // it ran away).
        if (point >= 4) return Rotate(ps, n, (point == 4 ? 1 : -1) * Math.PI / 3);
        // L1-L3: fixed fractions of the present distance (the circular problem's balance, scale-free).
        double m1 = s.Parent.Mu, m2 = s.Mu, w2 = m1 / (r * r * r);
        double rh = r * Math.Pow(m2 / (3 * m1), 1.0 / 3.0);
        // In the turning plane (x toward the body, y along its motion): pulls + indirect + centrifugal.
        (double, double) Net(double x, double y)
        {
            double d1 = Math.Sqrt(x * x + y * y), dx2 = x - r, d2 = Math.Sqrt(dx2 * dx2 + y * y);
            double k1 = m1 / (d1 * d1 * d1), k2 = m2 / (d2 * d2 * d2), ki = m2 / (r * r);
            return (-k1 * x - k2 * dx2 - ki + w2 * x, -k1 * y - k2 * y + w2 * y);
        }
        double gx, gy;
        switch (point)
        {
            case 1: gx = r - rh; gy = 0; break;
            case 2: gx = r + rh; gy = 0; break;
            case 3: gx = -r; gy = 0; break;
            default: gx = 0.5 * r; gy = (point == 4 ? 1 : -1) * r * Math.Sqrt(3) / 2; break;
        }
        for (int i = 0; i < 25; i++)
        {
            var (fx, fy) = Net(gx, gy);
            double e = Math.Max(1.0, r * 1e-7);
            var (fxx, fyx) = Net(gx + e, gy); var (fxy, fyy) = Net(gx, gy + e);
            double a = (fxx - fx) / e, b = (fxy - fx) / e, c = (fyx - fy) / e, d = (fyy - fy) / e;
            double det = point <= 3 ? a : a * d - b * c;
            if (Math.Abs(det) < 1e-30) break;
            double sx, sy;
            if (point <= 3) { sx = fx / a; sy = 0; }   // the collinear points stay on the line
            else { sx = (d * fx - b * fy) / det; sy = (a * fy - c * fx) / det; }
            gx -= sx; gy -= sy;
            if (Math.Abs(sx) + Math.Abs(sy) < 1e-3) break;
        }
        return e1 * gx + e2 * gy;
    }

    /// <summary>
    /// The Lagrange orbit (a game's approximation, a distorted Kepler): in the frame turning with the body
    /// you orbit the point on a closed ellipse, as round a small body, at the pair's real libration rate
    /// (w = n sqrt(27 mu / 4): six days at Kemik's). Out: the body's angular velocity (the frame's turn)
    /// and w.
    /// </summary>
    public static bool LagrangeOrbit(GravityBody s, double t, out Vector3D omega, out double w)
    {
        omega = Vector3D.Zero; w = 0;
        if (s?.Parent == null) return false;
        var st = s.StateInParentAt(t);
        double r = st.Position.Length();
        if (!(r > 0)) return false;
        omega = Vector3D.Cross(st.Position, st.Velocity) / (r * r);
        double mu = s.Mu / (s.Mu + s.Parent.Mu);
        w = omega.Length() * Math.Sqrt(27.0 / 4.0 * mu);
        return w > 0;
    }

    /// <summary>The pull (non-turning axes) that makes that ellipse: a harmonic pull toward the point in the turning frame, plus the turning's own terms.</summary>
    public static Vector3D LagrangeAccel(Vector3D d, Vector3D v, Vector3D omega, double w)
    {
        Vector3D vRot = v - Vector3D.Cross(omega, d);
        return -w * w * d + 2 * Vector3D.Cross(omega, vRot) + Vector3D.Cross(omega, Vector3D.Cross(omega, d));
    }

    /// <summary>Where on its Lagrange orbit you are after tau (turning frame, axes as at the start): the ellipse through (d, v).</summary>
    public static Vector3D LagrangeOrbitAt(Vector3D d, Vector3D v, Vector3D omega, double w, double tau)
    {
        Vector3D vRot = v - Vector3D.Cross(omega, d);
        return d * Math.Cos(w * tau) + vRot * (Math.Sin(w * tau) / w);
    }

    /// <summary>Gravity at a root-frame point from a primary and its body, with the indirect term (what balances a Lagrange point).</summary>
    public static Vector3D PairGravity(GravityBody s, Vector3D x, double t)
    {
        Vector3D o1 = s.Parent.OriginInRoot(t).Position, o2 = s.OriginInRoot(t).Position, rel = o2 - o1;
        Vector3D a = x - o1, b = x - o2;
        double la = a.Length(), lb = b.Length(), lr = rel.Length();
        Vector3D g = Vector3D.Zero;
        if (la > 1) g -= a * (s.Parent.Mu / (la * la * la));
        if (lb > 1) g -= b * (s.Mu / (lb * lb * lb));
        if (lr > 1) g -= rel * (s.Mu / (lr * lr * lr));
        return g;
    }

    /// <summary>The period the sector moves round with (its ring's, its body's orbit, its pair's).</summary>
    public static double Period(Home h, SystemRegistry reg)
    {
        var root = reg.Root;
        double P(double mu, double r) => 2 * Math.PI * Math.Sqrt(r * r * r / mu);
        switch (h.Kind)
        {
            case Kind.Ring: { var b = reg.Find(h.Host) ?? root; return P(b.Mu, 0.5 * (h.Inner + h.Outer)); }
            case Kind.Lagrange: { var s = reg.Find(h.Host); return s?.Parent != null ? P(s.Parent.Mu, s.StateInParentAt(0).Position.Length()) : 1; }
            default:
                if (h.Future) return P(root.Mu, h.AU * SystemHost.AU);
                { var b = reg.Find(h.Host); return b?.Parent != null ? P(b.Parent.Mu, b.StateInParentAt(0).Position.Length()) : 1; }
        }
    }

    /// <summary>A circular orbit (radius r about a body of mu), phase at t = 0, in a plane tilted about a node.</summary>
    static Vector3D Circular(double mu, double r, double phase, double tilt, double node, double t)
    {
        double a = phase + Math.Sqrt(mu / (r * r * r)) * t;
        Vector3D p = new Vector3D(Math.Cos(a) * r, Math.Sin(a) * r, 0);
        if (tilt != 0) p = Rotate(p, new Vector3D(Math.Cos(node), Math.Sin(node), 0), tilt);
        return p;
    }

    /// <summary>Rotate v about a unit axis by an angle (Rodrigues).</summary>
    static Vector3D Rotate(Vector3D v, Vector3D k, double ang)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang);
        return v * c + Vector3D.Cross(k, v) * s + k * Vector3D.Dot(k, v) * (1 - c);
    }
}
