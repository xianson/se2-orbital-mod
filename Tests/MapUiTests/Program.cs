using System;
using System.Collections.Generic;
using SEAerospace.MapUi;
using SEAerospace.Orbital;

namespace MapUiTest
{
    // The map / HUD UI arithmetic of the 2026-10-18 UI audit's fixes (Core/MapUi/MapUiMath.cs): each test names its finding.
    // No framework: assert helpers, a PASS/FAIL summary, nonzero exit on any failure.
    internal static class Program
    {
        static int _passed, _failed;
        static void Check(bool ok, string what) { if (ok) _passed++; else { _failed++; Console.WriteLine("FAIL  " + what); } }
        static void Near(double a, double b, double tol, string what) => Check(Math.Abs(a - b) <= tol, $"{what}: {a} vs {b} (tol {tol})");

        static int Main()
        {
            Console.WriteLine("=== map UI tests ===");
            ClockNeverMinusZero();
            TargetLinesPlain();
            HeaderLabelled();
            CardFlipsAboveTheGamesBox();
            ApsisLabelKeptOffTheSpdBox();
            HandleScalesWithZoom();
            FineAndSnap();
            UndoStack();
            PorkchopCellMapping();
            PorkchopScale();
            EjectionBurnLeavesAlongVinf();
            EjectionNearestTime();
            TextFloor();
            Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
            return _failed == 0 ? 0 : 1;
        }

        // C4: "◆ Kemik · 14,353 km · -0:00"
        static void ClockNeverMinusZero()
        {
            Check(Words.Clock(-0.2) == "0:00", "Clock(-0.2) is 0:00, not -0:00: " + Words.Clock(-0.2));
            Check(Words.Clock(0.3) == "0:00", "Clock(0.3)");
            Check(Words.Clock(252) == "4:12", "Clock(252) = 4:12: " + Words.Clock(252));
            Check(Words.Clock(-90) == "-1:30", "a real negative keeps its sign: " + Words.Clock(-90));
            Check(Words.Clock(3900) == "1h 05m", "Clock(3900): " + Words.Clock(3900));
            Check(Words.Clock(double.NaN) == "-", "Clock(NaN)");
        }

        static void TargetLinesPlain()
        {
            // receding, no nearer pass ahead: one line
            Words.TargetLines("Kemik", 14_353_000, 12, true, 0.2, 14_353_000, false, false, false, out var l1, out var l2);
            Check(l1 == "Kemik 14,353 km, receding", "line 1 receding: " + l1);
            Check(l2 == null, "no second line at the closest-now (was -0:00): " + l2);
            // closing with an approach ahead
            Words.TargetLines("TestStation", 40_000, -25, true, 252, 3100, false, false, false, out l1, out l2);
            Check(l1 == "TestStation 40 km, closing", "line 1 closing: " + l1);
            Check(l2 == "closest 3.1 km in 4:12", "line 2: " + l2);
            // a rendezvous
            Words.TargetLines("Rock", 9000, -3, true, 60, 850, false, false, true, out _, out l2);
            Check(l2 == "rendezvous 850 m in 1:00", "rendezvous: " + l2);
            // a body: its altitude, and an impact
            Words.TargetLines("Verdure", 2e6, -100, true, 3900, 84_000, true, false, false, out _, out l2);
            Check(l2 == "closest 84 km up in 1h 05m", "body: " + l2);
            Words.TargetLines("Verdure", 2e6, -100, true, 720, 0, true, true, false, out _, out l2);
            Check(l2 == "impact in 12:00", "impact: " + l2);
            // holding
            Words.TargetLines("Station", 500, 0.01, false, double.NaN, double.NaN, false, false, false, out l1, out l2);
            Check(l1 == "Station 500 m, holding" && l2 == null, "holding: " + l1);
            // a pass ahead that is no nearer than now: no second line
            Words.TargetLines("X", 10_000, 5, true, 600, 12_000, false, false, false, out _, out l2);
            Check(l2 == null, "a farther 'closest' ahead is not shown");
        }

        // C3: "1h 22m · 0.0°" unlabelled
        static void HeaderLabelled()
        {
            Check(Words.PeriodIncl(4920, 0.04) == "period 1h 22m  ·  incl 0.0°", "header: " + Words.PeriodIncl(4920, 0.04));
        }

