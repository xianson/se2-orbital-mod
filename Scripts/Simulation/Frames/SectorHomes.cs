using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Where each colonization sector lives in the solar system, and where it is at time t.
/// A sector region's motion is PRESCRIBED (an ephemeris), so it can sit on a Kepler ellipse
/// about its planet, loop around the planet's L1/L2 point (a planar Lyapunov orbit, the
/// circular restricted three-body motion that patched conics cannot give), or ride the
/// planet's orbit 60 degrees ahead or behind (L4/L5 Trojans). Ships merged into a region ride
/// with it; the ephemeris is the truth for maps and rendezvous.
///
/// Positions are in the planet's ecliptic frame (x, y; z = 0), metres, relative to the planet.
/// </summary>
public static class SectorHomes
{
    public enum Kind { OwnPlanet, Ellipse, L1, L2, L4, L5, Belt, Ring }

    public sealed class Home
    {
        public string Sector, Host;
        public Kind Kind;
        public double A, E, Omega, M0;   // ellipse
        public double Size;              // charted size, m
        /// <summary>Inclination and ascending node of an ellipse sector's orbit (about its planet's equator plane).</summary>
        public double I, Node;
        /// <summary>A belt: the sector is the whole ring of its orbit round the planet, not a section of it.</summary>
        public bool Belt;
        /// <summary>A ring sector's own orbit about the star (in AU), and where on it at t = 0 (NaN: by name).</summary>
        public double AU = RingAU;
        public double Phase = double.NaN;
        public int Slot;                 // index within an L-point group
    }

    /// <summary>Default homes for the Survival campaign (design section 6); others fall back to rules.</summary>
    public static readonly Dictionary<string, Kind> Campaign = new Dictionary<string, Kind>
    {
        { "Vantaris", Kind.L1 }, { "Cygnark", Kind.L2 },
        { "Byblos Sector", Kind.Ring }, { "Trinarc", Kind.Belt }, { "Pyrethra", Kind.Belt },
        // Deep sectors the nearest-planet rule had filed under Kemik: they are Delfos's.
        { "Delfos Sector", Kind.Ring },   // the star's own: an inner orbit, inside Verdure's
        { "Zarkon", Kind.Ring },          // its own orbit between Kemik and the belt
        { "Axionis", Kind.L4 }, { "Tarnyx", Kind.L5 },   // Kemik's leading and trailing Trojans
    };

    // Planar Lyapunov orbit (linearised about L1/L2, small mass ratio): in-plane frequency
    // lambda ~ 2.53 n, along-track/radial amplitude ratio ~ 3.2.
    public const double LyapunovLambda = 2.53, LyapunovKappa = 3.2, LyapunovAmplitude = 0.12;

    /// <summary>Planet sectors that are a belt round their planet (the whole ring of the orbit): Kemik's outermost.</summary>
    public static readonly HashSet<string> Belts = new HashSet<string>();
    /// <summary>Ring sectors' orbits about the star (AU); others at RingAU.</summary>
    public static readonly Dictionary<string, double> RingOrbits = new Dictionary<string, double> { { "Delfos Sector", 0.45 }, { "Zarkon", 1.85 } };

    /// <summary>The star's own sector on the game's chart (the centre cell, round Delfos).</summary>
    public const string StarSector = "Delfos Sector";
    /// <summary>The system's extent on the map (AU): the chart's outer ring is at about 1.</summary>
    public const double SystemOuterAU = 1.15;

    /// <summary>
    /// A sector of Delfos's (every sector but the planets' own): a circular orbit about the star at
    /// its charted distance from Delfos (in Verdure's charted distance = 1 AU) and charted bearing.
    /// </summary>
    public static Home MakeHelio(string sector, string host, double au, double phase, double size)
        => new Home { Sector = sector, Host = host, Size = size, Kind = Kind.Ring, AU = au, Phase = phase, E = 0 };

    /// <summary>Ellipse eccentricity and orientation, deterministic per sector name.</summary>
    public static Home Make(string sector, string host, double chartDistance, double chartBearing, double size, int slot)
    {
        var h = new Home { Sector = sector, Host = host, Size = size, Slot = slot };
        h.Kind = Campaign.TryGetValue(sector, out var k) ? k : chartDistance > MapView.TrojanThreshold ? (slot % 2 == 0 ? Kind.L4 : Kind.L5) : Kind.Ellipse;
        if (RingOrbits.TryGetValue(sector, out var au)) h.AU = au;
        uint hash = 2166136261;
        foreach (char c in sector) hash = (hash ^ c) * 16777619;
        h.A = chartDistance * SystemHost.SectorOrbitScale;   // the charted distance, scaled for sectors
        // Near circular (eccentric orbits of similar size crossed each other into a tangle), and tilted:
        // most a little, some steeply, some polar or retrograde, so a planet's sectors spread in 3D.
        h.E = 0.02 + 0.08 * ((hash & 0xFFFF) / 65535.0);
        h.Omega = ((hash >> 16) & 0xFFFF) / 65535.0 * 2 * Math.PI;
        // (A proper mix: the name hash's own high bits were alike for alike names, and four of Kemik's
        // seven sectors came out the same.)
        uint h2 = hash; h2 ^= h2 >> 16; h2 *= 0x85EBCA6Bu; h2 ^= h2 >> 13; h2 *= 0xC2B2AE35u; h2 ^= h2 >> 16;
        double[] tilts = { 0, 6, 14, 28, 45, 65, 90, 115 };
        h.I = tilts[(h2 >> 5) % (uint)tilts.Length] * Math.PI / 180;
        h.Node = ((h2 >> 12) & 0x3FF) / 1023.0 * 2 * Math.PI;
        // A belt: a ring of rock round the planet (flat-ish and circular), chosen by name.
        h.Belt = Belts.Contains(sector);
        if (h.Belt) { h.E = 0.01; h.I = Math.Min(h.I, 12 * Math.PI / 180); }
        h.M0 = chartBearing - h.Omega - h.Node;   // starts near its charted bearing
        return h;
    }

