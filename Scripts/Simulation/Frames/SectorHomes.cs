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
    public enum Kind { OwnPlanet, Ellipse, L1, L2, L4, L5 }

    public sealed class Home
    {
        public string Sector, Host;
        public Kind Kind;
        public double A, E, Omega, M0;   // ellipse
        public double Size;              // charted size, m
        public int Slot;                 // index within an L-point group
    }

    /// <summary>Default homes for the Survival campaign (design section 6); others fall back to rules.</summary>
    public static readonly Dictionary<string, Kind> Campaign = new Dictionary<string, Kind>
    {
        { "Vantaris", Kind.L1 }, { "Cygnark", Kind.L2 },
        { "Byblos Sector", Kind.L4 }, { "Trinarc", Kind.L4 }, { "Pyrethra", Kind.L5 },
    };

    // Planar Lyapunov orbit (linearised about L1/L2, small mass ratio): in-plane frequency
    // lambda ~ 2.53 n, along-track/radial amplitude ratio ~ 3.2.
    public const double LyapunovLambda = 2.53, LyapunovKappa = 3.2, LyapunovAmplitude = 0.12;

    /// <summary>Ellipse eccentricity and orientation, deterministic per sector name.</summary>
    public static Home Make(string sector, string host, double chartDistance, double chartBearing, double size, int slot)
    {
        var h = new Home { Sector = sector, Host = host, Size = size, Slot = slot };
        h.Kind = Campaign.TryGetValue(sector, out var k) ? k : chartDistance > MapView.TrojanThreshold ? (slot % 2 == 0 ? Kind.L4 : Kind.L5) : Kind.Ellipse;
        uint hash = 2166136261;
        foreach (char c in sector) hash = (hash ^ c) * 16777619;
        h.A = chartDistance;
        h.E = 0.06 + 0.22 * ((hash & 0xFFFF) / 65535.0);
        h.Omega = ((hash >> 16) & 0xFFFF) / 65535.0 * 2 * Math.PI;
        h.M0 = chartBearing - h.Omega;   // starts near its charted bearing
        return h;
    }

    /// <summary>Hill radius: the L1/L2 distance from the planet.</summary>
    public static double HillRadius(GravityBody planet) =>
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
                rel = new Vector3D(Math.Cos(h.Omega + nu) * r, Math.Sin(h.Omega + nu) * r, 0);
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

    /// <summary>A Trojan's heliocentric position: the planet's, 60 degrees ahead (L4) or behind (L5).</summary>
    public static Vector3D HelioTrojan(Home h, GravityBody planet, double t)
    {
        Vector3D hp = planet.StateInParentAt(t).Position;
        double ang = (h.Kind == Kind.L4 ? 1 : -1) * (Math.PI / 3 + h.Slot * 0.035);
        return PlanetBerths.RotateAboutAxis(hp, Vector3D.UnitZ, ang);
    }
}
