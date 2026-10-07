using System;
using System.Collections.Generic;
using SEAerospace.Orbital;
using SEAerospace.Frames;

namespace PlayerOrbitTest
{
    // Rendezvous, from every seat (Core/Frames/RendezvousPlot, PlayerOrbit): the anchor's pilot, a pilot in another ship
    // of the frame 5 km ahead, a walker 2 km out; targets ahead on the same orbit, lower and catching up, tilted, round a
    // moon, round another planet, the frame you are in. Each answer against a truth built here another way.
    internal static class RendezvousCases
    {
        static void Ok(string n, bool c, string d = "") => Program.Check(n, c, d);

        const double MuV = 7.785e10, RV = 63000, Alt = 300000, R0 = RV + Alt;
        static StateVector Circ(double r, double phase, double mu, double inclRad = 0)
        {
            double v = Math.Sqrt(mu / r);
            var p = new Vector3D(r * Math.Cos(phase), r * Math.Sin(phase), 0);
            var u = new Vector3D(-v * Math.Sin(phase), v * Math.Cos(phase), 0);
            // tilt about the x axis (the line of nodes through phase 0)
            double c = Math.Cos(inclRad), s = Math.Sin(inclRad);
            return new StateVector(new Vector3D(p.X, p.Y * c, p.Y * s), new Vector3D(u.X, u.Y * c, u.Y * s));
        }
        static KeplerianElements El(StateVector s, double mu, double t = 0) => OrbitalMath.ToElements(s, mu, t);
        static StateVector At(KeplerianElements e, double t) => OrbitPropagation.StateAt(e, t);

        // the seats: who is where in frame F (centre on a 300 km circular orbit, phase 0; inertial berth: world axes = celestial)
        struct Seat { public string Name; public Vector3D Off, Vel; public bool InAnchor; }
        static readonly Seat[] Seats =
        {
            new Seat { Name = "the anchor's pilot", Off = new Vector3D(0, 0, 0), Vel = Vector3D.Zero, InAnchor = true },
            new Seat { Name = "a pilot in another ship 5 km ahead", Off = new Vector3D(0, 5000, 0), Vel = new Vector3D(0, 0, 0) },
            new Seat { Name = "a walker 2 km out, drifting 1 m/s", Off = new Vector3D(2000, 0, 0), Vel = new Vector3D(0, 0, 1) },
        };