        // C1: the game's "Press P to Park/Lock/Connect" box over the card's lines (shot l6_08: box 334..638 x 537..575 at 1600x900)
        static void CardFlipsAboveTheGamesBox()
        {
            var box = new Rect(334, 537, 638, 575);
            var avoid = new List<Rect> { box };
            // the lines centred under a disc at x 336 +- 130, starting at y 530, 70 px tall; above: ending at 350
            Check(!CardLayout.Below(206, 466, 530, 350, 70, avoid), "lines go above the plot when the game's box is under it");
            Check(CardLayout.Below(206, 466, 530, 350, 70, new List<Rect>()), "no box: under");
            Check(CardLayout.Below(206, 466, 530, 350, 70, null), "no list: under");
            var both = new List<Rect> { box, new Rect(200, 250, 480, 360) };
            Check(CardLayout.Below(206, 466, 530, 350, 70, both), "both blocked: under as before");
            Check(CardLayout.Below(206, 466, 530, 350, 70, new List<Rect> { new Rect(800, 537, 1000, 575) }), "a box elsewhere: under");
        }

        // C3: "Pe 84 km · 13:22" ran under the SPD box
        static void ApsisLabelKeptOffTheSpdBox()
        {
            float x = CardLayout.ClampX(400, 90, 0, 418);   // the SPD box's left edge at 418
            Check(x + 90 <= 418 + 1e-3, "label's right edge left of the SPD box: " + x);
            Check(CardLayout.ClampX(100, 90, 0, 418) == 100, "a label clear of it stays");
            Check(CardLayout.ClampX(-20, 90, 0, 418) == 0, "kept on screen at the left");
            // ClearOf: the second apsis label kept off the first (2026-10-19 final-check: Ap pushed onto Pe)
            {
                var pe = new Rect(284, 446, 412, 464);
                Check(CardLayout.ClearOf(new Rect(240, 470, 380, 488), pe, 2) == 470, "clear of it: unchanged");
                Check(CardLayout.ClearOf(new Rect(240, 438, 380, 456), pe, 2) == 446 - 2 - 18, "overlapping, its middle higher: just above");
                Check(CardLayout.ClearOf(new Rect(240, 452, 380, 470), pe, 2) == 464 + 2, "overlapping, its middle lower: just below");
                Check(CardLayout.ClearOf(new Rect(240, 438, 380, 456), default, 2) == 438, "nothing placed yet: unchanged");
            }
        }

        // C5: a 70 px pull at a far zoom gave 0.11 m/s
        static void HandleScalesWithZoom()
        {
            double u = 1;
            double near = HandleDrag.ZoomFactor(1000, u), mid = HandleDrag.ZoomFactor(250, u), far = HandleDrag.ZoomFactor(40, u);
            Near(mid, 1, 1e-12, "the reference orbit size: factor 1");
            Check(far > mid && mid > near, $"zoomed out grows faster: {far} > {mid} > {near}");
            Check(HandleDrag.ZoomFactor(1, u) == HandleDrag.MaxZoom && HandleDrag.ZoomFactor(1e6, u) == HandleDrag.MinZoom, "clamped");
            Check(HandleDrag.ZoomFactor(0, u) == 1 && HandleDrag.ZoomFactor(double.NaN, u) == 1, "unknown size: 1");
            // 70 px for a second at that zoom (orbit 40 px): well over a few m/s now
            double r = HandleDrag.Rate(70, u, far, false);
            Check(r > 30, "70 px at a far zoom: " + r + " m/s per s");
            Near(HandleDrag.Rate(20, 1, 1, false), 0.5, 1e-12, "20 px at 1080p, factor 1: 0.5 m/s/s (unchanged)");
            Near(HandleDrag.Rate(40, 2, 1, false), 0.5, 1e-12, "4K: twice the pixels for the same rate");
            Check(HandleDrag.Rate(-5, 1, 1, false) == 0, "a pull back: 0");
        }

