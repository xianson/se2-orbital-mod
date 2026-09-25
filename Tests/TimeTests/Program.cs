using System;
using SEAerospace.Time;

namespace TimeTest
{
    /// <summary>
    /// Offline assertion harness for the Time & Warp (UniverseTime) layer. No game, no test
    /// framework. Exit 0 = all pass.
    ///
    /// Proves the clock model holds in built code:
    ///  - Advance accumulates dt * timescale into the universe time;
    ///  - StepUp/StepDown walk the warp ladder and clamp to its bounds;
    ///  - SetTimescale snaps an arbitrary request down to the nearest legal rung;
    ///  - the warp policy returns x1 when ANY ship is materialized and the requested step otherwise;
    ///  - the POCO Capture -> Apply round-trips epoch + timescale.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.WriteLine("Time & Warp (UniverseTime) offline tests");
            Console.WriteLine("========================================");

            try
            {
                TestAdvanceAccumulates();
                TestAdvanceScaled();
                TestLadderBoundsAndSteps();
                TestSetTimescaleClamps();
                TestCustomLadderNormalized();
                TestWarpPolicy();
                TestGradedCapStaleness();     // anti-jank #15/#16: seam-sized staleness margin
                TestEffectiveTimescale();
                TestPocoRoundTrip();
                TestApplyClampsAndNull();
            }
            catch (Exception ex)
            {
                Console.WriteLine("UNHANDLED: " + ex);
                _failed++;
            }