        public static void Run()
        {
            Console.WriteLine("\n=== rendezvous, from every seat ===");
            CurvilinearAxes();
            var frameEl = El(Circ(R0, 0, MuV), MuV);
            var f = new ProximityFrame { Id = 59, ParentBodyName = "Verdure", AnchorEntityId = 2, BerthCenter = new Vector3D(0, 5556700, 0), Elements = frameEl };
            f.Members.AddRange(new long[] { 2, 3 });

            foreach (var seat in Seats)
            {
                // what the game does: the seat's result -> own elements (the anchor's pilot: the frame's)
                var own = new RendezvousPlot.OwnOrbit();
                var mineEl = seat.InAnchor ? frameEl : own.Get(f.Id, frameEl, seat.Off, seat.Vel, 0);
                var truth = new StateVector(At(frameEl, 0).Position + seat.Off, At(frameEl, 0).Velocity + seat.Vel);
                var m0 = At(mineEl, 0);
                Ok($"{seat.Name}: own orbit passes through them now", (m0.Position - truth.Position).Length() < 0.01 && (m0.Velocity - truth.Velocity).Length() < 1e-5);

                // the anchor plot now: their offset in the frame, along / radial / cross
                var q = RendezvousPlot.Curvilinear(At(frameEl, 0), m0);
                double alongT = R0 * Math.Atan2(seat.Off.Y, R0 + seat.Off.X), radT = Math.Sqrt((R0 + seat.Off.X) * (R0 + seat.Off.X) + seat.Off.Y * seat.Off.Y) - R0;
                Ok($"{seat.Name}: the anchor plot puts them where they are", Math.Abs(q.X - alongT) < 0.01 && Math.Abs(q.Y - radT) < 0.01 && Math.Abs(q.Z - seat.Off.Z) < 0.01,
                   $"along {q.X:F1} m, radial {q.Y:F1} m");

                // a target 30 km ahead on the frame's own orbit: the plot 'now' about it, from them
                double th = 30000 / R0;
                var tEl = El(Circ(R0, th, MuV), MuV);
                var qt = RendezvousPlot.Curvilinear(At(tEl, 0), m0);
                double expAlong = R0 * (Math.Atan2(truth.Position.Y, truth.Position.X) - th), expRad = truth.Position.Length() - R0;
                Ok($"{seat.Name}: target 30 km ahead - the target plot from them", Math.Abs(qt.X - expAlong) < 0.5 && Math.Abs(qt.Y - expRad) < 0.5,
                   $"along {qt.X / 1000:F2} km (expected {expAlong / 1000:F2}), radial {qt.Y:F0} m");
                var qFrame = RendezvousPlot.Curvilinear(At(tEl, 0), At(frameEl, 0));
                if (!seat.InAnchor && seat.Off.Length() > 100)
                    Ok($"{seat.Name}: (planned from the frame's centre it would be off by {(qFrame - qt).Length() / 1000:F1} km)", (qFrame - qt).Length() > 1000);

                // a target 1 km lower, 20 km behind: it catches up (lower is faster) and passes under - the closest approach over 3 periods,
                // against a dense search of their own motion
                var low = El(Circ(R0 - 1000, -20000 / R0, MuV), MuV);
                double P = 2 * Math.PI * Math.Sqrt(R0 * R0 * R0 / MuV);
                var ca = RendezvousPlot.Closest(tk => At(mineEl, tk), tk => At(low, tk), 0, 3 * P);
                double bd = double.MaxValue, bt = 0;
                for (int k = 0; k <= 200000; k++) { double tk = 3 * P * k / 200000; double d = (At(mineEl, tk).Position - At(low, tk).Position).Length(); if (d < bd) { bd = d; bt = tk; } }
                Ok($"{seat.Name}: closest approach to a lower target catching up = the dense search", ca.Ok && bt > 1000 && Math.Abs(ca.Distance - bd) < 2 && Math.Abs(ca.T - bt) < 30,
                   $"{ca.Distance / 1000:F3} km at {ca.T:F0} s (dense {bd / 1000:F3} km at {bt:F0} s), rel {ca.RelSpeed:F2} m/s");

                // a target on a tilted orbit (0.5 degrees) crossing theirs at the node now
                var tilt = El(Circ(R0, 0.0, MuV, 0.5 * Math.PI / 180), MuV);
                var qx = RendezvousPlot.Curvilinear(At(tilt, P / 4), At(mineEl, P / 4));
                double expCross = -R0 * Math.Sin(0.5 * Math.PI / 180);   // a quarter period on: the target is above its node by r sin i; they are below its plane
                if (seat.Off.Length() < 1) Ok($"{seat.Name}: a tilted target a quarter orbit on - out of its plane by r·sin i", Math.Abs(qx.Z - expCross) < 50, $"cross {qx.Z:F0} m (expected {expCross:F0})");
            }

            EllipticAndStation();
            CommonBodies();
            PlotChoice();
            OwnOrbitCache();
            Degenerate();
        }