        static void FineAndSnap()
        {
            Near(HandleDrag.Rate(60, 1, 1, true), HandleDrag.Rate(60, 1, 1, false) * 0.1, 1e-12, "Shift: a tenth");
            Check(HandleDrag.SnapStep(4, false) == 1 && HandleDrag.SnapStep(40, false) == 10 && HandleDrag.SnapStep(400, false) == 100, "Ctrl steps 1/10/100");
            Check(HandleDrag.SnapStep(400, true) == 0.1, "Ctrl+Shift: 0.1");
            Near(HandleDrag.Snap(12.4, 1), 12, 1e-12, "snap 12.4 -> 12");
            Near(HandleDrag.Snap(-12.5, 1), -13, 1e-12, "snap -12.5 -> -13 (away from zero)");
            Near(HandleDrag.Snap(149, 100), 100, 1e-12, "snap 149 -> 100");
            Near(HandleDrag.Snap(0.26, 0.1), 0.3, 1e-12, "snap 0.26 -> 0.3");
            // a drag integrated raw and shown snapped never sticks on a step
            double raw = 0, shown = 0;
            for (int f = 0; f < 60; f++) { raw += HandleDrag.Rate(60, 1, 1, false) / 60; shown = HandleDrag.Snap(raw, HandleDrag.SnapStep(4.5, false)); }
            Near(shown, Math.Round(raw), 1e-9, "the snapped value follows the raw one");
            Check(shown >= 4, "a second's 60 px pull snapped: " + shown);
        }

        // C5: Ctrl+Z
        static void UndoStack()
        {
            var h = new EditHistory();
            var empty = new List<NodeSnap>();
            var a = new List<NodeSnap> { new NodeSnap(100, 5, 0, 0, false) };
            var b = new List<NodeSnap> { new NodeSnap(100, 9, 0, 0, false) };
            var c = new List<NodeSnap> { new NodeSnap(100, 9, 0, 0, false), new NodeSnap(500, 1, 0, 0, true) };
            Check(h.Mark(empty), "mark the empty plan (before the add)");
            Check(!h.Mark(empty), "the same plan twice: kept once");
            h.Mark(a); h.Mark(b);
            Check(h.Undo(c, out var p) && EditHistory.Same(p, b), "undo the add of the second node");
            Check(h.Undo(b, out p) && EditHistory.Same(p, a), "undo the drag (9 -> 5)");
            Check(h.Undo(a, out p) && p.Count == 0, "undo the first add: empty");
            Check(!h.Undo(empty, out _), "nothing left");
            // a mark with no edit after it is skipped
            h.Mark(a);
            Check(!h.Undo(a, out _), "a mark equal to now is no step");
            for (int i = 0; i < EditHistory.Cap + 10; i++) h.Mark(new List<NodeSnap> { new NodeSnap(i, 0, 0, 0, false) });
            Check(h.Count == EditHistory.Cap, "capped at " + EditHistory.Cap + ": " + h.Count);
            Check(!new NodeSnap(1, 2, 3, 4, false).Equals(new NodeSnap(1, 2, 3, 4, true)), "the auto flag counts");
            var m = new List<NodeSnap> { new NodeSnap(1, 1, 0, 0, false) }; var h2 = new EditHistory(); h2.Mark(m); m[0] = new NodeSnap(1, 2, 0, 0, false);
            Check(h2.Undo(m, out var kept) && kept[0].Pro == 1, "a kept plan is a copy");
        }

        // C2: click a cell
        static void PorkchopCellMapping()
        {
            // plot area 100..500 x 50 (top)..350 (bottom), 40 x 30 cells
            Check(PorkchopGrid.CellAt(100.5f, 349.5f, 100, 50, 500, 350, 40, 30, out int i, out int j) && i == 0 && j == 0, $"bottom-left cell: {i},{j}");
            Check(PorkchopGrid.CellAt(499.9f, 50.5f, 100, 50, 500, 350, 40, 30, out i, out j) && i == 39 && j == 29, $"top-right cell: {i},{j}");
            Check(PorkchopGrid.CellAt(305f, 205f, 100, 50, 500, 350, 40, 30, out i, out j) && i == 20 && j == 14, $"middle: {i},{j}");
            Check(!PorkchopGrid.CellAt(99f, 200f, 100, 50, 500, 350, 40, 30, out _, out _), "left of it: none");
            Check(!PorkchopGrid.CellAt(200f, 351f, 100, 50, 500, 350, 40, 30, out _, out _), "below it: none");
            // the cell's times: the same as the solver's (Porkchop.Dep / Tof)
            Near(PorkchopGrid.Dep(0, 4000, 40), 50, 1e-9, "first column's departure");
            Near(PorkchopGrid.Dep(39, 4000, 40), 3950, 1e-9, "last column's departure");
            Near(PorkchopGrid.Tof(0, 1000, 4000, 30), 1050, 1e-9, "bottom row's flight");
            Near(PorkchopGrid.Tof(29, 1000, 4000, 30), 3950, 1e-9, "top row's flight");
            // the click lands on the cell drawn there: centre times inside the cell's span
            for (int k = 0; k < 40; k++)
            {
                float x = 100 + (k + 0.5f) * 10f;
                PorkchopGrid.CellAt(x, 200, 100, 50, 500, 350, 40, 30, out i, out _);
                if (i != k) { Check(false, $"column {k} hit {i}"); break; }
            }
            _passed++;
        }

