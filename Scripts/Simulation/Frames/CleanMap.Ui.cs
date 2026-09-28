using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The map's UI: the sector list, the title path, hints, tooltips, focus, the context menu, pins.</summary>
public static partial class CleanMap
{

    /// <summary>The sector list: grouped, numbered, state dot, name, where; on the right, below the game's index box.</summary>
    static readonly ColorSRGB PanelFill = new ColorSRGB(0.02f, 0.05f, 0.07f, 0.78f);

    /// <summary>The list's screen area (last frame): a click there goes to a row.</summary>
    private static BoundingBox2 _listBox;

    /// <summary>
    /// The sector list, as a panel in the game's style: group headers, one row per sector (number,
    /// state dot, name, where), the hovered row lit, the selected one marked with a bar. A click on a
    /// row selects it (the game's own click) and the map glides to it.
    /// </summary>
    static void DrawList(List<Band> ordered, Func<Band, bool> expanded, Func<Band, bool> listed)
    {
        ordered = ordered.FindAll(b => listed(b));
        Vector2 scr = MapPipeline.ScreenSize;
        float k = MapPipeline.TextScale;   // rows and columns grow with the text
        float x = scr.X * 0.782f, y = scr.Y * 0.27f, line = scr.Y * 0.0275f * k, scale = 0.78f;
        float x0 = scr.X * 0.777f, x1 = scr.X * 0.975f;
        // Lay out first (the panel goes behind), then draw.
        var rows = new List<(Band b, bool header, string text, float y)>();
        string group = null;
        foreach (var b in ordered)
        {
            string g = Group(b);
            if (g != group)
            {
                if (group != null) y += line * 0.35f;
                int count = ordered.FindAll(o => Group(o) == g).Count;
                rows.Add((b, true, expanded(b) ? g.ToUpperInvariant() : $"{g.ToUpperInvariant()}  ({count})", y));
                y += line;
                group = g;
            }
            if (!expanded(b)) continue;
            rows.Add((b, false, null, y));
            y += line;
        }
        if (rows.Count == 0) return;
        float top = rows[0].y - line * 0.4f, bottom = y + line * 0.2f;
        MapPipeline.ScreenRect(new Vector2(x0, top), new Vector2(x1, bottom), PanelFill);
        _listBox = new BoundingBox2(new Vector2(x0, top), new Vector2(x1, bottom));
        foreach (var r in rows)
        {
            if (r.header) { MapPipeline.ScreenText(new Vector2(x, r.y), r.text, Dim, scale * 0.85f); continue; }
            var b = r.b;
            var rowMin = new Vector2(x0, r.y - line * 0.12f); var rowMax = new Vector2(x1, r.y + line * 0.88f);
            if (b.Name == Hovered) MapPipeline.ScreenRect(rowMin, rowMax, RowHover);
            if (b.Selected) MapPipeline.ScreenRect(rowMin, new Vector2(x0 + 3f, rowMax.Y), LineSel);
            var c = b.Selected ? LineSel : Text;
            MapPipeline.PickName = b.Name;
            MapPipeline.ScreenText(new Vector2(x, r.y), b.Number.ToString(), Dim, scale);
            MapPipeline.ScreenDot(new Vector2(x + scr.Y * 0.030f * k, r.y + line * 0.42f), 4.5f, StateColor(b));
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.045f * k, r.y), b.Name == Maneuvers.Target ? b.Name + "  · target" : b.Name, b.Name == Maneuvers.Target ? TargetText : c, scale);
            MapPipeline.ScreenText(new Vector2(x + scr.Y * 0.19f * k, r.y), Fit(Where(b), x1 - (x + scr.Y * 0.19f * k) - 6f, scale * 0.85f), Dim, scale * 0.85f);
            MapPipeline.PickName = null;
        }
        MapPipeline.Reserve(new Vector2(x0, top), new Vector2(scr.X, bottom));
    }

    /// <summary>A click on a list row: glide the map to that sector (the game selects it on the same click).</summary>
    static void ListInput(Func<Vector3D, Vector3D> W)
    {
        if (!MapInput.LeftReleased || MapCamera.DragEnded || Hovered == null) return;
        if (_listBox.Contains(Mouse) != ContainmentType.Contains) return;
        CentreOn(Hovered);
    }

    /// <summary>
    /// A line of the controls, in the map's open area above the game's own hint bar: what the mouse can
    /// do where it is (on a maneuver, on the path, or on the map).
    /// </summary>
    static void Hints()
    {
        var scr = MapPipeline.ScreenSize;
        string h;
        if (Maneuvers.OnGizmo) h = "Drag a handle to change the burn   \u00b7   Drag the node along the path   \u00b7   Right-click: options";
        else if (!double.IsNaN(Maneuvers.HoverT)) h = "Click: add a maneuver here   \u00b7   Right-click: options   \u00b7   Drag: pan";
        else h = "Drag: pan   \u00b7   Right-drag: orbit   \u00b7   Wheel: zoom   \u00b7   Double-click a body: focus   \u00b7   Right-click: menu   \u00b7   .  ,  /  warp";
        MapPipeline.ScreenText(new Vector2(scr.X * 0.265f, scr.Y * 0.9f), h, Dim, 0.74f);
    }

    // ───────────────────────────── encounters and GPS (both views) ─────────────────────────────

    private static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>
    /// Right click on the map: what is under the mouse decides the menu. A node: edit its axes exactly,
    /// delete it (or it and the later ones). The trajectory: add a maneuver there. A sector (orbit,
    /// marker, list row): plan a route to it. Anywhere with a route: clear it.
    /// </summary>
    private static void ContextMenu(List<Band> bands, double t)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        bool rp = MapInput.RightPressed;
        if (MapMenu.Draw(Mouse, MapInput.LeftPressed, rp, u)) { Maneuvers.ClaimsMouse = true; return; }
        if (MapMenu.Open) { Maneuvers.ClaimsMouse = true; return; }
        if (!rp) return;
        var items = new List<MapMenu.Item>();
        string title = null;
        bool hasNodes = Maneuvers.Nodes.Count > 0;
        var node = Maneuvers.HoverNode;
        double ht = Maneuvers.HoverT;
        var session = _session;
        if (node != null)
        {
            title = "Maneuver  ·  in " + Maneuvers.Clock(node.T - t);
            Maneuvers.Selected = node;
            // Warp to it (as KSP): top warp; warp stops by itself ahead of the burn.
            if (node.T - t > Maneuvers.WarpLead + 10) items.Add(new MapMenu.Item("Warp to maneuver", () => WarpControl.SetLevel(WarpControl.Levels.Length - 1), FrameHost.PlayerFrame != null));
            items.Add(new MapMenu.Item(node.Auto ? "Auto-burn: on" : "Auto-burn: off", () => node.Auto = !node.Auto));
            items.Add(new MapMenu.Item("Remove maneuver", () => Maneuvers.Delete(node, false)));
        }
        else if (!double.IsNaN(ht))
        {
            double T = ht;
            title = "Trajectory  ·  in " + Maneuvers.Clock(T - t);
            items.Add(new MapMenu.Item("Add maneuver", () => Maneuvers.AddNodeAt(T)));
            // Warp until the ship gets there (as KSP); other stops (burns, spheres) still come first.
            if (T - t > 30) items.Add(new MapMenu.Item("Warp here", () => { SystemHost.WarpStopAt = T; WarpControl.SetLevel(WarpControl.Levels.Length - 1); }, FrameHost.PlayerFrame != null));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (Hovered != null)
        {
            string sec = Hovered;
            title = sec;
            items.Add(new MapMenu.Item("Centre on " + sec, () => CentreOn(sec)));
            items.Add(Maneuvers.Target == sec ? new MapMenu.Item("Clear target", () => Maneuvers.Target = null)
                                              : new MapMenu.Item("Set as target", () => Maneuvers.Target = sec));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (hasNodes)
            items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        if (items.Count > 0) { MapMenu.Show(Mouse + new Vector2(4f * u, 4f * u), title, items); Maneuvers.ClaimsMouse = true; }
    }
    /// <summary>The mouse (screen px) for this frame's editors.</summary>
    public static Vector2 Mouse;
    static readonly ColorSRGB EncColor = new ColorSRGB(1.00f, 0.55f, 0.25f, 0.85f);

    /// <summary>
    /// Encounter frames (procedural spawns, authored sites; sector anchors are the sector orbits already):
    /// their orbit and where they are now. GPS markers: at their true place (frame-transferred), pinned to
    /// the edge when beyond the view. toLocal maps a sun-centred model position to the map.
    /// </summary>
    /// <summary>
    /// Where you are looking and where you are, at the top of the map's open area: "Delfos › Kemik",
    /// then your ship's situation (the orbit's Pe / Ap, or on the ground).
    /// </summary>
    // ---- focus (as KSP: double-click a body, or click the breadcrumb) ----

    private static readonly List<(GravityBody b, Vector2 s, float r)> _hits = new List<(GravityBody, Vector2, float)>();
    private static readonly List<(GravityBody b, BoundingBox2 box)> _crumbs = new List<(GravityBody, BoundingBox2)>();
    private static double _lastClick;
    /// <summary>A body being flown to: the zoom-in switch opens it, whatever is under the cursor.</summary>
    private static GravityBody _glide;
    private static bool _glideToStar;

    /// <summary>A body drawn this frame, clickable within its drawn size (or its ring when smaller).</summary>
    static void Hit(GravityBody b, Vector3D centre, Vector3D edge, float ringPx)
    {
        if (!MapPipeline.ToScreen(centre, out var sc)) return;
        float r = MapPipeline.ToScreen(edge, out var se) ? (se - sc).Length() : 0f;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        _hits.Add((b, sc, Math.Max(r, ringPx * u) + 8f * u));
    }

    private static Func<Vector3D, Vector3D> _lastW;
    public static string ClickDebug = "";

    /// <summary>DEV: focus a body as a double-click on it would.</summary>
    public static string DevFocus(string name)
    {
        var reg = SystemHost.Registry; var b = reg?.Find(name) ?? (name == StarName ? reg?.Root : null);
        if (b == null || _lastW == null) return "no body / map not drawn";
        FocusOn(b, reg, SystemHost.Now, _lastW, _lastSolar);
        return "focus " + name;
    }

    /// <summary>
    /// Hovering a body: a small card by the cursor with what it is, its size and reach, and how to
    /// focus it (double-click), so the map can be learnt by pointing at things.
    /// </summary>
    static void BodyTooltip(SystemRegistry reg, string playerPlanet, KeplerianElements? playerOrbit)
    {
        if (MapCamera.Dragging || MapMenu.Open || Maneuvers.OnGizmo || !InOpenArea(Mouse)) return;
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        if (best == null) return;
        var def = reg.FindDefinition(best.Name);
        string kind = best.Parent == null ? "Star" : best.Parent.Parent == null ? "Planet" : $"Moon of {best.Parent.Name}";
        var lines = new List<string> { $"{SystemHost.DisplayName(best.Name)}  ·  {kind}" };
        if (def != null && def.RadiusMeters > 0) lines.Add($"Radius {HudPanel.Km(def.RadiusMeters)}" + (best.Parent != null && !double.IsInfinity(best.SoiRadius) ? $"  ·  reach {HudPanel.Km(best.SoiRadius)}" : ""));
        if (best.Name == playerPlanet && playerOrbit.HasValue)
        {
            var o = playerOrbit.Value; double r = def?.RadiusMeters ?? 0;
            lines.Add($"You: Pe {HudPanel.Km(o.PeriapsisRadius - r)}" + (o.IsElliptic ? $"  Ap {HudPanel.Km(o.SemiMajorAxis * (1 + o.Eccentricity) - r)}" : "  escape"));
        }
        bool viewing = _viewBody == best || (_viewBody == null && best.Parent == null);
        lines.Add(viewing ? "Double-click: centre" : "Double-click: focus");
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        float w = 0, lh = 0;
        foreach (var l in lines) { var m = MapPipeline.MeasureText(l, 0.8f); w = Math.Max(w, m.X); lh = Math.Max(lh, m.Y); }
        var at = Mouse + new Vector2(18f * u, 14f * u);
        var size = new Vector2(w + 16f * u, lines.Count * lh + 10f * u);
        var scr = MapPipeline.ScreenSize;
        if (at.X + size.X > scr.X * 0.77f) at.X = Mouse.X - 18f * u - size.X;
        if (at.Y + size.Y > scr.Y * 0.9f) at.Y = Mouse.Y - 14f * u - size.Y;
        MapPipeline.ScreenRect(at, at + size, PanelFill);
        MapPipeline.ScreenRect(at, new Vector2(at.X + 2f * u, at.Y + size.Y), LineSel);
        for (int i = 0; i < lines.Count; i++)
            MapPipeline.ScreenText(at + new Vector2(8f * u, 5f * u + i * lh), lines[i], i == 0 ? Text : i == lines.Count - 1 ? Dim : Text, i == 0 ? 0.85f : 0.8f);
    }

    static void FocusInput(SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, bool solar)
    {
        if (MapInput.LeftReleased)
        {
            float nd = float.MaxValue; string nn = null;
            foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d < nd) { nd = d; nn = h.b.Name + $"(r{h.r:F0})"; } }
            ClickDebug = $"release at {Mouse} drag={MapCamera.DragEnded} gizmo={Maneuvers.OnGizmo} menu={MapMenu.Open} hits={_hits.Count} nearest {nn} {nd:F0}px";
        }
        if (!MapInput.LeftReleased || MapCamera.DragEnded || Maneuvers.OnGizmo || MapMenu.Open) return;
        foreach (var c in _crumbs)
            if (c.box.Contains(Mouse) == ContainmentType.Contains) { FocusOn(c.b, reg, t, W, solar); return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        bool dbl = now - _lastClick < 0.4 && (Mouse - _lastClickAt).Length() < 10f;
        _lastClick = dbl ? 0 : now; _lastClickAt = Mouse;
        if (!dbl) return;
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        if (best != null) FocusOn(best, reg, t, W, solar);
    }

    /// <summary>Fly the view to a body: the star (system view), a planet or a moon (its own view).</summary>
    public static void FocusOn(GravityBody b, SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, bool solar)
    {
        // The map becomes about that body (the view stays put), then glides to it and frames its system.
        ViewFocus = b.IsRoot ? StarName : b.Name;
        _zoomTo = b;
    }

    /// <summary>A breadcrumb part's size: the font's measure, but never under ~8 px a character per unit
    /// scale (the measure comes out short at high resolutions, and parts ran together).</summary>
    static Vector2 CrumbSize(string text)
    {
        return MapPipeline.MeasureText(text, 1.05f);   // (now never short)
    }

    private static Vector2 _titleAt;

    /// <summary>The title block's height: the path, your situation, and the burn and target lines when there are.</summary>
    static float TitleHeight(Vector2 sc)
    {
        return sc.Y * 0.045f;   // the path line only
    }

    static void Title(GravityBody view, string playerPlanet, KeplerianElements? orbit)
    {
        var scr = MapPipeline.ScreenSize;
        var at = new Vector2(scr.X * 0.265f, scr.Y * 0.125f);
        // The breadcrumb: each part a click target (up to the star, down to what is in view).
        var chain = new List<GravityBody>();
        for (var b = view; b != null; b = b.Parent) chain.Insert(0, b);
        if (chain.Count == 0 && SystemHost.Registry?.Root != null) chain.Add(SystemHost.Registry.Root);
        float x = at.X;
        for (int i = 0; i < chain.Count; i++)
        {
            string name = chain[i].Parent == null ? StarName : chain[i].Name;
            var size = CrumbSize(name);
            var box = new BoundingBox2(new Vector2(x, at.Y), new Vector2(x + size.X, at.Y + size.Y));
            bool last = i == chain.Count - 1;
            bool hot = !last && box.Contains(Mouse) == ContainmentType.Contains;
            MapPipeline.ScreenText(new Vector2(x, at.Y), name, hot ? LineSel : last ? Text : Dim, 1.05f);
            if (!last) _crumbs.Add((chain[i], box));
            x += size.X;
            if (!last)
            {
                float gap = 10f * Math.Max(1f, scr.Y / 1080f);   // the font drops leading spaces: gaps in pixels
                x += gap; MapPipeline.ScreenText(new Vector2(x, at.Y), ">", Dim, 1.05f); x += CrumbSize(">").X + gap;
            }
        }
        MapPipeline.Reserve(at - new Vector2(4, 4), at + new Vector2(scr.X * 0.3f, scr.Y * 0.035f));
        _titleAt = at;
        // (Only the path: the title box and the you / burn / target lines were removed on request.)
    }

    /// <summary>The map's open area on screen (between the game's panels, above the warp bar).</summary>
    public static bool InOpenArea(Vector2 s)
    {
        var scr = MapPipeline.ScreenSize;
        return s.X >= scr.X * 0.255f && s.X <= scr.X * 0.775f && s.Y >= scr.Y * 0.1f && s.Y <= scr.Y * 0.84f;
    }

    static string Km(double m) => Math.Abs(m) >= 10000 ? $"{m / 1000:N0} km" : $"{m / 1000:F1} km";

    private static void Overlay(Func<Vector3D, Vector3D> toLocal, double limit, Func<Vector3D, Vector3D> W, double t, SystemRegistry reg)
    {
        long player = FrameHost.PlayerId;
        lock (ServerFrames.FramesLock)
        {
            if (SystemHost.Frames == null) return;
            foreach (var f in SystemHost.Frames.Frames)
            {
                if (!f.IsEncounter || f.HasMember(player)) continue;
                var site = EncounterFrames.SiteOf(f.Id);
                if (site != null && site.Anchor) continue;
                var parent = reg.Find(f.ParentBodyName);
                if (parent == null || !(parent.IsRoot || _detail.Contains(parent.Name))) continue;
                Vector3D porg = parent.OriginInRoot(t).Position;
                var el = f.Elements;
                double T = el.IsElliptic && IsFinite(el.Period) ? el.Period : 6 * 3600.0;
                Vector3D prev = default; bool havePrev = false;
                for (int k = 0; k <= 120; k++)
                {
                    var st = OrbitPropagation.StateAt(el, t + T * k / 120);
                    if (!IsFinite(st.Position.X) || !IsFinite(st.Position.Y)) { havePrev = false; continue; }
                    Vector3D p = toLocal(porg + st.Position);
                    bool inView = Math.Sqrt(p.X * p.X + p.Z * p.Z) <= limit * 1.3;
                    if (havePrev && inView) MapPipeline.Line(W(prev), W(p), HudPanel.Alpha(EncColor, 0.55f), 1.4f);
                    prev = p; havePrev = inView;
                }
                var now = OrbitPropagation.StateAt(el, t);
                if (!IsFinite(now.Position.X) || !IsFinite(now.Position.Y)) continue;
                Vector3D here = toLocal(porg + now.Position);
                int npc = 0; foreach (long id in f.Members) if (EncounterFrames.IsNpcId(id)) npc++;
                string label = site != null ? site.Label : $"Encounter ({f.Members.Count})";
                Pin(here, limit, W, label, EncColor);
            }
        }

        // GPS markers at their true place.
        if (_session == null) return;
        foreach (var (name, color, model) in FrameMarkers.MapMarkers(_session, t))
            Pin(toLocal(model), limit, W, name, color, diamond: true);
    }

    /// <summary>
    /// A marker (an encounter, a station): where it is when in the map's open area; otherwise an arrow at
    /// the open area's edge in its direction, with its name ("Vallation Station >" sat in the title row).
    /// </summary>
    private static void Pin(Vector3D p, double limit, Func<Vector3D, Vector3D> W, string label, ColorSRGB c, bool diamond = false)
    {
        if (!MapPipeline.ToScreen(W(p), out var s)) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        if (InOpenArea(s))
        {
            if (diamond) MapPipeline.ScreenDiamond(W(p), 6f, c, 1.8f);
            else { MapPipeline.ScreenRing(W(p), 6f, c, 1.8f); MapPipeline.ScreenRing(W(p), 2.5f, c, 2.5f); }
            MapPipeline.TextScreen(s + new Vector2(0, 16f * u), label, c, 0.6f);
            return;
        }
        // The open area's centre, and where the ray to the marker leaves it.
        var scr = MapPipeline.ScreenSize;
        Vector2 min = new Vector2(scr.X * 0.265f, scr.Y * 0.2f), max = new Vector2(scr.X * 0.765f, scr.Y * 0.82f);
        Vector2 c0 = (min + max) * 0.5f, d = s - c0;
        if (d.LengthSquared() < 1e-6f) return;
        float k = Math.Min(Math.Abs((d.X > 0 ? max.X - c0.X : c0.X - min.X) / (Math.Abs(d.X) + 1e-6f)), Math.Abs((d.Y > 0 ? max.Y - c0.Y : c0.Y - min.Y) / (Math.Abs(d.Y) + 1e-6f)));
        Vector2 e = c0 + d * k, n = Vector2.Normalize(d), side = new Vector2(-n.Y, n.X);
        float a = 9f * u;
        // The arrow only with its name (a bare arrow says nothing).
        if (!MapPipeline.TextScreen(e - n * (a + 14f * u), label, c, 0.6f)) return;
        if (!MapPipeline.ScreenArrow(e - n * a * 0.5f, n, a * 0.75f, c))
        {
            MapPipeline.ScreenLine(e, e - n * a + side * a * 0.6f, c, 1.8f * u);
            MapPipeline.ScreenLine(e, e - n * a - side * a * 0.6f, c, 1.8f * u);
        }
    }
}
