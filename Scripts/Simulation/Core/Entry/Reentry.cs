using System;
using SEAerospace.Orbital;

namespace SEAerospace.Entry
{
    /// <summary>A body's braking band: from its top (the entry interface) down to the planet frame's border.</summary>
    public struct Band
    {
        /// <summary>Radii (m): the border (where physics takes over) and the entry interface.</summary>
        public double Bottom, Top;
        /// <summary>The envelope's scale height (m) and its braking strength at the border (e-folds per metre).</summary>
        public double Scale, Kappa0;
        public bool IsValid => Top > Bottom && Kappa0 > 0;
    }

    /// <summary>What a pass through the band did (or, predicted, will do).</summary>
    public class Pass
    {
        public double EnterTime = double.NaN;    // first time inside the band while over the cap
        public double MaxQTime = double.NaN, MaxQRadius, PeakDecel;
        public double BottomTime = double.NaN, SpeedAtBottom = double.NaN;
        public double Shed;                      // J/kg shed in all
        public double PeakHeat;                  // the heat's peak (J/kg)
        public double ArrivalSpeed = double.NaN; // air speed on entering the band
        public double DragShed;                  // J/kg of Shed from drag (the rest: the cap rule)
        public double Dv;                        // m/s of air speed taken off in all (drag and the cap rule): the pass's delta-v
        /// <summary>Predicted: the orbit on leaving the band through its top (HasAfter), or none - the pass reaches the
        /// border (the world flies it from there: Landing) or the horizon ends inside the band.</summary>
        public KeplerianElements After;
        public bool HasAfter, Landing;
        public double ExitTime = double.NaN;
        public bool Braked => Shed > 0;
    }

    /// <summary>
    /// REENTRY, on rails (game-free; tested offline in Tests/EntryTests). Inside a planet's frame the world's speed
    /// cap applies (you are a guest in the planet's frame), so a faster arrival must be slowed BEFORE the border:
    /// the planet's tenuous outer envelope, a band from the entry interface (<see cref="BandMult"/> atmosphere
    /// heights above the border) down to the border, brakes it. A game rule, not real drag:
    ///  - only speed OVER THE CAP is braked: climbs, ordinary orbits and anything already slow are untouched;
    ///  - the excess (v^2 - cap^2, in the planet's rotating air) decays e-fold by e-fold along the path through a
    ///    density growing toward the border: <see cref="VerticalEFolds"/> for a straight-down pass, more for a
    ///    shallow one (a longer path); what is left at the border is taken there, so you always arrive at or
    ///    under the cap and the handover to physics is seamless;
    ///  - the energy shed (J/kg) is the HEAT, cooling with <see cref="CoolSeconds"/>; past the ship's tolerance
    ///    (<see cref="ToleranceJPerKg"/>, double with forward armour) the game side damages its forward blocks.
    /// Every normal arrival in the system (1.3-1.6 km/s) comes in within tolerance at the default cap (1000 m/s)
    /// and at vanilla's (300). Airless bodies have no band: an arrival over the cap is clamped at the border, a
    /// jolt worth <see cref="AirlessJoltShare"/> of the same heat.
    /// </summary>
    public static class Reentry
    {
        /// <summary>The band's thickness above the frame's border, x the atmosphere's height: the "fake" outer atmosphere
        /// (the user, 2026-10-03: the border at the atmosphere's top, the band ~10 km over it at Verdure).</summary>
        public const double BandMult = 1.05;          // ~10 km at Verdure (atmosphere 9.5 km): the entry interface ~19.5 km up
        public const double ScaleMult = 0.53;         // the envelope's scale height (~5 km)
        public const double VerticalEFolds = 8;       // a straight-down pass keeps e^-8 of its excess
        public const double ToleranceJPerKg = 1.15e6; // 1.8 -> 1.0 km/s, or 1.55 -> 0.3 km/s
        public const double ShieldFactor = 2;         // forward heavy armour
        public const double CoolSeconds = 120;
        public const double AirlessJoltShare = 0.25;
        public const double MaxStepMetres = 250;      // the braking's path step (any warp)

