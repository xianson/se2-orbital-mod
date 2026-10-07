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
        static readonly double Border = R + 1.0 * H;   // (the planet frame's border: the atmosphere's top)
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
        static (KeplerianElements el, double t, double heat, Pass log) Fly(KeplerianElements el, double dt, double cap = Cap, Vector3D? w = null, double maxT = 4000, double beta = 0)
        {
            double t = 0, heat = 0; var log = new Pass();
            while (t < maxT)
            {
                el = Reentry.Advance(el, B, w ?? NoSpin, cap, t, t + dt, ref heat, log, beta);
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

            // Geometry: the border at the atmosphere's top (9.45 km), the band ~10 km over it (the entry interface ~19.4 km).
            Ok("band: entry interface ~19.4 km up", Math.Abs(B.Top - R - 19370) < 300, $"{(B.Top - R) / 1000:F1} km");
            Ok("band: border at the atmosphere's top", Math.Abs(B.Bottom - R - H) < 1, $"{(B.Bottom - R) / 1000:F2} km");
            Ok("band: none without an atmosphere", !Reentry.For(R, 0, Border).IsValid);
            {
                // the border's density: the body's own (aero's air there) when known, else the setting - no step at the handover
                var bb = B; bb.Rho0 = 6e-3;
                Ok("drag density at the border = the body's (aero's) when known", Math.Abs(Reentry.Density(bb, bb.Bottom) - 6e-3) < 1e-12, $"{Reentry.Density(bb, bb.Bottom):E2}");
                Ok("drag density at the border = the setting when not", Math.Abs(Reentry.Density(B, B.Bottom) - Reentry.DragDensity) < 1e-12);
                Ok("drag density falls off with height the same either way", Math.Abs(Reentry.Density(bb, bb.Bottom + bb.Scale) / Reentry.Density(bb, bb.Bottom) - Math.Exp(-1)) < 1e-9);
            }
            Ok("border: circular orbit there is about the cap", Math.Abs(Math.Sqrt(Mu / B.Bottom) - Cap) < 60, $"{Math.Sqrt(Mu / B.Bottom):F0} m/s");

            // Steep (70 deg) at 1600 m/s from just above the band. (A truly vertical entry, every sideways speed
            // cancelled, is both radial and near parabolic, where the core's element propagation loses precision.)
            {
                var (el, t, heat, log) = Fly(At(B.Top + 2000, 1600, 70), 1.0 / 60);
                double vb = log.SpeedAtBottom;
                Ok("steep 1600: at the border at or under the cap", vb <= Cap + 0.5, $"{vb:F1} m/s");
                double maxq = (log.MaxQRadius - R) / 1000;
                Ok("steep 1600: max-q inside the band", log.MaxQRadius >= B.Bottom && log.MaxQRadius <= B.Top + 1, $"{maxq:F1} km, {log.PeakDecel / 9.81:F1} g");
                Ok("steep 1600: heat within tolerance", Reentry.Share(log.PeakHeat, false) < 1, $"{Reentry.Share(log.PeakHeat, false):P0}");
            }

            // A typical transfer arrival, shallow (5 deg), at 1450 m/s at the entry interface: its periapsis is in the band,
            // so drag (an ordinary ship, the default ballistic coefficient) brakes it, not the cap rule.
            {
                var (el, t, heat, log) = Fly(At(B.Top + 500, 1450, 5), 1.0 / 60, beta: Reentry.DefaultBeta);
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
                double rp = B.Top - 0.1 * (B.Top - B.Bottom), va = 1500;
                double vp = Math.Sqrt(va * va + 2 * Mu / rp - 2 * Mu / (B.Top + 20000)); // speed at periapsis from 1500 at 55 km up
                var el0 = Reentry.ToElements(new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, vp, 0)), Mu, 0);
                el0 = OrbitPropagation.AtTime(el0, 0);
                var start = OrbitPropagation.StateAt(el0, -200);
                var e = Reentry.ToElements(start, Mu, 0);
                var (el, t, heat, log) = Fly(e, 1.0 / 60);
                // (the cap rule is for crossing the border: a graze whose periapsis stays in the band is not capped - with
                //  no drag it is untouched, and comes back out)
                Ok("graze (no drag): not capped - its periapsis is above the border", !log.Braked && Radius(el, t) > B.Top, $"shed {log.Shed / 1000:F0} kJ/kg");
                var (elG, tG, _, logG) = Fly(e, 1.0 / 60, beta: 300);
                Ok("graze (drag): braked and back out of the band", logG.Braked && Radius(elG, tG) > B.Top && double.IsNaN(logG.BottomTime), $"dv {logG.Dv:F1} m/s");
            }

            // AEROBRAKING (drag in the band): an elliptic orbit dipping into the band, under no cap.
            {
                const double NoCap = 1e9, Fluffy = 300;
                double rp = 0.5 * (B.Bottom + B.Top), ra = R + 200000, a = 0.5 * (rp + ra);
                double vp = Math.Sqrt(Mu * (2 / rp - 1 / a));
                var atPe = OrbitPropagation.AtTime(Reentry.ToElements(new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, vp, 0)), Mu, 0), 0);
                var e0 = Reentry.ToElements(OrbitPropagation.StateAt(atPe, -300), Mu, 0);
                var (el, t, heat, log) = Fly(e0, 1.0, cap: NoCap, beta: Fluffy);
                double ap0 = atPe.SemiMajorAxis * (1 + atPe.Eccentricity), ap1 = el.SemiMajorAxis * (1 + el.Eccentricity);
                Ok("aerobrake: through and out again", log.Braked && Radius(el, t) > B.Top && double.IsNaN(log.BottomTime));
                Ok("aerobrake: apoapsis lowered", ap1 < ap0 - 1000, $"Ap {(ap0 - R) / 1000:F0} -> {(ap1 - R) / 1000:F0} km, dv {log.Dv:F1} m/s");
                Ok("aerobrake: all of it drag", Math.Abs(log.DragShed - log.Shed) < 1e-6 * Math.Max(1, log.Shed));
                // a denser ship (10x the ballistic coefficient) loses about a tenth
                var (elD, tD, _, logD) = Fly(e0, 1.0, cap: NoCap, beta: Fluffy * 10);
                Ok("aerobrake: 10x beta, ~1/10 the dv", Math.Abs(logD.Dv / log.Dv - 0.1) < 0.03, $"{logD.Dv:F2} vs {log.Dv:F2} m/s");
                // the prediction is the flight
                var pr = Reentry.Predict(e0, B, NoSpin, NoCap, 0, 4000, 0, Fluffy);
                double apP = pr.After.SemiMajorAxis * (1 + pr.After.Eccentricity);
                Ok("aerobrake: predicted orbit after the pass", pr.HasAfter && Math.Abs(apP - ap1) < 0.01 * (ap0 - ap1) + 50, $"Ap {(apP - R) / 1000:F1} vs flown {(ap1 - R) / 1000:F1} km");
                Ok("aerobrake: predicted dv", Math.Abs(pr.Dv - log.Dv) < 0.02 * log.Dv + 0.05, $"{pr.Dv:F2} vs {log.Dv:F2} m/s");
                // drag off: the same pass untouched
                double keep = Reentry.DragDensity; Reentry.DragDensity = 0;
                double h0 = 0; var l0 = new Pass();
                var same = Reentry.Advance(e0, B, NoSpin, NoCap, 0, 1200, ref h0, l0, Fluffy);
                Reentry.DragDensity = keep;
                Ok("aerobrake: drag off, unchanged", !l0.Braked && Math.Abs(same.SemiMajorAxis - e0.SemiMajorAxis) < 1e-6);
            }

            // Periapsis from a state (the cap rule's gate): an ellipse, a hyperbola, a near-radial fall.
            {
                double rp = B.Bottom + 3000, ra = R + 300000, a = 0.5 * (rp + ra);
                var pe = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, Math.Sqrt(Mu * (2 / rp - 1 / a)), 0));
                var elE = OrbitPropagation.AtTime(Reentry.ToElements(pe, Mu, 0), 0);
                var sE = OrbitPropagation.StateAt(elE, 400);
                Ok("periapsis from state: ellipse", Math.Abs(Reentry.PeriapsisRadius(sE.Position, sE.Velocity, Mu) - rp) < 5, $"{Reentry.PeriapsisRadius(sE.Position, sE.Velocity, Mu) - rp:F2} m");
                var hp = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, 1.3 * Math.Sqrt(2 * Mu / rp), 0));
                var elH = OrbitPropagation.AtTime(Reentry.ToElements(hp, Mu, 0), 0);
                var sH = OrbitPropagation.StateAt(elH, -300);
                Ok("periapsis from state: hyperbola", Math.Abs(Reentry.PeriapsisRadius(sH.Position, sH.Velocity, Mu) - rp) < 5, $"{Reentry.PeriapsisRadius(sH.Position, sH.Velocity, Mu) - rp:F2} m");
                double rr = Reentry.PeriapsisRadius(new Vector3D(R + 50000, 0, 0), new Vector3D(-1500, 1e-3, 0), Mu);
                Ok("periapsis from state: near radial ~0 (no NaN)", !double.IsNaN(rr) && rr >= 0 && rr < 1, $"{rr:E2} m");
            }

            // Drag alone captures a hyperbolic arrival (a deep dip, no cap): the orbit after the pass is bound.
            {
                const double NoCap = 1e9;
                double rp = B.Bottom + 0.25 * (B.Top - B.Bottom);
                var hp = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, 1.02 * Math.Sqrt(2 * Mu / rp), 0));
                var at = OrbitPropagation.AtTime(Reentry.ToElements(hp, Mu, 0), 0);
                var e0 = Reentry.ToElements(OrbitPropagation.StateAt(at, -400), Mu, 0);
                // (the lightest ship that still skips out: scan the ballistic coefficient up from very draggy)
                Pass pr = null; double used = 0;
                foreach (double beta in new[] { 30.0, 60, 100, 150, 250, 400, 700, 1000 })
                {
                    var p1 = Reentry.Predict(e0, B, NoSpin, NoCap, 0, 4000, 0, beta);
                    if (p1.HasAfter) { pr = p1; used = beta; break; }
                }
                Ok("aerocapture: hyperbolic in, bound out", e0.Eccentricity > 1 && pr != null && pr.After.Eccentricity < 1, $"e {e0.Eccentricity:F3} -> {(pr != null ? pr.After.Eccentricity.ToString("F3") : "none")} at beta {used}");
            }

            // An orbit inside the band all the way round: the prediction ends in the air (landing), not "after".
            {
                double rc = B.Bottom + 0.4 * (B.Top - B.Bottom);
                var c0 = At(rc, Math.Sqrt(Mu / rc), 0);
                var pr = Reentry.Predict(c0, B, NoSpin, 2000, 0, 6 * 3600, 0, 30);
                Ok("inside the band: the prediction lands", pr.Landing && !pr.HasAfter, $"landing {pr.Landing}, after {pr.HasAfter}");
            }

            // Decay: a circular orbit inside the band, under the cap, sinks with drag (and stays put without it).
            {
                double rc = 0.5 * (B.Bottom + B.Top);
                var c0 = At(rc, Math.Sqrt(Mu / rc), 0);
                double h1 = 0; var l1 = new Pass();
                var c1 = Reentry.Advance(c0, B, NoSpin, 2000, 0, 600, ref h1, l1, 300);
                Ok("decay: a low orbit sinks with drag", c1.SemiMajorAxis < c0.SemiMajorAxis - 1, $"{(c0.SemiMajorAxis - c1.SemiMajorAxis):F0} m in 600 s");
                double h2 = 0; var l2 = new Pass();
                var above = At(B.Top + 5000, Math.Sqrt(Mu / (B.Top + 5000)), 0);
                var a2 = Reentry.Advance(above, B, NoSpin, 2000, 0, 600, ref h2, l2, 300);
                Ok("decay: an orbit above the band is untouched", !l2.Braked && Math.Abs(a2.SemiMajorAxis - above.SemiMajorAxis) < 1e-6);
            }

            // Only what is over the cap: an orbit under the cap in the band is untouched.
            {
                var el0 = At(B.Bottom + 8000, 960, 0);
                double heat = 0; var log = new Pass();
                var el = Reentry.Advance(el0, B, NoSpin, Cap + 150, 0, 600, ref heat, log);   // (the border's circular speed is ~1036 now: a cap over it)
                Ok("under the cap: unchanged", !log.Braked && Math.Abs(el.SemiMajorAxis - el0.SemiMajorAxis) < 1e-6);
                var high = At(B.Top + 20000, 1500, 0);
                double h2 = 0; var l2 = new Pass();
                Reentry.Advance(high, B, NoSpin, Cap, 0, 600, ref h2, l2);
                Ok("periapsis above the band: unchanged", !l2.Braked);
            }

            // Vanilla's cap (300): the same arrival sheds far more, still within tolerance at 1.45 km/s.
            {
                var (el, t, heat, log) = Fly(At(B.Top + 500, 1450, 30), 1.0 / 60, cap: 300);   // (steep enough to reach the border)
                Ok("vanilla cap: at the border at or under 300", log.SpeedAtBottom <= 300.5, $"{log.SpeedAtBottom:F1} m/s");
                Ok("vanilla cap: 1450 within tolerance", Reentry.Share(log.PeakHeat, false) < 1, $"{Reentry.Share(log.PeakHeat, false):P0}");
                // a low orbit faster than 300 decays
                double rLo = 0.5 * (B.Bottom + B.Top);
                var lo = At(rLo, Math.Sqrt(Mu / rLo), 0);
                double h = 0; var l = new Pass();
                var after = Reentry.Advance(lo, B, NoSpin, 300, 0, 5, ref h, l, Reentry.DefaultBeta);
                Ok("vanilla cap: a low orbit in the band decays (drag)", after.SemiMajorAxis < lo.SemiMajorAxis);
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
