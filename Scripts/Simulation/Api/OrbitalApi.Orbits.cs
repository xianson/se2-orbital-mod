using SEAerospace.Api;
using SEAerospace.Orbital;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// (API version 2) ORBIT MATH: the mod's own two-body conic math (Core/Orbital, the code the rails fly on) for any element
/// set - a frame's, a grid's, a body's, one of your own. An orbit is passed FLAT, as the eight numbers TryGetFrameOrbit gives:
/// semi-major axis (m; negative when hyperbolic), eccentricity, inclination, longitude of the ascending node, argument of
/// periapsis, true anomaly (rad) at the epoch (universe time, s), and the body's gravitational parameter (m3/s2). Positions
/// and velocities are about that body, in the solar system's inertial axes (m, m/s). Pure: no lock, no allocation, any thread.
/// False: a set the math cannot use (non-finite, mu &lt;= 0, e &lt; 0, the parabolic knife-edge e == 1, a's sign not the regime's).
/// </summary>
public static partial class OrbitalApi
{
    /// <summary>(v2) Where an orbit is at a universe time: position and velocity about its body (m, m/s, inertial). Two-body on
    /// rails - no sphere-of-influence change, no burn.</summary>
    public static bool PropagateOrbit(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis,
        double trueAnomaly, double epoch, double mu, double time, out Vector3D position, out Vector3D velocity)
        => OrbitQueries.Propagate(OrbitQueries.Elements(semiMajorAxis, eccentricity, inclination, raan, argPeriapsis, trueAnomaly, epoch, mu), time, out position, out velocity);

    /// <summary>(v2) The osculating orbit of a state about a body (m, m/s, inertial; mu m3/s2) at a universe time: the elements,
    /// their epoch being that time. A radial state (straight up or down, or at rest) gets a needle-thin ellipse, not NaN.</summary>
    public static bool TryStateToOrbit(Vector3D position, Vector3D velocity, double mu, double time, out double semiMajorAxis,
        out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly)
    {
        semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = 0;
        if (!OrbitQueries.FromState(position, velocity, mu, time, out var el)) return false;
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly;
        return true;
    }

    /// <summary>(v2) An orbit's apsides and timing: periapsis and apoapsis RADIUS (m, from the body's centre - less its radius for
    /// an altitude; apoapsis +infinity when hyperbolic), period (s; +infinity when hyperbolic), and the time from the epoch to the
    /// next periapsis and apoapsis (s; add the epoch for a universe time). Elliptic: the next passage, in [0, period). Hyperbolic:
    /// the one periapsis, negative once past; apoapsis NaN.</summary>
    public static bool TryGetOrbitEvents(double semiMajorAxis, double eccentricity, double trueAnomaly, double mu,
        out double periapsisRadius, out double apoapsisRadius, out double period, out double timeToPeriapsis, out double timeToApoapsis)
        => OrbitQueries.Events(OrbitQueries.Elements(semiMajorAxis, eccentricity, 0, 0, 0, trueAnomaly, 0, mu),
            out periapsisRadius, out apoapsisRadius, out period, out timeToPeriapsis, out timeToApoapsis);

    /// <summary>(v2) An orbit sampled evenly in TIME over [t0, t1] (universe times, s) into your buffer: x, y, z per point (m, about
    /// its body, inertial), xyz.Length / 3 points, the first at t0 and the last at t1. Returns the points written (0: nothing
    /// usable, or room for fewer than 2). For the path ahead of a frame or a grid: its orbit from TryPeekFrameOrbit /
    /// TryGetGridOrbit, t0 now. One conic: no sphere-of-influence change.</summary>
    public static int SampleOrbit(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis,
        double trueAnomaly, double epoch, double mu, double t0, double t1, double[] xyz)
        => OrbitQueries.SampleByTime(OrbitQueries.Elements(semiMajorAxis, eccentricity, inclination, raan, argPeriapsis, trueAnomaly, epoch, mu), t0, t1, xyz);

    /// <summary>(v2) An orbit's SHAPE into your buffer, as the mod's map draws it (x, y, z per point, m, about its body,
    /// inertial; xyz.Length / 3 points): an ellipse evenly in eccentric anomaly, one loop from periapsis (the last point is not
    /// the first: close it yourself); a hyperbola evenly in hyperbolic anomaly out to maxRadius (m) on both legs (0 or
    /// +infinity: a fixed wide arc). Returns the points written.</summary>
    public static int SampleOrbitShape(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis,
        double mu, double maxRadius, double[] xyz)
        => OrbitQueries.SampleShape(OrbitQueries.Elements(semiMajorAxis, eccentricity, inclination, raan, argPeriapsis, 0, 0, mu), maxRadius, xyz);
}
