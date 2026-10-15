using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The map's cards: the hover card by the cursor, the orbit info cards, the target panel in the right column.</summary>
public static partial class CleanMap
{
    /// <summary>
    /// Hovering something: a small panel by the cursor with what it is and its orbit (apoapsis, periapsis, inclination,
    /// period), so the map can be read by pointing at it. What is under the cursor, first found: a ship's pin, a body, an
    /// orbit line (a ship's, a planet's, a moon's), your own path (that arc's orbit), a sector.
    /// </summary>
    static void HoverCard(SystemRegistry reg, string playerPlanet, KeplerianElements? playerOrbit, double t)
    {
        if (MapCamera.Dragging || MapMenu.Open || Maneuvers.OnGizmo || !InOpenArea(Mouse)) return;
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        OrbitInfo.Info info = null;
        string ship = null;
        foreach (var pn in _pins) if (Targets.GridOf(pn.key, out _) && (pn.s - Mouse).Length() <= PinHit * u) { ship = pn.key; break; }
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        string key = ship ?? (best != null && !best.IsRoot ? best.Name : null) ?? _hoverOrbitLast;
        string NHint(string k) => Maneuvers.Target == k ? "N: clear target" : "N: target";
        if (best != null && ship == null && (key == null || key == best.Name))
        {
            var def = reg.FindDefinition(best.Name);
            bool viewing = _viewBody == best || (_viewBody == null && best.Parent == null);
            string foot = (viewing ? "Double-click: centre" : "Double-click: focus") + (best.IsRoot ? "" : "   \u00b7   " + NHint(best.Name));
            if (best.IsRoot) { info = new OrbitInfo.Info { Title = StarName, Sub = "Star", Foot = foot }; }
            else info = OrbitInfo.Hover(SystemHost.DisplayName(best.Name), OrbitInfo.Kind(best.Name), OrbitInfo.Of(best.Name, t), foot);
            if (def != null && def.RadiusMeters > 0)
            {
                var sz = info.Add(null).Row("Radius", HudPanel.Km(def.RadiusMeters));
                if (best.Parent != null && !double.IsInfinity(best.SoiRadius)) sz.Row("Gravity reach", HudPanel.Km(best.SoiRadius));
            }
        }
        else if (key != null)
            info = OrbitInfo.Hover(Targets.Name(key), OrbitInfo.Kind(key), OrbitInfo.Of(key, t), NHint(key));
        else if (!double.IsNaN(Maneuvers.HoverT) && Maneuvers.Trajectory(t, out var legs, out _))
        {
            double ht = Maneuvers.HoverT;
            var leg = legs.Find(l => ht >= l.T0 - 1e-6 && ht <= l.T1 + 1e-6);
            if (leg.Body == null) return;
            info = OrbitInfo.Hover("Your path", $"in {OrbitInfo.Dur(ht - t)}", OrbitInfo.Of(leg.Body, leg.El, ht), "Click: add a maneuver here");
        }
        else if (Hovered != null && _bandsLast?.Find(b => b.Name == Hovered) is Band hb && !hb.Virtual)
            info = OrbitInfo.Hover(Label(hb), OrbitInfo.Kind(hb.Name), OrbitInfo.Of(hb.Name, t), NHint(hb.Name));
        if (info == null) return;
        // by the cursor, kept inside the map's open area
        var size = DrawInfo(info, Vector2.Zero, 0, u, measureOnly: true);
        var at = Mouse + new Vector2(18f * u, 14f * u);
        if (at.X + size.X > MapLayout.X(0.77f)) at.X = Mouse.X - 18f * u - size.X;
        if (at.Y + size.Y > MapLayout.Y(0.9f)) at.Y = Mouse.Y - 14f * u - size.Y;
        DrawInfo(info, at, size.X, u);
    }

    static readonly ColorSRGB PanelSolid = new ColorSRGB(0.02f, 0.05f, 0.07f, 0.92f);
    static readonly ColorSRGB Good = new ColorSRGB(0.45f, 1.00f, 0.55f, 1f);
    static readonly ColorSRGB Warn = new ColorSRGB(1.00f, 0.55f, 0.35f, 1f);
    static readonly ColorSRGB Rule = new ColorSRGB(0.70f, 0.78f, 0.88f, 0.18f);

