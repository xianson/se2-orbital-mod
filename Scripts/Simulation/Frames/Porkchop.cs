using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;

#pragma warning disable
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
        string key = $"{from.Name}>{to.Name}|{from.ParkR:F0}|{from.Mu:G3}";
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
        MapPipeline.ScreenPath(new List<Vector2> { min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y) }, true, edge, 1f * u);
        var dim = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.8f);
        float padL = 58f * u, padB = 34f * u, padT = 30f * u, padR = 14f * u;
        var a0 = new Vector2(min.X + padL, min.Y + padT);
        var a1 = new Vector2(max.X - padR, max.Y - padB);
        string toName = SystemHost.DisplayName(to.Name);
        MapPipeline.ScreenText(new Vector2(min.X + 10f * u, min.Y + 6f * u), $"Transfer windows to {toName}" + (to.Name != targetName ? $" (for {targetName})" : "") + "  ·  total delta-v", new ColorSRGB(1f, 1f, 1f, 1f), 0.7f);
        float cw = (a1.X - a0.X) / Nx, ch = (a1.Y - a0.Y) / Ny;
        double lo = _best, hi = 4 * _best;
        int hi_i = -1, hi_j = -1;
        for (int i = 0; i < Nx; i++)
            for (int j = 0; j < Ny; j++)
            {
                if (i * Ny + j >= _next) continue;
                var cmin = new Vector2(a0.X + i * cw, a1.Y - (j + 1) * ch);
                var cmax = new Vector2(cmin.X + cw + 0.5f, cmin.Y + ch + 0.5f);
                MapPipeline.ScreenRect(cmin, cmax, Colour(_dv[i, j], lo, hi));
                if (mouse.X >= cmin.X && mouse.X < cmax.X && mouse.Y >= cmin.Y && mouse.Y < cmax.Y) { hi_i = i; hi_j = j; }
            }
        // Axes.
        MapPipeline.ScreenText(new Vector2(a0.X, a1.Y + 8f * u), "now", dim, 0.55f);
        MapPipeline.ScreenText(new Vector2(a1.X - 70f * u, a1.Y + 8f * u), "+" + Maneuvers.Clock(_span), dim, 0.55f);
        MapPipeline.ScreenText(new Vector2(0.5f * (a0.X + a1.X) - 30f * u, a1.Y + 8f * u), "leave  →", dim, 0.55f);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, a1.Y - 14f * u), Maneuvers.Clock(_tof0), dim, 0.5f);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, a0.Y), Maneuvers.Clock(_tof1), dim, 0.5f);
        MapPipeline.ScreenText(new Vector2(min.X + 6f * u, 0.5f * (a0.Y + a1.Y) - 8f * u), "flight ↑", dim, 0.5f);
        Vector2 At(double dep, double tof) => new Vector2(a0.X + (float)((dep - _t0) / _span) * (a1.X - a0.X), a1.Y - (float)((tof - _tof0) / (_tof1 - _tof0)) * (a1.Y - a0.Y));
        // The cheapest (circled), your plan (a cross), the cell under the mouse.
        string Cell(int i, int j) => $"{_dv[i, j] / 1000:F2} km/s  ·  leave in {Maneuvers.Clock(Dep(i))}  ·  {Maneuvers.Clock(Tof(j))} flight";
        var white = new ColorSRGB(1f, 1f, 1f, 1f);
        if (_bi >= 0) MapPipeline.ScreenCircle(new Vector2(a0.X + (_bi + 0.5f) * cw, a1.Y - (_bj + 0.5f) * ch), 6f * u, white, 1.8f * u);
        float ly = max.Y + 6f * u;
        if (_bi >= 0) { MapPipeline.ScreenText(new Vector2(min.X + 8f * u, ly), "Cheapest   " + Cell(_bi, _bj), new ColorSRGB(0.6f, 1f, 0.6f, 1f), 0.62f); ly += 22f * u; }
        var planCol = new ColorSRGB(0.45f, 0.85f, 1f, 1f);
        if (!double.IsNaN(planDep) && !double.IsNaN(planArr))
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
            MapPipeline.ScreenText(new Vector2(min.X + 8f * u, ly), $"Your plan  {planDv / 1000:F2} km/s so far  ·  leaves in {Maneuvers.Clock(Math.Max(0, planDep - t))}  ·  {toName} Entry after {Maneuvers.Clock(tof)}" + (planNote != null ? "  ·  " + planNote : "") + (inside ? "" : "  (off the chart)"), planCol, 0.62f);
        }
        else MapPipeline.ScreenText(new Vector2(min.X + 8f * u, ly), planNote ?? $"Your plan: no {toName} Entry yet", planCol, 0.62f);
        ly += 22f * u;
        if (hi_i >= 0 && _dv[hi_i, hi_j] < double.PositiveInfinity)
            MapPipeline.ScreenText(new Vector2(min.X + 8f * u, ly), "Here   " + Cell(hi_i, hi_j), dim, 0.62f);
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

    /// <summary>Green at the cheapest, through yellow and orange, to red at four times it (by ratio: the window stands out).</summary>
    static ColorSRGB Colour(double v, double lo, double hi)
    {
        if (double.IsInfinity(v) || double.IsNaN(v)) return new ColorSRGB(0.22f, 0.24f, 0.27f, 0.6f);   // no arc there (a half-turn)
        float x = (float)Math.Clamp(Math.Log(Math.Max(v, lo) / lo) / Math.Log(4.0), 0, 1);
        var stops = new[] { (0.10f, 0.55f, 0.20f), (0.35f, 0.80f, 0.25f), (0.95f, 0.85f, 0.20f), (0.95f, 0.50f, 0.15f), (0.80f, 0.15f, 0.12f) };
        float f = x * (stops.Length - 1);
        int k = Math.Min(stops.Length - 2, (int)f);
        float w = f - k;
        var (r0, g0, b0) = stops[k]; var (r1, g1, b1) = stops[k + 1];
        return new ColorSRGB(r0 + (r1 - r0) * w, g0 + (g1 - g0) * w, b0 + (b1 - b0) * w, 0.92f);
    }
}
