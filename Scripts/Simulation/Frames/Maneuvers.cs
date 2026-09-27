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
        public string TBody; public KeplerianElements TAfter;
        public void Edit() => Dirty = true;
    }

    public static readonly List<Node> Nodes = new List<Node>();   // lock(Nodes)
    public static Node Selected;
    public const double WarpLead = 30.0;      // s: warp stops this long before a node
    public const double DoneDv = 0.1;         // m/s
    public static string Status = "";

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
    public static bool Trajectory(double t, out List<Leg> legs, out List<Applied> applied)
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
            if (n.T < tc) continue;
            var arcs = PatchedConic.Propagate(body, OrbitPropagation.StateAt(el, tc), tc, n.T - tc, 8, 256);
            if (arcs == null || arcs.Count == 0) return legs.Count > 0;
            foreach (var a in arcs) legs.Add(new Leg { Body = a.Body, El = a.Elements, T0 = a.StartTime, T1 = Math.Min(a.EndTime, n.T), Planned = planned });
            var last = arcs[arcs.Count - 1];
            body = last.Body;
            var st = OrbitPropagation.StateAt(last.Elements, n.T);
            KeplerianElements after;
            Vector3D dv;
            if (n.Dirty || n.TBody != body.Name)
            {
                Axes(st, out var P, out var N, out var R);
                dv = P * n.Pro + N * n.Nor + R * n.Rad;
                after = OrbitalMath.ToElements(new StateVector(st.Position, st.Velocity + dv), body.Mu, n.T);
                if (!IsFinite(after.SemiMajorAxis)) return true;
                n.TAfter = after; n.TBody = body.Name; n.Dirty = false;
            }
            else
            {
                after = n.TAfter;
                dv = OrbitPropagation.StateAt(after, n.T).Velocity - st.Velocity;   // what is left
            }
            applied.Add(new Applied { Node = n, Body = body, Before = st, Dv = dv, After = after });
            el = after; tc = n.T; planned = true;
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
            bool drawn = !(li == 0 && !l.Planned && l.Body.Name == focusBody);   // the map draws your current orbit
            double span = l.T1 - l.T0;
            if (!(span > 0)) continue;
            int n = Math.Max(8, Math.Min(240, (int)(span / Math.Max(1, (legs[legs.Count - 1].T1 - t)) * 360)));
            Vector2 prev = default; bool hp = false;
            for (int k = 0; k <= n; k++)
            {
                double tk = l.T0 + span * k / n;
                Vector3D loc = LegLoc(l, tk);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04) { hp = false; continue; }
                if (!MapPipeline.ToScreen(W(loc), out var s)) { hp = false; continue; }
                samples.Add(new Sample { T = tk, S = s, Planned = l.Planned });
                if (hp && drawn)
                {
                    if (l.Planned) MapPipeline.ScreenDashed(prev, s, col, 2f * u, u);
                    else MapPipeline.ScreenLine(prev, s, col, 2f * u);
                }
                prev = s; hp = true;
            }
        }

        // Patch points: where the trajectory leaves one SOI for another.
        for (int li = 1; li < legs.Count; li++)
        {
            var pa = legs[li - 1]; var nb = legs[li].Body;
            if (nb == pa.Body) continue;
            double tp = legs[li].T0;
            Vector3D loc = LegLoc(pa, tp);
            if (!MapPipeline.ToScreen(W(loc), out var sp)) continue;
            var col = legColour[li];
            MapPipeline.ScreenCircle(sp, 5f * u, col, 2f * u);
            bool escape = nb == pa.Body.Parent;
            // An encounter: the body where it will be, named (the ghost the arc is drawn about).
            if (!escape && !nb.IsRoot && nb.Name != focusBody && MapPipeline.ToScreen(W(Loc(nb, Vector3D.Zero, tp)), out var gs))
            {
                MapPipeline.ScreenCircle(gs, 11f * u, new ColorSRGB(col.R, col.G, col.B, 0.6f), 1.5f * u);
                MapPipeline.ScreenCircle(gs, 3f * u, new ColorSRGB(col.R, col.G, col.B, 0.6f), 2f * u);
                HudPanel.TagAt(gs + new Vector2(14f * u, 0), nb.Name, col, u, diamond: false);
            }
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
        }

        // Handles of the selected node.
        var handles = new List<(Vector2 at, Vector2 dir, int axis, double sign, string label, ColorSRGB c)>();
        var selA = applied.Find(a => a.Node == Selected);
        Vector2 selS = default;
        bool edit = focusBody != null;   // the solar view shows nodes; editing is in a planet's view
        if (edit && Selected != null && selA.Node != null && nodeScreen.Exists(x => x.n == Selected))
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
            float d0 = 46f * u;
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
        ClaimsMouse = _drag != Drag.None || hoverHandle >= 0 || hoverNode != null || !double.IsNaN(hoverT);

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
            else if (!double.IsNaN(hoverT) && hoverT > t + 5) { Selected.T = hoverT; Selected.Edit(); }
        }
        else if (lPressed)
        {
            if (hoverHandle >= 0) { _drag = Drag.Handle; _axis = handles[hoverHandle].axis; _sign = handles[hoverHandle].sign; _anchor = handles[hoverHandle].at; _dir = handles[hoverHandle].dir; }
            else if (hoverNode != null) { Selected = hoverNode; _drag = Drag.Slide; }
            else if (!double.IsNaN(hoverT) && hoverT > t + 5)
            {
                var n = new Node { T = hoverT };
                lock (Nodes) Nodes.Add(n);
                Selected = n;
            }
            else Selected = null;
        }
        if (rPressed && hoverNode != null)
        {
            lock (Nodes) Nodes.Remove(hoverNode);
            if (Selected == hoverNode) Selected = null;
        }

        // The handles: KSP's navball symbols on arms from the node; the one under the mouse (or being
        // pulled) is highlighted and named.
        for (int i = 0; i < handles.Count; i++)
        {
            var (at, dir, axis, sign, label, c) = handles[i];
            bool hot = i == hoverHandle || (_drag == Drag.Handle && _axis == axis && _sign == sign);
            Vector2 tip = at;
            if (_drag == Drag.Handle && _axis == axis && _sign == sign) tip = _anchor + _dir * Math.Max(0f, Vector2.Dot(mouse - _anchor, _dir));
            MapPipeline.ScreenLine(selS + dir * 11f * u, tip - dir * 9f * u, new ColorSRGB(c.R, c.G, c.B, hot ? 0.9f : 0.45f), (hot ? 2f : 1.3f) * u);
            Icon(label, tip, c, hot, u);
            // The component on this axis, beyond the handle it points along (P shows + prograde,
            // R shows retrograde): the number reads along the node's own axes.
            double comp = axis == 0 ? Selected.Pro : axis == 1 ? Selected.Nor : Selected.Rad;
            bool mine = sign > 0 ? comp > 0.05 : comp < -0.05;
            if (mine || hot)
            {
                string txt = hot && !mine ? HandleName(label) : $"{Math.Abs(comp):F1} m/s";
                var ts = MapPipeline.MeasureText(txt, 0.5f * u);
                Vector2 p0 = tip + dir * (16f * u);
                // centre the text on the axis beyond the icon
                Vector2 lp = p0 + new Vector2(dir.X < -0.3f ? -ts.X : dir.X > 0.3f ? 0 : -ts.X * 0.5f, dir.Y > 0.3f ? ts.Y * 0.5f : dir.Y < -0.3f ? -ts.Y * 0.5f : 0);
                HudPanel.LabelAt(lp, txt, c, u);
            }
        }
        if (Selected != null && selS != default)
        {
            double dvm = Math.Sqrt(Selected.Pro * Selected.Pro + Selected.Nor * Selected.Nor + Selected.Rad * Selected.Rad);
            double left = selA.Dv.Length();
            string head = (Math.Abs(left - dvm) > 0.05 ? $"{left:F1} of {dvm:F1} m/s" : $"{dvm:F1} m/s") + "   in " + Clock(Selected.T - t);
            string after = OrbitText(selA);
            // Beside the node, away from the prograde arm.
            Vector2 side = handles.Count > 0 ? -Vector2.Normalize(handles[0].dir + handles[4].dir) : new Vector2(1, 0);
            Vector2 tp0 = selS + side * (70f * u);
            if (side.X < 0) tp0.X -= MapPipeline.MeasureText(head, 0.5f * u).X;
            HudPanel.LabelAt(tp0, head, NodeColor, u);
            if (after != null) HudPanel.LabelAt(tp0 + new Vector2(0, 22f * u), after, new ColorSRGB(0.75f, 0.85f, 0.95f, 1f), u);
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

    static readonly ColorSRGB[] PatchColors =
    {
        new ColorSRGB(0.35f, 0.90f, 1.00f, 0.95f), new ColorSRGB(1.00f, 0.62f, 0.25f, 0.95f),
        new ColorSRGB(0.85f, 0.50f, 1.00f, 0.95f), new ColorSRGB(0.55f, 1.00f, 0.55f, 0.95f),
    };

    static string HandleName(string l) => l switch
    {
        "P" => "Prograde", "R" => "Retrograde", "N" => "Normal", "AN" => "Anti-normal", "RO" => "Radial out", "RI" => "Radial in", _ => l,
    };

    /// <summary>KSP's navball symbols, drawn with screen lines.</summary>
    static void Icon(string kind, Vector2 c, ColorSRGB col, bool hot, float u)
    {
        float r = (hot ? 10f : 8.5f) * u, w = (hot ? 2.4f : 1.8f) * u, t = 5f * u;
        void L(Vector2 a, Vector2 b) => MapPipeline.ScreenLine(c + a, c + b, col, w);
        void Tri(float s, bool up)
        {
            float k = up ? 1 : -1;
            Vector2 a = new Vector2(0, -r * k * s), b = new Vector2(r * 0.87f * s, r * 0.5f * k * s), d = new Vector2(-r * 0.87f * s, r * 0.5f * k * s);
            L(a, b); L(b, d); L(d, a);
        }
        // a dark disc behind, so it reads over orbit lines
        MapPipeline.ScreenRect(c - new Vector2(r, r * 0.7f), c + new Vector2(r, r * 0.7f), new ColorSRGB(0.02f, 0.045f, 0.07f, 0.55f));
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
                double tAbs = SystemHost.Now + _devT; Sample best = default; double bd = double.MaxValue;
                foreach (var s in _lastSamples) { double d = Math.Abs(s.T - tAbs); if (d < bd) { bd = d; best = s; } }
                if (bd < double.MaxValue) { mouse = best.S; _devPressed = true; _devLeft = false; }
                _devOp = null; _devLeft = null;
                return;
            }
            case "rclick":
            {
                var l = new List<(Node n, Vector2 s)>(_lastNodes); l.Sort((a, b) => a.n.T.CompareTo(b.n.T));
                int i = (int)_devPx;
                if (i >= 0 && i < l.Count) { mouse = l[i].s; _devRight = true; }
                _devOp = null;
                return;
            }
        }
    }
    private static Vector2 _devAt, _devDir;

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
        if (Nodes.Count == 0 || MapView.Visible || OrbitalMap.Active) return;
        // Nodes left in the past without being flown stay until deleted; a flown one completes.
        if (!NextBurn(t, out var node, out var rem, out var body)) return;
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
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            var c = new ColorSRGB(0.35f, 0.90f, 1.00f, 1f);
            MapPipeline.HudMarker(camera.Position + dirW * 1e4, $"Burn {left:F1} m/s", node.T > t ? "T-" + Clock(node.T - t) : "now", c);
        }
        finally { MapPipeline.UiEnd(); }
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
        lock (Nodes) foreach (var n in Nodes) l.Add($"node {n.T.ToString("R", inv)} {n.Pro.ToString("R", inv)} {n.Nor.ToString("R", inv)} {n.Rad.ToString("R", inv)}");
        return l;
    }

    public static void Restore(double t, double pro, double nor, double rad)
    {
        lock (Nodes) Nodes.Add(new Node { T = t, Pro = pro, Nor = nor, Rad = rad });
    }

    /// <summary>DEV: search node time x prograde delta-v for a trajectory that enters `body`'s SOI; keep the first found.</summary>
    public static string DevFindEncounter(string body, double t)
    {
        if (!Base(t, out var b0, out var el)) return "no orbit";
        double period = el.IsElliptic ? el.Period : 3600;
        lock (Nodes) Nodes.Clear();
        var n = new Node();
        lock (Nodes) Nodes.Add(n);
        for (double dv = -5; dv >= -150; dv -= 5)
            for (double dt = 120; dt < period; dt += 120)
            {
                n.T = t + dt; n.Pro = dv; n.Nor = 0; n.Rad = 0; n.Edit();
                if (Trajectory(t, out var legs, out _) && legs.Exists(l => l.Body.Name == body))
                {
                    Selected = n;
                    return $"encounter {body}: node in {Clock(dt)} prograde {dv:F0} m/s";
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