        /// <summary>AEROBRAKING: the band is also a thin "fake" outer atmosphere with real drag - a = rho v^2 / (2 beta)
        /// along the air-relative velocity, beta the ship's ballistic coefficient (kg/m2: mass / drag area). Its density
        /// at the border (kg/m3), falling e-fold per the band's scale height; 0 turns drag off (the cap rule alone).
        /// 2e-3: a 3 t/m2 ship dipping to the border at 1 km/s feels ~0.3 m/s2 - a few tens of m/s a pass; a fluffy one
        /// (300 kg/m2) ten times that. Settings: Entry.DragDensity.</summary>
        public static double DragDensity = 2e-3;
        /// <summary>The ballistic coefficient assumed without a better one (kg/m2).</summary>
        public const double DefaultBeta = 3000;

        /// <summary>The fake atmosphere's density at radius r (kg/m3; 0 above the band or with drag off).</summary>
        public static double Density(Band b, double r)
            => !b.IsValid || r >= b.Top || !(DragDensity > 0) ? 0 : DragDensity * Math.Exp(-Math.Max(0, r - b.Bottom) / b.Scale);

        /// <summary>The band of a body (no atmosphere: none) whose frame's border is at bottomRadius.</summary>
        public static Band For(double radius, double atmosphereHeight, double bottomRadius)
        {
            if (!(atmosphereHeight > 0) || !(radius > 0)) return default;
            double top = bottomRadius + BandMult * atmosphereHeight;   // (on top of the frame's border)
            if (top <= bottomRadius) return default;
            double hs = ScaleMult * atmosphereHeight, d = top - bottomRadius;
            return new Band { Bottom = bottomRadius, Top = top, Scale = hs, Kappa0 = VerticalEFolds / (hs * (1 - Math.Exp(-d / hs))) };
        }

        /// <summary>The braking strength at radius r (e-folds of the excess per metre of path).</summary>
        public static double Kappa(Band b, double r)
            => !b.IsValid || r >= b.Top ? 0 : b.Kappa0 * Math.Exp(-Math.Max(0, r - b.Bottom) / b.Scale);

        /// <summary>The air's velocity at r (the planet's spin w = axis x rate): the cap is on speed through it.</summary>
        public static Vector3D AirVelocity(Vector3D r, Vector3D v, Vector3D w) => v - Vector3D.Cross(w, r);

        public static double Cool(double heat, double dt) => heat > 0 && dt > 0 ? heat * Math.Exp(-dt / CoolSeconds) : heat;

        /// <summary>A heat (J/kg) as a share of the tolerance (1 = at the limit).</summary>
        public static double Share(double heat, bool shielded) => heat / (ToleranceJPerKg * (shielded ? ShieldFactor : 1));