    /// <summary>Hill radius: the L1/L2 distance from the planet.</summary>
    public static double HillRadius(GravityBody planet) => planet.Parent == null ? double.PositiveInfinity :
        planet.StateInParentAt(0).Position.Length() * Math.Pow(planet.Mu / (3 * planet.Parent.Mu), 1.0 / 3.0);

    /// <summary>Position relative to the planet at time t (ecliptic x, y), or false for L4/L5 (use HelioTrojan).</summary>
    public static bool Rel(Home h, GravityBody planet, double t, out Vector3D rel)
    {
        rel = default;
        switch (h.Kind)
        {
            case Kind.Ellipse:
            {
                double n = Math.Sqrt(planet.Mu / (h.A * h.A * h.A));
                double M = h.M0 + n * t;
                double E = M;
                for (int i = 0; i < 12; i++) E -= (E - h.E * Math.Sin(E) - M) / (1 - h.E * Math.Cos(E));
                double nu = 2 * Math.Atan2(Math.Sqrt(1 + h.E) * Math.Sin(E / 2), Math.Sqrt(1 - h.E) * Math.Cos(E / 2));
                double r = h.A * (1 - h.E * Math.Cos(E));
                // In its own plane (argument of latitude u from the node), tilted by I about the node line.
                double u = h.Omega + nu, cu = Math.Cos(u), su = Math.Sin(u), cn = Math.Cos(h.Node), sn = Math.Sin(h.Node), ci = Math.Cos(h.I);
                rel = new Vector3D(r * (cn * cu - sn * su * ci), r * (sn * cu + cn * su * ci), r * su * Math.Sin(h.I));
                return true;
            }
            case Kind.L1:
            case Kind.L2:
            {
                var s = planet.StateInParentAt(t);
                Vector3D radial = Vector3D.Normalize(new Vector3D(s.Position.X, s.Position.Y, 0));   // away from the sun
                Vector3D along = Vector3D.Normalize(new Vector3D(s.Velocity.X, s.Velocity.Y, 0));
                double rH = HillRadius(planet);
                double n = Math.Sqrt(planet.Parent.Mu / Math.Pow(s.Position.Length(), 3));
                double ph = LyapunovLambda * n * t + h.Slot * 1.7;
                double ax = LyapunovAmplitude * rH;
                Vector3D centre = radial * (h.Kind == Kind.L1 ? -rH : rH);
                rel = centre + radial * (-ax * Math.Cos(ph)) + along * (LyapunovKappa * ax * Math.Sin(ph));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Period of the home's motion (for band sections): the ellipse period or the Lyapunov period.</summary>
    public static double Period(Home h, GravityBody planet)
    {
        if (h.Kind == Kind.Ellipse) return 2 * Math.PI * Math.Sqrt(h.A * h.A * h.A / planet.Mu);
        double n = Math.Sqrt(planet.Parent.Mu / Math.Pow(planet.StateInParentAt(0).Position.Length(), 3));
        return 2 * Math.PI / (LyapunovLambda * n);
    }

    /// <summary>The main belt: 2.2 to 3.2 AU (scaled), between Kemik and where Jupiter would be.</summary>
    public const double BeltInnerAU = 2.2, BeltOuterAU = 3.2;

    /// <summary>A belt sector's heliocentric position: a circular orbit in the belt, spread by name.</summary>
    public static Vector3D HelioBelt(Home h, double starMu, double t)
    {
        uint hash = 2166136261;
        foreach (char c in h.Sector) hash = (hash ^ c) * 16777619;
        double a = (BeltInnerAU + (BeltOuterAU - BeltInnerAU) * (0.2 + 0.6 * ((hash & 0xFFFF) / 65535.0))) * SystemHost.AU;
        double ph = ((hash >> 16) & 0xFFFF) / 65535.0 * 2 * Math.PI;
        double n = Math.Sqrt(starMu / (a * a * a));
        return new Vector3D(Math.Cos(ph + n * t) * a, Math.Sin(ph + n * t) * a, 0);
    }

    /// <summary>A sector with its own orbit around the sun (a planet-like ring), beyond the belt.</summary>
    public const double RingAU = 3.9;

    public static Vector3D HelioRing(Home h, double starMu, double t)
    {
        uint hash = 2166136261;
        foreach (char c in h.Sector) hash = (hash ^ c) * 16777619;
        double a = (h.AU > 0 ? h.AU : RingAU) * SystemHost.AU, ph = !double.IsNaN(h.Phase) ? h.Phase : (hash & 0xFFFF) / 65535.0 * 2 * Math.PI;
        double n = Math.Sqrt(starMu / (a * a * a));
        return new Vector3D(Math.Cos(ph + n * t) * a, Math.Sin(ph + n * t) * a, 0);
    }

    /// <summary>A Trojan's heliocentric position: the planet's, 60 degrees ahead (L4) or behind (L5).</summary>
    public static Vector3D HelioTrojan(Home h, GravityBody planet, double t)
    {
        Vector3D hp = planet.StateInParentAt(t).Position;
        double ang = (h.Kind == Kind.L4 ? 1 : -1) * (Math.PI / 3 + h.Slot * 0.035);
        return PlanetBerths.RotateAboutAxis(hp, Vector3D.UnitZ, ang);
    }
}
