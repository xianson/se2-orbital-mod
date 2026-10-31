using System;
using System.Collections.Generic;
using Keen.VRage.Library.Mathematics;

namespace SEAerospace.MapUi
{
    /// <summary>
    /// THE ORBIT AND RENDEZVOUS HUD'S WORDS AND TEXT SLOTS (2026-10-31, the user's approved text cleanup; tested offline:
    /// Tests/MapUiTests). The drawings (disc, plot, markers) carry no text; their words live in three fixed slots:
    ///  (a) one block centred under the disc - the orbit (the next apsis big, the other and the period small), or the burn;
    ///  (b) the relative plot's top edge - the target's name left, its distance right;
    ///  (c) one block left-aligned under the plot - closing / opening / holding, then the closest approach.
    /// One time format (<see cref="Span"/>), two text sizes (<see cref="Primary"/>, <see cref="Secondary"/>), "closest"
    /// for the closest approach everywhere. OrbitHud draws what this says.
    /// </summary>
    public static class HudText
    {
        /// <summary>The two text sizes (x the UI scale): a block's big line, and everything else.</summary>
        public const float Primary = 0.46f, Secondary = 0.40f;
        /// <summary>A countdown this short (s) is "now".</summary>
        public const double NowWindow = 3;
        /// <summary>Under this eccentricity an orbit is "circular" (one line: its altitude and period).</summary>
        public const double CircularE = 0.005;
        /// <summary>An inclination under this (deg) is not shown (it is ~0: the equator's plane).</summary>
        public const double FlatInclDeg = 0.05;
        /// <summary>A range rate under this (m/s, either way) is "holding".</summary>
        public const double HoldingRate = 0.05;

        /// <summary>A duration, one format everywhere on the HUD: "43s", "2m 43s" under 10 min, "57m" under an hour, "1h 57m",
        /// "2d 3h". NaN or infinite: "-". Negative: its size (the caller says "late").</summary>
        public static string Span(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
            s = Math.Abs(s);
            long t = (long)Math.Round(s);
            if (t < 60) return $"{t}s";
            if (s < 600) return $"{t / 60}m {t % 60}s";
            long m = (long)Math.Round(s / 60);
            if (m < 60) return $"{m}m";
            if (m < 24 * 60) return $"{m / 60}h {m % 60}m";
            long h = (long)Math.Round(s / 3600);
            return $"{h / 24}d {h % 24}h";
        }