    /// <summary>
    /// A panel. Laid out with room to breathe: its title (larger) and what it is (dim) on a card of their own, then each
    /// section on its own card (cards: a gap between them) or below a rule (one card) - a dim heading with space under it,
    /// then rows of a label (left, dim) and a value (right-aligned; a dim second part after it, "222 km  in 34 min") - and
    /// a hint at the foot. A folded section shows only its heading ("MORE ELEMENTS  +"); with toggles, a click on that
    /// heading is returned in <see cref="_infoToggle"/>. width 0: as wide as its text. k scales it all. Returns its size.
    /// </summary>
    static Vector2 DrawInfo(OrbitInfo.Info info, Vector2 at, float width, float u, bool measureOnly = false, bool toggles = false, float k = 1f, bool cards = false)
    {
        float mt = MapPipeline.MapText;   // (the map's text: its 4K look at any size)
        float kS = 0.74f * k * mt, vS = 0.8f * k * mt, hS = 0.6f * k * mt, tS = 1.0f * k * mt, sS = 0.72f * k * mt;
        float pad = 14f * u * k, gap = 20f * u * k, cardGap = 8f * u * k;
        float row = Math.Max(MapPipeline.MeasureText("Ag", vS).Y, 14f * u * k) * 1.18f;   // a row's height
        float th = Math.Max(MapPipeline.MeasureText("Ag", tS).Y, 16f * u * k);
        float headH = row * 0.95f;                                                              // a heading and the space under it
        float secSep = cards ? cardGap : row * 0.6f;                                            // between sections
        // the width the text needs
        if (width <= 0)
        {
            float w = Math.Max(MapPipeline.MeasureText(info.Title ?? "", tS).X, MapPipeline.MeasureText(info.Sub ?? "", sS).X);
            foreach (var sec in info.Sections)
            {
                if (sec.Head != null) w = Math.Max(w, MapPipeline.MeasureText(sec.Head.ToUpperInvariant() + "  +", hS).X);
                if (sec.Folded) continue;
                foreach (var r in sec.Rows) w = Math.Max(w, MapPipeline.MeasureText(r.K, kS).X + gap + MapPipeline.MeasureText(r.V, vS).X);
            }
            if (info.Foot != null) w = Math.Max(w, MapPipeline.MeasureText(info.Foot, kS).X);
            width = w + 2 * pad;
        }
        // the blocks: the title's, then each section's (its height)
        float titleH = pad + th + (info.Sub != null ? row * 0.95f : 0) + pad * 0.6f;
        var secH = new List<float>();
        foreach (var sec in info.Sections)
            secH.Add((cards ? pad * 0.8f : 0) + (sec.Head != null ? headH : 0) + (sec.Folded ? 0 : sec.Rows.Count * row) + (cards ? pad * 0.6f : 0));
        float footH = info.Foot != null ? row * 0.5f + row : 0;
        float h = titleH;
        foreach (var x in secH) h += secSep + x;
        h += footH + (cards ? 0 : pad * 0.8f);
        var size = new Vector2(width, h);
        if (measureOnly) return size;

        var accent = info.Target ? TargetText : LineSel;
        float x0 = at.X + pad, x1 = at.X + width - pad, y = at.Y;
        void Block(float top, float height)
        {
            MapPipeline.ScreenRect(new Vector2(at.X, top), new Vector2(at.X + width, top + height), PanelSolid);
            MapPipeline.ScreenRect(new Vector2(at.X, top), new Vector2(at.X + 3f * u, top + height), accent);
        }
        if (cards) Block(y, titleH); else Block(y, h);
        y += pad;
        MapPipeline.ScreenText(new Vector2(x0, y), Fit(info.Title ?? "", x1 - x0, tS), info.Target ? TargetText : Text, tS); y += th + row * 0.1f;
        if (info.Sub != null) { MapPipeline.ScreenText(new Vector2(x0, y), Fit(info.Sub, x1 - x0, sS), Dim, sS); }
        y = at.Y + titleH;
        for (int si = 0; si < info.Sections.Count; si++)
        {
            var sec = info.Sections[si];
            y += secSep;
            float sTop = y;
            if (cards) { Block(sTop, secH[si]); y += pad * 0.8f; }
            else MapPipeline.ScreenRect(new Vector2(x0, y - secSep * 0.5f), new Vector2(x1, y - secSep * 0.5f + Math.Max(1f, u)), Rule);
            if (sec.Head != null)
            {
                bool toggle = toggles && (sec.Head == "More elements" || sec.Head == "Now") && sec.Rows.Count > 0;   // (the two take turns)
                string head = sec.Head.ToUpperInvariant() + (toggle ? (sec.Folded ? "  +" : "  \u2212") : "");
                var hb = new BoundingBox2(new Vector2(at.X, sTop), new Vector2(at.X + width, y + headH));
                bool hot = toggle && hb.Contains(Mouse) == ContainmentType.Contains;
                if (hot) MapPipeline.ScreenRect(hb.Min, hb.Max, RowHover);
                MapPipeline.ScreenText(new Vector2(x0, y), head, hot ? Text : HudPanel.Alpha(Dim, 0.85f), hS);
                if (hot && MapInput.LeftReleased && !MapCamera.DragEnded) _infoToggle = true;
                y += headH;
            }
            if (!sec.Folded)
                foreach (var r in sec.Rows)
                {
                    var vc = r.Tone == 1 ? Good : r.Tone == 2 ? Warn : Text;
                    // a value's second part (after two spaces) dim: "222 km  in 34 min"
                    string v1 = r.V, v2 = null;
                    int cut = r.V.IndexOf("  ", StringComparison.Ordinal);
                    if (cut > 0) { v1 = r.V.Substring(0, cut); v2 = r.V.Substring(cut); }
                    float w1 = MapPipeline.MeasureText(v1, vS).X, w2 = v2 != null ? MapPipeline.MeasureText(v2, kS).X : 0;
                    float ky = y + (MapPipeline.MeasureText("Ag", vS).Y - MapPipeline.MeasureText("Ag", kS).Y) * 0.5f;
                    MapPipeline.ScreenText(new Vector2(x0, ky), Fit(r.K, x1 - x0 - w1 - w2 - gap, kS), Dim, kS);
                    MapPipeline.ScreenText(new Vector2(x1 - w2 - w1, y), v1, vc, vS);
                    if (v2 != null) MapPipeline.ScreenText(new Vector2(x1 - w2, ky), v2, Dim, kS);
                    y += row;
                }
            y = sTop + secH[si];
        }
        if (info.Foot != null)
        {
            y += row * 0.5f;
            MapPipeline.ScreenText(new Vector2(x0, y), Fit(info.Foot, x1 - x0, kS), HudPanel.Alpha(Dim, 0.75f), kS);
        }
        return size;
    }
    static bool _infoToggle;

