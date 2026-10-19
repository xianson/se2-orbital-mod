using System.Reflection;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using SEAerospace.MapUi;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE MAP'S RENDEZVOUS TAB. A third tab after the game's Colonization and GPS (the game's tabs are fixed:
/// this one is drawn on their row, in their look, and switches theirs off while it is open). It shows your
/// planned path relative to your target, in the target's own curvilinear frame about the lowest body you
/// both go round (a planet for a rock, a station or its moon; the star for another planet's things): the
/// target fixed, NASA axes (ahead to the left, away from the body up), its merge and split zones dotted.
/// Your maneuver nodes are on that path and edited there exactly as on the map (the same editor: click the
/// path to add, drag the handles, right-click for exact values); the path answers as you pull.
/// Display only: nothing here ever places or tunes a burn.
/// </summary>
public static class RendezvousView
{
    public static bool Active;
    public static string Status = "-";

    // The plot in map space: its origin (the target) and plane (facing the camera), and its scale (map units per metre).
    private static Vector3D _origin, _right, _up;
    private static double _k;
    private static string _fitFor;
    private static GravityBody _common;

    static readonly ColorSRGB Axis = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.35f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.75f, 0.82f, 0.9f, 0.7f);
    static readonly ColorSRGB TargetCol = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);

    // ───────────────────────────── the tab ─────────────────────────────

    /// <summary>Our sub screen's id: the Map category, so the game keeps the map open on it.</summary>
    public const string TabId = "Map/Rendezvous";
    private static object _term;          // the terminal view model we added our tab to
    private static object _ourSub;        // our TerminalSubScreen in it
    private static bool _injectFailed;
    public static string TabStatus = "-";

    /// <summary>
    /// A REAL tab: the terminal's tab row is its list of sub screens, and the map's two are entries of the
    /// Map category. A third entry there ("Map/Rendezvous", headed "Rendezvous") is drawn, selected and
    /// highlighted by the game itself, the map kept open as it switches (any Map sub screen keeps it). Its
    /// content is the Colonization tab's (the left panels, and the game's own enter / leave / close of the
    /// map), so only what the map draws changes. Added once per terminal it opens with.
    /// Should that fail, a tab drawn on the row in the game's look stands in (<see cref="DrawnTab"/>).
    /// </summary>
    public static void Tab(Keen.VRage.Core.Game.Systems.Session session, Vector2 mouse)
    {
        object term = null;
        try { term = GameUi.TopScreenObject(session); } catch { }
        if (term != null && term.GetType().Name == "TerminalScreenViewModel")
        {
            if (!ReferenceEquals(term, _term)) { _term = term; _ourSub = null; _injectFailed = false; Inject(term); }
            if (_ourSub != null)
            {
                bool now = ReferenceEquals(Prop(term, "SelectedSubScreen"), _ourSub);
                if (now && !Active) _fitFor = null;
                Active = now;
                return;
            }
        }
        if (_injectFailed) DrawnTab(session, mouse);
    }

    static object Prop(object o, string name)
    {
        try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); } catch { return null; }
    }

    static void Inject(object term)
    {
        try
        {
            var subsProp = term.GetType().GetProperty("SubScreens");
            if (!(subsProp?.GetValue(term) is System.Collections.IList subs)) { Fail("no sub screen list"); return; }
            object gps = null, col = null;
            foreach (var sub in subs)
            {
                string id = Prop(Prop(sub, "Id"), "Value") as string;
                if (id == TabId) { _ourSub = sub; TabStatus = "tab already there"; return; }
                if (id == "Map/GPS") gps = sub;
                if (id == "Map/Colonization") col = sub;
            }
            if (gps == null || col == null)
            {
                var ids = new List<string>();
                foreach (var sub in subs) ids.Add((Prop(Prop(sub, "Id"), "Value") as string) ?? Prop(sub, "Id")?.ToString() ?? "?");
                Fail("no map sub screens among " + string.Join(", ", ids));
                return;
            }
            Type subType = gps.GetType();
            ConstructorInfo ctor = null;
            foreach (var c in subType.GetConstructors()) if (c.GetParameters().Length == 6) ctor = c;
            if (ctor == null) { Fail("no sub screen constructor"); return; }
            var ps = ctor.GetParameters();
            object idV = Activator.CreateInstance(ps[1].ParameterType, TabId);
            object loc = ps[2].ParameterType.GetMethod("FromString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)?.Invoke(null, new object[] { "Rendezvous" });
            if (loc == null) { Fail("no LocKey.FromString"); return; }
            object colContent = Prop(col, "Content");   // the Colonization tab's content (created as the game would)
            // A sectorless world has no colonization view model (the game makes it only with sectors): its content is null,
            // and a null content hid every panel - the GPS list went with it. The GPS tab's content then (its left panel, and
            // the map's own enter / leave through the map tab).
            Func<object, object> factory = _ => colContent ?? Prop(col, "Content") ?? Prop(gps, "Content");
            object ours = ctor.Invoke(new object[] { Prop(col, "Category"), idV, loc, true, null, factory });
            // the Colonization tab's control hints (zoom, move)
            try { subType.GetProperty("ControlHints")?.SetValue(ours, Prop(col, "ControlHints") ?? Prop(gps, "ControlHints")); } catch { }
            var arr = Array.CreateInstance(subType, subs.Count + 1);
            int i = 0;
            foreach (var sub in subs) arr.SetValue(sub, i++);
            arr.SetValue(ours, i);
            subsProp.GetSetMethod(true).Invoke(term, new object[] { arr });
            // tell the tab row
            MethodInfo raise = null;
            for (var t = term.GetType(); t != null && raise == null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "OnPropertyChanged" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) { raise = m; break; }
            raise?.Invoke(term, new object[] { "SubScreens" });
            _ourSub = ours;
            // the game's W A S D / wheel hints under this map: they move nothing here (UI audit C6) - emptied, on the UI thread
            if (CleanMap.Enabled) { var tm = term; var cs = col; UiThread.Post(() => MapChrome.ClearMapHints(tm, cs, ours)); }
            TabStatus = raise != null ? "tab added" : "tab added (row not told)";
        }
        catch (Exception e) { Fail((e.InnerException ?? e).Message); }
    }

    static void Fail(string why) { _injectFailed = true; TabStatus = "tab not added: " + why; }

    /// <summary>Select the tab (as a click on it): harness.</summary>
    public static void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        _fitFor = null;
        if (_term != null && _ourSub != null) { try { _term.GetType().GetProperty("SelectedSubScreen")?.SetValue(_term, _ourSub); } catch { } }
        else Active = true;
    }

    /// <summary>The map closed: our state only (the terminal is the game's to close; the next one gets its own tab).</summary>
    public static void Reset() { Active = false; _fitFor = null; }

    /// <summary>Back to the map's tab: Colonization - or, where the world has none (its tab hidden: sectorless), the Contacts
    /// (GPS) tab, which shows the map there (selecting the hidden Colonization tab left no sub-tab lit - UI audit cosmetic,
    /// shot l6_05). The porkchop's pick and the harness.</summary>
    public static void Close()
    {
        if (Active && _term != null && _ourSub != null && ReferenceEquals(Prop(_term, "SelectedSubScreen"), _ourSub))
        {
            try
            {
                object col = null, gps = null;
                if (Prop(_term, "SubScreens") is System.Collections.IList subs)
                    foreach (var sub in subs)
                    {
                        string id = Prop(Prop(sub, "Id"), "Value") as string;
                        if (id == "Map/Colonization") col = sub; else if (id == "Map/GPS") gps = sub;
                    }
                object to = col != null && Prop(col, "IsVisible") is bool v && v ? col : gps ?? col;
                if (to != null) _term.GetType().GetProperty("SelectedSubScreen")?.SetValue(_term, to);
            }
            catch { }
        }
        Active = false;
        _fitFor = null;
    }

    static readonly ColorSRGB TabText = new ColorSRGB(179 / 255f, 190 / 255f, 196 / 255f, 1f);
    static readonly ColorSRGB TabOrange = new ColorSRGB(212 / 255f, 145 / 255f, 38 / 255f, 1f);
    static readonly ColorSRGB TabEdge = new ColorSRGB(150 / 255f, 108 / 255f, 48 / 255f, 1f);
    static readonly ColorSRGB TabFill = new ColorSRGB(8 / 255f, 22 / 255f, 28 / 255f, 0.92f);

    /// <summary>
    /// The stand-in: a tab drawn on the game's row in its look (measured at 720p: 35 px high from y 72, text
    /// padded 12 px, 8 px between tabs, after GPS). Only when the real one could not be added.
    /// </summary>
    static void DrawnTab(Keen.VRage.Core.Game.Systems.Session session, Vector2 mouse)
    {
        var scr = MapPipeline.ScreenSize;
        float s = scr.Y / 720f;
        const string label = "Rendezvous";
        Vector2 m1 = MapPipeline.MeasureText(label, 1f);
        float scale = m1.Y > 0 ? 23f * s / m1.Y : 1f;
        Vector2 size = MapPipeline.MeasureText(label, scale);
        float x0 = 263f * s, y0 = 72f * s, h = 35f * s;
        var min = new Vector2(x0, y0);
        var max = new Vector2(x0 + size.X + 24f * s, y0 + h);
        bool hover = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
        if (hover && MapInput.LeftReleased && !MapCamera.DragEnded) { Active = !Active; _fitFor = null; }
        if (Active)
        {
            MapPipeline.ScreenFill(Rounded(min, max, 5f * s), TabFill);
            MapPipeline.ScreenPath(Rounded(min, max, 5f * s), true, TabEdge, 1f * s);
            MapPipeline.ScreenRect(new Vector2(min.X + 3f * s, min.Y), new Vector2(max.X - 3f * s, min.Y + 4f * s), TabOrange);
        }
        var tc = Active ? new ColorSRGB(1f, 1f, 1f, 1f) : hover ? new ColorSRGB(0.9f, 0.94f, 0.97f, 1f) : TabText;
        MapPipeline.ScreenText(new Vector2(x0 + 12f * s, y0 + (h - size.Y) * 0.5f), label, tc, scale);
        MapPipeline.Reserve(min, max);
    }

    static List<Vector2> Rounded(Vector2 min, Vector2 max, float r)
    {
        var pts = new List<Vector2>(24);
        void Arc(Vector2 c, double a0)
        {
            for (int i = 0; i <= 4; i++) { double a = a0 + Math.PI / 2 * i / 4; pts.Add(c + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r)); }
        }
        Arc(new Vector2(max.X - r, min.Y + r), -Math.PI / 2);
        Arc(new Vector2(max.X - r, max.Y - r), 0);
        Arc(new Vector2(min.X + r, max.Y - r), Math.PI / 2);
        Arc(new Vector2(min.X + r, min.Y + r), Math.PI);
        return pts;
    }

    // ───────────────────────────── the view ─────────────────────────────

    /// <summary>The target's state about its parent at t (a sector's site, a body, a ship: <see cref="Targets.At"/>), or false.</summary>
    static bool TargetAt(string target, double tk, out GravityBody parent, out StateVector rel) => Targets.At(target, tk, out parent, out rel);

    /// <summary>The lowest body both go round.</summary>
    static GravityBody Common(GravityBody a, GravityBody b) => SEAerospace.Frames.RendezvousPlot.Common(a, b) ?? SystemHost.Registry?.Root;

    /// <summary>The target is the frame you are in (its site's): you are there - no plot about it, no rendezvous.</summary>
    public static bool TargetIsOwnFrame(string target)
    {
        var f = FrameHost.PlayerFrame;
        if (f == null || target == null) return false;
        foreach (var st in EncounterFrames.Sites) if (st.Sector == target && st.FrameId == f.Id) return true;
        return Targets.InOwnFrame(target);
    }

    /// <summary>A sun-centred position at tk in the target's curvilinear frame about the common body: (along, radial, cross) in metres.</summary>
    static bool Rel(string target, GravityBody c, Vector3D rootPos, double tk, out Vector3D q)
    {
        q = default;
        if (!TargetAt(target, tk, out var tp, out var trel)) return false;
        var cs = c.OriginInRoot(tk);
        var ps = tp.OriginInRoot(tk);
        var ts = new StateVector(ps.Position + trel.Position - cs.Position, ps.Velocity + trel.Velocity - cs.Velocity);
        q = SEAerospace.Frames.RendezvousPlot.Curvilinear(ts, new StateVector(rootPos - cs.Position, Vector3D.Zero));
        return IsFinite(q.X) && IsFinite(q.Y) && IsFinite(q.Z);
    }

    private static double _hudAt = -1;
    private static List<(double along, double radial)> _hudPath;
    private static List<double> _hudCross;
    private static (double along, double radial) _hudVel;
    private static double _hudPeriod;
    private static string _hudFor;

    /// <summary>
    /// In flight with a target (a site, a rock, a station round the same planet or moon as you): the HUD's
    /// relative-motion plot about it, from your planned path over one of its revolutions (nodes included).
    /// Refreshed a few times a second. False: no target, a planet or moon target (its porkchop is on the map),
    /// or one only the star is common to.
    /// </summary>
    public static bool HudRelative(double t, OrbitHud.Readout r)
    {
        string target = Maneuvers.Target;
        var reg = SystemHost.Registry;
        if (r == null || target == null || reg == null) return false;
        if (Targets.Body(target) != null || TargetIsOwnFrame(target)) return false;
        if (!Maneuvers.Trajectory(t, out var legs, out _) || legs.Count == 0 || !TargetAt(target, t, out var tp, out var trel)) return false;
        var c = Common(legs[0].Body, tp);
        if (c == null || c.IsRoot) return false;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_hudFor != target || wall - _hudAt > 0.25 || _hudPath == null)
        {
            _hudFor = target; _hudAt = wall;
            // one revolution of the target about the common body
            StateVector ts = default;
            if (TargetAt(target, t, out var p0, out var r0))
            {
                var po = p0.OriginInRoot(t); var co = c.OriginInRoot(t);
                ts = new StateVector(po.Position + r0.Position - co.Position, po.Velocity + r0.Velocity - co.Velocity);
            }
            double period = SEAerospace.Frames.RendezvousPlot.PeriodAbout(ts, c.Mu, 3600);   // (its real period: an ellipse's, not a circle's at its radius now)
            const int n = 160;
            var path = new List<(double, double)>(n + 1);
            var cross = new List<double>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double tk = t + period * i / n;
                int li = legs.FindIndex(l => tk >= l.T0 - 1e-6 && tk <= l.T1 + 1e-6);
                if (li < 0) break;
                if (!Rel(target, c, Maneuvers.RootAt(legs[li], tk), tk, out var q)) break;
                path.Add((q.X, q.Y)); cross.Add(q.Z);
            }
            if (path.Count < 2) { _hudPath = null; return false; }   // (never the last target's path under this name)
            (double, double) vel = (0, 0);
            if (Rel(target, c, Maneuvers.RootAt(legs[0], t + 1), t + 1, out var q1)) vel = (q1.X - path[0].Item1, q1.Y - path[0].Item2);
            _hudPath = path; _hudCross = cross; _hudVel = vel; _hudPeriod = period;
        }
        r.Relative = _hudPath;
        r.RelNow = _hudPath[0];
        r.RelCross = _hudCross;
        r.RelNowCross = _hudCross[0];
        r.RelVel = _hudVel;
        r.RelPeriod = _hudPeriod;
        r.RelSamples = 161;
        r.AnchorName = Targets.Name(target);
        r.Holding = false;
        r.RelLeaves = false;
        r.RelIsTarget = true;
        r.RelBoundary = ServerFrames.Rendezvous.EnterRangeMeters;   // (the ring: where you merge with it)
        return true;
    }

    /// <summary>
    /// A planet or moon target: the porkchop about the body you both go round, and your plan on it (its first
    /// burn, its arrival at the target's space). Already round the target (or one of its moons): nothing to plan here.
    /// </summary>
    static void DrawWindows(SystemRegistry reg, double t, List<Maneuvers.Leg> legs, List<Maneuvers.Applied> applied,
                            GravityBody tbody, string target, Vector2 head, Vector2 mouse, float u, Vector2 scr)
    {
        var white = new ColorSRGB(1f, 1f, 1f, 1f);
        string tn = SystemHost.DisplayName(tbody.Name);
        for (var x = legs[0].Body; x != null; x = x.Parent)
            if (x == tbody)
            {
                MapPipeline.ClipRect = null;
                CleanMap.Plated(head, $"Rendezvous · {tn}", white, 1.05f * MapPipeline.MapText);
                CleanMap.Plated(head + new Vector2(0, 34f * u), $"At {tn}: plan your capture on the map", Dim, 0.8f * MapPipeline.MapText);
                Status = "at the target";
                return;
            }
        var c = Common(legs[0].Body, tbody);
        var to = Porkchop.Under(tbody, c);
        if (c == null || to == null) return;
        // What you leave: your planet (its parking orbit), or, going round c yourself, your own orbit.
        var from = new Porkchop.From();
        bool haveBase = Maneuvers.Base(t, out var bb, out var bel) && IsFinite(bel.SemiMajorAxis);
        if (legs[0].Body == c)
        {
            if (!haveBase || bb != c) return;
            from.Name = "you"; from.Mu = 0; from.State = tk => OrbitPropagation.StateAt(bel, tk);
        }
        else
        {
            var fb = Porkchop.Under(legs[0].Body, c);
            if (fb == null) return;
            from.Name = fb.Name; from.Mu = fb.Mu;
            from.ParkR = haveBase && bb == fb && bel.SemiMajorAxis > 0 ? bel.SemiMajorAxis : 1.5 * (reg.FindDefinition(fb.Name)?.RadiusMeters ?? 0);
            from.State = tk => { var a = fb.OriginInRoot(tk); var o = c.OriginInRoot(tk); return new StateVector(a.Position - o.Position, a.Velocity - o.Velocity); };
        }
        // Your plan: its first burn, its arrival in the target's space, its burns' total, and the periapsis there.
        double dep = double.NaN, arr = double.NaN, dv = 0; string note = null;
        if (applied != null && applied.Count > 0) { dep = applied[0].Node.T; foreach (var ap in applied) dv += ap.Dv.Length(); }
        foreach (var l in legs)
        {
            bool inTo = false;
            for (var x = l.Body; x != null; x = x.Parent) if (x == to) { inTo = true; break; }
            if (!inTo) continue;
            arr = l.T0;
            if (l.Body == to && IsFinite(l.El.PeriapsisRadius))
                note = $"Pe {HudPanel.Km(l.El.PeriapsisRadius - (reg.FindDefinition(to.Name)?.RadiusMeters ?? 0))}";
            break;
        }
        if (double.IsNaN(dep)) note = "Your plan: no burn yet (add a maneuver on the map)";
        Porkchop.Draw(MapLayout.P(0.27f, 0.22f), MapLayout.P(0.765f, 0.72f), c, from, to, tn,
                      dep, arr, dv, note, t, mouse, u);
        // A CLICK ON A WINDOW MAKES ITS BURN (UI audit C2: "it says 'pick a window' and clicking does nothing"): the transfer's
        // first burn as a maneuver, the map shown and framed on it, to fine-tune there
        if (Porkchop.Picked) MakeTransfer(c, from, to, t, haveBase, bb, bel, tn);
        MapPipeline.ClipRect = null;
        CleanMap.Plated(head, $"Rendezvous · {tn}", white, 1.05f * MapPipeline.MapText);
        CleanMap.Plated(head + new Vector2(0, 34f * u), PickHint, Dim, 0.8f * MapPipeline.MapText);
        Status = $"windows to {to.Name} about {c.Name}: {Porkchop.Status}; plan dep {(double.IsNaN(dep) ? "-" : Maneuvers.Clock(dep - t))} entry {(double.IsNaN(arr) ? "-" : Maneuvers.Clock(arr - t))} dv {dv:F0} {note}";
    }

    const string PickHint = "Click a window to make its burn: the map then shows it to fine-tune  ·  your plan is the cross";

    /// <summary>
    /// The transfer of the clicked porkchop cell (Porkchop.PickDep / PickTof) as your plan's first burn (UI audit C2). The
    /// Lambert arc from where you leave to the target then (the cheaper of its two senses, as the plot's cell). Leaving your
    /// own orbit about the body both go round: the burn at the departure, the arc's velocity minus yours, along your axes.
    /// Leaving a planet from a parking orbit: a prograde burn where the escape hyperbola's periapsis puts its asymptote along
    /// v-infinity (SEAerospace.MapUi.Ejection), on the revolution before the departure, of the periapsis speed minus yours.
    /// The plan so far is replaced (Ctrl+Z on the map brings it back), the map shown and framed on the node.
    /// </summary>
    static void MakeTransfer(GravityBody c, Porkchop.From from, GravityBody to, double t, bool haveBase, GravityBody bb, KeplerianElements bel, string tn)
    {
        double td = Porkchop.PickDep, tof = Porkchop.PickTof;
        var d = from.State(td);
        var to0 = to.OriginInRoot(td + tof); var c0 = c.OriginInRoot(td + tof);
        var a = new StateVector(to0.Position - c0.Position, to0.Velocity - c0.Velocity);
        Vector3D nrm = Vector3D.Cross(d.Position, d.Velocity), v1 = default;
        double cost = double.PositiveInfinity;
        for (int k = 0; k < 2; k++)
        {
            if (!SEAerospace.Rendezvous.Lambert.Solve(d.Position, a.Position, tof, c.Mu, k == 0, nrm, out var s1, out var s2)) continue;
            double vi1 = (s1 - d.Velocity).Length(), vi2 = (s2 - a.Velocity).Length();
            double cc = (from.Mu > 0 ? Ejection.PeriapsisSpeed(vi1, from.Mu, Math.Max(1, from.ParkR)) - Math.Sqrt(from.Mu / Math.Max(1, from.ParkR)) : vi1) + vi2;
            if (cc < cost) { cost = cc; v1 = s1; }
        }
        if (double.IsInfinity(cost)) { Messages.Say(SEAerospace.Flight.MessageKinds.Burn, "No transfer from that window (no arc): pick another cell"); return; }
        double T = td, pro, nor = 0, rad = 0;
        Vector3D vinf = v1 - d.Velocity;
        if (from.Mu <= 0)
        {
            // your own orbit about c: the burn is the arc's start minus your velocity, on your axes there
            Maneuvers.Axes(d, out var P, out var N, out var R);
            pro = Vector3D.Dot(vinf, P); nor = Vector3D.Dot(vinf, N); rad = Vector3D.Dot(vinf, R);
        }
        else if (haveBase && bb != null && bb.Name == from.Name && bel.IsElliptic && IsFinite(bel.Period))
        {
            var s0 = OrbitPropagation.StateAt(bel, td);
            Vector3D n = Vector3D.Cross(s0.Position, s0.Velocity);
            n = n.LengthSquared() > 0 ? Vector3D.Normalize(n) : Vector3D.UnitY;
            Vector3D pe = Ejection.Periapsis(vinf, n, bb.Mu, s0.Position.Length());
            double t0 = Math.Max(t + 15, td - bel.Period), t1 = Math.Max(t0 + 1, td);
            double tb = pe.LengthSquared() > 0 ? Ejection.NearestTime(tk => OrbitPropagation.StateAt(bel, tk).Position, t0, t1, pe, 360, out _) : double.NaN;
            if (IsFinite(tb)) T = tb;
            var sb = OrbitPropagation.StateAt(bel, T);
            pro = Ejection.PeriapsisSpeed(vinf.Length(), bb.Mu, sb.Position.Length()) - sb.Velocity.Length();
        }
        else pro = Ejection.PeriapsisSpeed(vinf.Length(), from.Mu, Math.Max(1, from.ParkR)) - Math.Sqrt(from.Mu / Math.Max(1, from.ParkR));
        if (T < t + 15) T = t + 15;
        bool had = Maneuvers.Nodes.Count > 0;
        Maneuvers.UndoMark();
        Maneuvers.ClearAll();
        Maneuvers.AddNodeAt(T);
        var node = Maneuvers.Selected;
        if (node == null) return;
        node.Pro = pro; node.Nor = nor; node.Rad = rad; node.Edit();
        CleanMap.ViewFocus = null;   // (the map about your planet: the node's)
        Maneuvers.FrameNode(node);
        Close();
        Messages.Say(SEAerospace.Flight.MessageKinds.Burn, $"Transfer to {tn}: burn {Math.Sqrt(pro * pro + nor * nor + rad * rad):F0} m/s in {Maneuvers.Clock(T - t)} - fine-tune it on the map"
            + (had ? " (your plan replaced: Ctrl+Z on the map brings it back)" : ""), 8);
        Status = $"transfer node at {T - t:F0} s: {pro:F1} / {nor:F1} / {rad:F1} m/s (window {Porkchop.PickDv:F0} m/s)";
    }

    /// <summary>
    /// The view, inside the map's UI batch (instead of the map's own contents). W: map space to world
    /// (the map camera's: its wheel zooms, its drag pans).
    /// </summary>
    public static void Draw(Keen.VRage.Core.Game.Systems.Session session, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, Vector2 mouse)
    {
        var scr = MapPipeline.ScreenSize;
        float u = MapPipeline.UiScale(scr.Y);
        MapPipeline.ClipRect = MapLayout.OpenArea;
        var head = MapLayout.P(0.265f, 0.158f);
        string target = Maneuvers.Target;
        if (target == null)
        {
            CleanMap.Plated(head, "No target", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f * MapPipeline.MapText);
            CleanMap.Plated(head + new Vector2(0, 34f * u), "On the map: point at a ship, a sector or a body and press N (or right-click: Set as target), or pick one in the target list", Dim, 0.8f * MapPipeline.MapText);
            Status = "no target";
            return;
        }
        bool targetKnown = TargetAt(target, t, out var tpar, out _);
        if (!Maneuvers.Trajectory(t, out var legs, out var applied) || legs.Count == 0 || !targetKnown)
        {
            CleanMap.Plated(head, $"Rendezvous · {Targets.Name(target)}", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f * MapPipeline.MapText);
            // (a target that is not there any more - a site dissolved, a rock you left: say so, not "no path" forever)
            CleanMap.Plated(head + new Vector2(0, 34f * u), targetKnown ? "No path to compare yet" : $"{Targets.Name(target)} can't be found now (out of your sensors' range, or gone)  ·  pick another target on the map, or clear it (× in the map's target panel)", Dim, 0.8f * MapPipeline.MapText);
            Status = targetKnown ? "no path" : "target lost";
            return;
        }
        // A planet or a moon: its transfer windows (the porkchop) with your plan on them, instead of the plot.
        var tbody = Targets.Body(target);
        if (tbody != null)
        {
            DrawWindows(reg, t, legs, applied, tbody, target, head, mouse, u, scr);
            return;
        }
        var c = Common(legs[0].Body, tpar);
        if (c == null) return;

        // The plot's plane: through where the map is looking, facing the camera (upright as you orbit the view).
        Vector3D camPos = MapPipeline.CameraPosition ?? W(MapCamera.FocusV);
        var cq = (QuaternionD)MapPipeline.CameraOrientation;
        Vector3D Loc(Vector3D world) => MapCamera.FromWorld(world);
        string key = target + "|" + c.Name;
        if (_fitFor != key || _common != c)
        {
            // Fit: your path's reach from the target on screen at about a third of its height.
            _fitFor = key; _common = c;
            _origin = MapCamera.FocusV;
            double ext = 20000;
            foreach (var l in legs)
            {
                double t0 = Math.Max(l.T0, t), t1 = Math.Min(l.T1, t + 6 * 3600);
                for (int i = 0; i <= 48 && t1 > t0; i++)
                {
                    double tk = t0 + (t1 - t0) * i / 48;
                    if (Rel(target, c, Maneuvers.RootAt(l, tk), tk, out var q)) ext = Math.Max(ext, Math.Max(Math.Abs(q.X), Math.Abs(q.Y)));
                }
            }
            // map units per screen pixel at the origin, from a unit step along the camera's right
            Vector3D rw = cq * Vector3D.Right, uw = cq * Vector3D.Up;
            Vector3D oW = W(_origin);
            _right = Vector3D.Normalize(Loc(oW + rw) - Loc(oW));
            _up = Vector3D.Normalize(Loc(oW + uw) - Loc(oW));
            // (a small step: a whole map unit can be far off the screen)
            double step = Math.Max(1e-9, MapCamera.Distance * 0.05), px = 0;
            if (MapPipeline.ToScreen(W(_origin), out var s0) && MapPipeline.ToScreen(W(_origin + _right * step), out var s1)) px = (s1 - s0).Length() / step;
            _k = px > 0 ? MapLayout.LenY(0.33f) / (ext * px) : 1e-9;
        }
        else
        {
            // keep facing the camera as it turns
            Vector3D oW = W(_origin);
            _right = Vector3D.Normalize(Loc(oW + cq * Vector3D.Right) - Loc(oW));
            _up = Vector3D.Normalize(Loc(oW + cq * Vector3D.Up) - Loc(oW));
        }
        // NASA axes: behind to the right (ahead to the left), away from the body up.
        Vector3D P(double along, double radial) => _origin + _right * (-along * _k) + _up * (radial * _k);
        Func<Vector3D, double, Vector3D> toMap = (root, tk) => Rel(target, c, root, tk, out var q) ? P(q.X, q.Y) : _origin;

        // Axes through the target, its merge and split zones, the target itself.
        if (MapPipeline.ToScreen(W(_origin), out var os))
        {
            var clip = MapPipeline.ClipRect.Value;
            MapPipeline.ScreenLine(new Vector2(clip.Min.X, os.Y), new Vector2(clip.Max.X, os.Y), Axis, 1f * u);
            MapPipeline.ScreenLine(new Vector2(os.X, clip.Min.Y), new Vector2(os.X, clip.Max.Y), Axis, 1f * u);
            MapPipeline.ScreenText(new Vector2(clip.Min.X + 8f * u, os.Y - 22f * u), "ahead", Dim, 0.6f * MapPipeline.MapText);
            MapPipeline.ScreenText(new Vector2(clip.Max.X - 70f * u, os.Y - 22f * u), "behind", Dim, 0.6f * MapPipeline.MapText);
            MapPipeline.ScreenText(new Vector2(os.X + 8f * u, clip.Min.Y + 6f * u), $"away from {SystemHost.DisplayName(c.Name)}", Dim, 0.6f * MapPipeline.MapText);
            MapPipeline.ScreenText(new Vector2(os.X + 8f * u, clip.Max.Y - 24f * u), $"toward {SystemHost.DisplayName(c.Name)}", Dim, 0.6f * MapPipeline.MapText);
            foreach (var (rz, name) in new[] { (ServerFrames.Rendezvous.EnterRangeMeters, "arrive"), ((double)ServerFrames.SlotRadius, "leave") })
                if (MapPipeline.ToScreen(W(P(rz, 0)), out var rs))
                {
                    float rpx = Math.Abs(rs.X - os.X);
                    if (rpx > 6f * u && rpx < scr.Y) { MapStyle.BoundaryCircle(os, rpx, HudPanel.Alpha(TargetCol, 0.5f), MapStyle.Thin(u), u); if (rpx > 40f * u) MapPipeline.ScreenText(os + new Vector2(rpx * 0.71f + 4f * u, -rpx * 0.71f - 18f * u), $"{name} {HudPanel.Km(rz)}", HudPanel.Alpha(TargetCol, 0.7f), 0.5f * MapPipeline.MapText); }
                }
            float h = 5f * u;
            MapPipeline.ScreenFill(new List<Vector2> { os + new Vector2(-h, -h), os + new Vector2(h, -h), os + new Vector2(h, h), os + new Vector2(-h, h) }, TargetCol);
            MapPipeline.ScreenText(os + new Vector2(10f * u, 6f * u), Targets.Name(target), TargetCol, 0.7f * MapPipeline.MapText);
        }

        // Your path and nodes: the map's own editor, drawn through the relative frame (no ghost pinning: every arc at its true time).
        Maneuvers.MapDraw(toMap, W, double.PositiveInfinity, t, mouse, null, c.Name, allLive: true);

        MapPipeline.ClipRect = null;
        CleanMap.Plated(head, $"Rendezvous · {Targets.Name(target)} · about {SystemHost.DisplayName(c.Name)}", new ColorSRGB(1f, 1f, 1f, 1f), 1.05f * MapPipeline.MapText);
        string line = Maneuvers.TargetLine(t);
        if (line != null) CleanMap.Plated(head + new Vector2(0, 34f * u), line, Dim, 0.8f * MapPipeline.MapText);
        CleanMap.DrawWrapped(MapLayout.P(0.265f, 0.9f), "Click the path: add a maneuver   ·   Drag a handle: the burn (Shift: fine, Ctrl: snap)   ·   Right-click: options   ·   Wheel: zoom   ·   Ctrl+Z: undo", MapLayout.X(0.775f) - MapLayout.X(0.265f), 0.74f, Dim);   // (wrapped to the map: cut, it lost its last hints)
        Status = $"target {target} about {c.Name}, k {_k:G3}";
    }
}
