using System;
using System.Collections.Generic;
using Keen.VRage.Library.Mathematics;

namespace SEAerospace.MapUi
{
    /// <summary>
    /// THE MAP AND HUD'S ARITHMETIC, ENGINE-FREE (2026-10-18, the UI audit's fixes; tested offline: Tests/MapUiTests): the
    /// words a readout says, where a block of text goes clear of the game's own boxes, how a maneuver handle's pull turns
    /// into delta-v (zoom, fine, snap), the editor's undo, and the porkchop's cells and ejection burns. The drawing that uses
    /// it lives in Hud/, Map/ and Flight/Maneuvers.MapEditor.cs.
    /// </summary>
    public static class Words
    {
        /// <summary>A duration as a clock: "4:12", "1h 05m", "2d 3h". Never "-0:00": under half a second either way is "0:00"
        /// (the target line said "-0:00" at the closest approach - 2026-10-18 audit C4); a real negative keeps its sign.</summary>
        public static string Clock(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
            if (Math.Abs(s) < 0.5) return "0:00";
            if (s < 0) return "-" + Clock(-s);
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h" : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m" : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        /// <summary>A distance: "850 m" under 1 km, "3.1 km" under 10 km, "84 km", "14,353 km".</summary>
        public static string Km(double m)
        {
            if (double.IsNaN(m) || double.IsInfinity(m)) return "-";
            double a = Math.Abs(m);
            if (a < 1000) return $"{m:F0} m";
            return a >= 1e6 ? $"{m / 1000:N0} km" : a >= 1e4 ? $"{m / 1000:F0} km" : $"{m / 1000:F1} km";
        }

        /// <summary>The range rate in words: closing (getting nearer), receding, or holding (within 0.05 m/s plus 1e-7 of the
        /// range per second).</summary>
        public static string Trend(double range, double rangeRate)
        {
            if (double.IsNaN(rangeRate)) return null;
            double still = 0.05 + 1e-7 * Math.Abs(range);
            return rangeRate < -still ? "closing" : rangeRate > still ? "receding" : "holding";
        }

        /// <summary>
        /// THE TARGET, PLAINLY (audit C4: "◆ Kemik · 14,353 km · -0:00"). First line: the name, how far it is now and whether
        /// you are closing or receding - "Kemik 14,353 km, receding". Second line (null when none ahead): the closest approach
        /// still ahead on your path - "closest 3.1 km in 4:12", "rendezvous 850 m in 4:12", a body's "closest 84 km up in
        /// 1h 05m" / "impact in 12:00". caIn under a second: the closest is now, the first line says it.
        /// </summary>
        public static void TargetLines(string name, double rangeNow, double rangeRate, bool caOk, double caIn, double caDist,
                                       bool body, bool impact, bool rendezvous, out string line1, out string line2)
        {
            string trend = Trend(rangeNow, rangeRate);
            line1 = double.IsNaN(rangeNow) ? name : trend != null ? $"{name} {Km(rangeNow)}, {trend}" : $"{name} {Km(rangeNow)}";
            line2 = null;
            if (!caOk || double.IsNaN(caIn) || caIn < 1) return;
            if (impact) { line2 = $"impact in {Clock(caIn)}"; return; }
            if (!double.IsNaN(rangeNow) && caDist >= rangeNow * 0.999 && !body) return;   // (no nearer pass ahead: the first line says it)
            line2 = body ? $"closest {Km(caDist)} up in {Clock(caIn)}" : rendezvous ? $"rendezvous {Km(caDist)} in {Clock(caIn)}" : $"closest {Km(caDist)} in {Clock(caIn)}";
        }

        /// <summary>The orbit card's header: "period 1h 22m · incl 0.0°" (audit C3: "1h 22m · 0.0°" said nothing).</summary>
        public static string PeriodIncl(double period, double inclDeg) => $"period {Clock(period)}  ·  incl {inclDeg:F1}°";
    }