        static void EllipticAndStation()
        {
            // an elliptic anchor (300 x 600 km): you on the same orbit 60 s behind - the plot closes over its period,
            // and its along-track gap is the angle between you times the anchor's radius (largest at periapsis)
            double rp = RV + 300000, ra = RV + 600000, a = 0.5 * (rp + ra), e = (ra - rp) / (ra + rp);
            var peri = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, Math.Sqrt(MuV * (2 / rp - 1 / a)), 0));
            var anc = El(peri, MuV);
            var you = El(At(anc, -60), MuV, 0);   // (the same orbit, 60 s back)
            double P = 2 * Math.PI * Math.Sqrt(a * a * a / MuV);
            var q0 = RendezvousPlot.Curvilinear(At(anc, 0), At(you, 0));
            var qP = RendezvousPlot.Curvilinear(At(anc, P), At(you, P));
            Ok("elliptic anchor: the same orbit 60 s behind - the plot closes over its period", (qP - q0).Length() < 1, $"{(qP - q0).Length():F3} m");
            double worst = 0;
            for (int k = 0; k < 24; k++)
            {
                double tk = P * k / 24;
                var A = At(anc, tk); var Y = At(you, tk);
                double ang = Math.Atan2(Y.Position.Y, Y.Position.X) - Math.Atan2(A.Position.Y, A.Position.X);
                while (ang > Math.PI) ang -= 2 * Math.PI; while (ang < -Math.PI) ang += 2 * Math.PI;
                var q = RendezvousPlot.Curvilinear(A, Y);
                worst = Math.Max(worst, Math.Abs(q.X - ang * A.Position.Length()));
            }
            Ok("elliptic anchor: along-track = the angle between you × its radius, all round", worst < 0.01, $"worst {worst:E1} m");
            var qa = RendezvousPlot.Curvilinear(At(anc, P / 2), At(you, P / 2));
            Ok("elliptic anchor: the gap is largest at periapsis, smallest at apoapsis", Math.Abs(q0.X) > Math.Abs(qa.X) * 1.5, $"peri {q0.X / 1000:F2} km, apo {qa.X / 1000:F2} km");
            Ok("period of an ellipse's state = its own (not a circle's at its radius)", Math.Abs(RendezvousPlot.PeriodAbout(At(anc, 1234), MuV, 0) - P) < 1e-6 * P,
               $"{RendezvousPlot.PeriodAbout(At(anc, 1234), MuV, 0):F1} s vs {P:F1} s; a circle's at r would be {2 * Math.PI * Math.Sqrt(Math.Pow(At(anc, 1234).Position.Length(), 3) / MuV):F1} s");
            var esc = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, 2 * Math.Sqrt(MuV / rp), 0));
            Ok("period of an escape: the fallback", RendezvousPlot.PeriodAbout(esc, MuV, 3600) == 3600);

            // a late burn: 20 m/s prograde planned at T, half of it flown; asked a quarter orbit later what is left
            {
                var before = El(Circ(R0, 0, MuV), MuV);
                var atT = At(before, 0);
                Vector3D pro = Vector3D.Normalize(atT.Velocity);
                var target = El(new StateVector(atT.Position, atT.Velocity + pro * 20), MuV);
                var halfFlown = El(new StateVector(atT.Position, atT.Velocity + pro * 10), MuV);
                double Pc = 2 * Math.PI * Math.Sqrt(R0 * R0 * R0 / MuV), late = Pc / 4;
                var left = RendezvousPlot.RemainingBurn(halfFlown, target, 0);
                var naive = At(target, late).Velocity - At(halfFlown, late).Velocity;
                Ok("a late burn: what is left = the unflown 10 m/s (at the node point)", Math.Abs(left.Length() - 10) < 1e-6, $"{left.Length():F3} m/s");
                Ok("a late burn: (compared now, a quarter orbit late, it read differently)", Math.Abs(naive.Length() - 10) > 0.5, $"{naive.Length():F2} m/s");
                var late2 = RendezvousPlot.RemainingBurn(halfFlown, target, 0);
                Ok("a late burn: the same answer however late (it converges)", (late2 - left).Length() < 1e-9);
            }

            // a new anchor elected 12 km from the berth's centre (the old one destroyed): the orbit rebased onto it, every
            // member re-pinned by -12 km - each stays where it was in space
            {
                var fe = El(Circ(R0, 0, MuV), MuV);
                var fr = new ProximityFrame { Id = 70, Elements = fe };
                Vector3D newAnchor = new Vector3D(0, 12000, 0), member = new Vector3D(500, 3000, 0);
                var before = At(fe, 0).Position + member;
                bool ok = FrameRails.TryRebaseShift(fr, 0, newAnchor);
                var after = At(fr.Elements, 0).Position + (member - newAnchor);
                Ok("a new anchor 12 km off: the frame's orbit moved onto it, members stay put in space", ok && (after - before).Length() < 1e-3, $"{(after - before).Length():E1} m");
                var naive = At(fe, 0).Position + (member - newAnchor);
                Ok("a new anchor: (re-pinned without the rebase, a member jumped 12 km)", Math.Abs((naive - before).Length() - 12000) < 1);
            }

            // a station anchor 4 km off its berth's centre; you 1 km past it: the plot is about the station (you 1 km from it)
            var frameEl = El(Circ(R0, 0, MuV), MuV);
            var fc = At(frameEl, 0);
            Vector3D station = new Vector3D(0, 4000, 0), me = new Vector3D(0, 5000, 0);
            var stEl = El(new StateVector(fc.Position + station, fc.Velocity), MuV);
            var myEl = new RendezvousPlot.OwnOrbit().Get(1, frameEl, me, Vector3D.Zero, 0);
            var qs = RendezvousPlot.Curvilinear(At(stEl, 0), At(myEl, 0));
            var qf = RendezvousPlot.Curvilinear(fc, At(myEl, 0));
            Ok("a station anchor off the berth's centre: the plot puts you 1 km from it (not 5 km from the centre)", Math.Abs(Math.Sqrt(qs.X * qs.X + qs.Y * qs.Y) - 1000) < 5,
               $"{Math.Sqrt(qs.X * qs.X + qs.Y * qs.Y):F0} m from the station; {Math.Sqrt(qf.X * qf.X + qf.Y * qf.Y):F0} m from the centre");
        }

        static void CurvilinearAxes()
        {
            var a = Circ(R0, 0, MuV);
            Vector3D Q(Vector3D p) => RendezvousPlot.Curvilinear(a, new StateVector(p, Vector3D.Zero));
            double d = 1000;
            var ahead = Q(new Vector3D(R0 * Math.Cos(d / R0), R0 * Math.Sin(d / R0), 0));
            var behind = Q(new Vector3D(R0 * Math.Cos(-d / R0), R0 * Math.Sin(-d / R0), 0));
            var above = Q(new Vector3D(R0 + d, 0, 0));
            var north = Q(new Vector3D(R0, 0, d));
            Ok("curvilinear: 1 km ahead on its orbit -> along +1000, radial 0", Math.Abs(ahead.X - d) < 1e-6 && Math.Abs(ahead.Y) < 1e-6);
            Ok("curvilinear: 1 km behind -> along -1000", Math.Abs(behind.X + d) < 1e-6);
            Ok("curvilinear: 1 km higher -> radial +1000, along 0", Math.Abs(above.Y - d) < 1e-6 && Math.Abs(above.X) < 1e-6);
            Ok("curvilinear: 1 km along its orbit normal -> cross +1000, radial 0 (in-plane radial)", Math.Abs(north.Z - d) < 1e-6 && Math.Abs(north.Y) < 1e-6);
            var far = Q(new Vector3D(-R0, 1e-3, 0));
            Ok("curvilinear: half an orbit ahead -> along ±πr, finite", Math.Abs(Math.Abs(far.X) - Math.PI * R0) < 1);
            var nan = RendezvousPlot.Curvilinear(new StateVector(new Vector3D(R0, 0, 0), Vector3D.Zero), a);
            Ok("curvilinear: an anchor with no orbit plane -> NaN (not a wrong number)", double.IsNaN(nan.X));
        }

        static void CommonBodies()
        {
            var sun = new GravityBody { Name = "Sun", Mu = 1.3e20, SoiRadius = double.PositiveInfinity };
            var verdure = new GravityBody { Name = "Verdure", Mu = MuV, SoiRadius = 3e7, Parent = sun };
            var kemik = new GravityBody { Name = "Kemik", Mu = 1e9, SoiRadius = 2e6, Parent = verdure };
            var ardent = new GravityBody { Name = "Ardent", Mu = 5e10, SoiRadius = 3e7, Parent = sun };
            var other = new GravityBody { Name = "Elsewhere", Mu = 1e20, SoiRadius = double.PositiveInfinity };
            Ok("common: you round Verdure, a station round its moon -> Verdure", RendezvousPlot.Common(verdure, kemik) == verdure);
            Ok("common: you round the moon, a station round Verdure -> Verdure", RendezvousPlot.Common(kemik, verdure) == verdure);
            Ok("common: both round the moon -> the moon", RendezvousPlot.Common(kemik, kemik) == kemik);
            Ok("common: Verdure and another planet's station -> the star (no plot: Choose)", RendezvousPlot.Common(verdure, ardent) == sun);
            Ok("common: another system -> none", RendezvousPlot.Common(verdure, other) == null);
            Ok("common: nothing -> none", RendezvousPlot.Common(null, verdure) == null);
        }

        static void PlotChoice()
        {
            RendezvousPlot.Plot C(bool fly, bool ride, bool at, bool tgt, bool own, bool shares)
                => RendezvousPlot.Choose(new RendezvousPlot.Who { Flying = fly, Riding = ride, AtAnchor = at, HasTarget = tgt, TargetIsOwnFrame = own, TargetSharesBody = shares });
            var N = RendezvousPlot.Plot.None; var D = RendezvousPlot.Plot.Disc; var A = RendezvousPlot.Plot.Anchor; var T = RendezvousPlot.Plot.Target;
            var rows = new (string name, RendezvousPlot.Plot got, RendezvousPlot.Plot want)[]
            {
                ("walking (jetpack off): nothing",                                       C(false, true, false, true, false, true), N),
                ("the anchor's pilot, no target: the disc",                               C(true, false, true, false, false, true), D),
                ("the anchor's pilot with a target: the target plot (it was hidden)",    C(true, false, true, true, false, true), T),
                ("riding 5 km from the anchor, no target: the anchor plot",              C(true, true, false, false, false, true), A),
                ("riding beside the anchor, no target: the disc (it drew nothing)",      C(true, true, true, false, false, true), D),
                ("riding beside the anchor with a target: the target plot",              C(true, true, true, true, false, true), T),
                ("riding away, targeting the frame you are in: the anchor plot",         C(true, true, false, true, true, true), A),
                ("not riding, targeting the frame you are in: the disc",                  C(true, false, false, true, true, true), D),
                ("a target only the star is common to: no target plot",                  C(true, true, false, true, false, false), A),
                ("no frame, no target: the disc",                                         C(true, false, false, false, false, true), D),
            };
            foreach (var r in rows) Ok("plot: " + r.name, r.got == r.want, $"{r.got}");
        }

        static void OwnOrbitCache()
        {
            var frameEl = El(Circ(R0, 0, MuV), MuV);
            // the truth: a ship 3 km ahead, 0.4 m/s apart - its own Kepler orbit; measured each tick with noise
            var shipEl = El(new StateVector(At(frameEl, 0).Position + new Vector3D(0, 3000, 0), At(frameEl, 0).Velocity + new Vector3D(0.4, 0, 0)), MuV);
            var c = new RendezvousPlot.OwnOrbit();
            var rnd = new Random(7);
            double worst = 0;
            for (int k = 0; k < 600; k++)
            {
                double t = k / 60.0;
                var fs = At(frameEl, t); var ss = At(shipEl, t);
                Vector3D noiseP = new Vector3D(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * 2;
                Vector3D noiseV = new Vector3D(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * 0.02;
                var el = c.Get(59, frameEl, ss.Position - fs.Position + noiseP, ss.Velocity - fs.Velocity + noiseV, t);
                worst = Math.Max(worst, (At(el, t).Position - ss.Position).Length());
            }
            Ok("own orbit: 10 s of noisy measurements -> one solve, kept (the plan is not re-solved every tick)", c.Rebuilds == 1, $"{c.Rebuilds} solves, worst {worst:F2} m off");
            Ok("own orbit: the kept orbit stays on the ship", worst < c.PosTol);
            // a 1 m/s burn
            double tb = 10;
            var fsb = At(frameEl, tb); var ssb = At(shipEl, tb);
            c.Get(59, frameEl, ssb.Position - fsb.Position, ssb.Velocity - fsb.Velocity + new Vector3D(0, 1, 0), tb);
            Ok("own orbit: a 1 m/s burn -> solved again", c.Rebuilds == 2);
            c.Get(60, frameEl, ssb.Position - fsb.Position, ssb.Velocity - fsb.Velocity + new Vector3D(0, 1, 0), tb);
            Ok("own orbit: another frame -> solved again", c.Rebuilds == 3);
            var bad = c.Get(61, frameEl, new Vector3D(double.NaN, 0, 0), Vector3D.Zero, tb);
            Ok("own orbit: a NaN offset -> the frame's orbit, no exception", bad.SemiMajorAxis == frameEl.SemiMajorAxis);
        }

        static void Degenerate()
        {
            // at rest over the body (a hover, a fall): elements that follow the fall
            var s = new StateVector(new Vector3D(R0, 0, 0), Vector3D.Zero);
            var e = RendezvousPlot.Capture(s, MuV, 0);
            double g = MuV / (R0 * R0), fall = (At(e, 10).Position.Length() - R0);
            Ok("capture: at rest over the body -> a fall (½gt² in 10 s)", Math.Abs(fall + 0.5 * g * 100) < 1, $"{fall:F2} m, expected {-0.5 * g * 100:F2}");
            // escaping: a hyperbola - the plot is still a number
            var esc = El(new StateVector(new Vector3D(R0, 0, 0), new Vector3D(0, 1.6 * Math.Sqrt(MuV / R0), 0)), MuV);
            var q = RendezvousPlot.Curvilinear(Circ(R0, 0, MuV), At(esc, 600));
            Ok("an escape (hyperbolic) path in the plot: finite", !double.IsNaN(q.X) && !double.IsNaN(q.Y));
            // a target never defined on the span (round another body there): no approach, not a zero
            var none = RendezvousPlot.Closest(tk => Circ(R0, 0, MuV), tk => (StateVector?)null, 0, 1000);
            Ok("closest approach with no target on the span: none (not 0 m)", !none.Ok);
            // two passes: a broad 12 km one sampled dead on (t=200), a sharp 5 km one between samples (t=735, 10 s wide)
            double D(double tk) => Math.Min(12000 + 0.5 * (tk - 200) * (tk - 200), 5000 + 600 * Math.Abs(tk - 735));
            StateVector? Me(double tk) => new StateVector(Vector3D.Zero, Vector3D.Zero);
            StateVector? It(double tk) => new StateVector(new Vector3D(D(tk), 0, 0), Vector3D.Zero);
            var two = RendezvousPlot.Closest(Me, It, 0, 1000, 100);   // (samples every 10 s: 730 and 740 read 8 km - a local minimum)
            Ok("closest approach: the sharp pass between samples, not the broad one sampled dead on", two.Ok && Math.Abs(two.Distance - 5000) < 5 && Math.Abs(two.T - 735) < 0.5,
               $"{two.Distance / 1000:F3} km at {two.T:F1} s");
            // the local orbit in a planet's space: noisy measurements kept on one solve
            {
                var c = new RendezvousPlot.OwnOrbit(); var rnd = new Random(3);
                var truthEl = El(Circ(R0, 0.3, MuV), MuV);
                for (int k = 0; k < 600; k++)
                {
                    double tk = k / 60.0; var st = At(truthEl, tk);
                    c.GetState(7, new StateVector(st.Position + new Vector3D(rnd.NextDouble() - 0.5, 0, 0), st.Velocity + new Vector3D(0, (rnd.NextDouble() - 0.5) * 0.02, 0)), MuV, tk);
                }
                Ok("a planet's space: 10 s of noisy local measurements -> one solve (not one a frame)", c.Rebuilds == 1, $"{c.Rebuilds}");
            }
            var back = RendezvousPlot.Closest(tk => Circ(R0, 0, MuV), tk => Circ(R0, 0, MuV), 100, 50);
            Ok("closest approach over a backwards span: none", !back.Ok);
        }
    }
}
