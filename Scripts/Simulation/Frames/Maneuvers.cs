using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// MANEUVER NODES (docs/design-maneuver-nodes.md), manual editor.
///
/// A node is a burn at an absolute time on the player's trajectory: delta-v in prograde / normal /
/// radial. Nodes chain: each applies to the trajectory the earlier ones produced, and the trajectory
/// is patched-conic (it follows SOI changes). On the map:
///  - click your trajectory to add a node there;
///  - drag a node's handles (P / R prograde-retrograde, N / AN normal, RO / RI radial out-in): the
///    further you pull, the faster the delta-v grows;
///  - drag the node itself to slide it along the trajectory; right-click it to delete it;
///  - the planned trajectory is drawn dashed; with a sector selected, its closest approach to that
///    sector's site is marked.
/// In flight, the next node is a HUD marker in the burn direction with the delta-v still to do,
/// counting down as you thrust (planned velocity minus actual, both at the current time); the node
/// completes below 0.1 m/s. Warp stops ahead of a node.
/// </summary>
public static class Maneuvers
{
    /// <summary>
    /// A burn. Pro/Nor/Rad are what the player set, relative to the trajectory at the time they set it;
    /// that fixes a TARGET (the post-burn orbit). Burning does not move the target: what is left is the
    /// target's velocity minus the current orbit's, at the node point (as KSP). Editing re-targets.
    /// </summary>
    public sealed class Node
    {
        public double T; public double Pro, Nor, Rad;
        public bool Dirty = true;               // re-target on the next trajectory pass
        public bool Auto;                       // fly it automatically at the burn start
        public string TBody; public KeplerianElements TAfter;
        public void Edit() => Dirty = true;
    }

    public static readonly List<Node> Nodes = new List<Node>();   // lock(Nodes)
    public static Node Selected;
    /// <summary>Under the mouse this frame (for the context menu): a node, or a time on the trajectory.</summary>
    public static Node HoverNode; public static double HoverT = double.NaN;

    public static void AddNodeAt(double T)
    {
        var n = new Node { T = T };
        lock (Nodes) Nodes.Add(n);
        Selected = n;
    }

    public static void Delete(Node n, bool andLater)
    {
        lock (Nodes)
        {
            if (andLater) Nodes.RemoveAll(x => x.T >= n.T); else Nodes.Remove(n);
        }
        if (Selected != null && !Nodes.Contains(Selected)) Selected = null;
    }

    public static void ClearAll() { lock (Nodes) Nodes.Clear(); Selected = null; }
    /// <summary>A new world: the last one's nodes (a reload doubled the saved ones), target, Lagrange plan and path cache.</summary>
    public static void ResetWorld()
    {
        ClearAll();
        Target = null; _lag = null;
        _cT = double.NaN; _cLegs = null; _cApplied = null; _cOk = false;
    }

    /// <summary>The next burn, one line for the orbit card (null when none).</summary>
    public static string BurnLine;
    /// <summary>The next burn's direction (world axes) and what is left (m/s), from the last HUD tick.</summary>
    public static Vector3D BurnDirWorld; public static double BurnLeft;
    public const double WarpLead = 30.0;      // s: warp stops this long before a burn (time to line up)
    public const double AutoWarpLead = 5.0;   // s: before an auto-burn (it turns and fires by itself)

    /// <summary>How long before the next burn warp stops: shorter when that node flies itself.</summary>
    public static double WarpLeadAt(double t0)
    {
        double tn = NextNodeTime(t0);
        if (double.IsNaN(tn)) return WarpLead;
        bool auto = false;
        lock (Nodes) foreach (var n in Nodes) if (n.T == tn && n.Auto) auto = true;
        // An auto-burn turns to its burn during warp, so it needs no time to line up after; unless it
        // is still well off (warp faster than the gyros can follow the burn round): then the full lead.
        return auto && DevFlight.OffBurnDeg <= 5 ? AutoWarpLead : WarpLead;
    }
    public const double DoneDv = 0.1;         // m/s
    public static string Status = "";

