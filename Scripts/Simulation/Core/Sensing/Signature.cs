using System;

namespace SEAerospace.Sensing
{
    /// <summary>
    /// A ship's SIGNATURE, from what it is and what it does (Contacts; game-free, tested offline in Tests/SensingTests).
    /// 2026-10-19: every grid counted as a 150 m, 290 K ball - a fighter as big as a station, a burning ship as cold as a
    /// drifting one. Now:
    ///  - SIZE: its mean cross-section, from its box: a convex body's area seen from a random direction averages a
    ///    quarter of its surface - for a box x by y by z, (xy + yz + zx) / 2 (Cauchy). Radar and sunlight go with it;
    ///    the equivalent radius r = sqrt(area / pi) feeds SensorModel's pi r^2 formulas unchanged.
    ///  - PLUME: engines firing are hot - seen in infrared at <see cref="PlumeK"/> x sqrt(thrust in N) (the plume's
    ///    power goes as the thrust; seen to sqrt of it, inverse square): a large ship burning 4 MN ~600 km, a fighter's
    ///    200 kN ~130 km, a drifting ship nothing.
    ///  - EMISSION: a transmitting radar is heard one-way (inverse square, not the echo's fourth power): any telescope
    ///    or radar hears it at <see cref="EmitK"/> x sqrt(power) - ~4000 km at full power, ~10x the radar's own reach
    ///    on a large ship. Hearing gives a bearing, not an orbit.
    ///  - KIND: what a contact looks like (Classify) - debris, small or large ship, station, wreck - and rocks, sectors.
    /// </summary>
    public static class Signature
    {
        /// <summary>Plume reach per sqrt(newton) of thrust firing (m).</summary>
        public const double PlumeK = 300;
        /// <summary>How far a transmitting radar is heard at full power (m).</summary>
        public const double EmitK = 4.0e6;
        /// <summary>A grid whose box is not known yet: a cube this size (m).</summary>
        public const double DefaultSize = 10;
        /// <summary>A rock's radar albedo (rough dark stone; a metal hull's is 1).</summary>
        public const double RockRadarAlbedo = 0.1;
        /// <summary>Kind thresholds: below DebrisSize (largest side, m) or DebrisMass (kg) a grid is debris; from
        /// LargeSize on a ship is a large one.</summary>
        public const double DebrisSize = 4, DebrisMass = 1500, LargeSize = 25;

        /// <summary>The mean cross-section of a box (m^2): (xy + yz + zx) / 2. A missing side counts as DefaultSize.</summary>
        public static double MeanArea(double x, double y, double z)
        {
            x = x > 0 ? x : DefaultSize; y = y > 0 ? y : DefaultSize; z = z > 0 ? z : DefaultSize;
            return (x * y + y * z + z * x) / 2;
        }

        /// <summary>The radius of a disc of that area (SensorModel's target radius).</summary>
        public static double EquivalentRadius(double area) => Math.Sqrt(Math.Max(0, area) / Math.PI);

        /// <summary>How far a plume of this much thrust firing (N) is seen in infrared (m).</summary>
        public static double PlumeReach(double firingNewtons) => PlumeK * Math.Sqrt(Math.Max(0, firingNewtons));

        /// <summary>How far a radar transmitting at this power (0..1) is heard (m).</summary>
        public static double EmissionReach(double power) => EmitK * Math.Sqrt(Math.Max(0, Math.Min(1, power)));

        public enum Class { Unknown, Debris, SmallShip, LargeShip, Station, Wreck, Rock, Sector }

        /// <summary>
        /// What a grid looks like: debris (tiny or light), a station (fixed), a wreck (no owner and not an encounter's),
        /// else a small or large ship by its largest side.
        /// </summary>
        public static Class Classify(bool isStatic, bool unowned, double largestSide, double massKg)
        {
            if (largestSide > 0 && largestSide < DebrisSize || massKg > 0 && massKg < DebrisMass) return Class.Debris;
            if (isStatic) return Class.Station;
            if (unowned) return Class.Wreck;
            return largestSide >= LargeSize ? Class.LargeShip : Class.SmallShip;
        }

