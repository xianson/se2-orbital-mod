using SEAerospace.Entry;
using SEAerospace.Orbital;

namespace EntryTest
{
    /// <summary>
    /// Offline checks of reentry on rails (Core/Entry/Reentry). Verdure's numbers (radius 63 km, atmosphere
    /// 9.45 km, mu 7.785e10, the frame's border 15.1 km up): passes straight down, shallow, grazing, warped,
    /// in a spinning atmosphere, at the default cap and at vanilla's. No game; exit 0 = all pass.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool cond, string detail = "")
        {
            if (cond) { _passed++; Console.WriteLine("   PASS " + name + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            else { _failed++; Console.WriteLine("   FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }

        const double R = 63000, H = 9450, Mu = 7.785e10, Cap = 1000;
        static readonly double Border = R + 1.6 * H;
        static readonly Band B = Reentry.For(R, H, Border);
        static readonly Vector3D NoSpin = Vector3D.Zero;

        /// <summary>A state at radius r with speed v, flight path angle gamma (degrees below the horizon).</summary>
        static KeplerianElements At(double r, double v, double gammaDeg, double t = 0)
        {
            double g = gammaDeg * Math.PI / 180;
            var pos = new Vector3D(r, 0, 0);
            var vel = new Vector3D(-v * Math.Sin(g), v * Math.Cos(g), 0);
            return Reentry.ToElements(new StateVector(pos, vel), Mu, t);
        }
        static double Speed(KeplerianElements el, double t) => OrbitPropagation.StateAt(el, t).Velocity.Length();
        static double Radius(KeplerianElements el, double t) => OrbitPropagation.StateAt(el, t).Position.Length();

        /// <summary>Flies el from t0 in steps of dt until the border (or out of the band), as the game's ticks would.</summary>
        static (KeplerianElements el, double t, double heat, Pass log) Fly(KeplerianElements el, double dt, double cap = Cap, Vector3D? w = null, double maxT = 4000)
        {
            double t = 0, heat = 0; var log = new Pass();
            while (t < maxT)
            {
                el = Reentry.Advance(el, B, w ?? NoSpin, cap, t, t + dt, ref heat, log);
                t += dt;
                double r = Radius(el, t);
                if (!double.IsNaN(log.BottomTime) || r <= B.Bottom) break;
                if (log.Braked && r > B.Top) break;
            }
            return (el, t, heat, log);
        }

        static int Main()
        {
            Console.WriteLine("Entry (Reentry) offline tests");

            // Geometry: the entry interface ~35 km up, the border ~15 km up, max-q in between.
            Ok("band: entry interface ~35 km up", Math.Abs(B.Top - R - 35000) < 500, $"{(B.Top - R) / 1000:F1} km");
            Ok("band: border ~15 km up", Math.Abs(B.Bottom - R - 15100) < 200, $"{(B.Bottom - R) / 1000:F1} km");
            Ok("band: none without an atmosphere", !Reentry.For(R, 0, Border).IsValid);
            Ok("border: circular orbit there is about the cap", Math.Abs(Math.Sqrt(Mu / B.Bottom) - Cap) < 30, $"{Math.Sqrt(Mu / B.Bottom):F0} m/s");

            // Steep (70 deg) at 1600 m/s from just above the band. (A truly vertical entry, every sideways speed
            // cancelled, is both radial and near parabolic, where the core's element propagation loses precision.)
            {
                var (el, t, heat, log) = Fly(At(B.Top + 2000, 1600, 70), 1.0 / 60);
                double vb = log.SpeedAtBottom;
                Ok("steep 1600: at the border at or under the cap", vb <= Cap + 0.5, $"{vb:F1} m/s");
                double maxq = (log.MaxQRadius - R) / 1000;
                Ok("steep 1600: max-q 20-30 km up", maxq > 20 && maxq < 30, $"{maxq:F1} km, {log.PeakDecel / 9.81:F1} g");
                Ok("steep 1600: heat within tolerance", Reentry.Share(log.PeakHeat, false) < 1, $"{Reentry.Share(log.PeakHeat, false):P0}");
            }

            // A typical transfer arrival, shallow (5 deg), at 1450 m/s at the entry interface.
            {
                var (el, t, heat, log) = Fly(At(B.Top + 500, 1450, 5), 1.0 / 60);
                Ok("shallow 1450: braked", log.Braked, $"shed {log.Shed / 1000:F0} kJ/kg");
                Ok("shallow 1450: within tolerance (no damage)", Reentry.Share(log.PeakHeat, false) < 0.6, $"{Reentry.Share(log.PeakHeat, false):P0}");
                bool atBorder = !double.IsNaN(log.SpeedAtBottom);
                Ok("shallow 1450: at the border under the cap, or through and out slower (aerobraking)",
                    atBorder ? log.SpeedAtBottom <= Cap + 0.5 : Radius(el, t) > B.Top && log.Shed > 0, atBorder ? $"{log.SpeedAtBottom:F0} m/s at the border" : $"out again, e={el.Eccentricity:F3}");
            }

            // Fast and steep: over the tolerance (damage), never a ship destroyed by the rule itself.
            {
                var (el, t, heat, log) = Fly(At(B.Top + 500, 2300, 30), 1.0 / 60);
                Ok("steep 2300: over the tolerance", Reentry.Share(log.PeakHeat, false) > 1, $"{Reentry.Share(log.PeakHeat, false):P0}");
                Ok("steep 2300: forward armour halves it", Reentry.Share(log.PeakHeat, true) < Reentry.Share(log.PeakHeat, false) * 0.51);
                Ok("steep 2300: still at the border under the cap", log.SpeedAtBottom <= Cap + 0.5, $"{log.SpeedAtBottom:F1} m/s");
            }

            // Aerobraking: a grazing pass (periapsis in the band's upper half) loses speed and leaves again.
            {
                double rp = B.Top - 6000, va = 1500;
                double vp = Math.Sqrt(va * va + 2 * Mu / rp - 2 * Mu / (B.Top + 20000)); // speed at periapsis from 1500 at 55 km up
                var el0 = Reentry.ToElements(new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, vp, 0)), Mu, 0);
                el0 = OrbitPropagation.AtTime(el0, 0);
                var start = OrbitPropagation.StateAt(el0, -200);
                var e = Reentry.ToElements(start, Mu, 0);
                var (el, t, heat, log) = Fly(e, 1.0 / 60);
                Ok("graze: braked and back out of the band", log.Braked && Radius(el, t) > B.Top, $"shed {log.Shed / 1000:F0} kJ/kg");
                Ok("graze: energy lost (aerobraking)", el.SpecificEnergy < el0.SpecificEnergy, $"{el0.SpecificEnergy / 1e3:F0} -> {el.SpecificEnergy / 1e3:F0} kJ/kg");
                Ok("graze: never below the border", double.IsNaN(log.BottomTime));
            }

            // Only what is over the cap: an orbit under the cap in the band is untouched.
            {
                var el0 = At(B.Bottom + 8000, 960, 0);
                double heat = 0; var log = new Pass();
                var el = Reentry.Advance(el0, B, NoSpin, Cap, 0, 600, ref heat, log);
                Ok("under the cap: unchanged", !log.Braked && Math.Abs(el.SemiMajorAxis - el0.SemiMajorAxis) < 1e-6);
                var high = At(B.Top + 20000, 1500, 0);
                double h2 = 0; var l2 = new Pass();
                Reentry.Advance(high, B, NoSpin, Cap, 0, 600, ref h2, l2);
                Ok("periapsis above the band: unchanged", !l2.Braked);
            }

            // Vanilla's cap (300): the same arrival sheds far more, still within tolerance at 1.45 km/s.
            {
                var (el, t, heat, log) = Fly(At(B.Top + 500, 1450, 10), 1.0 / 60, cap: 300);
                Ok("vanilla cap: at the border at or under 300", log.SpeedAtBottom <= 300.5, $"{log.SpeedAtBottom:F1} m/s");
                Ok("vanilla cap: 1450 within tolerance", Reentry.Share(log.PeakHeat, false) < 1, $"{Reentry.Share(log.PeakHeat, false):P0}");
                // a low orbit faster than 300 decays
                var lo = At(B.Bottom + 12000, Math.Sqrt(Mu / (B.Bottom + 12000)), 0);
                double h = 0; var l = new Pass();
                var after = Reentry.Advance(lo, B, NoSpin, 300, 0, 5, ref h, l);
                Ok("vanilla cap: a low orbit in the band decays", after.SemiMajorAxis < lo.SemiMajorAxis);
            }

            // Warp: the same pass at 1/60 s ticks and at 30 s ticks ends the same.
            {
                var e0 = At(B.Top + 30000, 1500, 50);
                var a = Fly(e0, 1.0 / 60); var b = Fly(e0, 30);
                Ok("warp: same speed at the border", Math.Abs(a.log.SpeedAtBottom - b.log.SpeedAtBottom) < 2, $"{a.log.SpeedAtBottom:F1} vs {b.log.SpeedAtBottom:F1}");
                Ok("warp: same shed", Math.Abs(a.log.Shed - b.log.Shed) < 0.02 * a.log.Shed, $"{a.log.Shed / 1e3:F0} vs {b.log.Shed / 1e3:F0} kJ/kg");
            }

            // The planet's spin: the cap is on the speed through its air.
            {
                var w = new Vector3D(0, 0, 2 * Math.PI / 6000);  // a fast spin (6000 s day: ~80 m/s at the border), prograde
                var e0 = At(B.Top + 500, 1450, 30);
                var still = Fly(e0, 1.0 / 60); var spun = Fly(e0, 1.0 / 60, w: w);
                Ok("spin: a prograde arrival sheds less", spun.log.Shed < still.log.Shed, $"{spun.log.Shed / 1e3:F0} < {still.log.Shed / 1e3:F0} kJ/kg");
                Ok("spin: at the border at or under the cap through the air", spun.log.SpeedAtBottom <= Cap + 0.5, $"{spun.log.SpeedAtBottom:F1}");
            }

            // Prediction matches the flight.
            {
                var e0 = At(B.Top + 40000, 1500, 50);
                var fly = Fly(e0, 1.0 / 60);
                var p = Reentry.Predict(e0, B, NoSpin, Cap, 0, 4000);
                Ok("predict: same speed at the border", Math.Abs(p.SpeedAtBottom - fly.log.SpeedAtBottom) < 2, $"{p.SpeedAtBottom:F1} vs {fly.log.SpeedAtBottom:F1}");
                Ok("predict: entry time", Math.Abs(p.EnterTime - fly.log.EnterTime) < 3, $"{p.EnterTime:F1} vs {fly.log.EnterTime:F1} s");
                Ok("predict: peak heat", Math.Abs(p.PeakHeat - fly.log.PeakHeat) < 0.03 * fly.log.PeakHeat);
                Ok("predict: max-q time", Math.Abs(p.MaxQTime - fly.log.MaxQTime) < 3);
            }

            // Cooling, and the airless jolt.
            Ok("cooling: half in ~83 s", Math.Abs(Reentry.Cool(1, 83.18) - 0.5) < 0.01);
            Ok("airless jolt: a quarter of the heat", Math.Abs(Reentry.Jolt(1300, 1000) - 0.25 * (1300.0 * 1300 - 1e6) / 2) < 1);
            Ok("airless jolt: none under the cap", Reentry.Jolt(900, 1000) == 0);

            Console.WriteLine($"== {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
