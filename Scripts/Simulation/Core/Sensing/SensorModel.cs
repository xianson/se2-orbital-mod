using System;
using System.Collections.Generic;

namespace SEAerospace.Sensing
{
    /// <summary>
    /// What can be seen from where (Contacts' physics; game-free, tested offline in Tests/SensingTests).
    /// Real sensor models, no drive glare:
    ///  - eyes / cockpit: everything within <see cref="EyeRange"/>;
    ///  - telescope, optical: sunlight off the target, range ~ r sqrt(albedo x phase), nothing in a body's
    ///    shadow, nothing within <see cref="SunExclusion"/> of the sun;
    ///  - telescope, infrared: its own warmth, day or night, range ~ r sqrt(emissivity) (T/300)^2;
    ///  - radar: its echo, range ~ (power x cross-section pi r^2)^1/4.
    /// Always with no body between the looker and the target. Positions are one frame's (the solar
    /// system's, sun-centred model axes); lengths in metres.
    /// </summary>
    public static class SensorModel
    {
        public enum Kind { Eyes, Telescope, Radar }

        public const double EyeRange = 20000;
        // A sunlit 1.5 km rock ~1500 km, a 150 m station ~275 km (optical); a 150 m warm hull ~100 km
        // (infrared); a station ~300 km and a rock ~950 km by radar at full power.
        public const double OpticalK = 2600, InfraredK = 700, Emissivity = 0.9, RadarK = 18400;
        public const double SunExclusion = 15 * Math.PI / 180;
        /// <summary>A body's radius is shrunk by this for line of sight (not blocked by the ground it sits on).</summary>
        public const double GroundPad = 0.98;

        public struct Body { public Vector3D Centre; public double Radius; public bool IsSun; }
        public struct Looker { public Vector3D At; public Kind Kind; public double Power; }
        public struct Target { public Vector3D At; public double Radius, Albedo, TempK; }

        /// <summary>The lit fraction seen from the observer: 1 with the sun behind you, 0.05 (a glint) with it behind the target.</summary>
        public static double Phase(Vector3D observer, Vector3D target, Vector3D sun)
        {
            double a = Angle(sun - target, observer - target);
            return Math.Max(0.05, 0.5 * (1 + Math.Cos(a)));
        }

        public static double Angle(Vector3D a, Vector3D b)
        {
            double la = a.Length(), lb = b.Length();
            if (!(la > 0) || !(lb > 0)) return 0;
            double c = Vector3D.Dot(a, b) / (la * lb);
            return Math.Acos(c < -1 ? -1 : c > 1 ? 1 : c);
        }

        /// <summary>No body (bar the sun, when asked) between a and b.</summary>
        public static bool Clear(Vector3D a, Vector3D b, IList<Body> bodies, bool ignoreSun = false)
        {
            Vector3D d = b - a;
            double d2 = d.LengthSquared();
            if (!(d2 > 0)) return true;
            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (ignoreSun && body.IsSun) continue;
                double k = Vector3D.Dot(body.Centre - a, d) / d2;
                k = k < 0 ? 0 : k > 1 ? 1 : k;
                if ((a + d * k - body.Centre).Length() < body.Radius * GroundPad) return false;
            }
            return true;
        }

        public static double InfraredReach(double radius, double tempK)
            => InfraredK * radius * Math.Sqrt(Emissivity) * (tempK / 300.0) * (tempK / 300.0);

        public static double OpticalReach(double radius, double albedo, double phase)
            => OpticalK * radius * Math.Sqrt(Math.Max(0, albedo * phase));

        public static double RadarReach(double radius, double power)
            => RadarK * Math.Pow(Math.Max(0, power) * Math.PI * radius * radius, 0.25);

        /// <summary>How far this looker sees this target (m): 0 when it cannot at all (in shadow, near the sun, ...).</summary>
        public static double Reach(Looker o, Target t, Vector3D sun, bool lit)
        {
            switch (o.Kind)
            {
                case Kind.Eyes: return EyeRange;
                case Kind.Radar: return RadarReach(t.Radius, o.Power);
                default:
                    double optical = lit && Angle(t.At - o.At, sun - o.At) >= SunExclusion
                        ? OpticalReach(t.Radius, t.Albedo, Phase(o.At, t.At, sun)) : 0;
                    return Math.Max(optical, InfraredReach(t.Radius, t.TempK));
            }
        }

        /// <summary>The first looker that sees the target (its index), or -1. Lit: no body between it and the sun.</summary>
        public static int SeenBy(IList<Looker> lookers, Target t, IList<Body> bodies, Vector3D sun)
        {
            bool lit = Clear(t.At, sun, bodies, ignoreSun: true);
            for (int i = 0; i < lookers.Count; i++)
            {
                var o = lookers[i];
                double d = (t.At - o.At).Length();
                if (!(d <= Reach(o, t, sun, lit))) continue;
                if (!Clear(o.At, t.At, bodies)) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Warp's bill for one store (WarpBill): its charge after the surplus seconds a warped frame did not
        /// simulate, at its current flow (per second; + filling, - draining), held to [0, max]. Dry: it ran out.
        /// </summary>
        public static double AgeStore(double charge, double flow, double max, double surplusSeconds, out bool dry)
        {
            double v = charge + flow * surplusSeconds;
            dry = false;
            if (v <= 0) { v = 0; dry = flow < 0; }
            else if (v > max) v = max;
            return v;
        }
    }
}
