using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.HudMath;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── harness: drive the editor's own paths (a test shell cannot move the real mouse) ──
    private static readonly List<(string label, Vector2 at, Vector2 dir)> _lastHandles = new List<(string, Vector2, Vector2)>();
    private static readonly List<(Node n, Vector2 s)> _lastNodes = new List<(Node, Vector2)>();
    private static List<Sample> _lastSamples = new List<Sample>();
    private static bool? _devLeft; private static bool _devPressed, _devRight;
    private static string _devOp; private static string _devArg; private static double _devPx, _devUntil, _devT;
    private static int _devPhase;

    /// <summary>DEV: pull handle `label` by px (1080p units) for seconds.</summary>
    public static void DevPull(string label, double px, double seconds) { _devOp = "pull"; _devArg = label; _devPx = px; _devUntil = seconds; _devPhase = 0; }
    /// <summary>DEV: left-click the trajectory at minutes from now (adds a node).</summary>
    public static void DevClickAt(double minutes) { _devOp = "click"; _devT = minutes * 60; _devPhase = 0; }
    /// <summary>DEV: drag node i along the path toward minutes-from-now over a few frames, then release.</summary>
    public static void DevSlide(int i, double minutes) { _devOp = "slide"; _devPx = i; _devT = minutes * 60; _devPhase = 0; }
    /// <summary>DEV: right-click node i (its menu).</summary>
    public static void DevRightClickNode(int i) { _devOp = "rclick"; _devPx = i; _devPhase = 0; }

    private static void DevStep(ref Vector2 mouse, float u)
    {
        if (_devOp == null) return;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        switch (_devOp)
        {
            case "pull":
            {
                var h = _lastHandles.Find(x => x.label == _devArg);
                if (h.label == null) { _devOp = null; _devLeft = null; return; }
                if (_devPhase == 0) { mouse = h.at; _devLeft = true; _devPressed = true; _devPhase = 1; _devUntil += wall; _devAt = h.at; _devDir = h.dir; return; }
                mouse = _devAt + _devDir * (float)(_devPx * u);
                if (wall > _devUntil) { _devLeft = null; _devOp = null; }
                return;
            }
            case "click":
            {
                // Press on the path point, then release there next frame (a click, not a drag).
                if (_devPhase == 0)
                {
                    double tAbs = SystemHost.Now + _devT; Sample best = default; double bd = double.MaxValue;
                    foreach (var s in _lastSamples) { double d = Math.Abs(s.T - tAbs); if (d < bd) { bd = d; best = s; } }
                    if (bd == double.MaxValue) { _devOp = null; return; }
                    _devAt = best.S; mouse = _devAt; _devPressed = true; _devLeft = true; _devPhase = 1;
                    return;
                }
                mouse = _devAt; _devLeft = false;
                if (_devPhase++ >= 2) { _devOp = null; _devLeft = null; }
                return;
            }
            case "slide":
            {
                var l = new List<(Node n, Vector2 s)>(_lastNodes); l.Sort((a, b) => a.n.T.CompareTo(b.n.T));
                int i = (int)_devPx;
                if (i < 0 || i >= l.Count) { _devOp = null; _devLeft = null; return; }
                if (_devPhase == 0) { _devAt = l[i].s; mouse = _devAt; _devLeft = true; _devPressed = true; _devPhase = 1; return; }
                double tAbs = SystemHost.Now + _devT; Sample best = default; double bd = double.MaxValue;
                foreach (var sm in _lastSamples) if (!sm.Planned) { double d = Math.Abs(sm.T - tAbs); if (d < bd) { bd = d; best = sm; } }
                if (bd == double.MaxValue) { _devOp = null; _devLeft = null; return; }
                float f = Math.Min(1f, _devPhase / 20f);
                mouse = _devAt + (best.S - _devAt) * f;
                _devLeft = _devPhase < 24;
                if (_devPhase++ > 26) { _devOp = null; _devLeft = null; }
                return;
            }
            case "rclick":
            {
                var l = new List<(Node n, Vector2 s)>(_lastNodes); l.Sort((a, b) => a.n.T.CompareTo(b.n.T));
                int i = (int)_devPx;
                if (i >= 0 && i < l.Count)
                {
                    // Right-click it through the map's own path: the mouse there, and a right press.
                    var sz = MapPipeline.ScreenSize;
                    GameMap.DevMouse = new Vector2(l[i].s.X / sz.X, l[i].s.Y / sz.Y);
                    MapInput.DevRightClick();
                }
                _devOp = null;
                return;
            }
        }
    }
    private static Vector2 _devAt, _devDir;

    // ───────────────────────────── persistence / harness ─────────────────────────────

    public static List<string> SaveLines()
    {
        var l = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string R(double v) => v.ToString("R", inv);
        lock (Nodes)
            foreach (var n in Nodes)
            {
                string line = $"node {R(n.T)} {R(n.Pro)} {R(n.Nor)} {R(n.Rad)}";
                // The fixed target (post-burn orbit): a half-flown burn keeps what is left across a load.
                if (!n.Dirty && n.TBody != null)
                {
                    var e = n.TAfter;
                    line += $" {n.TBody.Replace(" ", "%20")} {R(e.SemiMajorAxis)} {R(e.Eccentricity)} {R(e.Inclination)} {R(e.Raan)} {R(e.ArgPeriapsis)} {R(e.TrueAnomaly)} {R(e.Mu)} {R(e.Epoch)}";
                }
                if (n.Auto) line += " auto";   // (armed: a reload disarmed every node, and warp still stopped for burns that never fired)
                l.Add(line);
            }
        return l;
    }

    public static void Restore(double t, double pro, double nor, double rad, string tBody = null, KeplerianElements? tAfter = null, bool auto = false)
    {
        var n = new Node { T = t, Pro = pro, Nor = nor, Rad = rad, Auto = auto };
        if (tBody != null && tAfter.HasValue) { n.TBody = tBody; n.TAfter = tAfter.Value; n.Dirty = false; }
        lock (Nodes) Nodes.Add(n);
    }

    /// <summary>DEV: as DevFindEncounter, but the pass must have its periapsis radius within [peMin, peMax] m.</summary>
    public static string DevFindArrival(string body, double t, double peMin, double peMax)
    {
        if (!Base(t, out var b0, out var el)) return "no orbit";
        double period = el.IsElliptic ? el.Period : 3600;
        lock (Nodes) Nodes.Clear();
        var n = new Node();
        lock (Nodes) Nodes.Add(n);
        double bestMiss = double.MaxValue; (double T, double P, double R) best = default;
        for (double dt = 120; dt < period; dt += 90)
            for (int step = 1; step <= 40; step++)
            {
                double dv = (step % 2 == 1 ? 1 : -1) * 2.5 * ((step + 1) / 2);
                for (double rad = -20; rad <= 20; rad += 10)
                {
                    n.T = t + dt; n.Pro = dv; n.Nor = 0; n.Rad = rad; n.Edit();
                    if (!Trajectory(t, out var legs, out _)) continue;
                    var l = legs.Find(x => x.Body.Name == body);
                    if (l.Body == null) continue;
                    double pe = l.El.PeriapsisRadius;
                    double miss = pe < peMin ? peMin - pe : pe > peMax ? pe - peMax : 0;
                    if (miss < bestMiss) { bestMiss = miss; best = (n.T, dv, rad); }
                    if (miss == 0) { Selected = n; return $"arrival at {body}: node in {Clock(dt)}, prograde {dv:F1}, radial {rad:F0} m/s, Pe radius {pe / 1000:F1} km"; }
                }
            }
        if (bestMiss < double.MaxValue) { n.T = best.T; n.Pro = best.P; n.Rad = best.R; n.Edit(); Selected = n; return $"closest: Pe off the window by {bestMiss / 1000:F1} km (node kept)"; }
        lock (Nodes) Nodes.Remove(n);
        return "no encounter found";
    }

    /// <summary>DEV: a capture burn at the next periapsis: retrograde to an orbit with its apoapsis at ~3x the periapsis radius.</summary>
    public static string DevCapture(double t)
    {
        if (!Base(t, out var body, out var el)) return "no orbit";
        double rp = el.PeriapsisRadius, mu = body.Mu;
        // time of the next periapsis
        double tp = double.NaN;
        if (el.IsElliptic && IsFinite(el.Period)) { double best = double.MaxValue; for (int k = 0; k < 720; k++) { double tk = t + el.Period * k / 720; double r = OrbitPropagation.StateAt(el, tk).Position.Length(); if (r < best) { best = r; tp = tk; } } }
        else { double best = double.MaxValue; for (int k = 0; k < 2000; k++) { double tk = t + 6 * 3600.0 * k / 2000; double r = OrbitPropagation.StateAt(el, tk).Position.Length(); if (r < best) { best = r; tp = tk; } if (r > best * 1.5 && tk > tp + 60) break; } }
        if (double.IsNaN(tp) || tp < t + 60) return "periapsis too soon";
        double v = OrbitPropagation.StateAt(el, tp).Velocity.Length();
        double ra = Math.Min(3 * rp, double.IsInfinity(body.SoiRadius) ? 3 * rp : 0.5 * body.SoiRadius);   // well inside the sphere
        if (ra < rp) ra = rp;
        double a = 0.5 * (rp + ra);
        double vWant = Math.Sqrt(mu * (2 / rp - 1 / a));
        if (!(v > vWant)) return $"already bound (v {v:F0} <= {vWant:F0} m/s)";
        lock (Nodes) Nodes.Clear();
        var n = new Node { T = tp, Pro = -(v - vWant) };
        lock (Nodes) Nodes.Add(n);
        n.Edit(); Selected = n;
        return $"capture at {body.Name} Pe in {Clock(tp - t)}: {v - vWant:F1} m/s retrograde (v {v:F0} -> {vWant:F0}), Ap radius {ra / 1000:F0} km of reach {body.SoiRadius / 1000:F0} km";
    }

    /// <summary>DEV: search node time x prograde delta-v for a trajectory that enters `body`'s SOI; keep the first found.</summary>
    public static string DevFindEncounter(string body, double t)
    {
        if (!Base(t, out var b0, out var el)) return "no orbit";
        double period = el.IsElliptic ? el.Period : 3600;
        lock (Nodes) Nodes.Clear();
        var n = new Node();
        lock (Nodes) Nodes.Add(n);
        for (int step = 1; step <= 60; step++)
        {
            double dv = (step % 2 == 1 ? 1 : -1) * 5 * ((step + 1) / 2);   // +5, -5, +10, -10, ...
            for (double dt = 120; dt < period; dt += 120)
            {
                n.T = t + dt; n.Pro = dv; n.Nor = 0; n.Rad = 0; n.Edit();
                if (Trajectory(t, out var legs, out _) && legs.Exists(l => l.Body.Name == body))
                {
                    Selected = n;
                    return $"encounter {body}: node in {Clock(dt)} prograde {dv:F0} m/s";
                }
            }
        }
        lock (Nodes) Nodes.Remove(n);
        return "no encounter found";
    }

    public static string Describe(double t)
    {
        var sb = new System.Text.StringBuilder();
        List<Node> nodes; lock (Nodes) nodes = new List<Node>(Nodes);
        nodes.Sort((a, b) => a.T.CompareTo(b.T));
        int i = 0;
        foreach (var n in nodes) sb.Append($"[{i++}] in {Clock(n.T - t)} P {n.Pro:F1} N {n.Nor:F1} R {n.Rad:F1}{(n == Selected ? " (selected)" : "")}; ");
        if (NextBurn(t, out var nx, out var rem, out _)) sb.Append($"next burn left {rem.Length():F2} m/s; ");
        if (Trajectory(t, out var lg, out var ap))
        {
            foreach (var a in ap) { var o = OrbitText(a); if (o != null) sb.Append(o + "; "); }
            sb.Append("patches: ");
            string last = null;
            foreach (var l in lg) if (l.Body.Name != last) { sb.Append(last == null ? l.Body.Name : " > " + l.Body.Name); last = l.Body.Name; }
            sb.Append("; ");
            foreach (var l in lg)
            {
                double rmax = 0;
                for (int k = 0; k <= 40; k++) { double tk = l.T0 + (l.T1 - l.T0) * k / 40; rmax = Math.Max(rmax, OrbitPropagation.StateAt(l.El, tk).Position.Length()); }
                sb.Append($"[{l.Body.Name} {Clock(l.T0 - t)}..{Clock(l.T1 - t)} rmax {rmax / 1000:F0} km e {l.El.Eccentricity:F2}{(l.Planned ? " plan" : "")}] ");
            }
        }
        return sb.Length > 0 ? sb.ToString() : "no nodes";
    }
}
