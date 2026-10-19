using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

public static partial class Maneuvers
{
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
    // (2026-10-18, UI audit C5) a handle's pull: the component as pulled (unsnapped), the zoom's factor at the pull's start
    // (HandleDrag), the selected node's orbit on screen this frame (px, node to its body's centre); a node to frame once drawn
    private static double _dragRaw, _dragZoom = 1, _orbitPx;
    private static Node _frameReq; private static int _frameTries;

    /// <summary>Glide the map onto this node's orbit (its body centred, the orbit about a third of the screen tall) once it
    /// is drawn - a new node, a porkchop window (UI audit C5: a node made at a far zoom was a 40 px knot).</summary>
    public static void FrameNode(Node n) { _frameReq = n; _frameTries = 0; }

    /// <summary>Ctrl and Shift on the map's editor (the modifiers of a handle's pull, and Ctrl+Z).</summary>
    static bool CtrlDown => MapInput.KeyDown(Keen.VRage.Core.Input.KeyboardInputs.Control) || MapInput.DevKeys.Contains("ctrl");
    static bool ShiftDown => MapInput.KeyDown(Keen.VRage.Core.Input.KeyboardInputs.Shift) || MapInput.DevKeys.Contains("shift");

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
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);   // sizes are for 1080p
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
        // (the impact CutAtImpact found and cut the leg at: re-searching the cut leg found its own end point, exactly on
        //  the surface, below it or not by rounding - the tag flashed on and off every frame)
        foreach (var l in legs)
        {
            if (!l.Impact || !(l.T1 > l.T0)) continue;
            double hitT = l.T1;
            if (Live(l.Body) && MapPipeline.ToScreen(W(LegLoc(l, hitT)), out var hs) && InMapArea(hs))
            {
                float r = 6f * u;
                MapPipeline.ScreenLine(hs - new Vector2(r, r), hs + new Vector2(r, r), ImpactColor, 2.2f * u);
                MapPipeline.ScreenLine(hs - new Vector2(r, -r), hs + new Vector2(r, -r), ImpactColor, 2.2f * u);
                HudPanel.TagAt(hs + new Vector2(8f * u, 0), $"{l.Body.Name} Impact  in {Clock(hitT - t)}", ImpactColor, u, diamond: false);
            }
            break;
        }

        _entryMarkS.Clear();
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
                    _entryMarkS.Add(es);
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
            // ON THE PLAN too (UI audit U3: a planned deorbit leg showed no entry mark): each planned leg's pass through the air
            // (fx-flight's PlannedEntries), marked as the orbit's own; and where the path comes down (Landing), a cross and
            // its words ("Lands on Verdure near Outpost RHA99 (12 km) · 12°N 45°E")
            var planned = PlannedEntries(t);
            for (int k = 0; k < planned.Count; k++)
            {
                var pm = planned[k];
                if (pm.Pass == null || !Live(pm.Leg.Body)) continue;
                double tm = pm.Pass.EnterTime;
                if (double.IsNaN(tm) || tm < pm.Leg.T0 || tm > pm.Leg.T1 || !MapPipeline.ToScreen(W(LegLoc(pm.Leg, tm)), out var es) || !InMapArea(es)) continue;
                if (first.HasValue && (es - first.Value).Length() < 60f * u) continue;
                first ??= es;
                MapPipeline.ScreenCircle(es, 5f * u, EntryColor, 2f * u);
                MapPipeline.ScreenCircle(es, 1.5f * u, EntryColor, 2.5f * u);
                _entryMarkS.Add(es);
                HudPanel.TagAt(es + new Vector2(10f * u, 0), PlannedEntryText(k, pm.Pass), EntryColor, u, diamond: false);
            }
            if (Landing(t, out var land) && Live(land.Leg.Body) && MapPipeline.ToScreen(W(Loc(land.Leg.Body, land.At, land.T)), out var ls) && InMapArea(ls))
            {
                float r = 6f * u;
                MapPipeline.ScreenLine(ls - new Vector2(r, r), ls + new Vector2(r, r), EntryColor, 2.2f * u);
                MapPipeline.ScreenLine(ls - new Vector2(r, -r), ls + new Vector2(r, -r), EntryColor, 2.2f * u);
                _entryMarkS.Add(ls);
                if (land.Text != null) HudPanel.TagAt(ls + new Vector2(10f * u, 0), land.Text, EntryColor, u, diamond: false, must: true);   // (never culled: nothing else says it)
            }
            EntryMarkHover = false;
            foreach (var em in _entryMarkS) if ((em - mouse).Length() < 16f * u) { EntryMarkHover = true; break; }
        }

        // Target: the closest approach on the path, and where the target is then (a ring), joined.
        if (Target != null)
        {
            string tname = Targets.Name(Target);
            TargetWorld = null;
            // The target itself, now: brackets round it (a planet's round its disc).
            if (Targets.At(Target, t, out var tp0, out var trel0) && Live(tp0) && MapPipeline.ToScreen(W(Loc(tp0, trel0.Position, t)), out var ts0) && InMapArea(ts0))
            {
                float br = 11f * u;
                double tr = Targets.Radius(Target);
                if (tr > 0 && MapPipeline.ToScreen(W(Loc(tp0, trel0.Position + new Vector3D(tr, 0, 0), t)), out var te))
                    br = Math.Max(br, (te - ts0).Length() + 7f * u);
                TargetBrackets(ts0, br, u);
                TargetWorld = W(Loc(tp0, trel0.Position, t));
            }
            var ca = ClosestApproach(legs);
            if (ca.ok && Live(ca.leg.Body) && MapPipeline.ToScreen(W(LegLoc(ca.leg, ca.t)), out var cs1) && InMapArea(cs1))
            {
                bool rv = ca.d <= RendezvousRange && Targets.Radius(Target) <= 0;
                var col = rv ? TargetColor : HudPanel.Alpha(TargetColor, 0.8f);
                // Marked as an entry / escape is (a ring round a dot, named), in the target's colour.
                MapPipeline.ScreenCircle(cs1, 5f * u, col, 2f * u);
                MapPipeline.ScreenCircle(cs1, 1.5f * u, col, 2.5f * u);
                if (Targets.Radius(Target) <= 0 && Targets.About(Target, ca.leg.Body, ca.t, out var trel)
                    && MapPipeline.ToScreen(W(Loc(ca.leg.Body, trel.Position, ca.t)), out var cs2) && InMapArea(cs2))
                {
                    MapPipeline.ScreenCircle(cs2, 7f * u, col, 1.6f * u);
                    MapPipeline.ScreenDashed(cs1, cs2, HudPanel.Alpha(TargetColor, 0.6f), 1.2f * u);
                }
                string what = ApproachText(ca, t);
                HudPanel.TagAt(cs1 + new Vector2(10f * u, 0), $"{tname}  ·  {char.ToUpperInvariant(what[0])}{what.Substring(1)}", col, u, diamond: false);
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
        _orbitPx = 0;
        if (Selected != null && selA.Node != null)
        {
            var ns1 = nodeScreen.Find(x => x.n == Selected);
            if (ns1.n != null && MapPipeline.ToScreen(W(Loc(selA.Body, Vector3D.Zero, Selected.T)), out var bc1)) _orbitPx = (ns1.s - bc1).Length();
        }
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
                return s1 - s0;   // (unnormalised: how much of the axis the view shows)
            }
            // EACH AXIS ALONG ITS OWN PROJECTION (the user, 2026-10-10, a node seen edge-on in flight: "the maneuver nodes aren't
            // correctly oriented?" - normal always sat on the prograde/radial diagonal, the top-down map's convention; seen along
            // the orbit's plane prograde and radial project onto one line and all six handles piled on it, where normal is in
            // fact square to the path). An axis the view barely shows (pointing at the camera) or one lying on another falls
            // back: radial square to prograde, normal on their diagonal (the map from above, as before).
            Vector2 pR = Dir(P), rR = Dir(R), nR = Dir(N);
            float m = Math.Max(pR.Length(), Math.Max(rR.Length(), nR.Length()));
            bool Shown(Vector2 v) => m > 0f && v.Length() > 0.3f * m;
            static float Cross(Vector2 a, Vector2 b) => MathF.Abs(a.X * b.Y - a.Y * b.X);
            Vector2 dp = Shown(pR) ? Vector2.Normalize(pR) : new Vector2(1, 0);
            Vector2 dr = Shown(rR) && Cross(dp, Vector2.Normalize(rR)) > 0.35f ? Vector2.Normalize(rR)
                       : (Vector2.Dot(new Vector2(-dp.Y, dp.X), rR) >= 0 ? new Vector2(-dp.Y, dp.X) : new Vector2(dp.Y, -dp.X));
            Vector2 dn = Shown(nR) && Cross(dp, Vector2.Normalize(nR)) > 0.35f && Cross(dr, Vector2.Normalize(nR)) > 0.35f ? Vector2.Normalize(nR)
                       : Vector2.Normalize(dp + dr);
            Vector2 dan = -dn;
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
                    UndoMark();
                    var n = new Node { T = _addT };
                    lock (Nodes) Nodes.Add(n);
                    Selected = n;
                    FrameNode(n);   // (framed at once: at a far zoom its handles were a knot - UI audit C5)
                }
            }
        }

        // Ctrl+Z: the plan before the last edit (UI audit C5)
        if (edit && !MapMenu.Open && _drag == Drag.None && CtrlDown && (MapInput.KeyPressed(Keen.VRage.Core.Input.KeyboardInputs.Z) | MapInput.DevKeys.Remove("undo")))
            Status = Undo() ? "undone" : "nothing to undo";

        if (_drag == Drag.Handle && Selected != null)
        {
            if (!lDown) _drag = Drag.None;
            else
            {
                // Pull along the handle's axis: the delta-v grows faster the further you pull (20 px 0.5 m/s a second, 60 px
                // 4.5, 120 px 18 at 1080p), times the zoom's factor taken at the pull's start (zoomed out: faster), a tenth
                // with Shift; with Ctrl the component sits on a clean step (1 / 10 / 100 m/s by how fast it grows; 0.1 with
                // Shift) - the pull itself runs on unsnapped (SEAerospace.MapUi.HandleDrag; UI audit C5)
                float pull = Math.Max(0f, Vector2.Dot(mouse - _anchor, _dir));
                bool fine = ShiftDown, snap = CtrlDown;
                double rate = SEAerospace.MapUi.HandleDrag.Rate(pull, u, _dragZoom, fine);
                _dragRaw += _sign * rate * dt;
                double v = snap ? SEAerospace.MapUi.HandleDrag.Snap(_dragRaw, SEAerospace.MapUi.HandleDrag.SnapStep(rate, fine)) : _dragRaw;
                _dragMode = snap ? (fine ? 3 : 2) : fine ? 1 : 0;
                double was = _axis == 0 ? Selected.Pro : _axis == 1 ? Selected.Nor : Selected.Rad;
                if (v != was)
                {
                    if (_axis == 0) Selected.Pro = v; else if (_axis == 1) Selected.Nor = v; else Selected.Rad = v;
                    Selected.Edit();
                }
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
            if (hoverHandle >= 0)
            {
                UndoMark();
                _drag = Drag.Handle; _axis = handles[hoverHandle].axis; _sign = handles[hoverHandle].sign; _anchor = handles[hoverHandle].at; _dir = handles[hoverHandle].dir;
                _dragRaw = _axis == 0 ? Selected.Pro : _axis == 1 ? Selected.Nor : Selected.Rad;
                _dragZoom = SEAerospace.MapUi.HandleDrag.ZoomFactor(_orbitPx, u);
            }
            else if (hoverNode != null) { UndoMark(); Selected = hoverNode; _drag = Drag.Slide; }
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
            bool pulling = _drag == Drag.Handle && _axis == axis && _sign == sign;
            if (mine || hot)
            {
                // Centred on the axis, a fixed gap beyond the icon: the labels fan out with the handles. While pulled: the
                // number in m/s and the mode (fine / snap) - UI audit C5 (the strings kept per handle, made on a change)
                string txt = pulling ? HandleText(i, comp, _dragMode) : hot && !mine ? HandleName(label) : HandleText(i, Math.Abs(comp), -1);
                AxisLabel(tip, dir, 11f * u, txt, c, u);
            }
        }
        if (Selected != null && selS != default && InMapArea(selS))   // not pinned at the edge when off view
        {
            double dvm = Math.Sqrt(Selected.Pro * Selected.Pro + Selected.Nor * Selected.Nor + Selected.Rad * Selected.Rad);
            double left = selA.Dv.Length();
            string head = NodeHead(left, dvm, Selected, t);   // (its burn's length and start too - UI audit U2)
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

        // A node to frame (FrameNode): its body centred, the orbit through it about 0.3 screen heights from the centre - the
        // node on screen, its handles open (UI audit C5). In the Rendezvous tab's relative view: not (its scale is its own).
        if (_frameReq != null)
        {
            if (allLive) _frameReq = null;
            else
            {
                var fa = applied.Find(x => x.Node == _frameReq);
                var fn = nodeScreen.Find(x => x.n == _frameReq);
                Vector3D bw = fa.Node != null ? W(Loc(fa.Body, Vector3D.Zero, _frameReq.T)) : default;
                if (fa.Node != null && fn.n != null && MapCamera.Distance > 0 && MapPipeline.ToScreen(bw, out var fbc))
                {
                    float px = (fn.s - fbc).Length(), want = MapPipeline.ScreenSize.Y * 0.3f;
                    if (px > 1f) { MapCamera.PanTo(bw, smooth: true); MapCamera.ZoomTo(MapCamera.Distance * px / want); }
                    _frameReq = null;
                }
                else if (++_frameTries > 90) _frameReq = null;   // (not drawn for a second and a half: given up)
                else if (_frameTries == 5 && fa.Node != null && MapCamera.Distance > 0) MapCamera.PanTo(bw, smooth: true);   // (off the view: its body brought in first)
            }
        }

        // (Selecting a sector no longer marks your closest approach to it: that is what 'Set as target' is for.)
        Status = $"nodes {Nodes.Count}, legs {legs.Count}, mouse {MapInput.Status}";
    }

    static string _head; static long _headKey = long.MinValue;
    /// <summary>The selected node's tag: "12.4 m/s · 25 s burn · starts in 3:44" (left / total when part is flown); made anew only
    /// when what it says changes (it was a new string every frame).</summary>
    static string NodeHead(double left, double dvm, Node n, double t)
    {
        double a = Accel, dur = a > 0.01 ? dvm / a : double.NaN, start = BurnStart(n) - t;
        long key = (long)Math.Round(left * 10) * 1_000_003 + (long)Math.Round(dvm * 10) * 7919 + (long)Math.Floor(start) * 31 + (IsFinite(dur) ? (long)Math.Round(dur) : -1);
        if (key == _headKey && _head != null) return _head;
        _headKey = key;
        string dv = Math.Abs(left - dvm) > 0.05 ? $"{left:F1} / {dvm:F1} m/s" : $"{dvm:F1} m/s";
        string len = !IsFinite(dur) || dvm < 0.05 ? "" : dur < 60 ? $"  ·  {Math.Max(1, dur):F0} s burn" : $"  ·  {Clock(dur)} burn";
        string when = start > 0.5 ? (IsFinite(dur) && dvm >= 0.05 ? "  ·  starts in " : "  ·  in ") + Clock(start) : "  ·  now";
        return _head = dv + len + when;
    }

    /// <summary>The re-entry marks drawn this frame (screen), and the mouse on one (the map's hint line then explains the heat).</summary>
    static readonly List<Vector2> _entryMarkS = new List<Vector2>(8);
    public static bool EntryMarkHover;

    static readonly string[] _peText = new string[4]; static readonly object[] _peFor = new object[4];
    /// <summary>A planned pass's tag (made once per pass: the passes are recomputed at most a few times a second).</summary>
    static string PlannedEntryText(int k, SEAerospace.Entry.Pass p)
    {
        int i = k & 3;
        if (ReferenceEquals(_peFor[i], p) && _peText[i] != null) return _peText[i];
        _peFor[i] = p;
        double share = SEAerospace.Entry.Reentry.Share(p.PeakHeat, false);
        return _peText[i] = p.Landing ? $"Atmosphere entry (planned)  {p.ArrivalSpeed:N0} m/s · heat {share:P0}{(share > 1 ? " — too hot" : "")}"
                                      : p.HasAfter ? $"Aerobrake (planned)  −{p.Dv:N0} m/s · heat {share:P0}" : $"Atmosphere entry (planned)  heat {share:P0}";
    }

    /// <summary>The pull's mode: 0 normal, 1 fine (Shift), 2 snap (Ctrl), 3 fine snap.</summary>
    private static int _dragMode;
    static readonly string[] _hText = new string[6]; static readonly long[] _hKey = new long[6];
    /// <summary>A handle's number: "12.3" (mode -1), pulled "+12.3 m/s" with "· fine" / "· snap 10" (made only when it changes).</summary>
    static string HandleText(int i, double v, int mode)
    {
        long key = (long)Math.Round(v * 10) * 8 + (mode + 1);
        if (_hText[i] != null && _hKey[i] == key) return _hText[i];
        _hKey[i] = key;
        string n = mode < 0 ? $"{v:F1}" : $"{(v >= 0 ? "+" : "")}{v:F1} m/s";
        return _hText[i] = mode switch { 1 => n + "  · fine", 2 => n + "  · snap", 3 => n + "  · fine snap", _ => n };
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


    /// <summary>A duration as a clock ("4:12", "1h 05m"): SEAerospace.MapUi.Words.Clock (tested offline) - never "-0:00" (UI audit
    /// C4, 2026-10-18: the target line said "-0:00" at the closest approach).</summary>
    public static string Clock(double s) => SEAerospace.MapUi.Words.Clock(s);
}