    // ───────────────────────────── the target panel (the right column) ─────────────────────────────

    /// <summary>The right column shows the lists (sectors, targets) instead of the target's panel ("Lists").</summary>
    static bool _listsMode;
    static string _panelFor;
    static bool _panelMore;
    static OrbitInfo.Info _panel; static double _panelAt;

    /// <summary>With a target, the right column is its panel (a new target opens it; "Lists" goes back to the lists).</summary>
    public static bool TargetPanelShown
    {
        get
        {
            string k = Maneuvers.Target;
            if (k != _panelFor) { _panelFor = k; _listsMode = false; _panel = null; }
            return k != null && !_listsMode;
        }
    }

    /// <summary>
    /// The target's panel in the map's right column: its name and what it is, Orbit, Now, From you, the technical elements
    /// folded under More; buttons - back to the lists, show it on the map, the Rendezvous view, clear the target.
    /// Refreshed twice a second.
    /// </summary>
    static void TargetPanel(double t, bool ui, Func<Vector3D, Vector3D> W)
    {
        string key = Maneuvers.Target;
        if (key == null) return;
        _listBox = new BoundingBox2(MapLayout.P(0.777f, 0.27f), MapLayout.P(0.777f, 0.27f));   // (no sector list under it this frame)
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_panel == null || now - _panelAt > 0.5) { _panel = OrbitInfo.Panel(key, t, _panelMore); _panelAt = now; }
        float x0 = MapLayout.X(0.777f), x1 = MapLayout.X(0.975f), top = MapLayout.Y(0.268f);   // (clear of the game's index box above)
        float bs = 0.72f * MapPipeline.MapText, bh = Math.Max(MapPipeline.MeasureText("Ag", bs).Y, 14f * u) + 14f * u;
        bool click = ui && MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open;
        // the bar: back to the lists, show it on the map, the Rendezvous view, clear the target
        MapPipeline.ScreenRect(new Vector2(x0, top), new Vector2(x1, top + bh), PanelSolid);
        float by = top + 7f * u, bx = x0 + 10f * u, sp = 16f * u;
        if (Button(new Vector2(bx, by), "\u2039 Lists", bs, u, click)) { _listsMode = true; return; }
        bx += MapPipeline.MeasureText("\u2039 Lists", bs).X + sp;
        if (Button(new Vector2(bx, by), "Show on map", bs, u, click))
        {
            var b = Targets.Body(key);
            if (b != null) FocusOn(b, SystemHost.Registry, t, W, false);
            else if (Maneuvers.TargetWorld is Vector3D tw) MapCamera.PanTo(tw, smooth: true);
            else WarpControl.Say("Not on this view: focus its planet first");
        }
        bx += MapPipeline.MeasureText("Show on map", bs).X + sp;
        if (Button(new Vector2(bx, by), "Rendezvous \u203a", bs, u, click) && _session != null) RendezvousView.Open(_session);
        string clr = "\u00d7";
        if (Button(new Vector2(x1 - 12f * u - MapPipeline.MeasureText(clr, bs).X, by), clr, bs, u, click)) { SetTarget(null); return; }
        // the panel's cards, shrunk (if need be) to end above the game's Fast Travel button
        _infoToggle = false;
        float py = top + bh + 8f * u;
        float room = MapLayout.Y(0.80f) - py;
        var need = DrawInfo(_panel, Vector2.Zero, x1 - x0, u, measureOnly: true, cards: true);
        float k = need.Y > room && room > 0 ? Math.Max(0.6f, room / need.Y) : 1f;
        var size = DrawInfo(_panel, new Vector2(x0, py), x1 - x0, u, toggles: ui, k: k, cards: true);
        if (_infoToggle) { _panelMore = !_panelMore; _panel = null; }
        MapPipeline.Reserve(new Vector2(x0, top), new Vector2(MapPipeline.ScreenSize.X, py + size.Y));
        _panelBox = new BoundingBox2(new Vector2(x0, top), new Vector2(x1, py + size.Y));
    }
    static BoundingBox2 _panelBox;

    /// <summary>DEV: the right column's panel - "lists", "panel", "more" (fold / unfold).</summary>
    public static string DevPanel(string what)
    {
        if (what == "lists") _listsMode = true;
        else if (what == "panel") _listsMode = false;
        else if (what == "more") { _panelMore = !_panelMore; _panel = null; }
        return $"right column: {(TargetPanelShown ? "target panel" : "lists")}, more {_panelMore}";
    }

    /// <summary>A text button: lit under the mouse, true on its click.</summary>
    static bool Button(Vector2 at, string text, float scale, float u, bool click)
    {
        var m = MapPipeline.MeasureText(text, scale);
        var box = new BoundingBox2(at - new Vector2(5f * u, 3f * u), at + m + new Vector2(5f * u, 3f * u));
        bool hot = box.Contains(Mouse) == ContainmentType.Contains;
        if (hot) MapPipeline.ScreenRect(box.Min, box.Max, RowHover);
        MapPipeline.ScreenText(at, text, hot ? Text : Dim, scale);
        return hot && click;
    }
}
