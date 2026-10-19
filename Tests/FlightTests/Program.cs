using SEAerospace.Flight;
using SEAerospace.Orbital;

namespace FlightTest
{
    /// <summary>
    /// Offline checks of the flight rules the UI audit's fixes rest on (2026-10-18): SurfaceRule (B1: a ship on the ground
    /// is landed, not on an orbit through the planet; one read at the centre is nowhere), BurnWatch (B2: an auto-burn that
    /// cannot thrust or turn stops with a reason), MessageKinds / MessageBoard (B3: every notice has a kind and a line the
    /// map can draw), BurnNumbers (B4: typed exact values), GroundPoint (U3: where a path comes down). Verdure's numbers:
    /// radius 63 km, mu 7.785e10. No game; exit 0 = all pass.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool cond, string detail = "")
        {
            if (cond) { _passed++; Console.WriteLine("   PASS " + name + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            else { _failed++; Console.WriteLine("   FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }

        const double R = 63000, Mu = 7.785e10;
        const double Omega = 2 * Math.PI / 7200;   // a spin: a 2 h day (surface speed ~55 m/s)

        static int Main()
        {
            Console.WriteLine("Flight rules offline tests");
            Surface();
            Watch();
            Board();
            Numbers();
            Ground();
            Console.WriteLine($"== {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ── B1: landed / low / orbit / unknown ──
        static void Surface()
        {
            Console.WriteLine(" SurfaceRule");
            // The audit's "Crashed Grass Hopper": at rest on the ground 1.4 km below the datum. Inertial, it moves with the
            // ground (omega x r): its Kepler orbit falls through the planet (the bug: "Ap -1.4 km, Pe -63 km, Period 2 min").
            var r = new Vector3D(R - 1400, 0, 0);
            var vIn = Vector3D.Cross(new Vector3D(0, Omega, 0), r);
            double pe = SurfaceRule.Periapsis(r, vIn, Mu);
            var el = OrbitalMath.ToElements(new StateVector(r, vIn), Mu, 0);
            Ok("bug reproduced: a landed ship's Kepler orbit dips deep into the planet", el.PeriapsisRadius < 0.1 * R, $"Pe alt {(el.PeriapsisRadius - R) / 1000:F1} km");
            Ok("periapsis helper agrees with the elements", Math.Abs(pe - el.PeriapsisRadius) < 1, $"{pe:F0} vs {el.PeriapsisRadius:F0}");
            Ok("crashed ship at rest below the datum: Landed", SurfaceRule.Classify(r.Length(), R, 0.3, pe) == SurfaceKind.Landed);
            Ok("parked on a hill 2 km up: Landed", SurfaceRule.Classify(R + 2000, R, 0.0, 0) == SurfaceKind.Landed);
            Ok("creeping 4 m/s over the ground: Landed", SurfaceRule.Classify(R + 50, R, 4.0, 0) == SurfaceKind.Landed);
            // The audit's "Red Ship": read at the planet's very centre ("Ap -63 km, Period 0 s, 96,955 m/s").
            Ok("at the centre: Unknown (never a target state)", SurfaceRule.Classify(0, R, 0, double.NaN) == SurfaceKind.Unknown);
            Ok("deep inside (30% of the radius): Unknown", SurfaceRule.Classify(0.3 * R, R, 10, double.NaN) == SurfaceKind.Unknown);
            Ok("a NaN position: Unknown", SurfaceRule.Classify(double.NaN, R, 0, 0) == SurfaceKind.Unknown);
            // flying low on a hop: in the air, no orbit
            {
                var rp = new Vector3D(R + 2000, 0, 0); var vp = new Vector3D(0, 150, 0) + Vector3D.Cross(new Vector3D(0, Omega, 0), rp);
                double pep = SurfaceRule.Periapsis(rp, vp, Mu);
                Ok("a plane at 2 km, 150 m/s: Low", SurfaceRule.Classify(rp.Length(), R, 150, pep) == SurfaceKind.Low, $"Pe alt {(pep - R) / 1000:F0} km");
            }
            // a real low orbit that clears the ground: an orbit even inside the band
            {
                double rr = R + 4000, vc = Math.Sqrt(Mu / rr);
                double peo = SurfaceRule.Periapsis(new Vector3D(rr, 0, 0), new Vector3D(0, vc, 0), Mu);
                Ok("circular at 4 km: Orbit", SurfaceRule.Classify(rr, R, vc, peo) == SurfaceKind.Orbit, $"Pe {peo:F0}");
                Ok("circular orbit's periapsis is its radius", Math.Abs(peo - rr) < 1);
            }
            Ok("300 km up, at rest: Orbit (out of the ground band)", SurfaceRule.Classify(R + 300000, R, 0, 0) == SurfaceKind.Orbit);
            Ok("ground band: at least 5 km", SurfaceRule.Band(10000) == 5000 && Math.Abs(SurfaceRule.Band(R) - 5040) < 1);
            Ok("near the ground, speed unknown: Low (no orbit guessed)", SurfaceRule.Classify(R + 100, R, double.NaN, double.NaN) == SurfaceKind.Low);
            Ok("a body with no surface radius: Orbit", SurfaceRule.Classify(10, 0, 0, 0) == SurfaceKind.Orbit);
            Ok("a radial state's periapsis is 0", Math.Abs(SurfaceRule.Periapsis(new Vector3D(R, 0, 0), new Vector3D(-10, 0, 0), Mu)) < 1e-6);
            Ok("no orbit for Landed/Low/Unknown, one for Orbit", SurfaceRule.NoOrbit(SurfaceKind.Landed) && SurfaceRule.NoOrbit(SurfaceKind.Low)
                && SurfaceRule.NoOrbit(SurfaceKind.Unknown) && !SurfaceRule.NoOrbit(SurfaceKind.Orbit));
        }

        // ── B2: the auto-burn's watch ──
        static void Watch()
        {
            Console.WriteLine(" BurnWatch");
            const double dt = 1.0 / 60;
            // The audit's Tu-134: atmospheric engines in vacuum, lined up and firing, "6.0 m/s left" forever.
            {
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double t = 0;
                for (; t < 30 && v == BurnWatch.Verdict.Ok; t += dt) v = w.Step(t, true, false, 0.5, 6.0, double.NaN, true, 6.0);
                Ok("no thrust: stops with NoThrust", v == BurnWatch.Verdict.NoThrust, $"after {t:F1} s");
                Ok("no thrust: not before its window", t >= BurnWatch.NoThrustSeconds - 0.05 && t <= BurnWatch.NoThrustSeconds + 0.1, $"{t:F2} s");
                Ok("no thrust: the player's words", BurnWatch.Message(v, 6.0).StartsWith("Auto-burn stopped: no thrust along the burn"), BurnWatch.Message(v, 6.0));
            }
            {
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double t = 0;
                for (; t < 30 && v == BurnWatch.Verdict.Ok; t += dt) v = w.Step(t, true, false, 0.5, 6.0, 0.0, true, 6.0);
                Ok("thrust along the burn reads 0: stops sooner", v == BurnWatch.Verdict.NoThrust && t < 2.0, $"{t:F2} s");
            }
            {
                // a good burn: 2 m/s^2 from 50 m/s - fine all the way
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double left = 50;
                for (double t = 0; t < 25 && left > 0.1; t += dt) { v = w.Step(t, true, false, 0.5, left, 20000, true, 25); if (v != BurnWatch.Verdict.Ok) break; left -= 2 * dt; }
                Ok("a burn that makes progress: never stopped", v == BurnWatch.Verdict.Ok, $"left {left:F2}");
            }
            {
                // weak but real thrust (0.01 m/s^2, 3 m/s): slow progress is progress
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double left = 3; double t = 0;
                for (; t < 40; t += dt) { v = w.Step(t, true, false, 0.5, left, 1000, true, 300); if (v != BurnWatch.Verdict.Ok) break; left -= 0.01 * dt; }
                Ok("weak thrust that still works: not 'no thrust'", v == BurnWatch.Verdict.Ok, $"{v} at {t:F1} s");
            }
            {
                // can't turn: stuck 45 degrees off
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double t = 0;
                for (; t < 60 && v == BurnWatch.Verdict.Ok; t += dt) v = w.Step(t, false, true, 45, 10, 20000, true, 5);
                Ok("can't turn: stops with CantTurn", v == BurnWatch.Verdict.CantTurn && Math.Abs(t - BurnWatch.CantTurnSeconds) < 0.1, $"{t:F1} s");
                Ok("can't turn: the player's words", BurnWatch.Message(v, 10).Contains("can't turn to the burn"));
            }
            {
                // a slow turn (2 deg/s from 150): fine
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double off = 150;
                for (double t = 0; off > 3; t += dt) { v = w.Step(t, false, true, off, 10, 20000, true, 5); if (v != BurnWatch.Verdict.Ok) break; off -= 2 * dt; }
                Ok("a slow turn that gets there: never stopped", v == BurnWatch.Verdict.Ok);
            }
            {
                // left the seat mid-burn: paused (LeftSeat), back in the seat: going on
                var w = new BurnWatch(); double t = 0;
                for (; t < 2; t += dt) w.Step(t, true, false, 0.5, 20 - t, 20000, true, 10);
                var v = w.Step(t, false, false, double.NaN, 18, double.NaN, false, 10);
                Ok("left the seat: LeftSeat", v == BurnWatch.Verdict.LeftSeat && BurnWatch.Message(v, 18) == "Auto-burn paused: you left the seat");
                var v2 = w.Step(t + 20, true, false, 0.5, 18, 20000, true, 10);
                Ok("back in the seat: fine again (the stall window starts over)", v2 == BurnWatch.Verdict.Ok);
            }
            {
                // started on foot (the jetpack): no seat to leave
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok;
                for (double t = 0; t < 3; t += dt) { v = w.Step(t, true, false, double.NaN, 5 - t, double.NaN, false, 5); if (v != BurnWatch.Verdict.Ok) break; }
                Ok("on foot all along: never LeftSeat", v == BurnWatch.Verdict.Ok);
            }
            {
                // thrust the wrong way: what is left grows
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double left = 10; double t = 0;
                for (; t < 20; t += dt) { v = w.Step(t, true, false, 0.5, left, 20000, true, 5); if (v != BurnWatch.Verdict.Ok) break; left += 1 * dt; }
                Ok("going the wrong way: Diverging", v == BurnWatch.Verdict.Diverging, $"at {t:F1} s, left {left:F1}");
            }
            {
                // far too long: progress, but slower than any burn should take (expected 10 s: limit 60 s)
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double left = 100; double t = 0;
                for (; t < 200; t += dt) { v = w.Step(t, true, false, 0.5, left, 20000, true, 10); if (v != BurnWatch.Verdict.Ok) break; left -= 1.0 * dt; }
                Ok("timed out: TimedOut at the limit", v == BurnWatch.Verdict.TimedOut && Math.Abs(t - 60) < 0.1, $"{v} at {t:F1} s");
            }
            {
                // a turn between firings is no stall: 5 s firing without progress, 10 s turning, 5 s firing - fine
                var w = new BurnWatch(); var v = BurnWatch.Verdict.Ok; double t = 0;
                for (; t < 5; t += dt) v = w.Step(t, true, false, 0.5, 8, 20000, true, 5);
                for (; t < 15; t += dt) v = w.Step(t, false, true, 20 - (t - 5) * 2, 8, 20000, true, 5);
                for (; t < 20 && v == BurnWatch.Verdict.Ok; t += dt) v = w.Step(t, true, false, 0.5, 8, 20000, true, 5);
                Ok("the stall window starts over after a turn", v == BurnWatch.Verdict.Ok);
            }
            Ok("short words for the card", BurnWatch.Short(BurnWatch.Verdict.NoThrust) == "no thrust" && BurnWatch.Short(BurnWatch.Verdict.Ok) == null);
        }

        // ── B3: the notices ──
        static void Board()
        {
            Console.WriteLine(" MessageKinds / MessageBoard");
            // every one-argument caller's words find their kind (they toasted as "Time warp" before)
            (string text, string kind)[] said =
            {
                ("Target: Kemik", MessageKinds.Target), ("Target cleared", MessageKinds.Target),
                ("Nothing to target under the cursor", MessageKinds.Target), ("Nothing to target at the crosshair: aim at a marker and press N", MessageKinds.Target),
                ("Not on this view: focus its planet first", MessageKinds.Target),
                ("GPS point deleted: Base", MessageKinds.Contacts), ("Contact back: Red Ship", MessageKinds.Contacts),
                ("Contact hidden (under Hidden; Del there brings it back): Red Ship", MessageKinds.Contacts),
                ("Warp stopped: entry ahead", MessageKinds.Warp), ("Warp stopped: burn in 30 s", MessageKinds.Warp), ("Warp ×10", MessageKinds.Warp),
                ("Warp limited to ×5: a ship moving nearby", MessageKinds.Warp), ("Needs rails: leave the planet's space first", MessageKinds.Warp),
                ("Warp stopped: 'Tu-134' ran out of power or fuel", MessageKinds.Warp),
                ("Auto-burn stopped: no thrust along the burn (6.0 m/s left)", MessageKinds.Burn), ("Burn done, 0.04 m/s left over", MessageKinds.Burn),
                ("something else", MessageKinds.Map),
            };
            bool all = true; string bad = "";
            foreach (var s in said) if (MessageKinds.Of(s.text) != s.kind) { all = false; bad += $"[{s.text} -> {MessageKinds.Of(s.text)}] "; }
            Ok("every caller's words get their kind (no more 'Time warp' for a target)", all, bad);

            var b = new MessageBoard();
            var lines = new MessageBoard.Line[4];
            b.Post(MessageKinds.Target, "Target: Kemik", 100, 5);
            Ok("a notice shows as a line (the map draws these: it showed nothing before)", b.Showing(100.1, lines) == 1 && lines[0].Text == "Target: Kemik" && lines[0].Kind == "Target");
            b.Post(MessageKinds.Warp, "Warp ×5", 101, 3);
            b.Post(MessageKinds.Warp, "Warp ×10", 101.5, 3);
            int n = b.Showing(101.6, lines);
            Ok("one line per kind: warp x5 then x10 is one line", n == 2 && lines[0].Text == "Warp ×10" && lines[1].Text == "Target: Kemik", $"{n} lines");
            Ok("expired lines go", b.Showing(105.2, lines) == 0 && !b.Latest(106, out _));
            Ok("serials count up", b.Serial == 3);
            for (int i = 0; i < 6; i++) b.Post("K" + i, "n" + i, 200 + i, 10);
            n = b.Showing(206, lines);
            Ok("at most Capacity lines, newest first", n == MessageBoard.Capacity && lines[0].Text == "n5" && lines[3].Text == "n2");
            Ok("the newest of a kind", b.LatestOf("K4", 206, out var l4) && l4.Text == "n4" && !b.LatestOf("K0", 206, out _));
            b.Post(null, "Contact back: X", 300, 5);
            Ok("a kind guessed from the words when none is given", b.Latest(300, out var lx) && lx.Kind == MessageKinds.Contacts);
            b.Clear();
            Ok("cleared: nothing shows", !b.Latest(300, out _));
        }

        // ── B4: exact values ──
        static void Numbers()
        {
            Console.WriteLine(" BurnNumbers");
            Ok("three with commas", BurnNumbers.TryParse("120, 0, -5", out var p, out var nn, out var r, out _) && p == 120 && nn == 0 && r == -5);
            Ok("one: prograde only", BurnNumbers.TryParse(" 100 ", out p, out nn, out r, out _) && p == 100 && nn == 0 && r == 0);
            Ok("spaces and decimals", BurnNumbers.TryParse("12.5 -3.25 0.5", out p, out nn, out r, out _) && p == 12.5 && nn == -3.25 && r == 0.5);
            Ok("semicolons", BurnNumbers.TryParse("1;2;3", out p, out nn, out r, out _) && p == 1 && nn == 2 && r == 3);
            Ok("not a number: refused with a reason", !BurnNumbers.TryParse("12, abc", out _, out _, out _, out var e1) && e1.Contains("abc"), e1);
            Ok("four numbers: refused", !BurnNumbers.TryParse("1 2 3 4", out _, out _, out _, out var e2) && e2 != null);
            Ok("empty: refused", !BurnNumbers.TryParse("  ", out _, out _, out _, out var e3) && e3 != null);
            Ok("absurd: refused", !BurnNumbers.TryParse("1e9", out _, out _, out _, out var e4) && e4 != null);
            Ok("NaN / infinity: refused", !BurnNumbers.TryParse("NaN", out _, out _, out _, out _) && !BurnNumbers.TryParse("Infinity", out _, out _, out _, out _));
            string f = BurnNumbers.Format(120, 0, -5.25);
            Ok("what the dialog shows parses back", BurnNumbers.TryParse(f, out p, out nn, out r, out _) && p == 120 && nn == 0 && Math.Abs(r + 5.3) < 0.051, f);
        }

        // ── U3: ground points ──
        static void Ground()
        {
            Console.WriteLine(" GroundPoint");
            var up = new Vector3D(0, 1, 0);
            GroundPoint.LatLon(new Vector3D(0, R, 0), up, out double la, out _);
            Ok("the pole is 90°N", Math.Abs(la - 90) < 1e-9);
            GroundPoint.LatLon(new Vector3D(R, 0, 0), up, out la, out double lo);
            Ok("the reference meridian on the equator is 0°, 0°", Math.Abs(la) < 1e-9 && Math.Abs(lo) < 1e-9);
            GroundPoint.LatLon(new Vector3D(R * Math.Cos(Math.PI / 4), -R * Math.Sin(Math.PI / 6), 0), up, out la, out _);
            Ok("south is negative", la < 0);
            Ok("words", GroundPoint.Text(12.3, 45.6) == "12°N 46°E" && GroundPoint.Text(-5, -170) == "5°S 170°W" && GroundPoint.Text(90, 10) == "90°N" && GroundPoint.Text(0.2, 0.1) == "0° 0°",
               GroundPoint.Text(12.3, 45.6) + " / " + GroundPoint.Text(-5, -170));
            Ok("not at the centre", !GroundPoint.LatLon(Vector3D.Zero, up, out _, out _));
            Ok("a quarter round the planet", Math.Abs(GroundPoint.Arc(new Vector3D(1, 0, 0), new Vector3D(0, 0, 5), R) - Math.PI / 2 * R) < 1e-6);
            Ok("an axis along X still has a meridian", GroundPoint.LatLon(new Vector3D(0, 0, R), new Vector3D(1, 0, 0), out la, out lo) && Math.Abs(la) < 1e-9 && !double.IsNaN(lo));
        }
    }
}
