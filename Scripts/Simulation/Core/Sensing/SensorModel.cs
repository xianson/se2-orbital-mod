using System;
using System.Collections.Generic;

namespace SEAerospace.Sensing
{
    /// <summary>
    /// What can be seen from where (Contacts' physics; game-free, tested offline in Tests/SensingTests).
    /// Real sensor models, no drive glare:
    ///  - eyes / cockpit: everything within <see cref="EyeRange"/>;
    ///  - telescope, optical: sunlight off the target, range ~ r sqrt(albedo x light), nothing within
    ///    <see cref="SunExclusion"/> of the sun. Light = the sun's (none in a body's shadow), seen at its phase,
    ///    plus PLANET-SHINE: each planet's sunlit face lights what is near it (albedo x (R/d)^2 x how much of
    ///    its day side faces the target), which lights a crescent near a bright planet and does nothing on
    ///    the night side. GLARE: a target seen against a planet's sunlit face has little contrast: its
    ///    optical range drops (down to <see cref="GlareFactor"/> against the full day side; the night side
    ///    and empty space do not hide it);
    ///  - telescope, infrared: its own warmth, day or night, range ~ r sqrt(emissivity) (T/300)^2;
    ///  - radar: its echo, range ~ (power x cross-section pi r^2 x radar albedo)^1/4;
    ///  - a firing engine's PLUME (its heat, seen by the telescope's infrared): range ~ sqrt(thrust) (Signature);
    ///  - a transmitting radar's own EMISSION, heard by any telescope or radar: range ~ sqrt(its power) - one way, so
    ///    far beyond its own echo range (Signature.EmissionReach). Gives a bearing, not an orbit.
    /// Always with no body between the looker and the target. Positions are one frame's (the solar
    /// system's, sun-centred model axes); lengths in metres.
    /// </summary>
    public static class SensorModel
    {
        public enum Kind { Eyes, Telescope, Radar }

        public const double EyeRange = 20000;
        // A sunlit 1.5 km rock ~1500 km, a 150 m station ~275 km (optical); a 150 m warm hull ~100 km
        // (infrared). Radar at full power (2026-10-19, was 18400: a large ship on the ground was out of reach from a
        // 300 km orbit): a large ship (mean cross-section 1000 m^2, a 35 m airliner) ~420 km, a small one (60 m^2)
        // ~210 km, a 150 m station ~1200 km, a 1.5 km rock (radar albedo 0.1) ~2200 km.
        public const double OpticalK = 2600, InfraredK = 700, Emissivity = 0.9, RadarK = 75000;
        public const double SunExclusion = 15 * Math.PI / 180;
        /// <summary>A body's radius is shrunk by this for line of sight (not blocked by the ground it sits on).</summary>
        public const double GroundPad = 0.98;
        /// <summary>Optical range left against a planet's fully sunlit face (low contrast).</summary>
        public const double GlareFactor = 0.25;
        /// <summary>A planet's albedo when none is given (Bond albedo of a rocky, cloudy world).</summary>
        public const double PlanetAlbedo = 0.3;

        public struct Body { public Vector3D Centre; public double Radius; public bool IsSun; public double Albedo; }
        public struct Looker { public Vector3D At; public Kind Kind; public double Power; }
        /// <summary>What is looked at. Radius: its equivalent radius (cross-section pi r^2: Signature.EquivalentRadius).
        /// RadarAlbedo: its echo per area (0: a metal hull's 1; a rock's 0.1). Plume: how far its firing engines'
        /// heat is seen (m, 0: none - Signature.PlumeReach). Emit: how far its own transmitting radar is heard (m, 0: silent).</summary>
        public struct Target { public Vector3D At; public double Radius, Albedo, TempK, RadarAlbedo, Plume, Emit; }

        /// <summary>How a target was seen (Contacts' "detected by").</summary>
        public enum Via { None, Eyes, Optical, Infrared, Plume, Echo, Emission }

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

        /// <summary>The echo's reach for this target (its radar albedo: 0 counts as a hull's 1).</summary>
        public static double EchoReach(Target t, double power)
            => RadarReach(t.Radius, power) * (t.RadarAlbedo > 0 ? Math.Pow(t.RadarAlbedo, 0.25) : 1);

        /// <summary>How far this looker sees this target (m): 0 when it cannot at all (in shadow, near the sun, ...).
        /// Bodies (for planet-shine and glare) may be null: sunlight only.</summary>
        public static double Reach(Looker o, Target t, Vector3D sun, bool lit, IList<Body> bodies = null)
            => ReachVia(o, t, sun, lit, bodies, out _);