    static readonly ColorSRGB YouColor = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);   // your orbit: cyan (as KSP)
    static readonly ColorSRGB PlanColor = new ColorSRGB(0.35f, 0.90f, 1.00f, 0.9f);
    static readonly ColorSRGB NodeColor = new ColorSRGB(0.35f, 0.90f, 1.00f, 1f);
    static readonly ColorSRGB ProColor = new ColorSRGB(0.85f, 0.95f, 0.30f, 1f);
    static readonly ColorSRGB NorColor = new ColorSRGB(0.85f, 0.45f, 1.00f, 1f);
    static readonly ColorSRGB RadColor = new ColorSRGB(0.35f, 0.85f, 1.00f, 1f);
    static readonly ColorSRGB TgtColor = new ColorSRGB(1.00f, 0.55f, 0.25f, 1f);

    // ───────────────────────────── trajectory ─────────────────────────────

    public struct Leg { public GravityBody Body; public KeplerianElements El; public double T0, T1; public bool Planned; }
    public struct Applied { public Node Node; public GravityBody Body; public StateVector Before; public Vector3D Dv; public KeplerianElements After; }

    /// <summary>The player's orbit now: the rails frame's, or the local orbit in a planet cell.</summary>
    public static bool Base(double t, out GravityBody body, out KeplerianElements el)
    {
        body = null; el = default;
        var reg = SystemHost.Registry;
        if (reg == null) return false;
        var f = FrameHost.PlayerFrame;
        if (f != null) { body = reg.Find(f.ParentBodyName); el = f.Elements; return body != null; }
        string b = FrameHost.ObserverPlanet;
        if (b == null || !FrameHost.TryGetLocalOrbit(b, t, out el)) return false;
        body = reg.Find(b);
        return body != null;
    }

    /// <summary>The prograde / normal / radial-out axes at a state.</summary>
    public static void Axes(StateVector s, out Vector3D P, out Vector3D N, out Vector3D R)
    {
        P = s.Velocity.LengthSquared() > 1e-9 ? Vector3D.Normalize(s.Velocity) : Vector3D.UnitY;
        Vector3D h = Vector3D.Cross(s.Position, s.Velocity);
        N = h.LengthSquared() > 1e-9 ? Vector3D.Normalize(h) : Vector3D.UnitZ;
        R = Vector3D.Cross(P, N);
    }

    /// <summary>The whole trajectory from t: legs (patched), and each node as applied.</summary>
    private static double _cT = double.NaN; private static long _cSig; private static List<Leg> _cLegs; private static List<Applied> _cApplied; private static bool _cOk;

    /// <summary>The trajectory, memoised: the map, the HUD and warp ask for it every frame.</summary>
    public static bool Trajectory(double t, out List<Leg> legs, out List<Applied> applied)
    {
        long sig = Signature(t);
        if (t == _cT && sig == _cSig && _cLegs != null) { legs = _cLegs; applied = _cApplied; return _cOk; }
        _cOk = TrajectoryUncached(t, out legs, out applied);
        if (_cOk) CutAtImpact(legs, applied);
        _cT = t; _cSig = Signature(t); _cLegs = legs; _cApplied = applied;   // after: re-targeting clears Dirty
        return _cOk;
    }

    /// <summary>
    /// A path that hits a body ends there: the leg is cut at the impact and nothing follows (it drew a
    /// 'Palatine Escape' after the impact). The current orbit on the ground is handled elsewhere.
    /// </summary>
    private static void CutAtImpact(List<Leg> legs, List<Applied> applied)
    {
        for (int i = 0; i < legs.Count; i++)
        {
            var l = legs[i];
            double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
            if (!(R > 0) || !(l.El.PeriapsisRadius < R) || !(l.T1 > l.T0)) continue;
            double span = l.T1 - l.T0, prev = l.T0, hit = double.NaN;
            if (OrbitPropagation.StateAt(l.El, l.T0).Position.Length() < R) continue;   // starts inside: leave it
            // At least 64 samples a revolution (a long leg spans many; one dip is a small part of each).
            int n = 240;
            if (l.El.IsElliptic && IsFinite(l.El.Period) && l.El.Period > 0) n = (int)Math.Min(20000, Math.Max(240, span / l.El.Period * 64));
            for (int k = 1; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                if (OrbitPropagation.StateAt(l.El, tk).Position.Length() < R)
                {
                    double a = prev, b = tk;
                    for (int q = 0; q < 30; q++) { double m = 0.5 * (a + b); if (OrbitPropagation.StateAt(l.El, m).Position.Length() < R) b = m; else a = m; }
                    hit = 0.5 * (a + b); break;
                }
                prev = tk;
            }
            if (double.IsNaN(hit)) continue;
            l.T1 = hit; legs[i] = l;
            legs.RemoveRange(i + 1, legs.Count - i - 1);
            applied?.RemoveAll(a => a.Node != null && a.Node.T > hit);   // nothing is flown after the impact
            return;
        }
    }

    /// <summary>What the trajectory depends on, as a 64-bit hash (it was a string built every frame: ~150 KB/s
    /// of garbage and the doubles' formatting).</summary>
    private static long Signature(double t)
    {
        ulong h = 0x9E3779B97F4A7C15UL;
        void Mix(ulong x) { h ^= x; h *= 0xbf58476d1ce4e5b9UL; h ^= h >> 31; }
        void D(double x) => Mix((ulong)BitConverter.DoubleToInt64Bits(x));
        if (Base(t, out var b, out var el)) { Mix((ulong)(uint)(b.Name?.GetHashCode() ?? 0)); D(el.SemiMajorAxis); D(el.Eccentricity); D(el.Epoch); D(el.TrueAnomaly); D(el.Inclination); }
        else Mix(1);
        lock (Nodes)
            foreach (var n in Nodes)
            {
                Mix(0x7C);   // ('|')
                D(n.T); D(n.Pro); D(n.Nor); D(n.Rad); Mix(n.Dirty ? 1UL : 2UL); Mix((ulong)(uint)(n.TBody?.GetHashCode() ?? 0));
            }
        return (long)h;
    }

    private static bool TrajectoryUncached(double t, out List<Leg> legs, out List<Applied> applied)
    {
        legs = new List<Leg>(); applied = new List<Applied>();
        if (!Base(t, out var body, out var el)) return false;
        List<Node> nodes;
        lock (Nodes) { nodes = new List<Node>(Nodes); }
        nodes.Sort((a, b) => a.T.CompareTo(b.T));
        double tc = t;
        bool planned = false;
        foreach (var n in nodes)
        {
            double Tn = Math.Max(n.T, tc);   // a node already past applies now (burning late)
            StateVector st;
            // (the coast the burn integrates from: the last arc's, in ITS body's frame - after a sphere-of-influence change
            //  the old body's elements with the new body's mu made the finite burn garbage)
            KeplerianElements coastEl = el; double coastT0 = tc;
            if (Tn - tc < 0.5)
                st = OrbitPropagation.StateAt(el, Tn);   // the node is now: no coast before it
            else
            {
                var arcs = PatchedConic.Propagate(body, OrbitPropagation.StateAt(el, tc), tc, Tn - tc, 8, 256);
                if (arcs == null || arcs.Count == 0) return legs.Count > 0;
                foreach (var a in arcs) legs.Add(new Leg { Body = a.Body, El = a.Elements, T0 = a.StartTime, T1 = Math.Min(a.EndTime, Tn), Planned = planned });
                var last = arcs[arcs.Count - 1];
                body = last.Body;
                st = OrbitPropagation.StateAt(last.Elements, Tn);
                coastEl = last.Elements; coastT0 = last.StartTime;
            }
            KeplerianElements after;
            Vector3D dv;
            // A node in a Lagrange sector (last plan's stay): the sector's own directions (prograde your
            // drift relative to the point), re-targeted every pass until its burn is near (the Kepler
            // guess of where you will be drifts off the sector's orbit); frozen from then, so the burn
            // closes on it.
            var lg = _lag;
            Vector3D lom = Vector3D.Zero;
            bool inLagSector = lg != null && Tn >= lg.TE - 1 && (double.IsNaN(lg.TX) || Tn <= lg.TX)
                               && SectorHomes.LagrangeOrbit(SystemHost.Registry?.Find(lg.Site.Home.Host), Tn, out lom, out _);
            if (inLagSector && t < BurnStart(n) - AutoBurn.AlignLead - 5) n.Dirty = true;
            if (n.Dirty || n.TBody != body.Name)
            {
                Axes(st, out var P, out var N, out var R);
                if (inLagSector)
                {
                    SectorHomes.LagrangeState(lg.Site.Home, SystemHost.Registry, Tn, out var llp, out var llv);
                    var bo = body.OriginInRoot(Tn);
                    Vector3D ld = bo.Position + st.Position - llp, lu = bo.Velocity + st.Velocity - llv - Vector3D.Cross(lom, bo.Position + st.Position - llp);
                    Axes(new StateVector(ld, lu), out P, out N, out R);
                }
                dv = P * n.Pro + N * n.Nor + R * n.Rad;
                after = FiniteBurn(body, coastEl, coastT0, Tn, st, dv);
                if (!IsFinite(after.SemiMajorAxis)) return legs.Count > 0;   // (true with no legs drew nothing and reported a path)
                n.TAfter = after; n.TBody = body.Name; n.Dirty = false;
            }
            else
            {
                after = n.TAfter;
                dv = OrbitPropagation.StateAt(after, Tn).Velocity - st.Velocity;   // what is left
            }
            applied.Add(new Applied { Node = n, Body = body, Before = st, Dv = dv, After = after });
            el = after; tc = Tn; planned = true;
        }
        double horizon = el.IsElliptic && IsFinite(el.Period) ? Math.Min(el.Period * 1.02, 10 * 86400.0) : 10 * 86400.0;   // long enough for a transfer
        var tail = PatchedConic.Propagate(body, OrbitPropagation.StateAt(el, tc), tc, horizon, 8, 256);
        if (tail != null) foreach (var a in tail) legs.Add(new Leg { Body = a.Body, El = a.Elements, T0 = a.StartTime, T1 = Math.Min(a.EndTime, tc + horizon), Planned = planned });
        return legs.Count > 0;
    }

    public static Vector3D RootAt(Leg l, double t) => l.Body.OriginInRoot(t).Position + OrbitPropagation.StateAt(l.El, t).Position;

    // ───────────────────────────── map editor ─────────────────────────────

    private enum Drag { None, Handle, Slide }
    private static Drag _drag;
    private static int _axis;               // 0 pro, 1 nor, 2 rad
    private static double _sign;
    private static Vector2 _anchor, _dir;   // handle anchor (screen) and pull direction
    private static double _lastFrame;
    /// <summary>The mouse is on the editor (a node, a handle, the trajectory) or dragging: the map must not select a sector.</summary>
    public static bool ClaimsMouse;
    /// <summary>The mouse is on a node or a handle (or dragging one): the map camera must not pan.</summary>
    public static bool OnGizmo;
    /// <summary>The selected node's handles are folded away (zoomed out; hover the node to open them).</summary>
    public static bool GizmoCompact;
    private static bool _addPending; private static double _addT; private static Vector2 _addAt;

    private struct Sample { public double T; public Vector2 S; public bool Planned; }
    private static readonly List<Sample> _samplesA = new List<Sample>(1024), _samplesB = new List<Sample>(1024);

    /// <summary>
    /// Draw the trajectory, nodes and handles, and run the editor. Called inside the map's UI batch.
    /// toMap maps a sun-centred model position at time t to the map's local frame; W local to world.
    /// </summary>
    public static void MapDraw(Func<Vector3D, double, Vector3D> toMap, Func<Vector3D, Vector3D> W, double limit,
                               double t, Vector2 mouse, string selectedSector, string focusBody, bool allLive = false)
    {
        ClaimsMouse = false;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);   // sizes are for 1080p
        DevStep(ref mouse, u);
        bool lDown = _devLeft ?? MapInput.Left, lPressed = _devPressed || (_devLeft == null && MapInput.LeftPressed);
        bool rPressed = _devRight || MapInput.RightPressed;
        if (MapMenu.Open) { lPressed = false; rPressed = false; }   // the context menu has the clicks
        _devPressed = false; _devRight = false;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double dt = _lastFrame > 0 ? Math.Min(0.1, now - _lastFrame) : 0; _lastFrame = now;
        if (!Trajectory(t, out var legs, out var applied)) { Status = "no trajectory"; return; }
        if (OrbitHud.Walking && Nodes.Count == 0) { Status = "walking about"; return; }   // no path while walking about (a plan still shows)
        // A Lagrange sector ahead (or around you) takes over the path from its entry.
        long lg0 = ModCost.Start();
        var lagPlan = LagrangeCached(t, legs, applied);
        ModCost.Sec("trajectory.lagrange").Stop(lg0);
        if (lagPlan != null) legs = CutAtLagrange(legs, lagPlan.TE);
        HudPanel.BeginLabels();
        // The selected node's gizmo (as drawn last frame) comes first: other tags keep clear of it.
        if (Selected != null)
        {
            foreach (var h in _lastHandles) MapPipeline.Reserve(h.at - new Vector2(13f * u, 13f * u), h.at + new Vector2(13f * u, 13f * u));
            foreach (var ln in _lastNodes) if (ln.n == Selected) MapPipeline.Reserve(ln.s - new Vector2(12f * u, 12f * u), ln.s + new Vector2(12f * u, 12f * u));
        }

        // The PATCHED trajectory: each patch (an arc about one body) in its own colour, drawn about its
        // body. About the view's body, or the sun, at true time; about any other body (an encounter),
        // about that body where it will be when the trajectory gets there (KSP's encounter ghost).
        var anchor = new Dictionary<GravityBody, double>();
        foreach (var l in legs) if (!anchor.ContainsKey(l.Body)) anchor[l.Body] = Math.Max(t, l.T0);
        // A ghost-pinned arc is mapped at its pin time as a whole: the view's own centre moves too (Kemik
        // runs about 2.4 km/s round the sun), and mixing times would smear the arc across the map.
        bool Live(GravityBody b) => allLive || b.IsRoot || b.Name == focusBody;   // (allLive: a relative view maps every arc at its true time)
        // Each pass about a body has its own ghost: pinned where that body is when that pass begins.
        double PinT(GravityBody b, double tk)
        {
            if (Live(b)) return tk;
            double pin = double.NaN;
            for (int q = 0; q < legs.Count; q++)
            {
                if (legs[q].Body != b) continue;
                int s0 = q; while (s0 > 0 && legs[s0 - 1].Body == b) s0--;   // start of this pass
                if (tk >= legs[q].T0 - 1e-6 && tk <= legs[q].T1 + 1e-6) return Math.Max(t, legs[s0].T0);
                if (double.IsNaN(pin)) pin = Math.Max(t, legs[s0].T0);
            }
            return double.IsNaN(pin) ? tk : pin;
        }
        Vector3D Place(GravityBody b, Vector3D rel, double tk) => b.OriginInRoot(PinT(b, tk)).Position + rel;
        Vector3D Loc(GravityBody b, Vector3D rel, double tk) => toMap(Place(b, rel, tk), PinT(b, tk));
        Vector3D LegLoc(Leg l, double tk) => Loc(l.Body, OrbitPropagation.StateAt(l.El, tk).Position, tk);

        // (two lists in turn: the last frame's is kept for the mouse; a new one every frame, grown by doubling, was
        //  ~1.5 MB/s of garbage with the map open)
        var samples = ReferenceEquals(_lastSamples, _samplesA) ? _samplesB : _samplesA;
        samples.Clear();
        int patch = 0;
        var legColour = new List<ColorSRGB>();
        var legPatch = new List<int>();
        for (int li = 0; li < legs.Count; li++)
        {
            var l = legs[li];
            if (li > 0 && l.Body != legs[li - 1].Body) patch++;
            // Each later patch fainter; past MaxPatches none (KSP's conic patch limit: every predicted
            // encounter drawn was a tangle of loops and repeated labels).
            var col = HudPanel.Alpha(PatchColors[patch % PatchColors.Length], Math.Max(0.4f, 1f - 0.2f * patch));
            legColour.Add(col); legPatch.Add(patch);
            if (patch > MaxPatches) continue;
            bool planning = applied.Count > 0 || lagPlan != null;
            // Without nodes the map draws your orbit; with nodes, your orbit up to the first node
            // is drawn here (gold) and the rest of it faint, and the plan takes over from the node.
            bool drawn = planning || !(li == 0 && !l.Planned && l.Body.Name == focusBody);
            if (planning && !l.Planned) col = YouColor;
            double span = l.T1 - l.T0;
            if (!(span > 0)) continue;
            // One lap of each conic is enough (later laps retrace it), drawn finely.
            bool ownConic = l.Body.Name == focusBody || !l.Body.IsRoot;
            if (ownConic && l.El.IsElliptic && IsFinite(l.El.Period) && span > l.El.Period) span = l.El.Period;
            int n = Math.Max(24, Math.Min(200, (int)(160 * Math.Min(1.0, span / (l.El.IsElliptic && IsFinite(l.El.Period) ? l.El.Period : span)))));
            Vector2 prev = default; bool hp = false; double prevT = l.T0;
            var planRun = new List<Vector2>();
            void FlushPlan() { if (planRun.Count > 1) MapStyle.Plan(planRun, false, col, MapStyle.Thick(u), u); planRun.Clear(); }
            // Evenly in time, then refined where the screen gap is large: a hyperbolic pass spends
            // almost all its time far out, and its periapsis came out as a few straight kinks.
            bool Pt(double tk, out Vector2 sp)
            {
                sp = default;
                Vector3D loc = LegLoc(l, tk);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04) return false;
                return MapPipeline.ToScreen(W(loc), out sp) && InMapArea(sp);
            }
            void Emit(double tk, Vector2 sp)
            {
                samples.Add(new Sample { T = tk, S = sp, Planned = l.Planned });
                if (hp && drawn)
                {
                    if (l.Planned) { if (planRun.Count == 0) planRun.Add(prev); planRun.Add(sp); }
                    else MapPipeline.ScreenLine(prev, sp, col, MapStyle.Thick(u));
                }
                prev = sp; prevT = tk; hp = true;
            }
            void Refine(double ta, Vector2 sa, double tb, Vector2 sb, int depth)
            {
                if (depth < 7 && (sb - sa).Length() > 8f * u)
                {
                    double tm = 0.5 * (ta + tb);
                    if (Pt(tm, out var sm)) { Refine(ta, sa, tm, sm, depth + 1); Emit(tm, sm); Refine(tm, sm, tb, sb, depth + 1); }
                }
            }
            for (int k = 0; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                if (!Pt(tk, out var s)) { hp = false; FlushPlan(); continue; }
                if (hp) Refine(prevT, prev, tk, s, 0);
                Emit(tk, s);
            }
            FlushPlan();
            // A pass drawn about a ghost is also drawn where it really is about the view's body, faint
            // and thin: it joins the path before the encounter to the path after it.
            if (drawn && !Live(l.Body))
            {
                var faint = HudPanel.Alpha(col, 0.55f);
                Vector2 pv = default; bool hv = false;
                for (int k = 0; k <= n; k++)
                {
                    double tk = l.T0 + span * k / n;
                    Vector3D loc = toMap(RootAt(l, tk), tk);
                    if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sq) || !InMapArea(sq)) { hv = false; continue; }
                    if (hv) MapPipeline.ScreenLine(pv, sq, faint, 1.2f * u);
                    pv = sq; hv = true;
                }
            }
        }

        if (applied.Count > 0 && Base(t, out var bb, out var bel) && bel.IsElliptic && IsFinite(bel.Period))
        {
            double t0 = applied[0].Node.T, t1 = t + bel.Period;
            var faint = HudPanel.Alpha(YouColor, 0.3f);
            var run = new List<Vector2>();
            for (int k = 0; k <= 96 && t0 < t1; k++)
            {
                double tk = t0 + (t1 - t0) * k / 96;
                Vector3D loc = Loc(bb, OrbitPropagation.StateAt(bel, tk).Position, tk);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
                { MapStyle.Plan(run, false, faint, MapStyle.Thin(u), u); run.Clear(); continue; }
                run.Add(sp);
            }
            MapStyle.Plan(run, false, faint, MapStyle.Thin(u), u);
        }

        DrawLibration(lagPlan, t, toMap, W, u);

        // The orbit each patch puts you on (KSP): where a patch about a body is cut short by its next
        // event (a moon's escape into its planet, then back into the moon), the rest of its ellipse is
        // drawn faint and dashed, with its Pe / Ap, so the orbit you would be on reads at a glance.
        {
            var shown = new HashSet<string>();
            for (int li = 1; li < legs.Count; li++)
            {
                if (legPatch[li] > MaxPatches) break;
                var l = legs[li];
                if (l.Body == legs[li - 1].Body || l.Body.IsRoot || !l.El.IsElliptic || !IsFinite(l.El.Period)) continue;
                if (l.T1 - l.T0 >= l.El.Period * 0.98) continue;   // drawn whole already
                var col = legColour[li];
                var faintCol = HudPanel.Alpha(col, 0.45f);
                int m = 96;
                double a0 = l.T1, a1 = l.T0 + l.El.Period;
                var run = new List<Vector2>();
                for (int k = 0; k <= m; k++)
                {
                    double tk = a0 + (a1 - a0) * k / m;
                    Vector3D loc = Loc(l.Body, OrbitPropagation.StateAt(l.El, tk).Position, l.T0);
                    if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || MapPipeline.Occluded(W(loc)) || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
                    { MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u); run.Clear(); continue; }
                    run.Add(sp);
                }
                MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u);
                // Its apsides, once per body (the orbit about Verdure after leaving Palatine: Pe / Ap).
                if (!shown.Add(l.Body.Name)) continue;
                double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
                void Apsis(double nu, string what, double alt)
                {
                    Vector3D loc = Loc(l.Body, OrbitSampler.PositionAtTrueAnomaly(l.El, nu), l.T0);
                    if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp)) return;
                    HudPanel.TagAt(sp, $"{what} {HudPanel.Km(alt)}", col, u);
                }
                Apsis(0, "Pe", l.El.PeriapsisRadius - R);
                if (l.El.Eccentricity > 0.002) Apsis(Math.PI, "Ap", l.El.SemiMajorAxis * (1 + l.El.Eccentricity) - R);
            }
        }

        // Patch points: where the trajectory leaves one SOI for another.
        var entryNamed = new HashSet<string>();
        var labelled = new HashSet<string>();   // each crossing named once
        for (int li = 1; li < legs.Count; li++)
        {
            if (legPatch[li] > MaxPatches) break;
            var pa = legs[li - 1]; var nb = legs[li].Body;
            if (nb == pa.Body) continue;
            double tp = legs[li].T0;
            Vector3D loc = LegLoc(pa, tp);
            if (!MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp)) continue;
            var col = legColour[li];
            MapPipeline.ScreenCircle(sp, 3.5f * u, col, 2f * u);
            bool escape = nb == pa.Body.Parent;
            // A moon's comings and goings are too small to tell apart in the system view: not labelled there.
            bool sysView = focusBody == null || focusBody == SystemHost.Registry?.Root?.Name;
            GravityBody moonSide = escape ? pa.Body : nb;
            bool moonAtSolarScale = sysView && moonSide.Parent != null && !moonSide.Parent.IsRoot;
            // Where the crossing really is (about the view's body): marked and named after the body
            // crossed: "Caligo Entry" going in, "Caligo Escape" coming out, "Kemik Escape" to the sun.
            {
                Vector3D tl = toMap(RootAt(pa, tp), tp);
                if (!moonAtSolarScale && Math.Sqrt(tl.X * tl.X + tl.Z * tl.Z) <= limit * 1.04 && MapPipeline.ToScreen(W(tl), out var st) && InMapArea(st))
                {
                    MapPipeline.ScreenCircle(st, 5f * u, col, 2f * u);
                    MapPipeline.ScreenCircle(st, 1.5f * u, col, 2.5f * u);
                    string what = escape ? $"{SystemHost.DisplayName(pa.Body.Name)} Escape" : $"{SystemHost.DisplayName(nb.Name)} Entry";
                    if (labelled.Add(what)) HudPanel.TagAt(st + new Vector2(10f * u, 0), what, col, u, diamond: false);
                    if (!escape) entryNamed.Add(nb.Name);
                }
            }
            // An encounter: the body where it will be, named (the ghost the arc is drawn about).
            if (!escape && !nb.IsRoot && nb.Name != focusBody && !moonAtSolarScale && MapPipeline.ToScreen(W(Loc(nb, Vector3D.Zero, tp)), out var gs) && InMapArea(gs))
            {
                // The ghost ring: padded round the body's true size on the map (its radius mapped like everything else).
                double R = SystemHost.Registry?.FindDefinition(nb.Name)?.RadiusMeters ?? 0;
                float rpx = 0;
                foreach (var ax in new[] { Vector3D.UnitX, Vector3D.UnitY })
                    if (MapPipeline.ToScreen(W(Loc(nb, ax * R, tp)), out var es)) rpx = Math.Max(rpx, (es - gs).Length());
                rpx = Math.Max(rpx + 1.5f * u, 9f * u);   // snug on the body, never smaller than the old marker
                MapPipeline.ScreenCircle(gs, rpx, HudPanel.Alpha(col, 0.55f), 1.3f * u);
                // Its name only when no 'Entry' label already names it (the two landed on each other).
                if (!entryNamed.Contains(nb.Name)) HudPanel.TagAt(gs + new Vector2(rpx + 6f * u, 0), SystemHost.DisplayName(nb.Name), col, u, diamond: false);
            }
        }

        // Impact: where a leg dips inside its body (KSP marks it). A red cross and 'Impact', on the
        // first such point of the whole path.
        foreach (var l in legs)
        {
            double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
            if (!(R > 0) || !(l.El.PeriapsisRadius < R) || !(l.T1 > l.T0)) continue;
            double span = l.T1 - l.T0, prev = l.T0, hitT = double.NaN;
            for (int k = 1; k <= 240; k++)
            {
                double tk = l.T0 + span * k / 240;
                if (OrbitPropagation.StateAt(l.El, tk).Position.Length() < R)
                {
                    double a = prev, b = tk;
                    for (int q = 0; q < 30; q++) { double m = 0.5 * (a + b); if (OrbitPropagation.StateAt(l.El, m).Position.Length() < R) b = m; else a = m; }
                    hitT = 0.5 * (a + b); break;
                }
                prev = tk;
            }
            if (double.IsNaN(hitT)) continue;
            if (Live(l.Body) && MapPipeline.ToScreen(W(LegLoc(l, hitT)), out var hs) && InMapArea(hs))
            {
                float r = 6f * u;
                MapPipeline.ScreenLine(hs - new Vector2(r, r), hs + new Vector2(r, r), ImpactColor, 2.2f * u);
                MapPipeline.ScreenLine(hs - new Vector2(r, -r), hs + new Vector2(r, -r), ImpactColor, 2.2f * u);
                HudPanel.TagAt(hs + new Vector2(8f * u, 0), $"{l.Body.Name} Impact  in {Clock(hitT - t)}", ImpactColor, u, diamond: false);
            }
            break;
        }

        // Reentry: where the braking band starts (the entry interface) and max-q, on the leg about that body;
        // an airless body you would reach over the cap: its border, flagged.
        {
            var ep = EntryHost.Prediction; var air = EntryHost.Airless;
            Vector2? first = null;
            foreach (var l in legs)
            {
                void Mark(double tm, string what, bool strong)
                {
                    if (double.IsNaN(tm) || tm < l.T0 || tm > l.T1 || !Live(l.Body) || !MapPipeline.ToScreen(W(LegLoc(l, tm)), out var es) || !InMapArea(es)) return;
                    if (first.HasValue && (es - first.Value).Length() < 60f * u) return;   // (too close to the entry's: it says enough)
                    first ??= es;
                    MapPipeline.ScreenCircle(es, (strong ? 5f : 3.5f) * u, EntryColor, 2f * u);
                    if (strong) MapPipeline.ScreenCircle(es, 1.5f * u, EntryColor, 2.5f * u);
                    HudPanel.TagAt(es + new Vector2(10f * u, 0), what, EntryColor, u, diamond: false);
                }
                if (ep != null && l.Body.Name == EntryHost.PredictedBody)
                {
                    AtmosphereRings(l.Body, l.El, (rel, tk) => Loc(l.Body, rel, tk), W, u, limit, t);
                    double share = SEAerospace.Entry.Reentry.Share(ep.PeakHeat, false);
                    Mark(ep.EnterTime, $"Atmosphere entry  {ep.ArrivalSpeed:N0} m/s · heat {share:P0}{(share > 1 ? " — too hot" : "")}", true);
                    Mark(ep.MaxQTime, $"Max q  {ep.PeakDecel / 9.81:F1} g", false);
                    // (aerobraking: where the pass leaves the band again, what it took off; the orbit it leaves you on,
                    //  dashed, with its Pe / Ap - one more pass is one more prediction once you are on it)
                    if (ep.HasAfter)
                    {
                        first = null;   // (the exit is marked even when the pass is short on screen)
                        Mark(ep.ExitTime, $"Atmosphere exit  −{ep.Dv:N0} m/s", true);
                        AfterPass(l.Body, ep, (rel, tk) => Loc(l.Body, rel, tk), W, u, limit);
                    }
                    else if (ep.Landing) Mark(ep.BottomTime, $"Into the air  {ep.SpeedAtBottom:N0} m/s · −{ep.Dv:N0} m/s", false);
                    break;
                }
                if (air.HasValue && l.Body.Name == air.Value.body)
                {
                    Mark(air.Value.t, $"No air  {air.Value.speed:N0} > {EntryHost.Cap:N0} m/s", true);
                    break;
                }
            }
        }

        // Target: the closest approach on the path, and where the target is then (a ring), joined.
        if (Target != null)
        {
            var ca = ClosestApproach(legs);
            if (ca.ok && ca.d <= RendezvousRange && Live(ca.leg.Body) && MapPipeline.ToScreen(W(LegLoc(ca.leg, ca.t)), out var cs1) && InMapArea(cs1))
            {
                // Marked as an entry / escape is (a ring round a dot, named), in the target's colour.
                MapPipeline.ScreenCircle(cs1, 5f * u, TargetColor, 2f * u);
                MapPipeline.ScreenCircle(cs1, 1.5f * u, TargetColor, 2.5f * u);
                if (EncounterFrames.Ephemeris(ca.site, ca.t, out var tp, out var trel) && tp == ca.leg.Body
                    && MapPipeline.ToScreen(W(Loc(tp, trel.Position, ca.t)), out var cs2) && InMapArea(cs2))
                {
                    MapPipeline.ScreenCircle(cs2, 7f * u, TargetColor, 1.6f * u);
                    MapPipeline.ScreenDashed(cs1, cs2, HudPanel.Alpha(TargetColor, 0.6f), 1.2f * u);
                }
                HudPanel.TagAt(cs1 + new Vector2(10f * u, 0), $"{Target} Rendezvous  ·  {HudPanel.Km(ca.d)}  in {Clock(ca.t - t)}  ·  {RelSpeed(ca):N0} m/s", TargetColor, u, diamond: false);
            }
        }

        // Ring rocks: every pass within the rendezvous range, marked as the target's rendezvous is.
        {
            int shown = 0;
            foreach (var p in RingRocks.Passes(legs, t))
            {
                if (!Live(p.Leg.Body) || !MapPipeline.ToScreen(W(LegLoc(p.Leg, p.T)), out var ps) || !InMapArea(ps)) continue;
                MapPipeline.ScreenCircle(ps, 5f * u, RockColor, 2f * u);
                MapPipeline.ScreenCircle(ps, 1.5f * u, RockColor, 2.5f * u);
                if (shown++ < 4) HudPanel.TagAt(ps + new Vector2(10f * u, 0), $"{p.Label} Rendezvous  ·  {HudPanel.Km(p.D)}  in {Clock(p.T - t)}  ·  {p.V:N0} m/s", RockColor, u, diamond: false);
            }
        }

        // Sector crossings: where the path comes within the merge range of a sector's site (you arrive
        // there, by conjunction) and where it leaves the site's bubble again.
        long sc0 = ModCost.Start();
        var crossings = SectorCrossings(t, legs);
        ModCost.Sec("trajectory.crossings").Stop(sc0);
        foreach (var c in crossings)
        {
            if (c.T < t || !Live(c.Leg.Body)) continue;   // (passed: the list is kept while the plan holds)
            Vector3D loc = LegLoc(c.Leg, c.T);
            if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var cs) || !InMapArea(cs)) continue;
            var cc = c.Entry ? SectorIn : SectorOut;
            MapPipeline.ScreenCircle(cs, 5f * u, cc, 1.8f * u);
            MapPipeline.ScreenCircle(cs, 1.5f * u, cc, 2.5f * u);
            HudPanel.TagAt(cs + new Vector2(10f * u, 0), $"{c.Name} {(c.Entry ? "Entry" : "Exit")}", cc, u, diamond: false);
        }

        // Nodes on screen.
        var nodeScreen = new List<(Node n, Vector2 s, Applied a)>();
        foreach (var a in applied)
        {
            Vector3D loc = Loc(a.Body, a.Before.Position, a.Node.T);
            if (lagPlan != null && lagPlan.NodeAt.TryGetValue(a.Node, out var lagOff)) loc = LagLoc(lagPlan, lagOff, t, toMap);   // on the Lagrange path
            if (!MapPipeline.ToScreen(W(loc), out var s)) continue;
            nodeScreen.Add((a.Node, s, a));
            bool sel = a.Node == Selected;
            if (!MapPipeline.ScreenIcon("node", s, (sel ? 10f : 8f) * u, NodeColor))
            {
                MapPipeline.ScreenCircle(s, (sel ? 9f : 7f) * u, NodeColor, (sel ? 2.6f : 2f) * u);
                MapPipeline.ScreenCircle(s, 2.5f * u, NodeColor, 3f * u);
            }
            // Armed for auto-burn: said on the node itself (only the menu said so before).
            if (a.Node.Auto) HudPanel.TagAt(s + new Vector2((sel ? 12f : 10f) * u, 0), AutoBurn.Flying ? "auto-burning" : "auto-burn", AutoColor, u, diamond: false);
        }

        // Handles of the selected node.
        var handles = new List<(Vector2 at, Vector2 dir, int axis, double sign, string label, ColorSRGB c)>();
        var selA = applied.Find(a => a.Node == Selected);
        Vector2 selS = default;
        bool edit = focusBody != null;   // the solar view shows nodes; editing is in a planet's view
        // Compact when the orbit is small on screen (zoomed out): the handles would pile onto the planet.
        // They open when the mouse comes to the node, and stay while one is pulled.
        bool compact = false;
        if (Selected != null && selA.Node != null && _drag != Drag.Handle)
        {
            var ns0 = nodeScreen.Find(x => x.n == Selected);
            if (ns0.n != null && MapPipeline.ToScreen(W(Loc(selA.Body, Vector3D.Zero, Selected.T)), out var bc))
                compact = (ns0.s - bc).Length() < 90f * u && (mouse - ns0.s).Length() > 60f * u;
        }
        GizmoCompact = compact;
        if (edit && !compact && Selected != null && selA.Node != null && nodeScreen.Exists(x => x.n == Selected && InMapArea(x.s)))   // not when the node is off view
        {
            selS = nodeScreen.Find(x => x.n == Selected).s;
            Axes(selA.Before, out var P, out var N, out var R);
            Vector3D relAt = selA.Before.Position;
            // A node in a Lagrange sector: its handles along the sector's own directions, on its path.
            bool inLag = lagPlan != null && lagPlan.NodeAxes.TryGetValue(Selected, out var lax) && lagPlan.NodeAt.ContainsKey(Selected);
            if (inLag) { var ax3 = lagPlan.NodeAxes[Selected]; P = ax3.P; N = ax3.N; R = ax3.R; }
            Vector2 Dir(Vector3D v)
            {
                double eps = Math.Max(1.0, selA.Before.Position.Length() * 0.01);
                Vector3D a0, a1;
                if (inLag)
                {
                    Vector3D at = lagPlan.NodeAt[Selected];
                    double e2 = Math.Max(1000.0, lagPlan.Amp * 0.05);
                    a0 = LagLoc(lagPlan, at, t, toMap); a1 = LagLoc(lagPlan, at + v * e2, t, toMap);
                }
                else { a0 = Loc(selA.Body, relAt, Selected.T); a1 = Loc(selA.Body, relAt + v * eps, Selected.T); }
                if (!MapPipeline.ToScreen(W(a0), out var s0) || !MapPipeline.ToScreen(W(a1), out var s1)) return Vector2.Zero;
                var d = s1 - s0;
                return d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : Vector2.Zero;
            }
            Vector2 dp = Dir(P), dr = Dir(R);
            if (dp == Vector2.Zero) dp = new Vector2(1, 0);
            if (dr == Vector2.Zero) dr = new Vector2(-dp.Y, dp.X);
            // Normal is out of the map plane from above: its handles sit on the diagonals.
            Vector2 dn = Vector2.Normalize(dp + dr), dan = -dn;
            float d0 = 48f * u;
            handles.Add((selS + dp * d0, dp, 0, +1, "P", ProColor));
            handles.Add((selS - dp * d0, -dp, 0, -1, "R", ProColor));
            handles.Add((selS + dn * d0, dn, 1, +1, "N", NorColor));
            handles.Add((selS + dan * d0, dan, 1, -1, "AN", NorColor));
            handles.Add((selS + dr * d0, dr, 2, +1, "RO", RadColor));
            handles.Add((selS - dr * d0, -dr, 2, -1, "RI", RadColor));
        }

        _lastHandles.Clear(); foreach (var h in handles) _lastHandles.Add((h.label, h.at, h.dir));
        _lastSamples = samples;
        _lastNodes.Clear(); foreach (var ns in nodeScreen) _lastNodes.Add((ns.n, ns.s));

        // ── interaction ──
        float pickR = 12f * u;
        int hoverHandle = -1;
        for (int i = 0; i < handles.Count; i++) if ((handles[i].at - mouse).Length() < pickR) hoverHandle = i;
        Node hoverNode = null;
        foreach (var ns in nodeScreen) if ((ns.s - mouse).Length() < pickR) hoverNode = ns.n;
        double hoverT = double.NaN; float bestD = pickR;
        for (int i = 1; i < samples.Count; i++)
        {
            Vector2 a = samples[i - 1].S, b = samples[i].S, ab = b - a;
            float L = ab.LengthSquared();
            float k = L > 1e-6f ? Math.Clamp(Vector2.Dot(mouse - a, ab) / L, 0f, 1f) : 0f;
            float d = (a + ab * k - mouse).Length();
            if (d < bestD && Math.Abs(samples[i].T - samples[i - 1].T) < 3600 * 12) { bestD = d; hoverT = samples[i - 1].T + (samples[i].T - samples[i - 1].T) * k; }
        }
        if (!edit) { hoverNode = null; hoverT = double.NaN; }
        HoverNode = hoverNode; HoverT = hoverT;
        ClaimsMouse = _drag != Drag.None || hoverHandle >= 0 || hoverNode != null || !double.IsNaN(hoverT);
        OnGizmo = _drag != Drag.None || hoverHandle >= 0 || hoverNode != null;

        // A click on the path (press and release without dragging) adds a maneuver there; a drag
        // that starts on the path pans the map instead.
        if (_addPending)
        {
            if (MapCamera.Dragging || (mouse - _addAt).Length() > 5f * u) _addPending = false;
            else if (!lDown)
            {
                _addPending = false;
                if (!double.IsNaN(_addT) && _addT > t + 5)
                {
                    var n = new Node { T = _addT };
                    lock (Nodes) Nodes.Add(n);
                    Selected = n;
                }
            }
        }

        if (_drag == Drag.Handle && Selected != null)
        {
            if (!lDown) _drag = Drag.None;
            else
            {
                // Pull along the handle's axis: the delta-v grows faster the further you pull.
                float pull = Math.Max(0f, Vector2.Dot(mouse - _anchor, _dir));
                double rate = 0.5 * Math.Pow(pull / (20.0 * u), 2);      // m/s per second: 20 px 0.5, 60 px 4.5, 120 px 18
                double d = _sign * rate * dt;
                if (_axis == 0) Selected.Pro += d; else if (_axis == 1) Selected.Nor += d; else Selected.Rad += d;
                Selected.Edit();
                MapPipeline.ScreenLine(_anchor, _anchor + _dir * pull, NodeColor, 1.5f);
            }
        }
        else if (_drag == Drag.Slide && Selected != null)
        {
            if (!lDown) _drag = Drag.None;
            else
            {
                // Slide along the orbit the node sits on (its pre-burn orbit, which does not change as
                // it moves), between its neighbours: the nearest point to the mouse, no pick radius. On
                // the drawn path it jumped onto its own new trajectory and stopped 12 px off the line.
                double st = SlideT(Selected, mouse, t);
                if (!double.IsNaN(st) && Math.Abs(st - Selected.T) > 0.5) { Selected.T = st; Selected.Edit(); }
            }
        }
        else if (lPressed)
        {
            if (hoverHandle >= 0) { _drag = Drag.Handle; _axis = handles[hoverHandle].axis; _sign = handles[hoverHandle].sign; _anchor = handles[hoverHandle].at; _dir = handles[hoverHandle].dir; }
            else if (hoverNode != null) { Selected = hoverNode; _drag = Drag.Slide; }
            else if (!double.IsNaN(hoverT) && hoverT > t + 5) { _addPending = true; _addT = hoverT; _addAt = mouse; }
            else Selected = null;
        }

        double SlideT(Node n, Vector2 m, double now)
        {
            var aa = applied.Find(x => x.Node == n);
            if (aa.Node == null) return double.NaN;
            var el = OrbitalMath.ToElements(aa.Before, aa.Body.Mu, n.T);
            double P = el.IsElliptic && IsFinite(el.Period) ? el.Period : 7200;
            double lo = Math.Max(now + 5, n.T - P / 2), hi = n.T + P / 2;
            // Not across a sphere-of-influence change: from the start of the leg the node is on (same
            // body), and forward only while the pre-burn orbit stays in that body's sphere (see below).
            foreach (var lg in legs) if (lg.Body == aa.Body && lg.T0 <= n.T + 1e-6 && n.T <= lg.T1 + 1e-6) { lo = Math.Max(lo, lg.T0); break; }
            lock (Nodes) foreach (var o in Nodes)
            {
                if (o == n) continue;
                if (o.T < n.T) lo = Math.Max(lo, o.T + 5); else hi = Math.Min(hi, o.T - 5);
            }
            if (!(hi > lo)) return double.NaN;
            var leg = new Leg { Body = aa.Body, El = el, T0 = lo, T1 = hi };
            double best = double.NaN; float bd = float.MaxValue;
            const int N = 240;
            Vector2 prev = default; bool havePrev = false; double prevT = lo;
            for (int k = 0; k <= N; k++)
            {
                double tk = lo + (hi - lo) * k / N;
                if (tk > n.T && !double.IsInfinity(aa.Body.SoiRadius) && OrbitPropagation.StateAt(el, tk).Position.Length() > aa.Body.SoiRadius) break;
                if (!MapPipeline.ToScreen(W(LegLoc(leg, tk)), out var sp)) { havePrev = false; continue; }
                if (havePrev)
                {
                    Vector2 ab = sp - prev; float L = ab.LengthSquared();
                    float f = L > 1e-6f ? Math.Clamp(Vector2.Dot(m - prev, ab) / L, 0f, 1f) : 0f;
                    float d = (prev + ab * f - m).Length();
                    if (d < bd) { bd = d; best = prevT + (tk - prevT) * f; }
                }
                prev = sp; prevT = tk; havePrev = true;
            }
            return best;
        }

        // The handles: KSP's navball symbols on arms from the node; the one under the mouse (or being
        // pulled) is highlighted and named. A click this frame may have cleared or removed the node.
        if (Selected == null) handles.Clear();
        for (int i = 0; i < handles.Count; i++)
        {
            var (at, dir, axis, sign, label, c) = handles[i];
            bool hot = i == hoverHandle || (_drag == Drag.Handle && _axis == axis && _sign == sign);
            Vector2 tip = at;
            if (_drag == Drag.Handle && _axis == axis && _sign == sign) tip = _anchor + _dir * Math.Max(0f, Vector2.Dot(mouse - _anchor, _dir));
            MapPipeline.ScreenLine(selS + dir * 10f * u, tip - dir * 8f * u, HudPanel.Alpha(c, hot ? 0.8f : 0.28f), (hot ? 1.6f : 1f) * u);
            Icon(label, tip, c, hot, u);
            // The component on this axis, beyond the handle it points along (P shows + prograde,
            // R shows retrograde): the number reads along the node's own axes.
            double comp = axis == 0 ? Selected.Pro : axis == 1 ? Selected.Nor : Selected.Rad;
            bool mine = sign > 0 ? comp > 0.05 : comp < -0.05;
            if (mine || hot)
            {
                // Centred on the axis, a fixed gap beyond the icon: the labels fan out with the handles.
                string txt = hot && !mine ? HandleName(label) : $"{Math.Abs(comp):F1}";
                AxisLabel(tip, dir, 11f * u, txt, c, u);
            }
        }
        if (Selected != null && selS != default && InMapArea(selS))   // not pinned at the edge when off view
        {
            double dvm = Math.Sqrt(Selected.Pro * Selected.Pro + Selected.Nor * Selected.Nor + Selected.Rad * Selected.Rad);
            double left = selA.Dv.Length();
            string head = (Math.Abs(left - dvm) > 0.05 ? $"{left:F1} / {dvm:F1} m/s" : $"{dvm:F1} m/s") + "  ·  " + Clock(Selected.T - t);
            // In the widest gap between the handles (below the node when it can), clear of them all.
            Vector2 gap = new Vector2(0, 1);
            if (handles.Count > 0)
            {
                var angs = new List<double>();
                foreach (var h in handles) angs.Add(Math.Atan2(h.dir.Y, h.dir.X));
                angs.Sort();
                double bestScore = double.MinValue;
                var hs = MapPipeline.MeasureText(head, 0.5f * u);
                for (int q = 0; q < angs.Count; q++)
                {
                    double a0 = angs[q], a1 = q + 1 < angs.Count ? angs[q + 1] : angs[0] + 2 * Math.PI;
                    double mid = (a0 + a1) * 0.5, width = a1 - a0;
                    double score = width + 0.35 * Math.Sin(mid);   // wide first, then downward (screen y grows down)
                    // ...and where it lands clear of the game's panels and the list (it ran under the left panel).
                    var dir = new Vector2((float)Math.Cos(mid), (float)Math.Sin(mid));
                    var c = selS + dir * (58f * u + Math.Abs(dir.X) * hs.X * 0.5f + Math.Abs(dir.Y) * hs.Y * 0.5f);
                    if (!InMapArea(c - hs * 0.5f) || !InMapArea(c + hs * 0.5f)) score -= 10;
                    if (score > bestScore) { bestScore = score; gap = dir; }
                }
            }
            AxisLabel(selS, gap, 58f * u, head, NodeColor, u);
            // The orbit after the burn: its periapsis and apoapsis marked on the plan itself.
            var aft = selA.After;
            if (aft.IsElliptic && selA.Body.Name == focusBody)
            {
                double R = SystemHost.Registry?.FindDefinition(selA.Body.Name)?.RadiusMeters ?? 0;
                void Apsis(double nu, string name, double rad)
                {
                    Vector3D loc = Loc(selA.Body, OrbitSampler.PositionAtTrueAnomaly(aft, nu), Selected.T);
                    if (MapPipeline.ToScreen(W(loc), out var ps) && InMapArea(ps)) HudPanel.TagAt(ps, $"{name} {HudPanel.Km(rad - R)}", PlanColor, u);
                }
                Apsis(0, "Pe", aft.PeriapsisRadius);
                Apsis(Math.PI, "Ap", aft.ApoapsisRadius);
            }
        }
        else if (!double.IsNaN(hoverT) && _drag == Drag.None)
            HudPanel.TagAt(mouse + new Vector2(16f * u, 14f * u), $"Add maneuver  (in {Clock(hoverT - t)})", PlanColor, u, diamond: false);

        // (Selecting a sector no longer marks your closest approach to it: that is what 'Set as target' is for.)
        Status = $"nodes {Nodes.Count}, legs {legs.Count}, mouse {MapInput.Status}";
    }

    private static string OrbitText(Applied a)
    {
        if (a.Node == null) return null;
        var el = a.After;
        double R = SystemHost.Registry?.FindDefinition(a.Body.Name)?.RadiusMeters ?? 0;
        if (!el.IsElliptic) return $"After: escape {a.Body.Name}";
        double pe = el.SemiMajorAxis * (1 - el.Eccentricity) - R, ap = el.SemiMajorAxis * (1 + el.Eccentricity) - R;
        return $"After: Pe {HudPanel.Km(pe)}   Ap {HudPanel.Km(ap)}";
    }


    public static string Clock(double s)
    {
        if (!IsFinite(s)) return "-";
        if (s < 0) return "-" + Clock(-s);
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h" : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m" : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    static readonly ColorSRGB AutoColor = new ColorSRGB(0.96f, 0.62f, 0.18f, 1f);
    static readonly ColorSRGB ImpactColor = new ColorSRGB(1.00f, 0.30f, 0.25f, 1f);
    /// <summary>Reentry's marks (the entry interface, max-q, an airless border): the colour of heat.</summary>
    static readonly ColorSRGB EntryColor = new ColorSRGB(1.00f, 0.62f, 0.20f, 1f);

    /// <summary>The air you are about to dip into, in your orbit's plane: its top (the planet frame's border - physics
    /// below; "Atmosphere"), solid and faint, and the braking band's top (the entry interface), dotted.</summary>
    static void AtmosphereRings(GravityBody body, KeplerianElements el, Func<Vector3D, double, Vector3D> Loc, Func<Vector3D, Vector3D> W, float u, double limit, double t)
    {
        var b = EntryHost.BandOf(body.Name);
        if (!b.IsValid) return;
        var faint = HudPanel.Alpha(EntryColor, 0.35f);
        // (the ring's directions from a CIRCULAR copy of the orbit - same plane: a hyperbola's own positions past its
        //  asymptotes come out at a negative radius, the direction flipped - chords across the planet)
        el.Eccentricity = 0; el.SemiMajorAxis = 1;
        void Ring(double radius, bool dotted, string label)
        {
            var run = new List<Vector2>();
            const int m = 96;
            Vector2? top = null;
            for (int k = 0; k <= m; k++)
            {
                double nu = 2 * Math.PI * k / m;
                Vector3D dir = OrbitSampler.PositionAtTrueAnomaly(el, nu);
                double len = dir.Length();
                if (!(len > 0)) continue;
                Vector3D loc = Loc(dir * (radius / len), t);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
                { Flush(); continue; }
                run.Add(sp);
                if (!top.HasValue || sp.Y < top.Value.Y) top = sp;
            }
            Flush();
            if (label != null && top.HasValue) HudPanel.TagAt(top.Value + new Vector2(6f * u, -10f * u), label, faint, u, diamond: false);
            void Flush()
            {
                if (run.Count > 1) { if (dotted) MapStyle.Plan(run, false, faint, MapStyle.Thin(u), u); else MapPipeline.ScreenPath(run, false, faint, MapStyle.Thin(u)); }
                run.Clear();
            }
        }
        double R = SystemHost.Registry?.FindDefinition(body.Name)?.RadiusMeters ?? 0;
        Ring(b.Bottom, false, $"Atmosphere {HudPanel.Km(b.Bottom - R)}");
        Ring(b.Top, true, null);
    }

    /// <summary>The orbit a predicted aerobraking pass leaves you on: faint, dashed, in the entry colour, from the
    /// exit round (an ellipse: one period; an escape: as far as the view), with its Pe / Ap.</summary>
    static void AfterPass(GravityBody body, SEAerospace.Entry.Pass ep, Func<Vector3D, double, Vector3D> Loc, Func<Vector3D, Vector3D> W, float u, double limit)
    {
        var el = ep.After;
        if (!IsFinite(el.SemiMajorAxis)) return;
        double t0 = ep.ExitTime, span = el.IsElliptic && IsFinite(el.Period) ? el.Period : 6 * 3600;
        var faintCol = HudPanel.Alpha(EntryColor, 0.5f);
        var run = new List<Vector2>();
        const int m = 128;
        for (int k = 0; k <= m; k++)
        {
            double tk = t0 + span * k / m;
            Vector3D loc = Loc(OrbitPropagation.StateAt(el, tk).Position, t0);
            if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || MapPipeline.Occluded(W(loc)) || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
            { MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u); run.Clear(); continue; }
            run.Add(sp);
        }
        MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u);
        double R = SystemHost.Registry?.FindDefinition(body.Name)?.RadiusMeters ?? 0;
        void Apsis(double nu, string what, double alt)
        {
            Vector3D loc = Loc(OrbitSampler.PositionAtTrueAnomaly(el, nu), t0);
            if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp)) return;
            HudPanel.TagAt(sp, $"{what} {HudPanel.Km(alt)} (after pass)", EntryColor, u);
        }
        if (el.IsElliptic && el.Eccentricity > 0.002) Apsis(Math.PI, "Ap", el.SemiMajorAxis * (1 + el.Eccentricity) - R);
    }
    static readonly ColorSRGB SectorIn = new ColorSRGB(0.45f, 1.00f, 0.55f, 1f);
    static readonly ColorSRGB SectorOut = new ColorSRGB(0.70f, 0.85f, 0.75f, 0.9f);

    /// <summary>The sector targeted from the map (its site): the path's closest approach to it is marked.</summary>
    public static string Target;
    static readonly ColorSRGB TargetColor = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);
    /// <summary>A ring rock's rendezvous (the rocks' colour on the map).</summary>
    public static readonly ColorSRGB RockColor = new ColorSRGB(1.00f, 0.80f, 0.45f, 1f);

    /// <summary>The ring rocks on the planned path, for the orbit card (null: none).</summary>
    public static string RingLine(double t)
    {
        if (!Trajectory(t, out var legs, out _)) return null;
        var ps = RingRocks.Passes(legs, t);
        if (ps.Count == 0) return null;
        var p = ps[0];
        // This crossing's: the passes with no long gap after the first (the next crossing is its own).
        int n = 1;
        while (n < ps.Count && ps[n].T - ps[n - 1].T < 120) n++;
        return $"Ring crossing: {n} rendezvous  ·  next {p.Label} {HudPanel.Km(p.D)} in {Clock(p.T - t)}  ·  {p.V:N0} m/s";
    }
    private static double _caAt = -1, _caGameT; private static string _caSig; private static (bool ok, double t, double d, Leg leg, EncounterFrames.Site site) _ca;

    /// <summary>
    /// Closest approach of the path to the target's site, on the legs about the site's own body (as
    /// KSP's intercept markers): time and distance. Recomputed at most once a second or on a change.
    /// </summary>
    static (bool ok, double t, double d, Leg leg, EncounterFrames.Site site) ClosestApproach(List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        // Keyed on the plan (not the base orbit, which a planet cell re-derives every frame), and
        // refreshed once a second of real time or 30 s of game time (warp).
        var ks = new System.Text.StringBuilder(Target ?? "");
        lock (Nodes) foreach (var nd in Nodes) ks.Append('|').Append(nd.T).Append(nd.Pro).Append(nd.Nor).Append(nd.Rad);
        string sig = ks.ToString();
        double gt = SystemHost.Now;
        if (sig == _caSig && now - _caAt < 1.0 && Math.Abs(gt - _caGameT) < 30) return _ca;
        _caSig = sig; _caAt = now; _caGameT = gt; _ca = default;
        EncounterFrames.Site site = null;
        foreach (var s in EncounterFrames.Sites) if (s.Sector == Target && (site == null || s.Anchor)) site = s;
        if (site == null) return _ca;
        double best = double.MaxValue, bestT = double.NaN; Leg bestLeg = default;
        foreach (var l in legs)
        {
            double span = l.T1 - l.T0;
            if (!(span > 0)) continue;
            int n = Math.Min(800, Math.Max(120, (int)(span / 15)));
            double Dist(double tk)
            {
                if (!EncounterFrames.Ephemeris(site, tk, out var p, out var rel) || p != l.Body) return double.MaxValue;
                return (OrbitPropagation.StateAt(l.El, tk).Position - rel.Position).Length();
            }
            for (int k = 0; k <= n; k++)
            {
                double tk = l.T0 + span * k / n, d = Dist(tk);
                if (d < best) { best = d; bestT = tk; bestLeg = l; }
            }
            // refine around the best sample of this leg
            if (bestLeg.Body == l.Body && bestLeg.T0 == l.T0)
            {
                double a = Math.Max(l.T0, bestT - span / n), b = Math.Min(l.T1, bestT + span / n);
                for (int q = 0; q < 40; q++)
                {
                    double m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
                    if (Dist(m1) < Dist(m2)) b = m2; else a = m1;
                }
                double tr = 0.5 * (a + b), dr = Dist(tr);
                if (dr < best) { best = dr; bestT = tr; }
            }
        }
        if (best < double.MaxValue) _ca = (true, bestT, best, bestLeg, site);
        return _ca;
    }

    /// <summary>
    /// The rendezvous with the target on your path, for the flight disc: when, how close, and where you and
    /// the target are then (offsets from the body the path is about then). False: no target or no approach.
    /// </summary>
    public static bool Rendezvous(double t, out GravityBody body, out double at, out double dist, out Vector3D you, out Vector3D target)
    {
        body = null; at = dist = double.NaN; you = target = default;
        if (Target == null || !Trajectory(t, out var legs, out _)) return false;
        var ca = ClosestApproach(legs);
        if (!ca.ok || ca.d > RendezvousRange || !EncounterFrames.Ephemeris(ca.site, ca.t, out var tp, out var trel) || tp != ca.leg.Body) return false;
        body = ca.leg.Body; at = ca.t; dist = ca.d;
        you = OrbitPropagation.StateAt(ca.leg.El, ca.t).Position;
        target = trel.Position;
        return true;
    }

    /// <summary>A rendezvous is only one when the path passes this close (m): where frames merge on arrival (a sector's Entry).</summary>
    public const double RendezvousRange = 10000;

    /// <summary>Speed relative to the target at the closest approach (m/s), NaN when unknown.</summary>
    static double RelSpeed((bool ok, double t, double d, Leg leg, EncounterFrames.Site site) ca)
    {
        if (!ca.ok || !EncounterFrames.Ephemeris(ca.site, ca.t, out var p, out var rel) || p != ca.leg.Body) return double.NaN;
        return (OrbitPropagation.StateAt(ca.leg.El, ca.t).Velocity - rel.Velocity).Length();
    }

    /// <summary>The target's closest approach as a line for the orbit card (null: no target or no approach).</summary>
    public static string TargetLine(double t)
    {
        if (Target == null) return null;
        // a rock seen but not tracked: no orbit yet, so no closest approach (a telescope's lidar on it speeds the fit)
        if (Contacts.RockProgress(Target) is double prog && prog < 1)
            return $"Target ≈ {Target}  ·  tracking {prog:P0}{(Contacts.Lidar ? "  ·  lidar ranging" : "  ·  bearing only")}";
        if (!Trajectory(t, out var legs, out _)) return null;
        var ca = ClosestApproach(legs);
        return ca.ok && ca.d <= RendezvousRange ? $"Target {Target}  ·  rendezvous {HudPanel.Km(ca.d)} in {Clock(ca.t - t)}  ·  {RelSpeed(ca):N0} m/s" : $"Target {Target}  ·  no rendezvous on this path";
    }

    public struct Crossing { public string Name; public double T; public bool Entry; public Leg Leg; }
    private static List<Crossing> _cross = new List<Crossing>();
    private static double _crossAt = -1; private static long _crossSig = long.MinValue; private static object _crossSites;

    /// <summary>
    /// When the path comes within the merge range (10 km) of a sector's site, and when it is 20 km away
    /// again (the split range), on every leg about the site's own body. Recomputed at most once a
    /// second, or when the plan changes.
    /// </summary>
    public static List<Crossing> SectorCrossings(double t, List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        long sig = _cSig;
        // (they change only with the plan or the sites: recomputed then - ~7 ms - and as a fallback every 10 s; it
        //  was every second, a 7 ms frame each second with the map open)
        object sites = EncounterFrames.Sites;
        if (sig == _crossSig && ReferenceEquals(sites, _crossSites) && now - _crossAt < 10.0) return _cross;
        _crossSig = sig; _crossAt = now; _crossSites = sites;
        var list = new List<Crossing>();
        const double enter = 10000, leave = ServerFrames.SlotRadius;
        foreach (var site in EncounterFrames.Sites)
        {
            if (site.Home?.Kind == SectorHomes.Kind.Lagrange) continue;   // its region is its sphere of influence (the Lagrange preview)
            foreach (var l in legs)
            {
                double span = l.T1 - l.T0;
                if (!(span > 0)) continue;
                int n = Math.Min(600, Math.Max(60, (int)(span / 20)));
                double Dist(double tk)
                {
                    if (!EncounterFrames.Ephemeris(site, tk, out var p, out var rel) || p != l.Body) return double.MaxValue;
                    return (OrbitPropagation.StateAt(l.El, tk).Position - rel.Position).Length();
                }
                double Cross(double a, double b, double level)
                {
                    for (int k = 0; k < 30; k++) { double m = 0.5 * (a + b); if ((Dist(a) < level) == (Dist(m) < level)) a = m; else b = m; }
                    return 0.5 * (a + b);
                }
                double prevT = l.T0, prevD = Dist(prevT);
                bool inside = prevD < enter;
                for (int k = 1; k <= n; k++)
                {
                    double tk = l.T0 + span * k / n, d = Dist(tk);
                    if (d == double.MaxValue) { prevT = tk; prevD = d; continue; }
                    if (!inside && d < enter && prevD >= enter) { list.Add(new Crossing { Name = site.Label, T = Cross(prevT, tk, enter), Entry = true, Leg = l }); inside = true; }
                    else if (inside && d > leave && prevD <= leave) { list.Add(new Crossing { Name = site.Label, T = Cross(prevT, tk, leave), Entry = false, Leg = l }); inside = false; }
                    prevT = tk; prevD = d;
                }
            }
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        _cross = list;
        return list;
    }

    // ── the Lagrangian preview: your orbit in a Lagrange sector, now or from where you will enter one ──
    /// <summary>Your path in a Lagrange sector: from now (you are in one) or from where your path enters one.</summary>
    public sealed class LagPlan
    {
        public EncounterFrames.Site Site;
        public double TE, TX = double.NaN;    // entry (now, if Now) and exit (NaN: you stay)
        public bool Now;
        public double Period, Amp;
        public List<Vector3D> Path = new List<Vector3D>();    // offsets from the point, in the frame turning with the planet (axes as at TE)
        public List<Vector3D> After = new List<Vector3D>();   // past the exit, in the same frame
        public Dictionary<Node, Vector3D> NodeAt = new Dictionary<Node, Vector3D>();   // burns in the sector, where (same frame)
        public Vector3D Point;                                 // the point at TE (root)
        public double BestT = double.NaN, BestU, BestD;        // closest to the point before any burn: when, drift speed, distance
        public Vector3D BestX, BestV, Axis;                    // there: offset and drift (turning frame); the frame's axis
        public double W, Core;                                 // the sector's rate (rad/s), its calm core (m)
        public Dictionary<Node, (Vector3D P, Vector3D N, Vector3D R)> NodeAxes = new Dictionary<Node, (Vector3D, Vector3D, Vector3D)>();
        public Vector3D Omega;                                  // at BestT: the planet's turn (W above: the sector's rate there)
        public Vector3D P0; public GravityBody Host;           // the planet's place at TE (for its turn since)
        /// <summary>How far the planet has turned since TE (about Axis): offsets are kept as at TE.</summary>
        public double Theta(double t)
        {
            Vector3D p = Host.StateInParentAt(t).Position;
            return Math.Atan2(Vector3D.Dot(Axis, Vector3D.Cross(P0, p)), Vector3D.Dot(P0, p));
        }
    }
    private static LagPlan _lag;
    // The ghost (a sector drawn where it will be when you enter it) on screen last frame: the map fades
    // today's sectors under it, so the two times do not read as one.
    private static Vector2 _ghostAt; private static float _ghostR; private static string _ghostName; private static double _ghostWall;
    public static bool UnderGhost(Vector2 s, string sector)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        return _ghostName != null && sector != _ghostName && now - _ghostWall < 0.3 && (s - _ghostAt).Length() < _ghostR;
    }
    /// <summary>A Lagrange sector ahead or around you: the planner draws your path (cut at its entry).</summary>
    public static bool LagActive => _lag != null;
    public static LagPlan Lag => _lag;
    private static double _lagWall;
    public static string LibDebug = "-", LibStart = "-";

    public static Vector3D Turn(Vector3D v, Vector3D axis, double ang)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang);
        return v * c + Vector3D.Cross(axis, v) * s + axis * (Vector3D.Dot(axis, v) * (1 - c));
    }

    /// <summary>The plan: every frame while you are in a sector, a few times a second otherwise.</summary>
    static LagPlan LagrangeCached(double t, List<Leg> legs, List<Applied> applied)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var pf = FrameHost.PlayerFrame;
        bool inSector = pf != null && (EncounterFrames.RideOf(pf.Id) != null || EncounterFrames.SiteOf(pf.Id)?.Home?.Kind == SectorHomes.Kind.Lagrange);
        double every = inSector || (_lag != null && _lag.Now) ? 0.1 : 0.25;
        if (now - _lagWall > every)
        {
            try { _lag = PlanLagrange(t, legs, applied); } catch (Exception e) { _lag = null; LibDebug = "plan failed: " + e.Message; }
            _lagWall = now;
        }
        return _lag;
    }

    /// <summary>
    /// Lagrange sectors are spheres of influence (SectorHomes.LagrangeRegion): where your path first
    /// crosses into one (or now, if you are in one) you switch onto its Lagrange orbit, stepped with the
    /// sector's own model (a pull toward the point in the frame turning with the planet; none in the calm
    /// core), planned burns included, until it leaves the region; then your plain orbit again.
    /// </summary>
    static LagPlan PlanLagrange(double t, List<Leg> legs, List<Applied> applied)
    {
        var reg = SystemHost.Registry;
        if (reg == null) return null;
        var pf = FrameHost.PlayerFrame;
        EncounterFrames.Site site = null;
        double tE = double.NaN;
        Vector3D d = default, v = default;
        bool nowIn = false;
        if (pf != null)
        {
            var ride = EncounterFrames.RideOf(pf.Id);
            var own = EncounterFrames.SiteOf(pf.Id);
            if (ride != null) { site = ride.Site; tE = t; d = ride.D; v = ride.V; nowIn = true; }
            else if (own?.Home?.Kind == SectorHomes.Kind.Lagrange && FrameHost.RiderFrame == pf.Id)
            { site = own; tE = t; d = FrameHost.RiderOffset; v = FrameHost.RiderVelocity; nowIn = true; }
        }
        if (site == null)
        {
            // The first crossing into any sector's region along the path.
            foreach (var s in EncounterFrames.LagrangeSites())
            {
                var host = reg.Find(s.Home.Host);
                if (host?.Parent == null) continue;
                double P = SectorHomes.Period(s.Home, reg);
                foreach (var l in legs)
                {
                    double t1 = double.IsNaN(tE) ? l.T1 : Math.Min(l.T1, tE);
                    if (!(t1 > l.T0)) continue;
                    if (s.Home.Point >= 3)
                    {
                        if (l.Body != host.Parent) continue;
                        double rb = (SectorHomes.Where(s.Home, reg, l.T0, out var c0) - c0).Length();
                        double apo = l.El.IsElliptic ? l.El.ApoapsisRadius : double.PositiveInfinity;
                        if (l.El.PeriapsisRadius > rb * 1.04 || apo < rb * 0.96) continue;
                    }
                    else if (l.Body != host && l.Body != host.Parent) continue;
                    var leg = l;
                    bool In(double tk) { var test = SectorHomes.LagrangeRegion(s.Home, reg, tk); return test != null && test(RootAt(leg, tk)); }
                    if (In(l.T0)) { tE = l.T0; site = s; break; }
                    int n = (int)Math.Min(4000, Math.Max(48, (t1 - l.T0) / (P / 360)));
                    double prev = l.T0, hit = double.NaN;
                    for (int k = 1; k <= n; k++)
                    {
                        double tk = l.T0 + (t1 - l.T0) * k / n;
                        if (In(tk))
                        {
                            double lo = prev, hi = tk;
                            for (int q = 0; q < 30; q++) { double m = 0.5 * (lo + hi); if (In(m)) hi = m; else lo = m; }
                            hit = hi; break;
                        }
                        prev = tk;
                    }
                    if (!double.IsNaN(hit)) { tE = hit; site = s; break; }
                }
            }
            if (site == null) return null;
            foreach (var l in legs)
            {
                if (tE < l.T0 - 1e-6 || tE > l.T1 + 1e-6) continue;
                var o = l.Body.OriginInRoot(tE);
                var st = OrbitPropagation.StateAt(l.El, tE);
                SectorHomes.LagrangeState(site.Home, reg, tE, out var lp, out var lv);
                d = o.Position + st.Position - lp; v = o.Velocity + st.Velocity - lv;
                break;
            }
        }
        var body = reg.Find(site.Home.Host);
        if (!SectorHomes.LagrangeOrbit(body, tE, out var om, out var w)) return null;
        var plan = new LagPlan { Site = site, TE = tE, Now = nowIn, Period = 2 * Math.PI / w };
        LibStart = $"{site.Sector} from {(nowIn ? "now" : "entry")}: offset {d.Length() / 1000:F1} km, speed {v.Length():F1} m/s";
        // Stepped exactly as the sector flies you (EncounterFrames.RefreshRides: the same pull, with the
        // planet's turn and rate as they are at each moment: an eccentric planet's vary), then turned into
        // the frame turning with the planet (axes as at tE) to be drawn.
        double core = SectorHomes.LagrangeCore(site.Home, reg);
        Vector3D ax = Vector3D.Normalize(om);
        Vector3D p0 = body.StateInParentAt(tE).Position;
        plan.Core = core; plan.Axis = ax; plan.P0 = p0; plan.Host = body;
        Vector3D D = d, V = v;
        var burns = new List<Applied>();
        foreach (var ab in applied) if (ab.Node.T >= tE) burns.Add(ab);
        double horizon = 2.5 * plan.Period;   // a slow orbit round the calm core takes a while: shown whole
        if (burns.Count > 0) horizon = Math.Min(5 * plan.Period, burns[burns.Count - 1].Node.T - tE + 2.5 * plan.Period);   // a slow orbit round the calm core: shown whole
        int steps = (int)Math.Max(64, Math.Min(6000, horizon / 60.0));
        double h = horizon / steps;
        Vector3D lp0 = SectorHomes.Where(site.Home, reg, tE, out var cen);
        int every = Math.Max(1, steps / 400), bi = 0;
        double tau = 0, amp = D.Length();
        plan.Path.Add(D);
        for (int k = 1; k <= steps; k++)
        {
            double tk = tE + tau;
            if (!SectorHomes.LagrangeOrbit(body, tk, out var omk, out var wk)) break;
            while (bi < burns.Count && burns[bi].Node.T <= tk + h)
            {
                // In a sector a node's directions are the sector's own: prograde along your drift
                // relative to the point (as it turns), radial away from it.
                var bn = burns[bi].Node;
                Vector3D u0 = V - Vector3D.Cross(omk, D);
                Axes(new StateVector(D, u0), out var bP, out var bN, out var bR);
                V += bP * bn.Pro + bN * bn.Nor + bR * bn.Rad;
                double thb = plan.Theta(tk);
                plan.NodeAxes[bn] = (Turn(bP, ax, -thb), Turn(bN, ax, -thb), Turn(bR, ax, -thb));
                plan.NodeAt[bn] = Turn(D, ax, -thb);
                amp = D.Length();   // the size you settle into, after the burn
                bi++;
            }
            Vector3D omDot = SectorHomes.LagrangeTurnRate(body, tk);
            Vector3D a1 = SectorHomes.LagrangeAccel(D, V, omk, wk, core, omDot);
            Vector3D dm = D + V * (h / 2), vm = V + a1 * (h / 2);
            Vector3D a2 = SectorHomes.LagrangeAccel(dm, vm, omk, wk, core, omDot);
            D += vm * h; V += a2 * h; tau += h;
            tk = tE + tau;
            amp = Math.Max(amp, D.Length());
            if (bi == 0 && (double.IsNaN(plan.BestT) || D.Length() < plan.BestD))
            {
                plan.BestT = tk; plan.BestD = D.Length(); plan.BestX = D; plan.Omega = omk; plan.W = wk;
                plan.BestV = V - Vector3D.Cross(omk, D); plan.BestU = plan.BestV.Length();
            }
            var inside = SectorHomes.LagrangeRegion(site.Home, reg, tk);
            bool gone = inside != null && !inside(SectorHomes.Where(site.Home, reg, tk) + D);
            if (gone || k % every == 0 || k == steps) plan.Path.Add(Turn(D, ax, -plan.Theta(tk)));
            if (gone) { plan.TX = tk; break; }
        }
        plan.Amp = amp; plan.Point = lp0;
        if (!double.IsNaN(plan.TX))
        {
            // Back on a plain orbit: drawn in the same turning frame, drifting off the sector.
            double tX = plan.TX;
            SectorHomes.LagrangeState(site.Home, reg, tX, out var lpX, out var lvX);
            Vector3D pos = lpX + D, vel = lvX + V;
            var b2 = reg.Root.DeepestSoiContaining(pos, tX) ?? reg.Root;
            var o2 = b2.OriginInRoot(tX);
            var el = CaptureMath.CaptureElements(new StateVector(pos - o2.Position, vel - o2.Velocity), b2.Mu, tX);
            double far = 0.35 * (lp0 - cen).Length();
            if (IsFinite(el.SemiMajorAxis))
                for (int k = 0; k <= 160; k++)
                {
                    double tk = tX + plan.Period * k / 160;
                    Vector3D q = b2.OriginInRoot(tk).Position + OrbitPropagation.StateAt(el, tk).Position - SectorHomes.Where(site.Home, reg, tk);
                    Vector3D rq = Turn(q, ax, -plan.Theta(tk));
                    plan.After.Add(rq);
                    if (rq.Length() > far) break;
                }
        }
        return plan;
    }

    /// <summary>The legs up to tE (your plain orbit ends where the Lagrange sector takes over).</summary>
    static List<Leg> CutAtLagrange(List<Leg> legs, double tE)
    {
        var cut = new List<Leg>();
        foreach (var l in legs)
        {
            if (l.T0 >= tE && cut.Count > 0) break;
            var c = l; if (c.T1 > tE) c.T1 = Math.Max(c.T0, tE);
            cut.Add(c);
            if (l.T1 >= tE) break;
        }
        return cut;
    }

    /// <summary>A plan offset (turning frame, axes as at TE) on the map, as DrawLibration places it.</summary>
    static Vector3D LagLoc(LagPlan p, Vector3D off, double t, Func<Vector3D, double, Vector3D> toMap)
    {
        double tD = p.Now ? t : p.TE;
        return toMap(SectorHomes.Where(p.Site.Home, SystemHost.Registry, tD) + Turn(off, p.Axis, p.Theta(tD)), tD);
    }

    static void Marker(Vector2 s, ColorSRGB col, float u)
    {
        if (!MapPipeline.ScreenIcon("ring", s, 6f * u, col)) MapPipeline.ScreenCircle(s, 5f * u, col, 2f * u);
    }

    /// <summary>
    /// The Lagrange sector as a sphere of influence (KSP style): its teardrop, dotted, round the point as
    /// it is when you enter (or now); your orbit inside it, from the Entry to the Exit; past the Exit the
    /// plain orbit you leave on, dashed; the calm core ringed. Size and period on the point.
    /// </summary>
    static void DrawLibration(LagPlan plan, double t, Func<Vector3D, double, Vector3D> toMap, Func<Vector3D, Vector3D> W, float u)
    {
        if (plan == null || plan.Path.Count < 2) return;
        var reg = SystemHost.Registry;
        var home = plan.Site.Home;
        double tE = plan.Now ? t : plan.TE, turn = plan.Theta(tE);
        Vector3D pt = SectorHomes.Where(home, reg, tE, out Vector3D centre);
        var col = new ColorSRGB(0.95f, 0.75f, 0.35f, 0.9f);
        bool On(Vector3D off, out Vector2 sp) => MapPipeline.ToScreen(W(toMap(pt + Turn(off, plan.Axis, turn), tE)), out sp) && InMapArea(sp);
        // The region: a boundary (dim, dotted; gold is for you).
        var bcol = MapStyle.Zone;
        if (home.Point >= 3)
        {
            double P = SectorHomes.Period(home, reg);
            const double span = 0.035, k = 0.035;
            var outer = new List<Vector2>(); var inner = new List<Vector2>();
            for (int i = 0; i <= 32; i++)
            {
                double q = -span + 2 * span * i / 32, ki = k * Math.Max(0.08, Math.Sin(Math.PI * i / 32));
                // Relative to the parent where it is then: a moon's points only (its planet's own orbit left out).
                Vector3D r = SectorHomes.Where(home, reg, tE + P * q, out var cq) - cq;
                if (MapPipeline.ToScreen(W(toMap(centre + r * (1 + ki), tE)), out var so)) outer.Add(so);
                if (MapPipeline.ToScreen(W(toMap(centre + r * (1 - ki), tE)), out var si)) inner.Add(si);
            }
            var lens = new List<Vector2>(outer); for (int i = inner.Count - 1; i >= 0; i--) lens.Add(inner[i]);
            MapStyle.Boundary(lens, true, bcol, MapStyle.Thin(u), u);
        }
        else
        {
            var host = reg.Find(home.Host);
            double R = 0.15 * SectorHomes.HillRadius(host);
            Vector3D e1 = Vector3D.Normalize(pt - centre), e2 = Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(e1, host.StateInParentAt(tE).Velocity), e1));
            var zone = new List<Vector2>(48);
            for (int i = 0; i < 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                if (!On((e1 * Math.Cos(a) + e2 * Math.Sin(a)) * R, out var sp)) { zone.Clear(); break; }
                zone.Add(sp);
            }
            MapStyle.Boundary(zone, true, bcol, MapStyle.Thin(u), u);
        }
        // The calm core, ringed (when big enough to see).
        bool hasC = On(Vector3D.Zero, out var cs);
        if (!plan.Now && hasC && On(Vector3D.Normalize(plan.Point - centre) * (0.035 * (pt - centre).Length()), out var gw))
        {
            // The ghost's reach on screen: about its half-length (six times its half-width).
            _ghostAt = cs; _ghostR = Math.Max(20f * u, 6f * (gw - cs).Length()); _ghostName = plan.Site.Sector;
            _ghostWall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }
        if (hasC)
        {
            Vector3D side = Vector3D.Cross(plan.Axis, plan.Point - centre);
            side = side.LengthSquared() > 0 ? Vector3D.Normalize(side) : Vector3D.UnitX;
            if (plan.Core > 0 && On(side * plan.Core, out var es))
            {
                float rpx = (es - cs).Length();
                var ccol = HudPanel.Alpha(new ColorSRGB(0.55f, 0.85f, 1f, 1f), 0.5f);
                if (rpx > 4f * u) MapStyle.BoundaryCircle(cs, rpx, ccol, MapStyle.Thin(u), u);
                if (rpx > 40f * u) HudPanel.TagAt(cs + new Vector2(0, rpx + 8f * u), "Calm core", ccol, u, diamond: false);
            }
        }
        // The point itself (as it will be, for an entry ahead).
        if (!plan.Now && hasC && !MapPipeline.ScreenIcon("diamond", cs, 5f * u, col)) MapPipeline.ScreenCircle(cs, 3f * u, col, 2f * u);
        // Your orbit in the sector.
        Vector2 prev = default; bool hp = false;
        foreach (var off in plan.Path)
        {
            if (!On(off, out var sp)) { hp = false; continue; }
            if (hp) MapPipeline.ScreenLine(prev, sp, col, MapStyle.Thick(u));
            prev = sp; hp = true;
        }
        if (!plan.Now && On(plan.Path[0], out var en))
        {
            Marker(en, col, u);
            HudPanel.TagAt(en + new Vector2(10f * u, 0), $"{plan.Site.Sector} Entry  ·  in {Clock(plan.TE - SystemHost.Now)}", col, u, diamond: false);
        }
        if (!double.IsNaN(plan.TX))
        {
            var fcol = HudPanel.Alpha(YouColor, 0.55f);
            var run = new List<Vector2>();
            foreach (var off in plan.After)
            {
                if (!On(off, out var sp)) { MapStyle.Plan(run, false, fcol, MapStyle.Thick(u), u); run.Clear(); continue; }
                run.Add(sp);
            }
            MapStyle.Plan(run, false, fcol, MapStyle.Thick(u), u);
            if (On(plan.Path[plan.Path.Count - 1], out var ex))
            {
                Marker(ex, col, u);
                HudPanel.TagAt(ex + new Vector2(10f * u, 0), $"{plan.Site.Sector} Exit", col, u, diamond: false);
            }
        }
        LibDebug = $"{(plan.Now ? "in" : "entry in " + Clock(plan.TE - SystemHost.Now))} {plan.Site.Sector}: amp {plan.Amp / 1000:F0} km, {(double.IsNaN(plan.TX) ? "stays" : "exits after " + Clock(plan.TX - plan.TE))}, points {plan.Path.Count}+{plan.After.Count}";
        // The orbit's size and fate on the point: now, or (for an entry ahead) once the ghost is big
        // enough to read, named, so it is not taken for whatever sector sits there today.
        bool readable = plan.Now;
        if (!readable && hasC && On(Vector3D.Normalize(plan.Point - centre) * (0.035 * (pt - centre).Length()), out var ws)) readable = (ws - cs).Length() > 40f * u;
        if (hasC && readable)
        {
            string stay = double.IsNaN(plan.TX) ? "stays" : $"exit {(plan.Now ? "in" : "after")} {Clock(plan.TX - plan.TE)}";
            string lp = $"{SystemHost.DisplayName(home.Host)} L{home.Point}";
            string who = plan.Now ? lp : $"{plan.Site.Sector} ({lp}, in {Clock(plan.TE - SystemHost.Now)})";
            HudPanel.TagAt(cs, $"{who}  ·  orbit ±{HudPanel.Km(plan.Amp)}  ·  {stay}", col, u);
        }
    }

    /// <summary>Patches drawn after your orbit (KSP's conic patch limit).</summary>
    public static int MaxPatches = 3;

    static readonly ColorSRGB[] PatchColors =
    {
        // The plan (after a maneuver): orange as KSP, then each further patch its own colour.
        new ColorSRGB(1.00f, 0.62f, 0.25f, 0.95f), new ColorSRGB(0.85f, 0.50f, 1.00f, 0.95f),
        new ColorSRGB(0.55f, 1.00f, 0.55f, 0.95f), new ColorSRGB(1.00f, 0.45f, 0.65f, 0.95f),
    };

    /// <summary>A label centred on an axis from a point, its near edge `gap` beyond it.</summary>
    static (Vector2 min, Vector2 max) AxisLabel(Vector2 from, Vector2 dir, float gap, string text, ColorSRGB c, float u)
    {
        var ts = MapPipeline.MeasureText(text, 0.5f * u);
        float ext = Math.Abs(dir.X) * ts.X * 0.5f + Math.Abs(dir.Y) * ts.Y * 0.5f;   // half the box along the axis
        Vector2 centre = from + dir * (gap + ext + 4f * u);
        HudPanel.LabelAt(centre - new Vector2(ts.X * 0.5f, 0), text, c, u);
        return (centre - ts * 0.5f - new Vector2(4f * u, 4f * u), centre + ts * 0.5f + new Vector2(4f * u, 4f * u));
    }

    /// <summary>The map's open area: between the game's side panels (left details, right index and list) and the tab bars.</summary>
    static bool InMapArea(Vector2 s)
    {
        var sz = MapPipeline.ScreenSize;
        return s.X > sz.X * 0.255f && s.X < sz.X * 0.772f && s.Y > sz.Y * 0.14f && s.Y < sz.Y * 0.84f;   // above the warp bar and hints
    }

    static string HandleName(string l) => l switch
    {
        "P" => "Prograde", "R" => "Retrograde", "N" => "Normal", "AN" => "Anti-normal", "RO" => "Radial out", "RI" => "Radial in", _ => l,
    };

    /// <summary>KSP's navball symbols, drawn with screen lines.</summary>
    static void Icon(string kind, Vector2 c, ColorSRGB col, bool hot, float u)
    {
        // Our KSP icons (textures); the line drawings below when they cannot be drawn.
        string tex = kind switch { "P" => "prograde", "R" => "retrograde", "N" => "normal", "AN" => "antinormal", "RO" => "radialout", "RI" => "radialin", _ => null };
        if (tex != null && MapPipeline.ScreenIcon(tex, c, (hot ? 13f : 10.5f) * u, col)) return;
        float r = (hot ? 8f : 6.5f) * u, w = (hot ? 2.2f : 1.6f) * u, t = 3.5f * u;
        void L(Vector2 a, Vector2 b) => MapPipeline.ScreenLine(c + a, c + b, col, w);
        void Tri(float s, bool up)
        {
            float k = up ? 1 : -1;
            Vector2 a = new Vector2(0, -r * k * s), b = new Vector2(r * 0.87f * s, r * 0.5f * k * s), d = new Vector2(-r * 0.87f * s, r * 0.5f * k * s);
            L(a, b); L(b, d); L(d, a);
        }
        switch (kind)
        {
            case "P":   // circle, dot, three ticks (top, left, right)
                MapPipeline.ScreenCircle(c, r, col, w); MapPipeline.ScreenCircle(c, 1.6f * u, col, 2.4f * u);
                L(new Vector2(0, -r), new Vector2(0, -r - t)); L(new Vector2(-r, 0), new Vector2(-r - t, 0)); L(new Vector2(r, 0), new Vector2(r + t, 0));
                break;
            case "R":   // circle with an X, three ticks
                MapPipeline.ScreenCircle(c, r, col, w);
                float q = r * 0.62f;
                L(new Vector2(-q, -q), new Vector2(q, q)); L(new Vector2(-q, q), new Vector2(q, -q));
                L(new Vector2(0, -r), new Vector2(0, -r - t)); L(new Vector2(-r * 0.7f, r * 0.7f), new Vector2(-(r + t) * 0.7f, (r + t) * 0.7f)); L(new Vector2(r * 0.7f, r * 0.7f), new Vector2((r + t) * 0.7f, (r + t) * 0.7f));
                break;
            case "N":   // triangle, dot
                Tri(1f, true); MapPipeline.ScreenCircle(c, 1.6f * u, col, 2.4f * u);
                break;
            case "AN":  // triangle with its vertices extended
                Tri(0.8f, true);
                L(new Vector2(0, -r * 0.8f), new Vector2(0, -r - t)); L(new Vector2(r * 0.7f, r * 0.4f), new Vector2((r + t) * 0.87f, (r + t) * 0.5f)); L(new Vector2(-r * 0.7f, r * 0.4f), new Vector2(-(r + t) * 0.87f, (r + t) * 0.5f));
                break;
            case "RO":  // circle, four spokes out
                MapPipeline.ScreenCircle(c, r * 0.8f, col, w);
                for (int i = 0; i < 4; i++) { double a = Math.PI / 4 + i * Math.PI / 2; var d = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)); L(d * r * 0.8f, d * (r + t)); }
                break;
            case "RI":  // circle, four spokes in
                MapPipeline.ScreenCircle(c, r, col, w);
                for (int i = 0; i < 4; i++) { double a = Math.PI / 4 + i * Math.PI / 2; var d = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)); L(d * r, d * r * 0.35f); }
                break;
        }
    }

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
    private static Keen.VRage.Core.Game.Systems.Session _session;

    // ───────────────────────────── flying a node (HUD) ─────────────────────────────

    /// <summary>The next node and the delta-v still to do (world-axis direction), or false.</summary>
    public static bool NextBurn(double t, out Node node, out Vector3D remaining, out GravityBody body)
    {
        node = null; remaining = default; body = null;
        if (Nodes.Count == 0 || !Trajectory(t, out _, out var applied) || applied.Count == 0) return false;
        var a = applied[0];
        remaining = a.Dv;   // target minus my trajectory, at the node point
        node = a.Node; body = a.Body;
        return IsFinite(remaining.X) && IsFinite(remaining.Y) && IsFinite(remaining.Z);
    }

    /// <summary>World view: the next burn as a HUD marker; completes the node when done.</summary>
    public static void HudTick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        _session = session;
        if (Nodes.Count == 0) { BurnLine = null; BurnLeft = 0; return; }
        // Nodes left in the past without being flown stay until deleted; a flown one completes.
        if (!NextBurn(t, out var node, out var rem, out var body)) { BurnLeft = 0; BurnLine = null; return; }
        double left = rem.Length();
        double planned = Math.Sqrt(node.Pro * node.Pro + node.Nor * node.Nor + node.Rad * node.Rad);
        if (planned > DoneDv && left < DoneDv)
        {
            lock (Nodes) Nodes.Remove(node);
            if (Selected == node) Selected = null;
            return;
        }
        if (!(left > 1e-9)) { BurnLine = null; return; }   // (a fresh node with no delta-v: no direction - it was NaN)
        Vector3D dirW = rem / left;
        if (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanet != null) dirW = Chart.Of(FrameHost.ObserverPlanet, t).FromInertial(dirW);
        BurnDirWorld = dirW; BurnLeft = left;
        Accel = MaxAccel(session);
        double burn = Accel > 1e-3 ? left / Accel : double.NaN;
        double start = BurnStart(node);   // half the burn before the node (as KSP)
        BurnLine = (AutoBurn.Flying ? "Auto-burning " : node.Auto ? "Auto-burn " : "Next burn ") + $"{left:F1} m/s" + (IsFinite(burn) ? (burn < 60 ? $" ({Math.Max(1, burn):F0} s)" : $" ({Clock(burn)})") : "")
                   + (start > t ? $"   ·   in {Clock(start - t)}" : "   ·   now");
        if (MapView.Visible) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            HudPanel.BeginLabels();
            if (!MapPipeline.HudPoint(camera.Position + dirW * 1e4, out var s, out bool edge)) return;
            Icon("P", s, PlanColor, true, u);
            string when = start > t ? "in " + Clock(start - t) : "now";
            string dur = IsFinite(burn) ? (burn < 60 ? $"  ·  {Math.Max(1, burn):F0} s burn" : $"  ·  {Clock(burn)} burn") : "";
            HudPanel.LabelAt(s + new Vector2(16f * u, 0), $"{left:F1} m/s", PlanColor, u);
        }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>
    /// The burn as it really flies: constant thrust at the ship's acceleration along the planned delta-v
    /// (fixed in inertial space, as KSP), centred on the node, integrated through the body's gravity
    /// (RK4). Short or unknown-thrust burns are impulsive (the difference is negligible).
    /// </summary>
    private static KeplerianElements FiniteBurn(GravityBody body, KeplerianElements coast, double tc, double Tn, StateVector atNode, Vector3D dv)
    {
        double mag = dv.Length(), a = Accel;
        double tau = a > 0.01 ? mag / a : 0;
        if (tau < 2.0 || mag < 1e-6)
            return OrbitalMath.ToElements(new StateVector(atNode.Position, atNode.Velocity + dv), body.Mu, Tn);
        double t0 = Math.Max(tc, Tn - tau / 2);
        var s = OrbitPropagation.StateAt(coast, t0);
        Vector3D d = dv / mag;
        double mu = body.Mu;
        Vector3D Acc(Vector3D r) { double rr = r.Length(); return r * (-mu / (rr * rr * rr)) + d * a; }
        int steps = Math.Max(20, Math.Min(2000, (int)Math.Ceiling(tau / 0.5)));
        double h = tau / steps;
        Vector3D x = s.Position, v = s.Velocity;
        for (int i = 0; i < steps; i++)
        {
            Vector3D k1v = Acc(x), k1x = v;
            Vector3D k2v = Acc(x + k1x * (h / 2)), k2x = v + k1v * (h / 2);
            Vector3D k3v = Acc(x + k2x * (h / 2)), k3x = v + k2v * (h / 2);
            Vector3D k4v = Acc(x + k3x * h), k4x = v + k3v * h;
            x += (k1x + 2 * k2x + 2 * k3x + k4x) * (h / 6);
            v += (k1v + 2 * k2v + 2 * k3v + k4v) * (h / 6);
        }
        return OrbitalMath.ToElements(new StateVector(x, v), mu, t0 + tau);
    }

    /// <summary>When the next burn starts: half its duration before the node.</summary>
    public static double BurnStart(Node n)
    {
        double mag = Math.Sqrt(n.Pro * n.Pro + n.Nor * n.Nor + n.Rad * n.Rad);
        return Accel > 0.01 ? n.T - 0.5 * mag / Accel : n.T;
    }

    /// <summary>The acceleration the player can make (m/s²): the best-facing max thrust over the mass of
    /// the grid you sit in, or of the character (jetpack).</summary>
    public static double Accel = 0;

    private static double MaxAccel(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            Keen.VRage.DCS.Components.Entity e = null; double mass = 0;
            if (FrameHost.Seated)
            {
                OrbitalGridComponent best = null; double bd = 300;
                foreach (var g in GridMembers.All())
                {
                    if (!g.IsServer) continue;
                    double d = (GridMembers.Position(g) - FrameHost.PlayerPosition).Length();
                    if (d < bd) { bd = d; best = g; }
                }
                if (best != null) { e = best.Entity; mass = GridMembers.Mass(best); }
            }
            else
            {
                e = FrameHost.PlayerCharacter(session);
                if (e != null && e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) && mp.InvMass > 0) mass = 1.0 / mp.InvMass;
            }
            if (e == null || mass <= 0 || !e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)) return Accel;
            var p = mt.Regular.Positive; var n = mt.Regular.Negative;
            double f = Math.Max(Math.Max(Math.Max(p.X, p.Y), p.Z), Math.Max(Math.Max(n.X, n.Y), n.Z));
            return f > 0 ? f / mass : Accel;
        }
        catch { return Accel; }
    }

    /// <summary>Warp: when the next burn starts (half the burn before its node), NaN if none.</summary>
    public static double NextBurnStart(double t0)
    {
        double tn = NextNodeTime(t0);
        if (double.IsNaN(tn)) return tn;
        double dv = 0;
        lock (Nodes) foreach (var n in Nodes) if (n.T == tn) dv = Math.Sqrt(n.Pro * n.Pro + n.Nor * n.Nor + n.Rad * n.Rad);
        return Accel > 1e-3 ? tn - 0.5 * dv / Accel : tn;
    }

    /// <summary>Warp: the next sphere-of-influence change on the path after t0 (NaN if none).</summary>
    public static double NextSoiChange(double t0)
    {
        // Last frame's path when there is one (its times are absolute): not a second full solve per warp frame.
        List<Leg> legs = _cLegs;
        if (legs == null || !_cOk) { if (!Trajectory(t0, out legs, out _)) return double.NaN; }
        for (int i = 1; i < legs.Count; i++)
            if (legs[i].Body != legs[i - 1].Body && legs[i].T0 > t0) return legs[i].T0;
        return double.NaN;
    }

    /// <summary>Warp: the next node time after t0 (NaN if none).</summary>
    public static double NextNodeTime(double t0)
    {
        double best = double.NaN;
        lock (Nodes) foreach (var n in Nodes) if (n.T > t0 && (double.IsNaN(best) || n.T < best)) best = n.T;
        return best;
    }

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
                l.Add(line);
            }
        return l;
    }

    public static void Restore(double t, double pro, double nor, double rad, string tBody = null, KeplerianElements? tAfter = null)
    {
        var n = new Node { T = t, Pro = pro, Nor = nor, Rad = rad };
        if (tBody != null && tAfter.HasValue) { n.TBody = tBody; n.TAfter = tAfter.Value; n.Dirty = false; }
        lock (Nodes) Nodes.Add(n);
    }

    /// <summary>DEV: search node time x prograde delta-v for a trajectory that enters `body`'s SOI; keep the first found.</summary>
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

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
