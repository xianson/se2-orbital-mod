using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// A PORKCHOP PLOT: the Rendezvous tab's whole view when the target is a planet or a moon. Departure time
/// (across, from now over one synodic period) against time of flight (up, about a Hohmann transfer's), each
/// cell coloured by the total delta-v of that transfer (a Lambert arc about the body both go round): the burn
/// out of your parking orbit about your planet (or, a moon of the planet you orbit, the burn from your orbit
/// itself) plus the capture into a circular orbit just outside the target's space. Cheap is green, dear red;
/// the cheapest is circled. Your own plan is the cross: when its first burn is and when it reaches the target
/// (its Entry), so pulling the map's nodes moves it across the chart.
/// Display only: it shows windows and where your plan falls; it never makes or tunes a burn.
/// The grid is solved a few rows a frame and kept until the target, the bodies or the window move on.
/// </summary>
public static class Porkchop
{
    public const int Nx = 40, Ny = 30;
    const double BudgetMs = 3.0;
    public static string Status = "-";

    private static string _key;
    private static double _t0, _span, _tof0, _tof1;
    private static double[,] _dv = new double[Nx, Ny];
    private static int _next;       // next cell to solve (row-major), Nx*Ny when done
    private static double _best; private static int _bi = -1, _bj = -1;

    /// <summary>The body directly under <paramref name="c"/> that <paramref name="b"/> is in (itself if its parent is c), or null.</summary>
    public static GravityBody Under(GravityBody b, GravityBody c)
    {
        for (var x = b; x != null; x = x.Parent) if (x.Parent == c) return x;
        return null;
    }

    /// <summary>What you are leaving (about the central body): your planet's state, or your own orbit's.</summary>
    public struct From
    {
        public string Name;
        public Func<double, StateVector> State;   // about the central body
        public double Mu, ParkR;                  // your planet's gravity and your parking orbit's radius; Mu 0: you leave from your orbit itself
    }