        /// <summary>A clock-style time in the text ("57:44", "1:05"): the format the HUD no longer uses (harness: hudcheck).</summary>
        public static bool HasClock(string s)
        {
            if (s == null) return false;
            for (int i = 1; i + 2 < s.Length; i++)
                if (s[i] == ':' && char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1]) && char.IsDigit(s[i + 2])) return true;
            return false;
        }

        /// <summary>A countdown: "now" at NowWindow s or less, else "in 2m 43s". Null: NaN or already past.</summary>
        public static string In(double s) => double.IsNaN(s) || s < -NowWindow ? null : s <= NowWindow ? "now" : "in " + Span(s);

        /// <summary>A distance: "850 m", "3.1 km", "84 km", "14,353 km".</summary>
        public static string Km(double m) => Words.Km(m);

        /// <summary>A speed: "0.4 m/s" under 10, "18 m/s".</summary>
        public static string Speed(double v) => Math.Abs(v) < 9.95 ? $"{Math.Abs(v):0.0} m/s" : $"{Math.Abs(v):N0} m/s";

        /// <summary>
        /// The orbit's block under the disc. Circular (e under CircularE): no big line, one small "300 km circular · period
        /// 1h 56m". Elliptic: big = the next apsis ("Ap 600 km in 57m"; "Ap 600 km now" within a small window either side of
        /// it - just past it the countdown was a whole period, P2), small = the other apsis and the period ("Pe 300 km in 1h
        /// 37m · period 3h 15m"). Escape: big = the periapsis ahead if any, small "escape". The inclination, when not ~0, ends
        /// the small line (" · incl 12.3°"). Altitudes above the surface; a periapsis under it is "impact".
        /// </summary>
        public static void Orbit(double peAlt, double apAlt, double peIn, double apIn, double period, double ecc, double inclDeg,
                                 bool escape, out string big, out string small)
        {
            big = small = null;
            string incl = Math.Abs(inclDeg) >= FlatInclDeg && !double.IsNaN(inclDeg) ? $" · incl {inclDeg:F1}°" : "";
            string pe = peAlt < 0 ? "impact" : Km(peAlt);
            if (escape)
            {
                big = !double.IsNaN(peIn) && peIn >= -NowWindow ? $"Pe {pe} {In(peIn)}" : null;
                small = "escape" + incl;
                return;
            }
            string per = period > 0 ? "period " + Span(period) : null;
            if (ecc < CircularE && peAlt >= 0)
            {
                small = $"{Km(0.5 * (peAlt + apAlt))} circular" + (per != null ? " · " + per : "") + incl;
                return;
            }
            double win = ApsisWindow(period);
            bool AtApsis(double s) => !double.IsNaN(s) && (s <= win || (period > 0 && period - s <= win));
            // the apsis you are at, else the one you reach first
            bool apFirst;
            if (AtApsis(apIn)) apFirst = true;
            else if (AtApsis(peIn)) apFirst = false;
            else apFirst = !double.IsNaN(apIn) && (double.IsNaN(peIn) || apIn < peIn);
            string Line(string name, string alt, double s) => AtApsis(s) ? $"{name} {alt} now" : double.IsNaN(s) ? $"{name} {alt}" : $"{name} {alt} in {Span(s)}";
            string ap = Km(apAlt);
            big = apFirst ? Line("Ap", ap, apIn) : Line("Pe", pe, peIn);
            string other = apFirst ? Line("Pe", pe, peIn) : Line("Ap", ap, apIn);
            small = other + (per != null ? " · " + per : "") + incl;
        }

        /// <summary>How close to an apsis (s, either side) is "at" it: NowWindow, or 0.2% of the period (23 s on a 3 h orbit,
        /// where the apsis is a slow place).</summary>
        public static double ApsisWindow(double period) => Math.Max(NowWindow, period > 0 ? 0.002 * period : 0);

        /// <summary>The burn's big line: "Burn 40 m/s in 2m 43s · 3s"; "now", "late 40s", "stopped: no thrust", "paused" in place
        /// of the countdown (when). Its length at full thrust last (NaN: unknown, left out).</summary>
        public static string Burn(double dvLeft, string when, double seconds)
        {
            string dv = dvLeft < 9.95 ? $"{dvLeft:0.0}" : $"{dvLeft:N0}";
            string len = double.IsNaN(seconds) || double.IsInfinity(seconds) ? "" : " · " + Span(Math.Max(1, seconds));
            return $"Burn {dv} m/s {when ?? "now"}{len}";
        }

        /// <summary>The burn's "when": "in 2m 43s", "now", "late 40s"; an auto-burn's reason ("stopped: no thrust", "paused").</summary>
        public static string BurnWhen(double burnIn, double late, string reason)
            => reason != null ? (reason == "paused" ? "paused" : "stopped: " + reason)
             : burnIn > NowWindow ? "in " + Span(burnIn)
             : !double.IsNaN(late) ? "late " + Span(late) : "now";

        /// <summary>How the range changes: "closing 18 m/s", "opening 0.4 m/s", "holding" (under HoldingRate either way). Null: unknown.</summary>
        public static string Rate(double rangeRate)
        {
            if (double.IsNaN(rangeRate)) return null;
            if (Math.Abs(rangeRate) < HoldingRate) return "holding";
            return (rangeRate < 0 ? "closing " : "opening ") + Speed(rangeRate);
        }

        /// <summary>The closest approach still ahead: "closest 2.0 km in 1h 31m", a body's "closest 84 km up in 1h 05m", "impact in
        /// 12m". Null: none ahead, or none nearer than now (rangeNow; NaN: unknown, any is shown).</summary>
        public static string Closest(bool ok, double inS, double dist, double rangeNow, bool body, bool impact)
        {
            if (!ok || double.IsNaN(inS) || inS < 1 || double.IsNaN(dist)) return null;
            if (impact) return "impact " + In(inS);
            if (!body && !double.IsNaN(rangeNow) && dist >= rangeNow * 0.999) return null;
            return body ? $"closest {Km(dist)} up {In(inS)}" : $"closest {Km(dist)} {In(inS)}";
        }

        // ── the slots ──

        /// <summary>One text line of a block: its words, big (Primary) or not, and its colour's role.</summary>
        public struct Line
        {
            public string Text; public bool Big; public int Role;
            public Line(string text, bool big, int role) { Text = text; Big = big; Role = role; }
        }
        /// <summary>Line roles (the drawer's colours): white, dim, the burn's orange, the target's magenta.</summary>
        public const int White = 0, Dim = 1, BurnRole = 2, TargetRole = 3;

        /// <summary>
        /// The orbit block's lines under the disc: a burn takes the big line (orange), and the orbit's own big line then goes
        /// small after the hold ("hold prograde · Ap 600 km in 1h 36m"); without a burn the orbit's big and small lines, then
        /// "hold prograde" when holding. Extra small lines (entry, the next patch event, a rock, transmitting) last.
        /// </summary>
        public static List<Line> DiscBlock(string orbitBig, string orbitSmall, string burn, string hold, IList<string> extra, List<Line> into = null)
        {
            var l = into ?? new List<Line>(6); l.Clear();
            if (burn != null)
            {
                l.Add(new Line(burn, true, BurnRole));
                string s = Join(hold, orbitBig ?? orbitSmall);
                if (s != null) l.Add(new Line(s, false, Dim));
            }
            else
            {
                if (orbitBig != null) l.Add(new Line(orbitBig, true, White));
                if (orbitSmall != null) l.Add(new Line(orbitSmall, false, Dim));
                if (hold != null) l.Add(new Line(hold, false, Dim));
            }
            if (extra != null) foreach (var e in extra) if (!string.IsNullOrEmpty(e)) l.Add(new Line(e, false, Dim));
            return l;
        }

        static string Join(string a, string b) => a == null ? b : b == null ? a : a + " · " + b;

        /// <summary>A block's height (px) for its lines: big lines bigH, others smallH each.</summary>
        public static float Height(IList<Line> lines, float bigH, float smallH)
        {
            float h = 0;
            if (lines != null) foreach (var x in lines) h += x.Big ? bigH : smallH;
            return h;
        }

        /// <summary>The plot box's text places (px; tops-left unless said): the title's name (left, its bottom on the box's top
        /// edge less gap) and distance (right-aligned to the box's right), the axis words in the box's top corners ("ahead"
        /// top-left, "behind" top-right - P13), the scale label bottom-right inside the box, the target block's top-left under the box
        /// (or under a strip below it).</summary>
        public struct PlotSlots
        {
            public Vector2 Name, Distance, Ahead, Behind, Scale, Block;
        }

        /// <summary>
        /// Where the plot's words go, all clear of the drawing: box (x0, y0)-(x1, y1); the words' sizes as measured; pad the inset
        /// inside the box, scaleInset the scale label's bottom above the box's bottom (clear of its bar), gap the space outside it; stripBottom the out-of-plane strip's bottom when it is drawn under the box
        /// (else NaN). The words inside the box are kept inside it (P5): a word wider than its corner is pulled in at the box's edge.
        /// </summary>
        public static PlotSlots Plot(Rect box, Vector2 nameSize, Vector2 distSize, Vector2 aheadSize, Vector2 behindSize, Vector2 scaleSize,
                                     float pad, float scaleInset, float gap, float stripBottom)
        {
            var s = new PlotSlots();
            s.Name = new Vector2(box.X0, box.Y0 - gap - nameSize.Y);
            s.Distance = new Vector2(Math.Max(box.X0 + nameSize.X + gap * 2, box.X1 - distSize.X), box.Y0 - gap - distSize.Y);
            s.Ahead = new Vector2(box.X0 + pad, box.Y0 + pad);
            s.Behind = new Vector2(Math.Max(box.X0 + pad + aheadSize.X + pad, box.X1 - pad - behindSize.X), box.Y0 + pad);
            s.Scale = new Vector2(Math.Max(box.X0 + pad, box.X1 - pad - scaleSize.X), Math.Max(box.Y0 + pad, box.Y1 - scaleInset - scaleSize.Y));
            float under = float.IsNaN(stripBottom) ? box.Y1 : Math.Max(box.Y1, stripBottom);
            s.Block = new Vector2(box.X0, under + gap);
            return s;
        }

        /// <summary>
        /// An off-screen target's edge arrow: where on the screen's edge (inset by margin) the way to it (dir, screen px from the
        /// centre; any length) leaves the screen, and its unit direction. False: no direction.
        /// </summary>
        public static bool EdgePoint(Vector2 size, Vector2 dir, float margin, out Vector2 at, out Vector2 unit)
        {
            at = unit = default;
            float l = dir.Length();
            if (!(l > 1e-6f) || float.IsNaN(l)) return false;
            unit = dir / l;
            var c = size * 0.5f;
            float hx = Math.Max(1f, c.X - margin), hy = Math.Max(1f, c.Y - margin);
            float tx = Math.Abs(unit.X) > 1e-6f ? hx / Math.Abs(unit.X) : float.MaxValue;
            float ty = Math.Abs(unit.Y) > 1e-6f ? hy / Math.Abs(unit.Y) : float.MaxValue;
            at = c + unit * Math.Min(tx, ty);
            return true;
        }

        /// <summary>The edge arrow's label place (top-left): beside the arrow, toward the screen's middle, kept on the screen.</summary>
        public static Vector2 EdgeLabel(Vector2 size, Vector2 at, Vector2 unit, Vector2 labelSize, float gap)
        {
            float x = unit.X > 0.3f ? at.X - gap - labelSize.X : unit.X < -0.3f ? at.X + gap : at.X - labelSize.X * 0.5f;
            float y = unit.Y > 0.3f ? at.Y - gap - labelSize.Y : unit.Y < -0.3f ? at.Y + gap : at.Y - labelSize.Y * 0.5f;
            x = Math.Clamp(x, 2f, Math.Max(2f, size.X - labelSize.X - 2f));
            y = Math.Clamp(y, 2f, Math.Max(2f, size.Y - labelSize.Y - 2f));
            return new Vector2(x, y);
        }
    }
}