        /// <summary><see cref="Reach"/>, and the channel that reaches furthest (how it would be seen).</summary>
        public static double ReachVia(Looker o, Target t, Vector3D sun, bool lit, IList<Body> bodies, out Via via)
        {
            double best; via = Via.None;
            switch (o.Kind)
            {
                case Kind.Eyes: via = Via.Eyes; return EyeRange;
                case Kind.Radar:
                    best = EchoReach(t, o.Power);
                    if (best > 0) via = Via.Echo;
                    break;
                default:
                    double optical = 0;
                    if (Angle(t.At - o.At, sun - o.At) >= SunExclusion)
                    {
                        double light = (lit ? Phase(o.At, t.At, sun) : 0) + PlanetShine(o.At, t.At, sun, bodies);
                        optical = OpticalReach(t.Radius, t.Albedo, light) * Glare(o.At, t.At, sun, bodies);
                    }
                    double ir = InfraredReach(t.Radius, t.TempK);
                    best = optical; if (best > 0) via = Via.Optical;
                    if (ir > best) { best = ir; via = Via.Infrared; }
                    if (t.Plume > best) { best = t.Plume; via = Via.Plume; }
                    break;
            }
            // a transmitting radar is heard by any sensor block (telescope or radar), never by the eye
            if (t.Emit > best) { best = t.Emit; via = Via.Emission; }
            return best;
        }

        /// <summary>
        /// The light the planets reflect onto the target, as seen from the observer (in units of full sunlight
        /// at full phase): for each planet, albedo x (R/d)^2 x the fraction of the planet's face toward the
        /// target that is sunlit, times the target's phase lit from the planet.
        /// </summary>
        public static double PlanetShine(Vector3D observer, Vector3D target, Vector3D sun, IList<Body> bodies)
        {
            if (bodies == null) return 0;
            double sum = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b.IsSun || !(b.Radius > 0)) continue;
                Vector3D toTarget = target - b.Centre;
                double d = toTarget.Length();
                if (!(d > b.Radius)) continue;
                // how much of the planet's hemisphere toward the target is day (1: the target over its noon, 0: over its midnight)
                double dayFraction = 0.5 * (1 + Math.Cos(Angle(sun - b.Centre, toTarget)));
                double albedo = b.Albedo > 0 ? b.Albedo : PlanetAlbedo;
                double illum = albedo * (b.Radius / d) * (b.Radius / d) * dayFraction;
                if (illum <= 1e-6) continue;
                // the target lit from the planet, seen from the observer: its phase with the planet as the lamp
                double a = Angle(b.Centre - target, observer - target);
                sum += illum * 0.5 * (1 + Math.Cos(a));
            }
            return sum;
        }

        /// <summary>
        /// Contrast against what is behind the target: 1 against space or a night side, down to GlareFactor
        /// against a fully sunlit planet face (the sight line from the observer, past the target, hits it).
        /// </summary>
        public static double Glare(Vector3D observer, Vector3D target, Vector3D sun, IList<Body> bodies)
        {
            if (bodies == null) return 1;
            Vector3D dir = target - observer;
            double len = dir.Length();
            if (!(len > 0)) return 1;
            dir = dir / len;
            double best = double.PositiveInfinity; double factor = 1;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b.IsSun || !(b.Radius > 0)) continue;
                // the first hit of the ray (from the target on) with the sphere
                Vector3D oc = target - b.Centre;
                double bq = Vector3D.Dot(oc, dir), c = oc.LengthSquared() - b.Radius * b.Radius;
                double disc = bq * bq - c;
                if (disc < 0) continue;
                double k = -bq - Math.Sqrt(disc);
                if (k < 0) k = -bq + Math.Sqrt(disc);
                if (k < 0 || k >= best) continue;
                best = k;
                Vector3D hit = target + dir * k;
                Vector3D n = hit - b.Centre, toSun = sun - hit;
                double cosInc = Vector3D.Dot(n, toSun) / (n.Length() * toSun.Length());   // sunlit where > 0
                factor = 1 - (1 - GlareFactor) * Math.Max(0, cosInc);
            }
            return factor;
        }

        /// <summary>The first looker that sees the target (its index), or -1. Lit: no body between it and the sun.</summary>
        public static int SeenBy(IList<Looker> lookers, Target t, IList<Body> bodies, Vector3D sun)
            => Detect(lookers, t, bodies, sun, out _);

        /// <summary>
        /// Who sees the target, and how: the looker that sees it best (an echo or sight - which fix its orbit - before a
        /// bearing; then the earliest looker), or -1 and Via.None. Lit: no body between it and the sun.
        /// </summary>
        public static int Detect(IList<Looker> lookers, Target t, IList<Body> bodies, Vector3D sun, out Via via)
        {
            bool lit = Clear(t.At, sun, bodies, ignoreSun: true);
            int found = -1; via = Via.None;
            for (int i = 0; i < lookers.Count; i++)
            {
                var o = lookers[i];
                double d = (t.At - o.At).Length();
                if (!(d <= ReachVia(o, t, sun, lit, bodies, out var v))) continue;
                if (!Clear(o.At, t.At, bodies)) continue;
                if (o.Kind == Kind.Radar && d <= EchoReach(t, o.Power)) v = Via.Echo;   // (in its echo's reach too: ranged, not just heard)
                if (found < 0 || Fixes(v) && !Fixes(via)) { found = i; via = v; }
                if (Fixes(via)) break;
            }
            return found;
        }

        /// <summary>This way of seeing gives a range and range-rate at once (sight, an echo): the orbit is known.</summary>
        public static bool Fixes(Via v) => v == Via.Eyes || v == Via.Echo;

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