        /// <summary>Who must be detected (Contacts): an encounter's grid, one with no owner (a wreck: not yours whoever you
        /// are), one owned by someone else. hasOwner false: ownership unknown - yours (never hidden from you by mistake).</summary>
        public static bool IsContact(bool encounter, bool hasOwner, bool ownerIsNobody, bool ownerIsYou)
            => encounter || hasOwner && (ownerIsNobody || !ownerIsYou);

        public static string Name(Class c)
        {
            switch (c)
            {
                case Class.Debris: return "debris";
                case Class.SmallShip: return "small ship";
                case Class.LargeShip: return "large ship";
                case Class.Station: return "station";
                case Class.Wreck: return "wreck";
                case Class.Rock: return "rock";
                case Class.Sector: return "sector";
                default: return "contact";
            }
        }

        /// <summary>How it was seen, in words ("detected by").</summary>
        public static string Name(SensorModel.Via v)
        {
            switch (v)
            {
                case SensorModel.Via.Eyes: return "sight";
                case SensorModel.Via.Optical: return "telescope";
                case SensorModel.Via.Infrared: return "telescope (infrared)";
                case SensorModel.Via.Plume: return "telescope (engine plume)";
                case SensorModel.Via.Echo: return "radar";
                case SensorModel.Via.Emission: return "its radar (heard)";
                default: return "-";
            }
        }

        /// <summary>
        /// A BEARING in your orbit's frame: azimuth (degrees in your local horizontal plane, clockwise seen from above:
        /// 0 ahead - the way you move over the ground - 90 right, 180 behind, 270 left) and elevation
        /// (degrees above your local horizontal, + up away from the body). rel: the target from you; r, v: your position and
        /// velocity about the body you orbit (any frame, the same axes). False when your frame is undefined.
        /// </summary>
        public static bool Bearing(Vector3D rel, Vector3D r, Vector3D v, out double azimuthDeg, out double elevationDeg)
        {
            azimuthDeg = elevationDeg = 0;
            double rl = r.Length(), dl = rel.Length();
            if (!(rl > 0) || !(dl > 0)) return false;
            Vector3D up = r / rl;
            Vector3D fwd = v - up * Vector3D.Dot(v, up);   // the horizontal part of your velocity: "ahead"
            if (!(fwd.LengthSquared() > 1e-12))
            {
                // (at rest relative to the body - on the ground: any horizontal axis; north-ish from the pole axis)
                fwd = Vector3D.Cross(up, Math.Abs(up.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
                fwd = Vector3D.Cross(fwd, up);
            }
            fwd = fwd / fwd.Length();
            Vector3D right = Vector3D.Cross(fwd, up);     // (right-handed: fwd x up points to your right)
            Vector3D d = rel / dl;
            double h = Vector3D.Dot(d, up);
            elevationDeg = Math.Asin(h < -1 ? -1 : h > 1 ? 1 : h) * 180 / Math.PI;
            double az = Math.Atan2(Vector3D.Dot(d, right), Vector3D.Dot(d, fwd)) * 180 / Math.PI;
            azimuthDeg = az < 0 ? az + 360 : az;
            return true;
        }

        /// <summary>A bearing in words: "ahead, 12° up" / "040°, 5° down" (the cardinal word within 20° of one).</summary>
        public static string BearingText(double az, double el)
        {
            string dir = az <= 20 || az >= 340 ? "ahead" : Math.Abs(az - 180) <= 20 ? "behind" : Math.Abs(az - 90) <= 20 ? "right" : Math.Abs(az - 270) <= 20 ? "left" : null;
            string a = dir ?? $"{Math.Round(az) % 360:000}°";
            if (Math.Abs(el) >= 80) return el > 0 ? "straight up" : "straight down";
            string e = Math.Abs(el) < 1 ? "level" : $"{Math.Abs(el):F0}° {(el > 0 ? "up" : "down")}";
            return $"{a}, {e}";
        }
    }
}