        /// <summary>
        /// Advances an orbit from t0 to t1 through the band, braking whatever is over the cap; returns the new
        /// elements (their epoch is where the braking ended). heat is carried and cooled; log (optional) records
        /// the pass. No band, or a periapsis above the band's top: the orbit is unchanged. Inside the band the
        /// state is integrated directly (gravity and braking, <see cref="MaxStepMetres"/> steps): a steep pass
        /// slowed from hyperbolic to elliptic goes through parabolic while nearly radial, where elements made at
        /// every step are singular; they are made once, on leaving the band or at t1.
        /// </summary>
        public static KeplerianElements Advance(KeplerianElements el, Band b, Vector3D w, double cap, double t0, double t1, ref double heat, Pass log = null, double beta = 0)
        {
            if (!(t1 > t0)) return el;
            bool drag = beta > 0 && DragDensity > 0;
            // (the cap rule only for an orbit that reaches the border - you cross it at or under the cap; a dip that stays
            //  in the band is drag's alone, and comes back out)
            bool capped = el.PeriapsisRadius < b.Bottom && CanExceed(el, b, w, cap);
            if (!b.IsValid || !(el.PeriapsisRadius < b.Top) || (!drag && !capped)) { heat = Cool(heat, t1 - t0); return el; }
            double t = t0, mu = el.Mu;
            for (int guard = 0; guard < 1000; guard++)
            {
                var s0 = OrbitPropagation.StateAt(el, t);
                double r0 = s0.Position.Length();
                if (!(r0 > 0) || double.IsNaN(r0)) break;
                if (r0 >= b.Top)
                {
                    if (t >= t1) break;
                    // Outside: on the conic, to where the band could first be reached (it cannot close faster than |v|).
                    double hout = Math.Min(t1 - t, Math.Max(0.25, (r0 - b.Top) / Math.Max(1, s0.Velocity.Length())));
                    heat = Cool(heat, hout); t += hout;
                    continue;
                }
                // Inside: integrate (velocity Verlet) until out of the band, at the border, or t1.
                Vector3D p = s0.Position, v = s0.Velocity;
                bool braked = false, atBorder = false;
                for (int i = 0; i < 200000; i++)
                {
                    double r = p.Length();
                    atBorder = r <= b.Bottom;
                    Vector3D spin = Vector3D.Cross(w, p);
                    Vector3D air = v - spin;
                    double va = air.Length();
                    double h = atBorder ? 0 : Math.Min(t1 - t, MaxStepMetres / Math.Max(1, va));
                    if (drag && h > 0 && va > 1)
                    {
                        // (drag over the step, dv/dt = -k v^2 with k = rho / (2 beta): exactly v / (1 + k v h))
                        double k = Density(b, r) / (2 * beta);
                        double va1 = va / (1 + k * va * h);
                        double shed = (va * va - va1 * va1) / 2;
                        if (shed > 0)
                        {
                            if (log != null && double.IsNaN(log.EnterTime)) { log.EnterTime = t; log.ArrivalSpeed = va; }
                            v = spin + air * (va1 / va);
                            air = v - spin;
                            heat += shed;
                            braked = true;
                            if (log != null)
                            {
                                log.Shed += shed; log.DragShed += shed; log.Dv += va - va1;
                                double decel = k * va * va;
                                if (decel > log.PeakDecel) { log.PeakDecel = decel; log.MaxQTime = t; log.MaxQRadius = r; }
                            }
                            va = va1;
                        }
                    }
                    // (the cap rule once the path reaches the border: its periapsis, from the state now, under it)
                    if (va > cap && (atBorder || h > 0) && (atBorder || PeriapsisRadius(p, v, mu) < b.Bottom))
                    {
                        if (log != null && double.IsNaN(log.EnterTime)) { log.EnterTime = t; log.ArrivalSpeed = va; }
                        double k = Kappa(b, r), e0 = va * va - cap * cap;
                        double e1 = atBorder ? 0 : e0 * Math.Exp(-k * va * h);   // the border: what is left is taken there
                        double va1 = Math.Sqrt(cap * cap + e1);
                        double shed = (va * va - va1 * va1) / 2;
                        if (log != null) log.Dv += va - va1;
                        v = spin + air * (va1 / va);
                        va = va1;
                        heat += shed;
                        braked = true;
                        if (log != null)
                        {
                            log.Shed += shed;
                            double decel = k * e0 / 2;
                            if (decel > log.PeakDecel) { log.PeakDecel = decel; log.MaxQTime = t; log.MaxQRadius = r; }
                        }
                    }
                    if (log != null)
                    {
                        if (heat > log.PeakHeat) log.PeakHeat = heat;
                        if (atBorder && double.IsNaN(log.BottomTime)) { log.BottomTime = t; log.SpeedAtBottom = va; }
                    }
                    if (atBorder || h <= 0 || r >= b.Top) break;   // (below the border the world flies it: the handover)
                    Vector3D a0 = -mu * p / (r * r * r);
                    Vector3D vh = v + a0 * (h / 2);
                    p += vh * h;
                    double r1 = p.Length();
                    v = vh + (-mu * p / (r1 * r1 * r1)) * (h / 2);
                    heat = Cool(heat, h);
                    t += h;
                }
                if (braked) el = ToElements(new StateVector(p, v), mu, t);
                if (atBorder || t >= t1) break;
                if (!braked) { var sx = OrbitPropagation.StateAt(el, t); if (sx.Position.Length() < b.Top) break; }
            }
            return el;
        }