    /// <summary>A screen rectangle (px; y grows down).</summary>
    public struct Rect
    {
        public float X0, Y0, X1, Y1;
        public Rect(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public bool Empty => !(X1 > X0) || !(Y1 > Y0);
        public bool Overlaps(Rect o) => !Empty && !o.Empty && X0 < o.X1 && X1 > o.X0 && Y0 < o.Y1 && Y1 > o.Y0;
        public static Rect FromXywh(float[] r) => r == null || r.Length < 4 ? default : new Rect(r[0], r[1], r[0] + r[2], r[1] + r[3]);
    }

    /// <summary>Where the orbit card's text goes (audit C1 / C3).</summary>
    public static class CardLayout
    {
        /// <summary>
        /// The caption block's place: under the plot (the default, top at belowTop) unless that block would overlap one of the
        /// game's boxes (its "Press P to Park/Lock/Connect" notification sat on the speed, BURN and target lines - C1); then
        /// above the plot (bottom at aboveBottom), unless that overlaps too (then under, as before). True: under.
        /// </summary>
        public static bool Below(float left, float right, float belowTop, float aboveBottom, float height, IList<Rect> avoid)
        {
            if (avoid == null || avoid.Count == 0 || !(height > 0)) return true;
            var under = new Rect(left, belowTop, right, belowTop + height);
            var over = new Rect(left, aboveBottom - height, right, aboveBottom);
            bool hitUnder = false, hitOver = false;
            foreach (var r in avoid) { if (r.Overlaps(under)) hitUnder = true; if (r.Overlaps(over)) hitOver = true; }
            return !hitUnder || hitOver;
        }

        /// <summary>A label's left x kept inside [minX, maxX - width] (an apsis label ran under the game's SPD box - C3); when it
        /// cannot fit, its left at minX.</summary>
        public static float ClampX(float x, float width, float minX, float maxX)
        {
            if (x + width > maxX) x = maxX - width;
            if (x < minX) x = minX;
            return x;
        }
    }

    /// <summary>
    /// A MANEUVER HANDLE'S PULL (audit C5: "a 70 px handle pull gave 0.11 m/s at that zoom"). The pull is a rate: the further
    /// from the handle's rest, the faster the delta-v grows (0.5 (pull / 20 px)^2 m/s per second at 1080p). Scaled by the
    /// zoom - the orbit's size on screen against RefOrbitPx: zoomed out, a pixel is more orbit, so the burn grows faster;
    /// zoomed in, finer - Shift for fine (x FineFactor), Ctrl snaps the component to a clean step (1, 10 or 100 m/s by how fast
    /// it is growing; 0.1 with Shift).
    /// </summary>
    public static class HandleDrag
    {
        public const double RefOrbitPx = 250, MinZoom = 0.25, MaxZoom = 8, FineFactor = 0.1;

        /// <summary>The zoom's factor for an orbit orbitPx across on screen (radius, at 1080p: x u).</summary>
        public static double ZoomFactor(double orbitPx, double u)
        {
            if (!(orbitPx > 0) || !(u > 0)) return 1;
            return Math.Clamp(RefOrbitPx * u / orbitPx, MinZoom, MaxZoom);
        }

        /// <summary>m/s per second for a pull of pullPx (px along the handle), u the UI scale (1 at 1080p).</summary>
        public static double Rate(double pullPx, double u, double zoom, bool fine)
        {
            if (!(pullPx > 0) || !(u > 0)) return 0;
            double r = 0.5 * Math.Pow(pullPx / (20.0 * u), 2) * (zoom > 0 ? zoom : 1);
            return fine ? r * FineFactor : r;
        }

        /// <summary>Ctrl's step: 1 m/s while it grows under 10 m/s a second, 10 under 100, else 100 (0.1 fine).</summary>
        public static double SnapStep(double rate, bool fine) => fine ? 0.1 : rate < 10 ? 1 : rate < 100 ? 10 : 100;

        /// <summary>A value on its step (away from zero at the half).</summary>
        public static double Snap(double v, double step) => step > 0 ? Math.Round(v / step, MidpointRounding.AwayFromZero) * step : v;
    }