        static void PorkchopScale()
        {
            Near(PorkchopGrid.Position(500, 500), 0, 1e-12, "the cheapest at 0");
            Near(PorkchopGrid.Position(2000, 500), 1, 1e-12, "4x at 1");
            Near(PorkchopGrid.Position(1000, 500), 0.5, 1e-12, "2x at the middle (log)");
            Check(double.IsNaN(PorkchopGrid.Position(double.PositiveInfinity, 500)), "no arc: NaN (the legend's black)");
            Near(PorkchopGrid.ValueAt(0.5, 500), 1000, 1e-9, "the legend's middle label: 2x");
            var g = PorkchopGrid.ColourAt(0); var r = PorkchopGrid.ColourAt(1);
            Check(g.g > g.r && r.r > r.g, "green to red");
        }

        // C2: the transfer node's burn - flown, it leaves along v-infinity
        static void EjectionBurnLeavesAlongVinf()
        {
            double mu = 3.986e14, r = 6.778e6;
            var n = new Vector3D(0, 0, 1);
            foreach (var vinf in new[] { new Vector3D(2500, 1200, 0), new Vector3D(-800, -3000, 0), new Vector3D(0, 1500, 300) })
            {
                var pe = Ejection.Periapsis(vinf, n, mu, r);
                Near(pe.Length(), 1, 1e-9, "periapsis direction is a unit vector");
                Near(Vector3D.Dot(pe, n), 0, 1e-9, "in the orbit plane");
                // at the periapsis, prograde (n x pe), at the periapsis speed
                double vp = Ejection.PeriapsisSpeed(vinf.Length(), mu, r);
                var sv = new StateVector(pe * r, Vector3D.Cross(n, pe) * vp);
                var el = OrbitalMath.ToElements(sv, mu, 0);
                var far = OrbitPropagation.StateAt(el, 3e8);   // (years: far out on the asymptote)
                Vector3D vplane = vinf - n * Vector3D.Dot(vinf, n);
                double cos = Vector3D.Dot(Vector3D.Normalize(far.Velocity), Vector3D.Normalize(vplane));
                Check(cos > 0.999, $"flown, it leaves along v-inf (in plane): cos {cos:F5}");
                Near(far.Velocity.Length(), vinf.Length(), vinf.Length() * 0.02, "and at v-inf's speed");
            }
            Check(Ejection.Periapsis(new Vector3D(0, 0, 5), n, mu, r).LengthSquared() == 0, "v-inf along the normal: no direction");
        }

        static void EjectionNearestTime()
        {
            // a circle, period 100 s: where it points along +y is t = 25
            Func<double, Vector3D> pos = t => new Vector3D(Math.Cos(2 * Math.PI * t / 100), Math.Sin(2 * Math.PI * t / 100), 0) * 7e6;
            double tb = Ejection.NearestTime(pos, 0, 100, new Vector3D(0, 1, 0), 400, out double ang);
            Near(tb, 25, 0.26, "nearest time on a circle");
            Check(ang < 0.02, "angle then: " + ang);
            Check(double.IsNaN(Ejection.NearestTime(pos, 10, 10, new Vector3D(0, 1, 0), 10, out _)), "an empty span: NaN");
        }

        static void TextFloor()
        {
            Near(TextSize.AtLeast(0.2f, 40f, 13f), 0.325f, 1e-6, "raised to 13 px");
            Near(TextSize.AtLeast(0.5f, 40f, 13f), 0.5f, 1e-6, "already larger: kept");
            Near(TextSize.AtLeast(0.5f, 0f, 13f), 0.5f, 1e-6, "no measure: kept");
        }
    }
}
