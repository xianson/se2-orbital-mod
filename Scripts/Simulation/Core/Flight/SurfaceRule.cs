namespace SEAerospace.Flight
{
    /// <summary>What a ship in a planet's space is doing: on an orbit, landed, flying low (in the air on a hop that never
    /// orbits), or somewhere no ship can be (deep inside the body: a position that is no real one).</summary>
    public enum SurfaceKind { Orbit, Landed, Low, Unknown }

    /// <summary>
    /// LANDED OR ORBITING (engine-free; Tests/FlightTests). 2026-10-18, the UI audit (B1): ships on the ground were given
    /// the Kepler orbit their state makes - "Ap -1.4 km, Pe -63 km, Period 2 min" for a crashed ship at rest, and one read
    /// at the planet's very centre "Ap -63 km, Period 0 s, 96,955 m/s" - and the closest approach was predicted from them.
    /// A ship near the ground that is at rest on it is LANDED; one moving near the ground on a path that never clears the
    /// surface is LOW (in the air); one deep inside the body is UNKNOWN (never a place: never targeted, never predicted).
    /// Only the rest is an orbit.
    /// </summary>
    public static class SurfaceRule
    {
        /// <summary>Over the ground slower than this (m/s): at rest on it.</summary>
        public const double LandedSpeed = 5.0;
        /// <summary>The ground band: within max(MinBand, BandShare x radius) of the surface (terrain is km high and deep).</summary>
        public const double MinBand = 5000, BandShare = 0.08;
        /// <summary>Nearer the centre than this share of the radius: no ship is there - the position is no real one.</summary>
        public const double InsideShare = 0.5;

        public static double Band(double radius) => Math.Max(MinBand, BandShare * radius);

        /// <summary>The hop rule (one place - it was FrameHost.IsGrounded's alone, the player's own orbit): a path whose
        /// periapsis is inside the body and whose apoapsis stays under max(HopMin, HopShare x radius) over the surface is no
        /// orbit - a hop, a fall, a ship on the ground.</summary>
        public const double HopMin = 15000, HopShare = 0.2;
        public static bool Grounded(double periapsisRadius, double apoapsisRadius, double radius)
            => radius > 0 && periapsisRadius < radius && apoapsisRadius < radius + Math.Max(HopMin, HopShare * radius);

        /// <summary>
        /// r: distance from the body's centre (m); radius: its surface radius (m; 0 or less: a body with no surface -
        /// always an orbit); groundSpeed: speed over the ground (m/s; NaN unknown); periapsisRadius / apoapsisRadius: the
        /// apsides of the inertial state (m; apoapsis +infinity when unbound; NaN unknown).
        /// </summary>
        public static SurfaceKind Classify(double r, double radius, double groundSpeed, double periapsisRadius, double apoapsisRadius)
        {
            if (!(radius > 0)) return SurfaceKind.Orbit;
            if (double.IsNaN(r) || double.IsInfinity(r)) return SurfaceKind.Unknown;
            if (r < InsideShare * radius) return SurfaceKind.Unknown;
            bool near = r - radius <= Band(radius);
            if (near && !(groundSpeed > LandedSpeed)) return double.IsNaN(groundSpeed) ? SurfaceKind.Low : SurfaceKind.Landed;
            // moving: no orbit when its path is a hop (the player's own rule); near the ground, any path that does not
            // clear the surface is in the air
            if (Grounded(periapsisRadius, apoapsisRadius, radius)) return SurfaceKind.Low;
            if (near && !(periapsisRadius > radius)) return SurfaceKind.Low;
            return SurfaceKind.Orbit;
        }

        /// <summary>Both apsides of an inertial state about mu (m): apoapsis +infinity when unbound; NaN when not finite.</summary>
        public static void Apsides(Vector3D r, Vector3D v, double mu, out double periapsis, out double apoapsis)
        {
            periapsis = Periapsis(r, v, mu); apoapsis = double.NaN;
            double rr = r.Length();
            if (!(rr > 0) || !(mu > 0)) return;
            double energy = 0.5 * v.LengthSquared() - mu / rr;
            if (!(energy < 0)) { apoapsis = double.PositiveInfinity; return; }
            double a = -mu / (2 * energy);
            apoapsis = 2 * a - (double.IsNaN(periapsis) ? 0 : periapsis);
        }

        /// <summary>The periapsis radius of an inertial state about a body of gravitational parameter mu (m; NaN when not
        /// finite). A radial state (no angular momentum) has periapsis 0.</summary>
        public static double Periapsis(Vector3D r, Vector3D v, double mu)
        {
            double rr = r.Length();
            if (!(rr > 0) || !(mu > 0)) return double.NaN;
            double h2 = Vector3D.Cross(r, v).LengthSquared();
            double energy = 0.5 * v.LengthSquared() - mu / rr;
            double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / (mu * mu)));
            double p = h2 / mu;
            double rp = p / (1 + e);
            return double.IsNaN(rp) || double.IsInfinity(rp) ? double.NaN : rp;
        }

        /// <summary>A target with this kind has no orbit to tell (no Ap / Pe / period).</summary>
        public static bool NoOrbit(SurfaceKind k) => k != SurfaceKind.Orbit;
    }
}
