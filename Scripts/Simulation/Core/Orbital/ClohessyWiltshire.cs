using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// Clohessy-Wiltshire (Hill) linearized relative motion in the LVLH frame of a
    /// reference orbit with mean motion n. Coordinates: X = radial (away from the body),
    /// Y = along-track, Z = cross-track. Velocities are RELATIVE TO THE ROTATING LVLH
    /// frame.
    ///
    /// This is the natural relative-orbital drift of a member offset from the frame
    /// origin: two grids in slightly different orbits separate exactly this way. In the
    /// berth we apply m·<see cref="Acceleration"/> as a force to each member grid (on top
    /// of the apparent force −m·A from anchor thrust); a member's own dampeners then
    /// either hold formation against it or let it drift toward a split.
    /// </summary>
    public static class ClohessyWiltshire
    {
        /// <summary>
        /// Relative acceleration (LVLH) from relative position+velocity (LVLH) and mean
        /// motion n: ax = 3n²x + 2n·vy, ay = −2n·vx, az = −n²z.
        /// </summary>
        public static Vector3D Acceleration(Vector3D r, Vector3D v, double n)
        {
            double ax = 3.0 * n * n * r.X + 2.0 * n * v.Y;
            double ay = -2.0 * n * v.X;
            double az = -n * n * r.Z;
            return new Vector3D(ax, ay, az);
        }

        /// <summary>
        /// Closed-form CW propagation (state-transition solution) — exact relative
        /// position/velocity after time t. Used to validate <see cref="Acceleration"/>
        /// and available for cheap analytic relative prediction (rendezvous).
        /// </summary>
        public static void Propagate(ref Vector3D r, ref Vector3D v, double n, double t)
        {
            double s = Math.Sin(n * t), c = Math.Cos(n * t), nt = n * t;
            double x0 = r.X, y0 = r.Y, z0 = r.Z, vx0 = v.X, vy0 = v.Y, vz0 = v.Z;

            double x = (4.0 - 3.0 * c) * x0 + (s / n) * vx0 + (2.0 / n) * (1.0 - c) * vy0;
            double y = 6.0 * (s - nt) * x0 + y0 + (2.0 / n) * (c - 1.0) * vx0 + ((4.0 * s - 3.0 * nt) / n) * vy0;
            double z = c * z0 + (s / n) * vz0;

            double vx = 3.0 * n * s * x0 + c * vx0 + 2.0 * s * vy0;
            double vy = 6.0 * n * (c - 1.0) * x0 - 2.0 * s * vx0 + (4.0 * c - 3.0) * vy0;
            double vz = -n * s * z0 + c * vz0;

            r = new Vector3D(x, y, z);
            v = new Vector3D(vx, vy, vz);
        }

        /// <summary>
        /// Sample the predicted relative DRIFT path (LVLH coords) of an object at relative
        /// state (r0, v0) over <paramref name="duration"/> seconds — for "here's how you'll
        /// drift away from the target". The shape reads the outcome: a closed loop means a
        /// bounded relative orbit (you stay near); an opening spiral means secular drift
        /// (you leave). The renderer transforms these into world space via the target's
        /// LVLH basis and draws them.
        /// </summary>
        public static Vector3D[] SampleRelativePath(Vector3D r0, Vector3D v0, double n, double duration, int count)
        {
            if (count < 2) count = 2;
            var pts = new Vector3D[count];
            for (int k = 0; k < count; k++)
            {
                double t = duration * (k / (double)(count - 1));
                Vector3D r = r0, v = v0;
                Propagate(ref r, ref v, n, t);
                pts[k] = r;
            }
            return pts;
        }
    }
}