    /// <summary>
    /// Draw it in a panel (screen rect) about central body c, from <paramref name="from"/> to the body
    /// <paramref name="to"/> (directly under c). planDep / planArr: your plan's first burn and its arrival at the
    /// target (NaN: none); planDv its burns' total.
    /// </summary>
    public static void Draw(Vector2 min, Vector2 max, GravityBody c, From from, GravityBody to, string targetName,
                            double planDep, double planArr, double planDv, string planNote, double t, Vector2 mouse, float u)
    {
        if (c == null || to == null || from.State == null) return;
        StateVector ToAt(double tk) => Sub(to.OriginInRoot(tk), c.OriginInRoot(tk));
        // The window: one synodic period (capped), flights about a Hohmann transfer's.
        var sf = from.State(t); var st = ToAt(t);
        double r1 = sf.Position.Length(), r2 = st.Position.Length(), mu = c.Mu;
        if (!(r1 > 0) || !(r2 > 0)) return;
        double n1 = Math.Sqrt(mu / (r1 * r1 * r1)), n2 = Math.Sqrt(mu / (r2 * r2 * r2));
        double syn = Math.Abs(n1 - n2) > 1e-12 ? 2 * Math.PI / Math.Abs(n1 - n2) : double.PositiveInfinity;
        double span = Math.Min(syn, 2 * 2 * Math.PI / Math.Min(n1, n2));
        double hoh = Math.PI * Math.Sqrt(Math.Pow(0.5 * (r1 + r2), 3) / mu);
        // (and the departure orbit's shape: energy and angular momentum, which a burn of yours changes)
        double en = 0.5 * sf.Velocity.LengthSquared() - mu / r1, hm = Vector3D.Cross(sf.Position, sf.Velocity).Length();
        string key = $"{from.Name}>{to.Name}|{from.ParkR:F0}|{from.Mu:G3}|{en:G3}|{hm:G3}";
        // a new grid: another target, or the window's start has moved on by a tenth of it
        if (key != _key || t - _t0 > 0.1 * _span || t < _t0)
        {
            _key = key; _t0 = t; _span = span; _tof0 = 0.4 * hoh; _tof1 = 1.6 * hoh;
            _next = 0; _best = double.PositiveInfinity; _bi = _bj = -1;
        }
        Solve(c, from, to, ToAt);

        // The panel.
        MapPipeline.ScreenRect(min, max, new ColorSRGB(0.02f, 0.04f, 0.06f, 0.82f));
        var edge = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.45f);
        _edge.Clear(); _edge.Add(min); _edge.Add(new Vector2(max.X, min.Y)); _edge.Add(max); _edge.Add(new Vector2(min.X, max.Y));
        MapPipeline.ScreenPath(_edge, true, edge, 1f * u);
        var dim = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.8f);
        // (every word at least MinTextPx tall - its axis labels were ~8 px, UI audit C2)
        float axS = Px(0.5f * MapPipeline.MapText), lineS = Px(0.62f * MapPipeline.MapText), headS = Px(0.7f * MapPipeline.MapText);
        float padL = Math.Max(58f * u, MapPipeline.MeasureText("10h 06m", axS).X + 14f * u), padB = MapPipeline.MeasureText("Ag", axS).Y + 16f * u;
        float padT = MapPipeline.MeasureText("Ag", headS).Y + 14f * u, legendW = Math.Max(96f * u, MapPipeline.MeasureText("no transfer", axS).X + 34f * u), padR = legendW + 22f * u;
        var a0 = new Vector2(min.X + padL, min.Y + padT);
        var a1 = new Vector2(max.X - padR, max.Y - padB);
        string toName = SystemHost.DisplayName(to.Name);
        if (_titleFor != _key) { _titleFor = _key; _title = $"Transfer windows to {toName}" + (to.Name != targetName ? $" (for {targetName})" : "") + "  ·  colour: total delta-v"; _axNow = "now"; _axEnd = "+" + Maneuvers.Clock(_span); _axTof0 = Maneuvers.Clock(_tof0); _axTof1 = Maneuvers.Clock(_tof1); }
        MapPipeline.ScreenText(new Vector2(min.X + 10f * u, min.Y + 6f * u), _title, new ColorSRGB(1f, 1f, 1f, 1f), headS);
        float cw = (a1.X - a0.X) / Nx, ch = (a1.Y - a0.Y) / Ny;
        double lo = _best;
        int hi_i = -1, hi_j = -1;
        if (SEAerospace.MapUi.PorkchopGrid.CellAt(mouse.X, mouse.Y, a0.X, a0.Y, a1.X, a1.Y, Nx, Ny, out int mi, out int mj) && mi * Ny + mj < _next) { hi_i = mi; hi_j = mj; }
        for (int i = 0; i < Nx; i++)
            for (int j = 0; j < Ny; j++)
            {
                if (i * Ny + j >= _next) continue;
                var cmin = new Vector2(a0.X + i * cw, a1.Y - (j + 1) * ch);
                var cmax = new Vector2(cmin.X + cw + 0.5f, cmin.Y + ch + 0.5f);
                MapPipeline.ScreenRect(cmin, cmax, Colour(_dv[i, j], lo));
            }
        // the cell under the mouse: outlined (a click makes its burn)
        if (hi_i >= 0)
        {
            var hmin = new Vector2(a0.X + hi_i * cw, a1.Y - (hi_j + 1) * ch);
            _edge.Clear(); _edge.Add(hmin); _edge.Add(hmin + new Vector2(cw, 0)); _edge.Add(hmin + new Vector2(cw, ch)); _edge.Add(hmin + new Vector2(0, ch));
            MapPipeline.ScreenPath(_edge, true, new ColorSRGB(1f, 1f, 1f, 0.95f), 1.6f * u);
        }
        // Axes.
        MapPipeline.ScreenText(new Vector2(a0.X, a1.Y + 6f * u), _axNow, dim, axS);
        MapPipeline.ScreenText(new Vector2(a1.X - MapPipeline.MeasureText(_axEnd, axS).X, a1.Y + 6f * u), _axEnd, dim, axS);
        MapPipeline.ScreenText(new Vector2(0.5f * (a0.X + a1.X) - MapPipeline.MeasureText(AxLeave, axS).X * 0.5f, a1.Y + 6f * u), AxLeave, dim, axS);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, a1.Y - MapPipeline.MeasureText("Ag", axS).Y), _axTof0, dim, axS);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, a0.Y), _axTof1, dim, axS);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, 0.5f * (a0.Y + a1.Y) - 8f * u), AxFlight, dim, axS);
        Vector2 At(double dep, double tof) => new Vector2(a0.X + (float)((dep - _t0) / _span) * (a1.X - a0.X), a1.Y - (float)((tof - _tof0) / (_tof1 - _tof0)) * (a1.Y - a0.Y));
        // The cheapest (circled), your plan (a cross), the cell under the mouse.
        var white = new ColorSRGB(1f, 1f, 1f, 1f);
        if (_bi >= 0) MapPipeline.ScreenCircle(new Vector2(a0.X + (_bi + 0.5f) * cw, a1.Y - (_bj + 0.5f) * ch), 6f * u, white, 1.8f * u);
        // THE LEGEND (UI audit C2: no colour legend; black cells and the white circle unexplained): the scale from the cheapest
        // (green) to 4x it (red) with its numbers, the black "no transfer" and the white circle "cheapest", right of the plot
        Legend(new Vector2(a1.X + 16f * u, a0.Y), new Vector2(max.X - 8f * u, a1.Y), axS, u, dim);
        float ly = max.Y + 6f * u, lh = MapPipeline.MeasureText("Ag", lineS).Y + 6f * u;
        var plate = new ColorSRGB(0.02f, 0.04f, 0.06f, 0.82f);
        void Line(string s, ColorSRGB c) { if (s == null) return; var sz = MapPipeline.MeasureText(s, lineS); MapPipeline.ScreenRect(new Vector2(min.X, ly - 2f * u), new Vector2(min.X + sz.X + 16f * u, ly + sz.Y + 2f * u), plate); MapPipeline.ScreenText(new Vector2(min.X + 8f * u, ly), s, c, lineS); ly += lh; }
        if (_bi >= 0) { if (_bestFor != _best) { _bestFor = _best; _bestLine = "Cheapest   " + Cell(_bi, _bj); } Line(_bestLine, new ColorSRGB(0.6f, 1f, 0.6f, 1f)); }
        var planCol = new ColorSRGB(0.45f, 0.85f, 1f, 1f);
        bool plan = !double.IsNaN(planDep) && !double.IsNaN(planArr);
        if (plan)
        {
            double tof = planArr - planDep;
            var ps = At(planDep, tof);
            bool inside = ps.X >= a0.X && ps.X <= a1.X && ps.Y >= a0.Y && ps.Y <= a1.Y;
            if (inside)
            {
                float r = 11f * u;
                var shade = new ColorSRGB(0f, 0f, 0f, 0.85f);
                MapPipeline.ScreenLine(ps - new Vector2(r + 1.5f * u, 0), ps + new Vector2(r + 1.5f * u, 0), shade, 5.5f * u);
                MapPipeline.ScreenLine(ps - new Vector2(0, r + 1.5f * u), ps + new Vector2(0, r + 1.5f * u), shade, 5.5f * u);
                MapPipeline.ScreenLine(ps - new Vector2(r, 0), ps + new Vector2(r, 0), planCol, 2.6f * u);
                MapPipeline.ScreenLine(ps - new Vector2(0, r), ps + new Vector2(0, r), planCol, 2.6f * u);
            }
            Line($"Your plan  {planDv / 1000:F2} km/s so far  ·  leaves in {Maneuvers.Clock(Math.Max(0, planDep - t))}  ·  {toName} Entry after {Maneuvers.Clock(tof)}" + (planNote != null ? "  ·  " + planNote : "") + (inside ? "" : "  (off the chart)"), planCol);
        }
        else Line(planNote ?? $"Your plan: no {toName} Entry yet", planCol);
        if (hi_i >= 0 && _dv[hi_i, hi_j] < double.PositiveInfinity)
        {
            if (hi_i != _hereI || hi_j != _hereJ || _hereLine == null) { _hereI = hi_i; _hereJ = hi_j; _hereLine = "Here   " + Cell(hi_i, hi_j) + "   ·   click: make this burn"; }
            Line(_hereLine, white);
        }
        else if (hi_i >= 0) Line("Here: no transfer (no arc joins them in that time)", dim);
        // A click on a solved cell (not the end of a drag, no menu open): that window's first burn (RendezvousView makes it).
        Picked = false;
        if (hi_i >= 0 && _dv[hi_i, hi_j] < double.PositiveInfinity && MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open)
        { Picked = true; PickDep = _t0 + Dep(hi_i); PickTof = Tof(hi_j); PickDv = _dv[hi_i, hi_j]; }
        if (_devPick >= 0 && _devPick < Nx * Ny && _devPick < _next && _dv[_devPick / Ny, _devPick % Ny] < double.PositiveInfinity)
        { Picked = true; PickDep = _t0 + Dep(_devPick / Ny); PickTof = Tof(_devPick % Ny); PickDv = _dv[_devPick / Ny, _devPick % Ny]; }
        _devPick = -1;
        Status = _next < Nx * Ny ? $"porkchop {_next * 100 / (Nx * Ny)}%" : $"porkchop best {_best:F0} m/s";
    }

    static double Dep(int i) => _span * (i + 0.5) / Nx;
    static double Tof(int j) => _tof0 + (_tof1 - _tof0) * (j + 0.5) / Ny;
    static StateVector Sub(StateVector a, StateVector b) => new StateVector(a.Position - b.Position, a.Velocity - b.Velocity);

    static void Solve(GravityBody c, From from, GravityBody to, Func<double, StateVector> toAt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_next < Nx * Ny && sw.Elapsed.TotalMilliseconds < BudgetMs)
        {
            int i = _next / Ny, j = _next % Ny;
            _next++;
            double td = _t0 + Dep(i), ta = td + Tof(j);
            var d = from.State(td);
            var a = toAt(ta);
            double best = double.PositiveInfinity;
            Vector3D nrm = Vector3D.Cross(d.Position, d.Velocity);
            foreach (bool pro in new[] { true, false })
            {
                if (!Lambert.Solve(d.Position, a.Position, Tof(j), c.Mu, pro, nrm, out var v1, out var v2)) continue;
                double vinf1 = (v1 - d.Velocity).Length(), vinf2 = (v2 - a.Velocity).Length();
                double dep = from.Mu > 0 ? Burn(vinf1, from.Mu, from.ParkR) : vinf1;   // (from your own orbit: the burn itself)
                double arr = Burn(vinf2, to.Mu, Capture(to));
                best = Math.Min(best, dep + arr);
            }
            _dv[i, j] = best;
            if (best < _best) { _best = best; _bi = i; _bj = j; }
        }
    }

    /// <summary>A hyperbolic excess speed to or from a circular orbit of radius r (the Oberth burn at periapsis).</summary>
    static double Burn(double vinf, double mu, double r) => r > 0 && mu > 0 ? Math.Sqrt(vinf * vinf + 2 * mu / r) - Math.Sqrt(mu / r) : vinf;

    /// <summary>The capture orbit's radius at a planet: just outside its keep radius (its frames' space).</summary>
    static double Capture(GravityBody b)
    {
        var def = SystemHost.Registry?.FindDefinition(b.Name);
        return def != null ? PlanetBerths.KeepRadius(def) * 1.25 : 0;
    }

    /// <summary>Green at the cheapest, through yellow and orange, to red at four times it (by ratio: the window stands out);
    /// a cell with no arc (a half-turn, or no solution in that time) black (SEAerospace.MapUi.PorkchopGrid: the legend's
    /// scale; the stops were a new array for every cell, every frame).</summary>
    static ColorSRGB Colour(double v, double lo)
    {
        double x = SEAerospace.MapUi.PorkchopGrid.Position(v, lo);
        if (double.IsNaN(x)) return NoArc;
        var (r, g, b) = SEAerospace.MapUi.PorkchopGrid.ColourAt(x);
        return new ColorSRGB(r, g, b, 0.92f);
    }
    static readonly ColorSRGB NoArc = new ColorSRGB(0.05f, 0.06f, 0.07f, 0.92f);

    /// <summary>The legend in [min, max]: "total delta-v", the scale top (red, 4x the cheapest) to bottom (green, the cheapest)
    /// with three numbers, then the black "no transfer" swatch and the white circle "cheapest".</summary>
    static void Legend(Vector2 min, Vector2 max, float s, float u, ColorSRGB dim)
    {
        float lh = MapPipeline.MeasureText("Ag", s).Y;
        MapPipeline.ScreenText(min, "total Δv", dim, s);
        float y0 = min.Y + lh + 6f * u, y1 = max.Y - 2 * (lh + 8f * u) - 6f * u, bw = 12f * u;
        if (y1 - y0 > 20f * u && _best < double.PositiveInfinity)
        {
            const int n = 24;
            for (int k = 0; k < n; k++)
            {
                double xa = 1.0 - (k + 0.5) / n;   // (top: dear)
                var (r, g, b) = SEAerospace.MapUi.PorkchopGrid.ColourAt(xa);
                float ya = y0 + (y1 - y0) * k / n, yb = y0 + (y1 - y0) * (k + 1) / n + 0.5f;
                MapPipeline.ScreenRect(new Vector2(min.X, ya), new Vector2(min.X + bw, yb), new ColorSRGB(r, g, b, 0.95f));
            }
            if (_legendFor != _best)
            {
                _legendFor = _best;
                _legend0 = $"{SEAerospace.MapUi.PorkchopGrid.ValueAt(0, _best) / 1000:F2} km/s";
                _legend1 = $"{SEAerospace.MapUi.PorkchopGrid.ValueAt(0.5, _best) / 1000:F2}";
                _legend2 = $"{SEAerospace.MapUi.PorkchopGrid.ValueAt(1, _best) / 1000:F2}+";
            }
            float tx = min.X + bw + 6f * u;
            MapPipeline.ScreenText(new Vector2(tx, y0), _legend2, dim, s);
            MapPipeline.ScreenText(new Vector2(tx, 0.5f * (y0 + y1) - lh * 0.5f), _legend1, dim, s);
            MapPipeline.ScreenText(new Vector2(tx, y1 - lh), _legend0, dim, s);
        }
        float sy = max.Y - 2 * (lh + 8f * u);
        MapPipeline.ScreenRect(new Vector2(min.X, sy), new Vector2(min.X + bw, sy + lh), NoArc);
        _edge.Clear(); _edge.Add(new Vector2(min.X, sy)); _edge.Add(new Vector2(min.X + bw, sy)); _edge.Add(new Vector2(min.X + bw, sy + lh)); _edge.Add(new Vector2(min.X, sy + lh));
        MapPipeline.ScreenPath(_edge, true, HudPanel.Alpha(dim, 0.6f), 1f * u);
        MapPipeline.ScreenText(new Vector2(min.X + bw + 6f * u, sy), "no transfer", dim, s);
        sy += lh + 8f * u;
        MapPipeline.ScreenCircle(new Vector2(min.X + bw * 0.5f, sy + lh * 0.5f), 5f * u, new ColorSRGB(1f, 1f, 1f, 1f), 1.6f * u);
        MapPipeline.ScreenText(new Vector2(min.X + bw + 6f * u, sy), "cheapest", dim, s);
    }

    /// <summary>A text scale raised to at least MinTextPx tall (a line of it).</summary>
    static float Px(float scale) => SEAerospace.MapUi.TextSize.AtLeast(scale, MapPipeline.MeasureText("Ag", 1f).Y, MinTextPx);
    /// <summary>The porkchop's smallest line height (px): its labels were ~8 px (UI audit C2: "readable axis labels, >= 11 px").</summary>
    public const float MinTextPx = 15f;

    static string Cell(int i, int j) => $"{_dv[i, j] / 1000:F2} km/s  ·  leave in {Maneuvers.Clock(Dep(i))}  ·  {Maneuvers.Clock(Tof(j))} flight";

    static readonly List<Vector2> _edge = new List<Vector2>(4);
    static string _titleFor, _title, _axNow, _axEnd, _axTof0, _axTof1, _bestLine, _hereLine, _legend0, _legend1, _legend2;
    static double _bestFor = double.NaN, _legendFor = double.NaN; static int _hereI = -1, _hereJ = -1;
    const string AxLeave = "leave  →", AxFlight = "flight ↑";

    /// <summary>A cell was clicked this frame: its departure (universe time), time of flight (s) and total delta-v (m/s).</summary>
    public static bool Picked; public static double PickDep, PickTof, PickDv;
    static int _devPick = -1;
    /// <summary>DEV: click cell (i, j) on the next draw ("cheapest": the best cell).</summary>
    public static string DevPick(string what)
    {
        if (what == "best" || what == "cheapest") { if (_bi < 0) return "no cheapest yet"; _devPick = _bi * Ny + _bj; return $"picking the cheapest ({_bi},{_bj})"; }
        var p = what.Split(',');
        if (p.Length == 2 && int.TryParse(p[0], out int i) && int.TryParse(p[1], out int j)) { _devPick = i * Ny + j; return $"picking ({i},{j})"; }
        return "porkchop pick best | i,j";
    }
}