        /// <summary>The periapsis radius of a state (m): where its conic comes closest.</summary>
        public static double PeriapsisRadius(Vector3D p, Vector3D v, double mu)
        {
            double r = p.Length(), h2 = Vector3D.Cross(p, v).LengthSquared();
            double energy = v.LengthSquared() / 2 - mu / r;
            double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / (mu * mu)));
            return h2 / (mu * (1 + e));
        }

        /// <summary>A ship's ballistic coefficient (kg/m2) from its mass and drag area (Cd x A, m2); the default when unknown.</summary>
        public static double Beta(double mass, double dragArea)
            => mass > 0 && dragArea > 1e-3 ? Math.Clamp(mass / dragArea, 30, 1e6) : DefaultBeta;

        /// <summary>Whether an orbit can be over the cap anywhere in the band (its periapsis speed, plus the spin).</summary>
        public static bool CanExceed(KeplerianElements el, Band b, Vector3D w, double cap)
        {
            double rp = Math.Max(el.PeriapsisRadius, b.Bottom);
            double vp = Math.Sqrt(Math.Max(0, el.Mu * (2 / rp - 1 / el.SemiMajorAxis)));
            return !(vp + w.Length() * b.Top <= cap);
        }

        /// <summary>
        /// Predicts the next pass (from t, up to horizon seconds): where the band starts braking, max-q, the speed
        /// at the border and the peak heat. Stops at the border, or once out of the band again after braking.
        /// </summary>
        public static Pass Predict(KeplerianElements el, Band b, Vector3D w, double cap, double t, double horizon, double heat0 = 0, double beta = 0)
        {
            var log = new Pass();
            if (!b.IsValid || !(el.PeriapsisRadius < b.Top)) return log;
            double heat = heat0, step = 2.0, end = t + horizon;
            while (t < end && double.IsNaN(log.BottomTime))
            {
                double next = Math.Min(end, t + step);
                el = Advance(el, b, w, cap, t, next, ref heat, log, beta);
                t = next;
                var s = OrbitPropagation.StateAt(el, t);
                double r = s.Position.Length();
                if (log.Braked && r >= b.Top) { log.After = el; log.HasAfter = true; log.ExitTime = t; break; }   // through and out again (aerobraking)
                // Far outside: jump toward the band (it cannot be reached sooner than (r - top) / |v|).
                step = r > b.Top ? Math.Max(2.0, (r - b.Top) / Math.Max(1, s.Velocity.Length())) : 2.0;
            }
            log.Landing = !double.IsNaN(log.BottomTime);
            return log;
        }

        /// <summary>An airless body: an arrival over the cap is clamped at the border; the heat-equivalent of the jolt.</summary>
        public static double Jolt(double airSpeed, double cap)
            => airSpeed > cap ? AirlessJoltShare * (airSpeed * airSpeed - cap * cap) / 2 : 0;

        /// <summary>Elements of a state; a purely radial state is nudged (zero angular momentum has no plane).</summary>
        public static KeplerianElements ToElements(StateVector s, double mu, double t)
        {
            Vector3D r = s.Position, v = s.Velocity;
            double rm = r.Length(), vm = v.Length();
            if (rm > 1 && Vector3D.Cross(r, v).Length() < 1e-6 * rm * Math.Max(vm, 1.0))
            {
                Vector3D radial = r / rm;
                Vector3D axis = Math.Abs(radial.Z) < 0.9 ? Vector3D.UnitZ : Vector3D.UnitX;
                v += Vector3D.Normalize(Vector3D.Cross(axis, radial)) * Math.Max(0.01, 1e-5 * vm);
            }
            // Never exactly parabolic (a -> infinity): a hair under escape speed instead.
            double energy = v.LengthSquared() / 2 - mu / rm;
            if (rm > 1 && Math.Abs(energy) < 1e-6 * mu / rm) v *= Math.Sqrt(Math.Max(0, (2 * mu / rm) * (1 - 2e-6)) / Math.Max(1e-9, v.LengthSquared()));
            return OrbitalMath.ToElements(new StateVector(r, v), mu, t);
        }
    }
}
