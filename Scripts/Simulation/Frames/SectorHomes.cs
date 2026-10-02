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
            case "Oblivara":   // Verdure's ring: the game's own torus, once read (its radii in Verdure radii until then)
                if (SystemHost.BeaconOf.TryGetValue("Verdure", out var vb) && AsteroidBridge.RingAround(vb.Center, out var ri, out var ro)) Ring("Verdure", ri, ro);
                else Ring("Verdure", VerdureRingInnerR * Rp("Verdure"), VerdureRingOuterR * Rp("Verdure"));
                break;
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
        // L4 / L5: 60 degrees ahead / behind, on the body's orbit path there (for the look: an eccentric
        // orbit's equilateral points sit off its drawn path by more than a sector is wide).
        if (point >= 4)
        {
            Vector3D dir = Vector3D.Normalize(Rotate(ps, n, (point == 4 ? 1 : -1) * Math.PI / 3));
            return dir * OrbitRadiusToward(s, t, dir);
        }
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
        // L3: the same fraction of the orbit's radius on the far side (on its path, as L4 / L5).
        if (point == 3) return -e1 * (Math.Abs(gx) / r * OrbitRadiusToward(s, t, -e1));
        return e1 * gx + e2 * gy;
    }

    /// <summary>The body's orbit radius (about its parent) toward a direction in its plane.</summary>
    public static double OrbitRadiusToward(GravityBody s, double t, Vector3D dir)
    {
        var st = s.StateInParentAt(t);
        double mu = s.Parent.Mu, r = st.Position.Length();   // as the map's orbit lines are drawn (the parent's mu)
        Vector3D h = Vector3D.Cross(st.Position, st.Velocity);
        Vector3D ev = ((st.Velocity.LengthSquared() - mu / r) * st.Position - Vector3D.Dot(st.Position, st.Velocity) * st.Velocity) / mu;
        double p = h.LengthSquared() / mu, den = 1 + Vector3D.Dot(ev, Vector3D.Normalize(dir));
        return den > 1e-6 && IsFiniteD(p / den) ? p / den : r;
    }
    static bool IsFiniteD(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

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

    /// <summary>The calm core, as a share of the sector's half-width (L3-L5) or of its sphere (L1 / L2).</summary>
    public const double LagrangeCoreShare = 0.6;

    /// <summary>
    /// The calm core: this close to a Lagrange point there is no pull at all (Newtonian flight, a big
    /// quiet place to park or build): 60 % of the sector's half-width (about 330 km at Kemik's L4).
    /// </summary>
    public static double LagrangeCore(Home h, SystemRegistry reg)
    {
        var s = h?.Kind == Kind.Lagrange ? reg?.Find(h.Host) : null;
        if (s?.Parent == null) return 0;
        if (h.Point <= 2) return LagrangeCoreShare * 0.15 * HillRadius(s);
        return LagrangeCoreShare * 0.035 * s.StateInParentAt(0).Position.Length();
    }

    /// <summary>
    /// The pull (non-turning axes) of the Lagrange orbit: out of the calm core a spring toward the point
    /// in the turning frame, starting at the core's edge, plus the turning's own terms; none in the core.
    /// </summary>
    public static Vector3D LagrangeAccel(Vector3D d, Vector3D v, Vector3D omega, double w, double core, Vector3D omegaDot)
    {
        double r = d.Length();
        if (r <= core) return Vector3D.Zero;
        Vector3D vRot = v - Vector3D.Cross(omega, d);
        // The turning's own terms, all of them: an eccentric planet turns faster at periapsis, and
        // without the last (Euler) term that change pumped orbits up (20x the spring at Kemik's L4).
        return -w * w * (1 - core / r) * d + 2 * Vector3D.Cross(omega, vRot) + Vector3D.Cross(omega, Vector3D.Cross(omega, d))
               + Vector3D.Cross(omegaDot, d);
    }

    /// <summary>How fast the body's turn changes (rad/s^2): an eccentric orbit turns faster near periapsis.</summary>
    public static Vector3D LagrangeTurnRate(GravityBody s, double t)
    {
        const double e = 30.0;
        if (!LagrangeOrbit(s, t + e, out var a, out _) || !LagrangeOrbit(s, t - e, out var b, out _)) return Vector3D.Zero;
        return (a - b) / (2 * e);
    }


    /// <summary>The drift (turning frame) that circles the point at distance r: none in the core (you stay put).</summary>
    public static double LagrangeCircleSpeed(double r, double w, double core) => r <= core ? 0 : w * Math.Sqrt(r * (r - core));

    /// <summary>
    /// The Lagrange sector as a region (a sphere of influence): L3-L5 the teardrop on the body's orbit
    /// (+-12.6 degrees of it, +-3.5 % of its radius at the middle, tapering to the tips; as thick out of
    /// the plane); L1 / L2 a sphere of 15 % of the Hill radius round the point.
    /// </summary>
    public static bool InLagrangeRegion(Home h, SystemRegistry reg, double t, Vector3D rootPos)
        => Region.Make(h, reg, t, out var r) && r.Contains(rootPos);   // (no closure: the server asks this every tick)

    /// <summary>The same test, set up once for a time t (for many points).</summary>
    public static Func<Vector3D, bool> LagrangeRegion(Home h, SystemRegistry reg, double t)
        => Region.Make(h, reg, t, out var r) ? q => r.Contains(q) : null;

    /// <summary>A Lagrange region set up for a time (see InLagrangeRegion).</summary>
    struct Region
    {
        Home H; GravityBody S; double T, R, Rb, PhiMax; Vector3D P, Centre, B, N; bool Sphere;

        public static bool Make(Home h, SystemRegistry reg, double t, out Region r)
        {
            r = default;
            if (h?.Kind != Kind.Lagrange) return false;
            var s = reg.Find(h.Host);
            if (s?.Parent == null) return false;
            r.H = h; r.S = s; r.T = t;
            r.P = Where(h, reg, t, out r.Centre);
            if (h.Point <= 2) { r.Sphere = true; r.R = 0.15 * HillRadius(s); return true; }
            r.B = r.P - r.Centre;
            r.Rb = r.B.Length();
            if (!(r.Rb > 0)) return false;
            var st = s.StateInParentAt(t);
            r.N = Vector3D.Normalize(Vector3D.Cross(st.Position, st.Velocity));
            r.PhiMax = 2 * Math.PI * 0.035;
            return true;
        }

        public bool Contains(Vector3D q)
        {
            if (Sphere) return (q - P).LengthSquared() <= R * R;
            Vector3D a = q - Centre;
            double z = Vector3D.Dot(a, N);
            Vector3D ap = a - N * z;
            if (!(ap.LengthSquared() > 0)) return false;
            double phi = Math.Atan2(Vector3D.Dot(N, Vector3D.Cross(B, ap)), Vector3D.Dot(B, ap));
            if (Math.Abs(phi) > PhiMax) return false;
            double half = 0.035 * Rb * Math.Max(0.08, Math.Cos(phi / PhiMax * Math.PI / 2));
            double mid = OrbitRadiusToward(S, T, ap) * (H.Point == 3 ? Rb / OrbitRadiusToward(S, T, B) : 1);   // along the orbit path
            return Math.Abs(ap.Length() - mid) <= half && Math.Abs(z) <= half;
        }
    }

    /// <summary>A Lagrange point's place and velocity (root frame) at t.</summary>
    public static void LagrangeState(Home h, SystemRegistry reg, double t, out Vector3D p, out Vector3D v)
    {
        const double e = 1.0;
        p = Where(h, reg, t);
        v = (Where(h, reg, t + e) - Where(h, reg, t - e)) / (2 * e);
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
    public static Vector3D Circular(double mu, double r, double phase, double tilt, double node, double t)
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
