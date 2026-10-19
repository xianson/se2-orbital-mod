namespace SEAerospace.Flight
{
    /// <summary>
    /// A point on a planet's ground as the player reads it (engine-free; Tests/FlightTests): latitude about the spin axis,
    /// longitude from the chart's own reference meridian (its X axis turned into the equator plane; Z when X is the axis),
    /// and the words "12°N 45°E". The landing estimate (U3, 2026-10-18) says where a path that hits the ground comes down.
    /// </summary>
    public static class GroundPoint
    {
        /// <summary>Latitude and longitude (degrees) of a chart (planet-fixed) position about a spin axis (any length;
        /// zero: +Y). False when the position is the centre.</summary>
        public static bool LatLon(Vector3D chartPos, Vector3D axis, out double latDeg, out double lonDeg)
        {
            latDeg = lonDeg = double.NaN;
            double r = chartPos.Length();
            if (!(r > 0)) return false;
            Vector3D up = axis.LengthSquared() > 1e-18 ? Vector3D.Normalize(axis) : Vector3D.UnitY;
            Vector3D refAxis = Math.Abs(up.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitZ;
            Vector3D e1 = Vector3D.Normalize(refAxis - up * Vector3D.Dot(refAxis, up));
            Vector3D e2 = Vector3D.Cross(up, e1);
            Vector3D p = chartPos / r;
            latDeg = Math.Asin(Math.Clamp(Vector3D.Dot(p, up), -1, 1)) * 180 / Math.PI;
            lonDeg = Math.Atan2(Vector3D.Dot(p, e2), Vector3D.Dot(p, e1)) * 180 / Math.PI;
            return true;
        }

        /// <summary>"12°N 45°E" (whole degrees; the poles without a longitude).</summary>
        public static string Text(double latDeg, double lonDeg)
        {
            if (double.IsNaN(latDeg)) return "";
            int la = (int)Math.Round(Math.Abs(latDeg));
            string ns = la == 0 ? "0°" : $"{la}°{(latDeg >= 0 ? "N" : "S")}";
            if (la >= 90 || double.IsNaN(lonDeg)) return ns;
            int lo = (int)Math.Round(Math.Abs(lonDeg));
            if (lo == 180) return $"{ns} 180°";
            string ew = lo == 0 ? "0°" : $"{lo}°{(lonDeg >= 0 ? "E" : "W")}";
            return $"{ns} {ew}";
        }

        /// <summary>Great-circle distance (m) over a sphere of radius r between two positions (any lengths).</summary>
        public static double Arc(Vector3D a, Vector3D b, double r)
        {
            if (!(a.LengthSquared() > 0) || !(b.LengthSquared() > 0)) return double.NaN;
            double c = Math.Clamp(Vector3D.Dot(Vector3D.Normalize(a), Vector3D.Normalize(b)), -1, 1);
            return Math.Acos(c) * r;
        }
    }
}