            Console.WriteLine();
            Console.WriteLine("passed=" + _passed + " failed=" + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // ---- assertion helpers ------------------------------------------------

        private static void Ok(bool cond, string msg)
        {
            if (cond) { _passed++; Console.WriteLine("  PASS  " + msg); }
            else { _failed++; Console.WriteLine("  FAIL  " + msg); }
        }

        // REGRESSION PIN (anti-jank #15/#16): the graded coast caps (intercept/arrival/SOI glide) re-stamp
        // only ONCE PER SEAM (SeamCheckInterval=30 ticks), so their HoldsRealtime freshness margin must
        // cover a FULL seam of universe advance at the effective rate (+1 ordering tick), not one tick —
        // else above ~x3 effective the stamp expires between its own re-stamps, the cap flaps to +inf, and
        // the rails forward-jump a full warp tick into the event the glide is meant to decelerate into.
        private static void TestGradedCapStaleness()
        {
            Console.WriteLine();
            Console.WriteLine("[graded-cap staleness margin (#15/#16)]");
            const double hold = 1.5;          // CrunchImminentHoldSeconds (graded-cap hold)
            const int seam = 30;              // FrameManager.SeamCheckInterval
            double ts = 100.0;                // a mid-ladder effective rate (well above the ~x3 flap threshold)
            double stamp = 0.0;
            // Worst-case stamp age just before the next per-seam re-stamp: a full seam + one ordering tick
            // of universe advance at the effective rate.
            double now = stamp + ts * (seam + 1) / 60.0;

            Ok(WarpPolicy.HoldsRealtime(now, stamp, hold, ts, (seam + 1) / 60.0),
                "seam-sized margin keeps the graded cap fresh across a full seam at x" + ts + " (no flap)");
            Ok(!WarpPolicy.HoldsRealtime(now, stamp, hold, ts, 1.0 / 60.0),
                "RED: a one-tick margin goes STALE between seams at x" + ts + " (the flap the fix removes)");
            // The hard-x1 locks pin effective=1, so even their one-tick margin is safe across a seam —
            // which is why only the GRADED caps needed the seam-sized margin.
            Ok(WarpPolicy.HoldsRealtime(stamp + 1.0 * (seam + 1) / 60.0, stamp, hold, 1.0, 1.0 / 60.0),
                "at x1 (hard-lock case) a one-tick margin still holds across a seam");
        }

        private static void Approx(double actual, double expected, double absTol, string msg)
        {
            Ok(Math.Abs(actual - expected) <= absTol,
                msg + " (actual=" + actual + " expected=" + expected + ")");
        }

        // ---- tests ------------------------------------------------------------

        private static void TestAdvanceAccumulates()
        {
            Console.WriteLine("[advance accumulates dt*timescale]");
            UniverseTime t = new UniverseTime();
            Ok(t.EpochSeconds == 0.0, "starts at epoch 0");
            Ok(t.Timescale == 1.0, "starts at real-time x1");

            // x1: 60 frames of 1/60 s == 1 s.
            for (int i = 0; i < 60; i++) t.Advance(1.0 / 60.0);
            Approx(t.EpochSeconds, 1.0, 1e-9, "60 frames at x1 == 1 universe second");

            // x100: one 0.5 s frame adds 50 s.
            t.SetTime(0.0);
            t.SetTimescale(100.0);
            t.Advance(0.5);
            Approx(t.EpochSeconds, 50.0, 1e-9, "0.5s frame at x100 == 50 universe seconds");

            // non-positive dt is a no-op.
            double before = t.EpochSeconds;
            t.Advance(0.0);
            t.Advance(-1.0);
            Ok(t.EpochSeconds == before, "non-positive dt does not advance");

            // SetTime overrides directly.
            t.SetTime(123456.0);
            Ok(t.EpochSeconds == 123456.0, "SetTime sets the universe clock");

            // round5: a non-finite epoch (from a crafted/corrupt save) must be REJECTED — else NaN
            // propagates into every analytic propagation and AdvanceScaled's `+=` keeps it NaN forever.
            // RED counterfactual: without the guard, EpochSeconds would become NaN here.
            t.SetTime(double.NaN);
            Ok(t.EpochSeconds == 123456.0, "SetTime(NaN) is rejected — prior epoch preserved");
            t.SetTime(double.PositiveInfinity);
            Ok(t.EpochSeconds == 123456.0, "SetTime(+Inf) is rejected — prior epoch preserved");
            t.SetTime(double.NegativeInfinity);
            Ok(t.EpochSeconds == 123456.0, "SetTime(-Inf) is rejected — prior epoch preserved");
        }

        // AdvanceScaled is the warp-policy advance: it adds dt*timescale at an EXPLICIT cap WITHOUT
        // mutating the stored Timescale, so the player's chosen warp survives a real-time lock and
        // resumes once everything is back on rails. (This is what UniverseClockComponent now calls.)
        private static void TestAdvanceScaled()
        {
            Console.WriteLine("[AdvanceScaled advances at an explicit cap without mutating Timescale]");
            UniverseTime t = new UniverseTime();
            t.SetTimescale(100.0);   // player's chosen warp

            // Policy says real-time (cap x1) because something is materialized: advance at x1.
            double added = t.AdvanceScaled(1.0 / 60.0, WarpPolicy.EffectiveTimescale(t, true));
            Approx(added, 1.0 / 60.0, 1e-12, "materialized -> advanced at x1 (real-time)");
            Approx(t.EpochSeconds, 1.0 / 60.0, 1e-12, "epoch advanced by one real frame at x1");
            Ok(t.Timescale == 100.0, "stored Timescale UNCHANGED through a real-time lock (resumes later)");

            // Back on rails: the stored x100 is honored.
            t.SetTime(0.0);
            double added2 = t.AdvanceScaled(0.5, WarpPolicy.EffectiveTimescale(t, false));
            Approx(added2, 50.0, 1e-9, "on rails -> 0.5s frame at the resumed x100 == 50s");
            Approx(t.EpochSeconds, 50.0, 1e-9, "epoch advanced by 50 universe seconds");

            // Floors: non-positive dt is a no-op; a sub-1 timescale is floored to real-time.
            double before = t.EpochSeconds;
            Ok(t.AdvanceScaled(0.0, 100.0) == 0.0 && t.EpochSeconds == before, "non-positive dt does not advance");
            t.SetTime(0.0);
            t.AdvanceScaled(1.0, 0.0);
            Approx(t.EpochSeconds, 1.0, 1e-12, "timescale below x1 floored to real-time (x1)");
        }

        private static void TestLadderBoundsAndSteps()
        {
            Console.WriteLine("[step up/down clamp to ladder bounds]");
            UniverseTime t = new UniverseTime();
            double[] steps = t.AllowedSteps;
            Ok(steps.Length == 10, "default ladder has 10 rungs");
            Ok(steps[0] == 1.0, "lowest rung is x1");
            Ok(t.MaxStep == 1000000.0, "highest rung is x1000000");
            Ok(t.MinStep == 1.0, "MinStep is x1");

            // Walk the whole ladder up.
            double[] expectedUp = { 2, 5, 10, 50, 100, 1000, 10000, 100000, 1000000 };
            for (int i = 0; i < expectedUp.Length; i++)
            {
                double v = t.StepUp();
                Ok(v == expectedUp[i], "StepUp -> x" + expectedUp[i]);
            }
            // At the top, StepUp is a no-op.
            Ok(t.StepUp() == 1000000.0, "StepUp at top clamps to max");

            // Walk all the way down.
            double[] expectedDown = { 100000, 10000, 1000, 100, 50, 10, 5, 2, 1 };
            for (int i = 0; i < expectedDown.Length; i++)
            {
                double v = t.StepDown();
                Ok(v == expectedDown[i], "StepDown -> x" + expectedDown[i]);
            }
            // At the bottom, StepDown is a no-op.
            Ok(t.StepDown() == 1.0, "StepDown at bottom clamps to x1");

            // ResetToRealtime drops from any rung to x1.
            t.SetTimescale(1000.0);
            t.ResetToRealtime();
            Ok(t.Timescale == 1.0, "ResetToRealtime forces x1");
        }

        private static void TestSetTimescaleClamps()
        {
            Console.WriteLine("[SetTimescale snaps to nearest legal rung at/below]");
            UniverseTime t = new UniverseTime();

            Ok(t.SetTimescale(7.0) == 5.0, "x7 request snaps down to x5");
            Ok(t.SetTimescale(10.0) == 10.0, "exact x10 stays x10");
            Ok(t.SetTimescale(9999999.0) == 1000000.0, "above max clamps to x1000000");
            Ok(t.SetTimescale(0.5) == 1.0, "below 1 clamps to x1");
            Ok(t.SetTimescale(-5.0) == 1.0, "negative clamps to x1");

            // The property setter clamps identically.
            t.Timescale = 60.0;
            Ok(t.Timescale == 50.0, "Timescale setter snaps x60 down to x50");

            // StepUp/StepDown are relative to where a clamped value landed.
            t.SetTimescale(7.0);          // -> x5
            Ok(t.StepUp() == 10.0, "StepUp from snapped x5 -> x10");
            t.SetTimescale(7.0);          // -> x5
            Ok(t.StepDown() == 2.0, "StepDown from snapped x5 -> x2");
        }

        private static void TestCustomLadderNormalized()
        {
            Console.WriteLine("[custom ladder is sorted/deduped/floored at 1]");
            // Unsorted, with dupes, a sub-1 value, and no explicit 1.
            UniverseTime t = new UniverseTime(500.0, new double[] { 20.0, 4.0, 4.0, 0.25, 20.0 });
            double[] steps = t.AllowedSteps;
            Ok(steps.Length == 3, "ladder normalized to {1,4,20}");
            Ok(steps[0] == 1.0 && steps[1] == 4.0 && steps[2] == 20.0, "sorted, deduped, 1 prepended, 0.25 dropped");
            Ok(t.EpochSeconds == 500.0, "custom start epoch honored");
            Ok(t.Timescale == 1.0, "custom ladder starts at x1");

            // Null/empty steps fall back to the default ladder.
            UniverseTime d = new UniverseTime(0.0, null);
            Ok(d.AllowedSteps.Length == 10, "null steps -> default 10-rung ladder");
        }

        private static void TestWarpPolicy()
        {
            Console.WriteLine("[warp policy: x1 when materialized, requested step otherwise]");
            // Locked (a ship is materialized below a shell) -> forced real-time.
            Ok(WarpPolicy.MaxTimescale(true, 10000.0) == 1.0, "materialized -> x1 regardless of request");
            Ok(WarpPolicy.MaxTimescale(true, 5.0) == 1.0, "materialized -> x1");

            // All on rails -> the requested step passes through.
            Ok(WarpPolicy.MaxTimescale(false, 10000.0) == 10000.0, "all on rails -> requested x10000");
            Ok(WarpPolicy.MaxTimescale(false, 50.0) == 50.0, "all on rails -> requested x50");

            // Never below x1 even when nothing is locked.
            Ok(WarpPolicy.MaxTimescale(false, 0.0) == 1.0, "request below 1 floored to x1");
            Ok(WarpPolicy.Realtime == 1.0, "Realtime constant is x1");
        }

        private static void TestEffectiveTimescale()
        {
            Console.WriteLine("[effective timescale: cap without mutating the clock's request]");
            UniverseTime t = new UniverseTime();
            t.SetTimescale(1000.0);

            // Materialized: effective drops to x1 but the stored request is preserved.
            Ok(WarpPolicy.EffectiveTimescale(t, true) == 1.0, "materialized -> effective x1");
            Ok(t.Timescale == 1000.0, "clock's requested warp unchanged by policy");

            // Back on rails: the player's chosen warp is restored automatically.
            Ok(WarpPolicy.EffectiveTimescale(t, false) == 1000.0, "all on rails -> effective x1000 restored");

            // Null clock is safe.
            Ok(WarpPolicy.EffectiveTimescale(null, false) == 1.0, "null clock -> x1");
        }

        private static void TestPocoRoundTrip()
        {
            Console.WriteLine("[POCO Capture -> Apply round-trips epoch + timescale]");
            UniverseTime src = new UniverseTime();
            src.SetTime(987654.321);
            src.SetTimescale(1000.0);

            UniverseTimeState state = src.Capture();
            Ok(state != null, "Capture returns a state");
            Approx(state.EpochSeconds, 987654.321, 1e-9, "captured epoch");
            Ok(state.Timescale == 1000.0, "captured timescale");

            // Apply into a FRESH clock restores both fields.
            UniverseTime dst = new UniverseTime();
            dst.Apply(state);
            Approx(dst.EpochSeconds, src.EpochSeconds, 1e-9, "applied epoch matches");
            Ok(dst.Timescale == src.Timescale, "applied timescale matches");

            // Default POCO ctor is serializer-friendly (parameterless, sane defaults).
            UniverseTimeState fresh = new UniverseTimeState();
            Ok(fresh.EpochSeconds == 0.0 && fresh.Timescale == 1.0, "default POCO is epoch 0 / x1");
        }

        private static void TestApplyClampsAndNull()
        {
            Console.WriteLine("[Apply clamps off-ladder values; null is ignored]");
            // A save made under a different ladder still lands on a legal rung.
            UniverseTimeState weird = new UniverseTimeState();
            weird.EpochSeconds = 42.0;
            weird.Timescale = 7.0;   // not on the default ladder
            UniverseTime t = new UniverseTime();
            t.Apply(weird);
            Ok(t.EpochSeconds == 42.0, "applied off-ladder epoch");
            Ok(t.Timescale == 5.0, "off-ladder x7 clamps down to legal x5");

            // Null state is a no-op.
            t.SetTime(99.0);
            t.SetTimescale(10.0);
            t.Apply(null);
            Ok(t.EpochSeconds == 99.0 && t.Timescale == 10.0, "Apply(null) leaves the clock untouched");
        }
    }
}
