using System;
using System.Collections.Generic;
using SEAerospace.Orbital;
using SEAerospace.Frames;

namespace PlayerOrbitTest
{
    // Whose orbit the player sees (Core/Frames/PlayerOrbit), offline: a frame on a real Verdure orbit, grids in its berth,
    // every case a seated or walking player can be in - each answer against the truth built independently here (the
    // frame's state plus that grid's offset and velocity).
    internal static class Program
    {
        static int _passed, _failed;
        public static void Check(string name, bool cond, string detail = "") => Ok(name, cond, detail);
        static void Ok(string name, bool cond, string detail = "")
        {
            if (cond) { _passed++; Console.WriteLine("   PASS " + name + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            else { _failed++; Console.WriteLine("   FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }
        static bool Near(Vector3D a, Vector3D b, double tol = 1e-6) => (a - b).Length() <= tol * Math.Max(1, b.Length());

        const double Mu = 7.785e10, R = 63000, T = 100;
        const long Anchor = 1000000002, Clone = 1000000817, Unframed = 1000000819, Far = 1000000820, Other = 1000000900, Char = 1;

        static ProximityFrame MakeFrame(long id, long anchor, Vector3D berth, double altKm, double phase = 0)
        {
            double r = R + altKm * 1000, v = Math.Sqrt(Mu / r);
            var sv = new StateVector(new Vector3D(r * Math.Cos(phase), r * Math.Sin(phase), 0), new Vector3D(-v * Math.Sin(phase), v * Math.Cos(phase), 0));
            var f = new ProximityFrame { Id = id, ParentBodyName = "Verdure", AnchorEntityId = anchor, BerthCenter = berth, Elements = OrbitalMath.ToElements(sv, Mu, 0) };
            return f;
        }

        static int Main()
        {
            Console.WriteLine("=== whose orbit the player sees ===");
            var berth = new Vector3D(0, 5556700, 0);
            var f = MakeFrame(59, Anchor, berth, 20);
            f.Members.AddRange(new long[] { Char, Anchor, Clone });
            var g = MakeFrame(60, Other, berth + new Vector3D(0, 0, 40000), 300, 1.0);   // another frame's berth, 40 km away
            g.Members.Add(Other);
            var frames = new[] { f, g };
            ProximityFrame FrameOf(long id) { foreach (var x in frames) if (x.Members.Contains(id)) return x; return null; }
            var anchorS = new PlayerOrbit.GridState(Anchor, berth + new Vector3D(10, 0, 0), Vector3D.Zero);
            var cloneS = new PlayerOrbit.GridState(Clone, berth + new Vector3D(500, 0, 0), new Vector3D(20, 0, 0));
            var unframedS = new PlayerOrbit.GridState(Unframed, berth + new Vector3D(200, 50, 0), new Vector3D(0, 5, 0));   // pasted in, not yet framed
            var farS = new PlayerOrbit.GridState(Far, berth + new Vector3D(2000, 0, 0), Vector3D.Zero);                    // pasted 1.5 km from any framed grid
            var otherS = new PlayerOrbit.GridState(Other, g.BerthCenter, Vector3D.Zero);
            var grids = new List<PlayerOrbit.GridState> { anchorS, cloneS, unframedS, farS, otherS };
            var fc = OrbitPropagation.StateAt(f.Elements, T);

            // 1. seated in the anchor
            var r = PlayerOrbit.Seated(anchorS, grids, FrameOf, 1);
            var own = PlayerOrbit.Own(r.Frame ?? f, r, T);
            Ok("seated in the anchor: its frame", r.Frame == f, r.Why);
            Ok("seated in the anchor: in it, at it", r.InAnchor && r.AtAnchor);
            Ok("seated in the anchor: its own offset (10 m off the berth's centre)", Near(r.Offset, new Vector3D(10, 0, 0)));
            Ok("seated in the anchor: the frame's speed", Math.Abs(own.Velocity.Length() - fc.Velocity.Length()) < 1e-6, $"{own.Velocity.Length():F3} m/s");

            // 2. seated in a framed clone that is not the anchor (the reported bug: no card / the wrong speed)
            r = PlayerOrbit.Seated(cloneS, grids, FrameOf, 1);
            own = PlayerOrbit.Own(r.Frame ?? f, r, T);
            var truth = new StateVector(fc.Position + new Vector3D(500, 0, 0), fc.Velocity + new Vector3D(20, 0, 0));
            Ok("seated in a framed non-anchor: its frame (a card)", r.Frame == f, r.Why);
            Ok("seated in a framed non-anchor: not the anchor, not at it (500 m)", !r.InAnchor && !r.AtAnchor);
            Ok("seated in a framed non-anchor: its own position and velocity", Near(own.Position, truth.Position) && Near(own.Velocity, truth.Velocity),
               $"speed {own.Velocity.Length():F2} m/s, the frame's {fc.Velocity.Length():F2}");

            // 3. seated in a grid just pasted in, not framed yet, near framed ones
            r = PlayerOrbit.Seated(unframedS, grids, FrameOf, 1);
            own = PlayerOrbit.Own(r.Frame ?? f, r, T);
            Ok("seated in an unframed grid near a frame: that frame (a card)", r.Frame == f, r.Why);
            Ok("seated in an unframed grid near a frame: its own state", Near(own.Velocity, fc.Velocity + new Vector3D(0, 5, 0)) && Near(r.Offset, new Vector3D(200, 50, 0)));

            // 4. seated in an unframed grid far from any framed grid: none (until the server frames it)
            r = PlayerOrbit.Seated(farS, grids, FrameOf, 1);
            Ok("seated in an unframed grid 1.5 km from any framed grid: no frame", r.Frame == null, r.Why);

            // 5. warp: the berth's relative motion runs N times faster
            var warped = new PlayerOrbit.GridState(Clone, cloneS.Position, new Vector3D(200, 0, 0));
            r = PlayerOrbit.Seated(warped, grids, FrameOf, 10);
            Ok("warp x10: the velocity in the frame divided by 10", Near(r.Velocity, new Vector3D(20, 0, 0)), $"{r.Velocity}");

            // 6. on foot
            r = PlayerOrbit.OnFoot(f, berth + new Vector3D(30, 0, 0), new Vector3D(0, 0, 3), 1, anchorS.Position);
            own = PlayerOrbit.Own(f, r, T);
            Ok("on foot in a frame: its own state", Near(own.Velocity, fc.Velocity + new Vector3D(0, 0, 3)) && Near(r.Offset, new Vector3D(30, 0, 0)));
            Ok("on foot 20 m from the anchor: at it", r.AtAnchor);
            r = PlayerOrbit.OnFoot(null, berth, Vector3D.Zero, 1);
            Ok("on foot in no frame: none", r.Frame == null);

            // 7. no state carried between calls: a walk far away, then seated in the clone - the clone's state
            PlayerOrbit.OnFoot(f, berth + new Vector3D(-9000, 0, 0), new Vector3D(999, 0, 0), 1);
            r = PlayerOrbit.Seated(cloneS, grids, FrameOf, 1);
            Ok("after a walk elsewhere, seated: the seat's state, nothing left from the walk", Near(r.Offset, new Vector3D(500, 0, 0)) && Near(r.Velocity, new Vector3D(20, 0, 0)));

            // 8. two frames: an unframed grid between them takes the nearer framed grid's
            var between = new PlayerOrbit.GridState(1000000950, g.BerthCenter + new Vector3D(0, 0, -100), Vector3D.Zero);
            var grids2 = new List<PlayerOrbit.GridState>(grids) { between };
            r = PlayerOrbit.Seated(between, grids2, FrameOf, 1);
            Ok("an unframed grid by another frame's anchor: that frame", r.Frame == g, r.Frame?.Id.ToString() ?? "none");

            // 9. degenerate input
            r = PlayerOrbit.Seated(new PlayerOrbit.GridState(Clone, new Vector3D(double.NaN, 0, 0), Vector3D.Zero), grids, FrameOf, 1);
            Ok("a seat grid with no position: no frame, no exception", r.Frame == null, r.Why);
            r = PlayerOrbit.Seated(new PlayerOrbit.GridState(Clone, cloneS.Position, new Vector3D(double.NaN, 1, 1)), grids, FrameOf, 1);
            Ok("a seat grid with a NaN velocity: its frame, velocity zero", r.Frame == f && r.Velocity == Vector3D.Zero);
            r = PlayerOrbit.Seated(new PlayerOrbit.GridState(0, cloneS.Position, Vector3D.Zero), grids, FrameOf, 1);
            Ok("a seat grid not known to the server (id 0): the frame it sits in", r.Frame == f, r.Why);
            var lone = MakeFrame(61, 1000000777, berth + new Vector3D(0, 90000, 0), 50);   // its anchor missing from the snapshot
            lone.Members.Add(1000000778);
            frames = new[] { f, g, lone };
            r = PlayerOrbit.Seated(new PlayerOrbit.GridState(1000000778, lone.BerthCenter, Vector3D.Zero), grids, FrameOf, 1);
            Ok("a frame whose anchor is not in the snapshot: its frame, not at the anchor", r.Frame == lone && !r.AtAnchor);

            // 9b. standing up in a ship whose frame never had you as a member (stowed while you sat in it): join its frame
            frames = new[] { f, g };
            var joined = PlayerOrbit.FrameToJoin(cloneS.Position + new Vector3D(3, 0, 0), grids, FrameOf);
            Ok("stood up beside a framed ship: its frame to join", joined == f, joined?.Id.ToString() ?? "none");
            Ok("on foot 1.5 km from any framed grid: none to join", PlayerOrbit.FrameToJoin(farS.Position + new Vector3D(0, 10, 0), grids, FrameOf) == null);
            Ok("between two frames' ships: the nearer one's", PlayerOrbit.FrameToJoin(g.BerthCenter + new Vector3D(0, 0, -50), grids2, FrameOf) == g);
            Ok("next to an unframed grid only (pasted, not framed yet): none (it is not the rails)", PlayerOrbit.FrameToJoin(unframedS.Position, new List<PlayerOrbit.GridState> { unframedS }, FrameOf) == null);
            Ok("a NaN position: none, no exception", PlayerOrbit.FrameToJoin(new Vector3D(double.NaN, 0, 0), grids, FrameOf) == null);

            // 9b'. a big ship: its origin 450 m from you, its hull 20 m - you join it (measured to the hull)
            {
                var big = new PlayerOrbit.GridState(Clone, berth + new Vector3D(450, 0, 0), Vector3D.Zero, berth + new Vector3D(20, -300, -300), berth + new Vector3D(900, 300, 300));
                var only = new List<PlayerOrbit.GridState> { big };
                Ok("on foot 20 m from a big ship's hull (450 m from its origin): its frame", PlayerOrbit.FrameToJoin(berth, only, FrameOf) == f);
                Ok("a box distance: inside it 0, 20 m off its face 20", Math.Abs(big.DistanceTo(berth + new Vector3D(100, 0, 0))) < 1e-9 && Math.Abs(big.DistanceTo(berth) - 20) < 1e-9);
                var originOnly = new PlayerOrbit.GridState(Clone, berth + new Vector3D(450, 0, 0), Vector3D.Zero);
                Ok("(no box known: its origin, 450 m - none to join)", PlayerOrbit.FrameToJoin(berth, new List<PlayerOrbit.GridState> { originOnly }, FrameOf) == null);
            }

            // 9c. moved out of the frame by something else; membership while seated
            Ok("on foot 300 m from the berth: still in it", !PlayerOrbit.MovedOut(f, berth + new Vector3D(300, 0, 0)));
            Ok("on foot 19 km out (inside the split): still in it", !PlayerOrbit.MovedOut(f, berth + new Vector3D(19000, 0, 0)));
            Ok("on foot 150 km from the berth (fast travel, another berth): moved out", PlayerOrbit.MovedOut(f, berth + new Vector3D(150000, 0, 0)));
            Ok("no frame / a NaN position: not 'moved out'", !PlayerOrbit.MovedOut(null, berth) && !PlayerOrbit.MovedOut(f, new Vector3D(double.NaN, 0, 0)));
            Ok("seated in a ship of the frame you are a member of: kept", PlayerOrbit.KeepMembershipSeated(f, f));
            Ok("seated, no membership: kept (nothing to leave)", PlayerOrbit.KeepMembershipSeated(null, g));
            Ok("seated in a ship that split off into another frame: leave the old one", !PlayerOrbit.KeepMembershipSeated(f, g));
            Ok("seated in a ship that arrived at a planet (no frame now): leave the old one", !PlayerOrbit.KeepMembershipSeated(f, null));

            // 10. the orbit the card shows: altitude and speed of the player's own state
            r = PlayerOrbit.Seated(cloneS, grids, FrameOf, 1);
            own = PlayerOrbit.Own(f, r, T);
            var el = OrbitalMath.ToElements(own, Mu, T);
            Ok("the card's orbit is the player's: speed from its elements", Math.Abs(OrbitPropagation.StateAt(el, T).Velocity.Length() - truth.Velocity.Length()) < 1e-3);

            FadeCases();
            LayoutCases();
            ZoneCases();
            RendezvousCases.Run();
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // Which map level draws a zone (Core/Frames/ZoneLevel).
        static void ZoneCases()
        {
            Console.WriteLine();
            Console.WriteLine("=== which map level draws a zone ===");
            var S = ZoneLevel.Level.Star; var B = ZoneLevel.Level.Body; var P = ZoneLevel.Level.Parent;
            var rows = new (string name, ZoneLevel.Level got, ZoneLevel.Level want)[]
            {
                ("the star's own space, a belt: the star",          ZoneLevel.Of(false, 0, true, false), S),
                ("a planet's own space, its ring: the planet",       ZoneLevel.Of(false, 0, false, true), B),
                ("a planet's L1 / L2: the planet",                   ZoneLevel.Of(true, 2, false, true), B),
                ("a planet's L3 / L4 / L5: the star (on its orbit)", ZoneLevel.Of(true, 4, false, true), S),
                ("a moon's L1: its planet",                          ZoneLevel.Of(true, 1, false, false), P),
                ("a moon's L4: its planet (not the star)",           ZoneLevel.Of(true, 4, false, false), P),
                ("a moon's own space: the moon",                     ZoneLevel.Of(false, 0, false, false), B),
            };
            foreach (var r in rows) Ok("zone: " + r.name, r.got == r.want, r.got.ToString());
        }

        // The terminal's layout on screen (Core/Frames/TerminalLayout): the map's numbers were measured at 16:9.
        static void LayoutCases()
        {
            Console.WriteLine("\n=== the terminal's layout at other screen shapes ===");
            bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;
            var hd = TerminalLayout.For(1920, 1080);
            Ok("1920x1080: every fraction as before (x = f W, y = f H)", Same(hd.X(0.255), 0.255 * 1920) && Same(hd.Y(0.84), 0.84 * 1080) && Same(hd.LenY(0.0275), 0.0275 * 1080));
            var qhd = TerminalLayout.For(2560, 1440, 0.75, 0.75);
            Ok("2560x1440 (design scale 0.75): as before", Same(qhd.X(0.775), 0.775 * 2560) && Same(qhd.Y(TerminalLayout.TabRowBottom), 160 / 0.75));
            var w10 = TerminalLayout.For(1920, 1200);
            Ok("1920x1200 (16:10): the tab row ends 60 px lower (the cell is centred) - 220 px, not 0.148 x 1200", Same(w10.Y(TerminalLayout.TabRowBottom), 220), $"{w10.Y(TerminalLayout.TabRowBottom):F0} px");
            var uw = TerminalLayout.For(3440, 1440, 0.75, 0.75);
            Ok("3440x1440 (21:9): the map's left edge clears the left panel - x 1093, not 0.255 x 3440 = 877", Same(Math.Round(uw.X(0.255)), 1093), $"{uw.X(0.255):F0} px");
            var deck = TerminalLayout.For(1280, 800);
            Ok("1280x800 (Steam Deck, fitted whole): the cell 1280x720, 40 px margins top and bottom", Same(deck.Y(0), 40) && Same(deck.Y(1), 760) && Same(deck.X(1), 1280));
            var bad = TerminalLayout.For(1920, 1080, double.NaN, 0);
            Ok("an unknown design scale: fitted whole (no NaN)", Same(bad.X(0.5), 960) && !double.IsNaN(bad.Y(0.5)));
        }

        // The crossing fade (Core/Frames/CrossingFadeMachine): driven frame by frame as the game drives it.
        static void FadeCases()
        {
            Console.WriteLine("\n=== the crossing fade ===");
            const double Dt = 1.0 / 60;
            // step until a condition or a time limit; returns the time it took (NaN: never)
            double Run(CrossingFadeMachine m, ref double now, int n, int c, bool seated, Func<CrossingFadeMachine, bool> until, double limit = 10, double dt = Dt)
            {
                double t0 = now;
                while (now - t0 < limit) { now += dt; m.Step(now, dt, n, c, seated, true); if (until(m)) return now - t0; }
                return double.NaN;
            }
            bool Idle(CrossingFadeMachine x) => x.State == CrossingFadeMachine.Phase.Idle;

            // 1. the normal case: announced, black, moved (a 1 s stall: no frames), steady again, back
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                double tBlack = Run(m, ref now, 1, 0, true, x => x.Alpha >= 1);
                Ok("announced, seated: black within the fade-out", tBlack <= m.OutSeconds + 2 * Dt, $"{tBlack:F2} s");
                Ok("black before the move: waiting for it", m.State == CrossingFadeMachine.Phase.Black, m.Status);
                now += 1.0; m.Step(now, 1.0, 1, 1, true, true);   // the move, then the engine's ~1 s stall in one frame
                Ok("the stall's long frame: still black, not back yet", m.Alpha >= 1 && m.State == CrossingFadeMachine.Phase.Settle, m.Status);
                bool reset = false;
                double tBack = Run(m, ref now, 1, 1, true, x => { reset |= x.ResetCameraNow; return Idle(x); });
                Ok("steady frames again: back, the chase camera reset while black", !double.IsNaN(tBack) && reset, $"{tBack:F2} s");
            }
            // 2. the move lands while still fading out (announced ahead; a slow fade)
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                Run(m, ref now, 1, 0, true, x => x.Alpha >= 0.5);
                double t = Run(m, ref now, 1, 1, true, Idle);
                Ok("the move during the fade-out: not missed (no 4 s black)", !double.IsNaN(t) && t < 1.5, $"back in {t:F2} s");
            }
            // 3. announced, the move never comes (the frame turned away, a merge): back after the limit
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                double t = Run(m, ref now, 1, 0, true, Idle, 20);
                Ok("no move: back after MaxBlackSeconds", !double.IsNaN(t) && t > m.MaxBlackSeconds && t < m.MaxBlackSeconds + 2, $"{t:F2} s");
            }
            // 4. not seated (on foot): no fade
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                Run(m, ref now, 1, 0, false, x => false, 1);
                Ok("announced, on foot: no fade", m.Alpha == 0 && Idle(m));
            }
            // 5. switched off
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                for (int i = 0; i < 60; i++) { now += Dt; m.Step(now, Dt, 1, 0, true, false); }
                Ok("switched off: no fade", m.Alpha == 0);
            }
            // 6. a crossing with no announcement (the stow, an unpiloted arrival): no fade
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                for (int i = 0; i < 60; i++) { now += Dt; m.Step(now, Dt, 0, 1, true, true); }
                Ok("a crossing with no announcement: no fade", m.Alpha == 0);
            }
            // 7. a second announcement mid-fade (two frames arriving): one fade, it ends
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                Run(m, ref now, 1, 0, true, x => x.Alpha >= 0.5);
                double t = Run(m, ref now, 2, 1, true, Idle);
                Ok("a second announcement mid-fade: one fade, it ends", !double.IsNaN(t), $"{t:F2} s");
            }
            // 8. a new session: counters already high are not news; a reset mid-fade clears it
            {
                var m = new CrossingFadeMachine(); m.Reset(5, 9); double now = 0;
                for (int i = 0; i < 30; i++) { now += Dt; m.Step(now, Dt, 5, 9, true, true); }
                Ok("a new session with old counters: no fade", m.Alpha == 0);
                m.Step(now += Dt, Dt, 6, 9, true, true); m.Step(now += Dt, Dt, 6, 9, true, true);
                m.Reset(6, 9);
                Ok("a reset mid-fade: clear at once", m.Alpha == 0 && Idle(m));
            }
            // 9. bad frame times (NaN, negative, zero): no exception, no jump
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                m.Step(now, double.NaN, 1, 0, true, true); m.Step(now, -1, 1, 0, true, true); m.Step(now, 0, 1, 0, true, true);
                Ok("NaN / negative / zero frame times: no jump", m.Alpha == 0 && m.State == CrossingFadeMachine.Phase.Out);
            }
            // 11. the server moved, this client sees it 0.8 s later: not back before it has
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                Run(m, ref now, 1, 0, true, x => x.Alpha >= 1);
                double movedAt = now, backAt = double.NaN; bool backBeforeSeen = false;
                while (now - movedAt < 6)
                {
                    now += Dt; bool seen = now - movedAt > 0.8;
                    m.Step(now, Dt, 1, 1, true, true, seen);
                    if (m.State == CrossingFadeMachine.Phase.In && double.IsNaN(backAt)) { backAt = now - movedAt; if (!seen) backBeforeSeen = true; }
                }
                Ok("the client sees the move 0.8 s after the server: not back before it", !backBeforeSeen && backAt > 0.8 && backAt < 1.5, $"fading in {backAt:F2} s after the move");
            }
            // 12. the client never reports the move: back after the wait anyway (not 4 s black)
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                Run(m, ref now, 1, 0, true, x => x.Alpha >= 1);
                double movedAt = now, backAt = double.NaN;
                while (now - movedAt < 6) { now += Dt; m.Step(now, Dt, 1, 1, true, true, false); if (m.State == CrossingFadeMachine.Phase.In && double.IsNaN(backAt)) backAt = now - movedAt; }
                Ok("the client never reports the move: back after its wait (not 4 s)", backAt > m.ClientWaitSeconds && backAt < m.ClientWaitSeconds + 0.5, $"{backAt:F2} s");
            }
            // 13. a 20 fps game: steady frames are 0.05 s - it still calms (it stayed black for 4 s)
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0; const double Slow = 0.05;
                for (int i = 0; i < 40; i++) { now += Slow; m.Step(now, Slow, 0, 0, true, true); }   // (its usual frame time, learned)
                Run(m, ref now, 1, 0, true, x => x.Alpha >= 1, 10, Slow);
                double movedAt = now, backAt = double.NaN;
                while (now - movedAt < 6) { now += Slow; m.Step(now, Slow, 1, 1, true, true); if (m.State == CrossingFadeMachine.Phase.In && double.IsNaN(backAt)) backAt = now - movedAt; }
                Ok("at 20 fps: back once frames are steady again (not 4 s black)", backAt < 1.0, $"{backAt:F2} s");
            }
            // 14. the fade's clock: a pause between ticks does not count
            {
                var c = new FadeClock(); c.Tick(100); c.Tick(100.016); double before = c.Now; c.Tick(110.016);   // (10 s paused)
                Ok("a 10 s pause moves the fade's clock by at most 0.25 s", c.Now - before <= 0.25 + 1e-9, $"{c.Now - before:F3} s");
                var m = new CrossingFadeMachine(); m.Reset(0, 0); var clk = new FadeClock(); double wall = 0;
                void T(double w, int n, int cr) { double d = clk.Tick(w); m.Step(clk.Now, d, n, cr, true, true); }
                for (int i = 0; i < 30; i++) { wall += Dt; T(wall, 1, 0); }   // black, waiting for the move
                wall += 10; T(wall, 1, 0);                                     // the pause
                Ok("paused 10 s while black: still black after (the move has not come)", m.State == CrossingFadeMachine.Phase.Black, m.Status);
            }
            // 15. the arrival notice: once ahead, and again after a warp stop left the first one stale
            {
                double nan = double.NaN;
                Ok("notice: x1000 warp, the crossing 300 s of game time ahead - due", ArrivalNotice.Due(300, 0, 1000, nan, 0));
                Ok("notice: x1, the crossing 40 s ahead - not yet", !ArrivalNotice.Due(40, 0, 1, nan, 0));
                Ok("notice: noticed 1 s ago - not again", !ArrivalNotice.Due(0.3, 0, 1, 10, 11));
                Ok("notice: noticed 30 s ago (warp stopped short), 0.3 s ahead now - again", ArrivalNotice.Due(0.3, 0, 1, 10, 40));
                Ok("notice: the crossing is past - no", !ArrivalNotice.Due(-1, 0, 1, nan, 0));
            }
            // 10. a slow game (10 fps): black within the 0.4 s notice
            {
                var m = new CrossingFadeMachine(); m.Reset(0, 0); double now = 0;
                double tBlack = Run(m, ref now, 1, 0, true, x => x.Alpha >= 1, 10, 0.1);
                Ok("at 10 fps: black within the 0.4 s notice", tBlack <= 0.4 + 1e-9, $"{tBlack:F2} s");
            }
        }
    }
}