    /// <summary>A maneuver node as the undo keeps it.</summary>
    public struct NodeSnap : IEquatable<NodeSnap>
    {
        public double T, Pro, Nor, Rad; public bool Auto;
        public NodeSnap(double t, double pro, double nor, double rad, bool auto) { T = t; Pro = pro; Nor = nor; Rad = rad; Auto = auto; }
        public bool Equals(NodeSnap o) => T == o.T && Pro == o.Pro && Nor == o.Nor && Rad == o.Rad && Auto == o.Auto;
        public override bool Equals(object o) => o is NodeSnap n && Equals(n);
        public override int GetHashCode() => T.GetHashCode() ^ (Pro.GetHashCode() * 31) ^ (Nor.GetHashCode() * 17) ^ (Rad.GetHashCode() * 7) ^ (Auto ? 1 : 0);
    }

    /// <summary>
    /// THE EDITOR'S UNDO (audit C5): the plan before each edit (a drag's start, an add, a removal, a typed value), newest
    /// last, at most Cap; the same plan twice in a row is kept once. Undo hands back the plan before the last edit.
    /// </summary>
    public sealed class EditHistory
    {
        public const int Cap = 50;
        readonly List<List<NodeSnap>> _stack = new List<List<NodeSnap>>();
        public int Count => _stack.Count;
        public void Clear() => _stack.Clear();

        /// <summary>Keep this plan (before an edit; a copy is kept). False: the same as the last kept (nothing new).</summary>
        public bool Mark(List<NodeSnap> plan)
        {
            var copy = plan != null ? new List<NodeSnap>(plan) : new List<NodeSnap>();
            if (_stack.Count > 0 && Same(_stack[_stack.Count - 1], copy)) return false;
            _stack.Add(copy);
            if (_stack.Count > Cap) _stack.RemoveAt(0);
            return true;
        }

        /// <summary>The plan to go back to (skipping kept plans equal to the current one: a mark with no edit after it).</summary>
        public bool Undo(List<NodeSnap> current, out List<NodeSnap> previous)
        {
            current ??= new List<NodeSnap>();
            while (_stack.Count > 0)
            {
                List<NodeSnap> top = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                if (!Same(top, current)) { previous = top; return true; }
            }
            previous = null;
            return false;
        }

