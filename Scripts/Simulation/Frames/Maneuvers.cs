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

    /// <summary>The next burn, one line for the orbit card (null when none).</summary>
    public static string BurnLine;
    /// <summary>The next burn's direction (world axes) and what is left (m/s), from the last HUD tick.</summary>
    public static Vector3D BurnDirWorld; public static double BurnLeft;
    public const double WarpLead = 30.0;      // s: warp stops this long before a node
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
    private static double _cT = double.NaN; private static string _cSig; private static List<Leg> _cLegs; private static List<Applied> _cApplied; private static bool _cOk;

    /// <summary>The trajectory, memoised: the map, the HUD and warp ask for it every frame.</summary>
    public static bool Trajectory(double t, out List<Leg> legs, out List<Applied> applied)
    {
        string sig = Signature(t);
        if (t == _cT && sig == _cSig && _cLegs != null) { legs = _cLegs; applied = _cApplied; return _cOk; }
        _cOk = TrajectoryUncached(t, out legs, out applied);
        if (_cOk) CutAtImpact(legs);
        _cT = t; _cSig = Signature(t); _cLegs = legs; _cApplied = applied;   // after: re-targeting clears Dirty
        return _cOk;
    }

    /// <summary>
    /// A path that hits a body ends there: the leg is cut at the impact and nothing follows (it drew a
    /// 'Palatine Escape' after the impact). The current orbit on the ground is handled elsewhere.
    /// </summary>
    private static void CutAtImpact(List<Leg> legs)
    {
        for (int i = 0; i < legs.Count; i++)
        {
            var l = legs[i];
            double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
            if (!(R > 0) || !(l.El.PeriapsisRadius < R) || !(l.T1 > l.T0)) continue;
            double span = l.T1 - l.T0, prev = l.T0, hit = double.NaN;
            if (OrbitPropagation.StateAt(l.El, l.T0).Position.Length() < R) continue;   // starts inside: leave it
            for (int k = 1; k <= 240; k++)
            {
                double tk = l.T0 + span * k / 240;
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
            return;
        }
    }

    private static string Signature(double t)
    {
        var sb = new System.Text.StringBuilder();
        if (Base(t, out var b, out var el)) sb.Append(b.Name).Append(el.SemiMajorAxis).Append(el.Eccentricity).Append(el.Epoch).Append(el.TrueAnomaly).Append(el.Inclination);
        lock (Nodes) foreach (var n in Nodes) sb.Append('|').Append(n.T).Append(n.Pro).Append(n.Nor).Append(n.Rad).Append(n.Dirty).Append(n.TBody);
        return sb.ToString();
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
            }
            KeplerianElements after;
            Vector3D dv;
            if (n.Dirty || n.TBody != body.Name)
            {
                Axes(st, out var P, out var N, out var R);
                dv = P * n.Pro + N * n.Nor + R * n.Rad;
                after = FiniteBurn(body, el, tc, Tn, st, dv);
                if (!IsFinite(after.SemiMajorAxis)) return true;
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
    private static bool _addPending; private static double _addT; private static Vector2 _addAt;

    private struct Sample { public double T; public Vector2 S; public bool Planned; }

    /// <summary>
    /// Draw the trajectory, nodes and handles, and run the editor. Called inside the map's UI batch.
    /// toMap maps a sun-centred model position at time t to the map's local frame; W local to world.
    /// </summary>
    public static void MapDraw(Func<Vector3D, double, Vector3D> toMap, Func<Vector3D, Vector3D> W, double limit,
                               double t, Vector2 mouse, string selectedSector, string focusBody)
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
        HudPanel.BeginLabels();

        // The PATCHED trajectory: each patch (an arc about one body) in its own colour, drawn about its
        // body. About the view's body, or the sun, at true time; about any other body (an encounter),
        // about that body where it will be when the trajectory gets there (KSP's encounter ghost).
        var anchor = new Dictionary<GravityBody, double>();
        foreach (var l in legs) if (!anchor.ContainsKey(l.Body)) anchor[l.Body] = Math.Max(t, l.T0);
        // A ghost-pinned arc is mapped at its pin time as a whole: the view's own centre moves too (Kemik
        // runs about 2.4 km/s round the sun), and mixing times would smear the arc across the map.
        bool Live(GravityBody b) => b.IsRoot || b.Name == focusBody;
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

        var samples = new List<Sample>();
        int patch = 0;
        var legColour = new List<ColorSRGB>();
        for (int li = 0; li < legs.Count; li++)
        {
            var l = legs[li];
            if (li > 0 && l.Body != legs[li - 1].Body) patch++;
            var col = PatchColors[patch % PatchColors.Length];
            legColour.Add(col);
            bool planning = applied.Count > 0;
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
            Vector2 prev = default; bool hp = false;
            for (int k = 0; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                Vector3D loc = LegLoc(l, tk);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04) { hp = false; continue; }
                if (!MapPipeline.ToScreen(W(loc), out var s) || !InMapArea(s)) { hp = false; continue; }
                samples.Add(new Sample { T = tk, S = s, Planned = l.Planned });
                if (hp && drawn)
                {
                    if (l.Planned) MapPipeline.ScreenDashed(prev, s, col, 2f * u, u);
                    else MapPipeline.ScreenLine(prev, s, col, 2.2f * u);
                }
                prev = s; hp = true;
            }
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
            var faint = HudPanel.Alpha(YouColor, 0.22f);
            Vector2 pv = default; bool hv = false;
            for (int k = 0; k <= 96 && t0 < t1; k++)
            {
                double tk = t0 + (t1 - t0) * k / 96;
                Vector3D loc = Loc(bb, OrbitPropagation.StateAt(bel, tk).Position, tk);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp)) { hv = false; continue; }
                if (hv) MapPipeline.ScreenLine(pv, sp, faint, 1.2f * u);
                pv = sp; hv = true;
            }
        }

        // Patch points: where the trajectory leaves one SOI for another.
        var entryNamed = new HashSet<string>();
        for (int li = 1; li < legs.Count; li++)
        {
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
                    HudPanel.TagAt(st + new Vector2(10f * u, 0), escape ? $"{SystemHost.DisplayName(pa.Body.Name)} Escape" : $"{SystemHost.DisplayName(nb.Name)} Entry", col, u, diamond: false);
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

        // Target: the closest approach on the path, and where the target is then (a ring), joined.
        if (Target != null)
        {
            var ca = ClosestApproach(legs);
            if (ca.ok && Live(ca.leg.Body) && MapPipeline.ToScreen(W(LegLoc(ca.leg, ca.t)), out var cs1) && InMapArea(cs1))
            {
                MapPipeline.ScreenCircle(cs1, 4f * u, TargetColor, 2.2f * u);
                if (EncounterFrames.Ephemeris(ca.site, ca.t, out var tp, out var trel) && tp == ca.leg.Body
                    && MapPipeline.ToScreen(W(Loc(tp, trel.Position, ca.t)), out var cs2) && InMapArea(cs2))
                {
                    MapPipeline.ScreenCircle(cs2, 7f * u, TargetColor, 1.6f * u);
                    MapPipeline.ScreenDashed(cs1, cs2, HudPanel.Alpha(TargetColor, 0.6f), 1.2f * u);
                }
                HudPanel.TagAt(cs1 + new Vector2(8f * u, 0), $"Closest {HudPanel.Km(ca.d)}  in {Clock(ca.t - t)}", TargetColor, u, diamond: false);
            }
        }

        // Sector crossings: where the path comes within the merge range of a sector's site (you arrive
        // there, by conjunction) and where it leaves the site's bubble again.
        foreach (var c in SectorCrossings(t, legs))
        {
            if (!Live(c.Leg.Body)) continue;
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
            if (!MapPipeline.ToScreen(W(loc), out var s)) continue;
            nodeScreen.Add((a.Node, s, a));
            bool sel = a.Node == Selected;
            MapPipeline.ScreenCircle(s, (sel ? 9f : 7f) * u, NodeColor, (sel ? 2.6f : 2f) * u);
            MapPipeline.ScreenCircle(s, 2.5f * u, NodeColor, 3f * u);
            // Armed for auto-burn: said on the node itself (only the menu said so before).
            if (a.Node.Auto) HudPanel.TagAt(s + new Vector2((sel ? 12f : 10f) * u, 0), AutoBurn.Flying ? "auto-burning" : "auto-burn", AutoColor, u, diamond: false);
        }

        // Handles of the selected node.
        var handles = new List<(Vector2 at, Vector2 dir, int axis, double sign, string label, ColorSRGB c)>();
        var selA = applied.Find(a => a.Node == Selected);
        Vector2 selS = default;
        bool edit = focusBody != null;   // the solar view shows nodes; editing is in a planet's view
        if (edit && Selected != null && selA.Node != null && nodeScreen.Exists(x => x.n == Selected && InMapArea(x.s)))   // not when the node is off view
        {
            selS = nodeScreen.Find(x => x.n == Selected).s;
            Axes(selA.Before, out var P, out var N, out var R);
            Vector3D relAt = selA.Before.Position;
            Vector2 Dir(Vector3D v)
            {
                double eps = Math.Max(1.0, selA.Before.Position.Length() * 0.01);
                Vector3D a0 = Loc(selA.Body, relAt, Selected.T), a1 = Loc(selA.Body, relAt + v * eps, Selected.T);
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

        // Closest approach to the selected sector's site along the whole trajectory.
        if (selectedSector != null) ClosestTo(selectedSector, legs, toMap, W, limit, t);
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

    private static void ClosestTo(string sector, List<Leg> legs, Func<Vector3D, double, Vector3D> toMap, Func<Vector3D, Vector3D> W, double limit, double t)
    {
        EncounterFrames.Site site = null;
        foreach (var s in EncounterFrames.Sites) if (s.Sector == sector && s.Anchor) site = s;
        if (site == null) return;
        double best = double.MaxValue, bt = double.NaN; Vector3D bm = default, bs = default;
        foreach (var l in legs)
        {
            double span = l.T1 - l.T0;
            if (!(span > 0)) continue;
            int n = 240;
            for (int k = 0; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                if (!EncounterFrames.Ephemeris(site, tk, out var p, out var rel)) continue;
                Vector3D sr = p.OriginInRoot(tk).Position + rel.Position, mr = RootAt(l, tk);
                double d = (sr - mr).Length();
                if (d < best) { best = d; bt = tk; bm = mr; bs = sr; }
            }
        }
        if (double.IsNaN(bt)) return;
        Vector3D a = toMap(bm, bt), b = toMap(bs, bt);
        if (MapPipeline.ToScreen(W(a), out var sa) && MapPipeline.ToScreen(W(b), out var sb))
        {
            MapPipeline.ScreenCircle(sa, 5f, TgtColor, 2f);
            MapPipeline.ScreenCircle(sb, 5f, TgtColor, 2f);
            MapPipeline.ScreenLine(sa, sb, TgtColor, 1.2f);
            string d = best >= 1e4 ? $"{best / 1000:N0} km" : $"{best / 1000:F1} km";
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            HudPanel.TagAt(sb + new Vector2(8f * u, 10f * u), $"Closest {d}  in {Clock(bt - t)}", TgtColor, u, diamond: false);
        }
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
    static readonly ColorSRGB SectorIn = new ColorSRGB(0.45f, 1.00f, 0.55f, 1f);
    static readonly ColorSRGB SectorOut = new ColorSRGB(0.70f, 0.85f, 0.75f, 0.9f);

    /// <summary>The sector targeted from the map (its site): the path's closest approach to it is marked.</summary>
    public static string Target;
    static readonly ColorSRGB TargetColor = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);
    private static double _caAt = -1; private static string _caSig; private static (bool ok, double t, double d, Leg leg, EncounterFrames.Site site) _ca;

    /// <summary>
    /// Closest approach of the path to the target's site, on the legs about the site's own body (as
    /// KSP's intercept markers): time and distance. Recomputed at most once a second or on a change.
    /// </summary>
    static (bool ok, double t, double d, Leg leg, EncounterFrames.Site site) ClosestApproach(List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string sig = _cSig + "|" + Target;
        if (sig == _caSig && now - _caAt < 1.0) return _ca;
        _caSig = sig; _caAt = now; _ca = default;
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

    public struct Crossing { public string Name; public double T; public bool Entry; public Leg Leg; }
    private static List<Crossing> _cross = new List<Crossing>();
    private static double _crossAt = -1; private static string _crossSig;

    /// <summary>
    /// When the path comes within the merge range (10 km) of a sector's site, and when it is 20 km away
    /// again (the split range), on every leg about the site's own body. Recomputed at most once a
    /// second, or when the plan changes.
    /// </summary>
    public static List<Crossing> SectorCrossings(double t, List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string sig = _cSig;
        if (sig == _crossSig && now - _crossAt < 1.0) return _cross;
        _crossSig = sig; _crossAt = now;
        var list = new List<Crossing>();
        const double enter = 10000, leave = ServerFrames.SlotRadius;
        foreach (var site in EncounterFrames.Sites)
        {
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
        return s.X > sz.X * 0.255f && s.X < sz.X * 0.772f && s.Y > sz.Y * 0.14f && s.Y < sz.Y * 0.93f;
    }

    static string HandleName(string l) => l switch
    {
        "P" => "Prograde", "R" => "Retrograde", "N" => "Normal", "AN" => "Anti-normal", "RO" => "Radial out", "RI" => "Radial in", _ => l,
    };

    /// <summary>KSP's navball symbols, drawn with screen lines.</summary>
    static void Icon(string kind, Vector2 c, ColorSRGB col, bool hot, float u)
    {
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
    /// <summary>DEV: right-click node i (deletes it).</summary>
    /// <summary>DEV: drag node i along the path toward minutes-from-now over a few frames, then release.</summary>
    public static void DevSlide(int i, double minutes) { _devOp = "slide"; _devPx = i; _devT = minutes * 60; _devPhase = 0; }
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
                    UnifiedMap.DevMouse = new Vector2(l[i].s.X / sz.X, l[i].s.Y / sz.Y);
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
        Vector3D dirW = rem / left;
        if (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanet != null) dirW = Chart.Of(FrameHost.ObserverPlanet, t).FromInertial(dirW);
        BurnDirWorld = dirW; BurnLeft = left;
        Accel = MaxAccel(session);
        double burn = Accel > 1e-3 ? left / Accel : double.NaN;
        double start = BurnStart(node);   // half the burn before the node (as KSP)
        BurnLine = (AutoBurn.Flying ? "Auto-burning " : node.Auto ? "Auto-burn " : "Next burn ") + $"{left:F1} m/s" + (IsFinite(burn) ? (burn < 60 ? $" ({Math.Max(1, burn):F0} s)" : $" ({Clock(burn)})") : "")
                   + (start > t ? $"   ·   in {Clock(start - t)}" : "   ·   now");
        if (MapView.Visible || OrbitalMap.Active) return;
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