        public static bool Same(List<NodeSnap> a, List<NodeSnap> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// THE PORKCHOP'S GRID (audit C2): departure across (column i, from now), time of flight up (row j, from the bottom); a
    /// cell's centre times; the cell under a screen point; the colour scale's stops (by ratio to the cheapest: 1x green .. 4x
    /// red) for the plot and its legend.
    /// </summary>
    public static class PorkchopGrid
    {
        public static double Dep(int i, double span, int nx) => span * (i + 0.5) / nx;
        public static double Tof(int j, double tof0, double tof1, int ny) => tof0 + (tof1 - tof0) * (j + 0.5) / ny;

        /// <summary>The cell under (mx, my) in the plot area [x0, x1] x [y0 (top), y1 (bottom)]; false outside.</summary>
        public static bool CellAt(float mx, float my, float x0, float y0, float x1, float y1, int nx, int ny, out int i, out int j)
        {
            i = j = -1;
            if (!(x1 > x0) || !(y1 > y0) || mx < x0 || mx >= x1 || my <= y0 || my > y1) return false;
            i = Math.Clamp((int)((mx - x0) / (x1 - x0) * nx), 0, nx - 1);
            j = Math.Clamp((int)((y1 - my) / (y1 - y0) * ny), 0, ny - 1);
            return true;
        }

        /// <summary>The colour scale: x (0 at the cheapest, 1 at Ratio x it) by log ratio.</summary>
        public const double Ratio = 4.0;
        public static double Position(double dv, double best)
        {
            if (!(best > 0) || double.IsNaN(dv) || double.IsInfinity(dv)) return double.NaN;
            return Math.Clamp(Math.Log(Math.Max(dv, best) / best) / Math.Log(Ratio), 0, 1);
        }
        /// <summary>The delta-v at scale position x (the legend's tick labels).</summary>
        public static double ValueAt(double x, double best) => best * Math.Pow(Ratio, Math.Clamp(x, 0, 1));

        /// <summary>The scale's stops (r, g, b): green, light green, yellow, orange, red.</summary>
        public static readonly (float r, float g, float b)[] Stops = { (0.10f, 0.55f, 0.20f), (0.35f, 0.80f, 0.25f), (0.95f, 0.85f, 0.20f), (0.95f, 0.50f, 0.15f), (0.80f, 0.15f, 0.12f) };
        public static (float r, float g, float b) ColourAt(double x)
        {
            float f = (float)Math.Clamp(x, 0, 1) * (Stops.Length - 1);
            int k = Math.Min(Stops.Length - 2, (int)f);
            float w = f - k;
            var (r0, g0, b0) = Stops[k]; var (r1, g1, b1) = Stops[k + 1];
            return (r0 + (r1 - r0) * w, g0 + (g1 - g0) * w, b0 + (b1 - b0) * w);
        }
    }

    /// <summary>
    /// A TRANSFER'S FIRST BURN from a porkchop cell (audit C2: "click a cell -> creates the transfer maneuver node"). Leaving a
    /// planet: the hyperbolic excess velocity v-infinity (the Lambert arc's start minus the planet's velocity) is reached by
    /// a prograde burn at the periapsis of the escape hyperbola - placed on your parking orbit where the outgoing asymptote
    /// points along v-infinity: the periapsis is v-infinity's direction turned back by the asymptote's true anomaly
    /// acos(-1/e), e = 1 + r v-inf^2 / mu. The burn: sqrt(v-inf^2 + 2 mu / r) minus your speed there.
    /// </summary>
    public static class Ejection
    {
        /// <summary>The escape hyperbola's eccentricity from a periapsis radius r.</summary>
        public static double Eccentricity(double vinf, double mu, double r) => 1 + r * vinf * vinf / mu;

        /// <summary>Speed at periapsis r of a hyperbola with excess speed vinf.</summary>
        public static double PeriapsisSpeed(double vinf, double mu, double r) => Math.Sqrt(vinf * vinf + 2 * mu / r);

        /// <summary>
        /// The periapsis direction (unit) in the orbit plane (normal n, unit; prograde motion about it) for a departure along
        /// vinf from radius r. vinf's part along n is dropped (a plane change is not attempted: the player tunes the normal).
        /// </summary>
        public static Vector3D Periapsis(Vector3D vinf, Vector3D n, double mu, double r)
        {
            Vector3D vp = vinf - n * Vector3D.Dot(vinf, n);
            double vl = vp.Length();
            if (!(vl > 1e-9)) return Vector3D.Zero;
            Vector3D d = vp / vl;
            double e = Eccentricity(vinf.Length(), mu, r);
            double th = Math.Acos(Math.Clamp(-1.0 / e, -1, 1));
            // turned back by th about n: d cos(-th) + (n x d) sin(-th)
            return d * Math.Cos(th) - Vector3D.Cross(n, d) * Math.Sin(th);
        }

        /// <summary>The time in [t0, t1] (n steps) at which pos(t)'s direction is nearest dir, and the angle (rad) then.</summary>
        public static double NearestTime(Func<double, Vector3D> pos, double t0, double t1, Vector3D dir, int n, out double angle)
        {
            angle = double.NaN;
            if (!(t1 > t0) || n < 1 || !(dir.LengthSquared() > 1e-18)) return double.NaN;
            Vector3D u = Vector3D.Normalize(dir);
            double best = double.NaN, bc = double.NegativeInfinity;
            for (int k = 0; k <= n; k++)
            {
                double t = t0 + (t1 - t0) * k / n;
                Vector3D p = pos(t);
                double l = p.Length();
                if (!(l > 0)) continue;
                double c = Vector3D.Dot(p / l, u);
                if (c > bc) { bc = c; best = t; }
            }
            if (!double.IsNaN(best)) angle = Math.Acos(Math.Clamp(bc, -1, 1));
            return best;
        }
    }

    /// <summary>A text line's size floor (audit: ~7-8 px labels): the scale that makes a line at least minPx tall, given a
    /// line's height at scale 1.</summary>
    public static class TextSize
    {
        public static float AtLeast(float scale, float lineHeightAtOne, float minPx)
        {
            if (!(lineHeightAtOne > 0) || !(minPx > 0)) return scale;
            return Math.Max(scale, minPx / lineHeightAtOne);
        }
    }
}
