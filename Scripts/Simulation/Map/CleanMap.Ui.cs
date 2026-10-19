using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The map's UI: the sector list, hints, the context menu, the overlay (encounters, ships, GPS pins). Targeting, focus and the cards are their own files (CleanMap.Targeting / .Focus / .Cards).</summary>
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
        _listBox = new BoundingBox2(MapLayout.P(0.777f, 0.27f), MapLayout.P(0.777f, 0.27f));   // (none this frame until drawn)
        Vector2 scr = MapPipeline.ScreenSize;
        float k = MapPipeline.TextScale;   // rows and columns grow with the text
        float x = MapLayout.X(0.782f), y = MapLayout.Y(0.27f), line = MapLayout.LenY(0.0275f) * k, scale = 0.78f * MapPipeline.MapText;
        float x0 = MapLayout.X(0.777f), x1 = MapLayout.X(0.975f);
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
            // (the columns at least as wide as the text drawn: at a small screen's floor size the number ran into the name)
            float dotX = x + Math.Max(MapLayout.LenY(0.030f) * k, MapPipeline.MeasureText("00", scale).X + 5f);
            float nameX = Math.Max(x + MapLayout.LenY(0.045f) * k, dotX + 9f);
            MapPipeline.ScreenText(new Vector2(x, r.y), b.Number.ToString(), Dim, scale);
            MapPipeline.ScreenDot(new Vector2(dotX, r.y + line * 0.42f), 4.5f, StateColor(b));
            // the "where" column right-aligned and whole when it fits beside the name ("87 km-137 k..." was cut - UI audit
            // cosmetic); the name gives way first, cut with an ellipsis
            string where = Where(b), name = b.Name == Maneuvers.Target ? Label(b) + "  · target" : Label(b);
            float ww = MapPipeline.MeasureText(where, scale * 0.85f).X, wx = Math.Max(x1 - 6f - ww, nameX + MapLayout.LenY(0.06f) * k);
            MapPipeline.ScreenText(new Vector2(nameX, r.y), Fit(name, wx - nameX - 8f, scale), b.Name == Maneuvers.Target ? TargetText : c, scale);
            MapPipeline.ScreenText(new Vector2(wx, r.y), Fit(where, x1 - wx - 6f, scale * 0.85f), Dim, scale * 0.85f);
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

    /// <summary>A hint line wrapped at its separators to a width (each piece kept whole; one wider than the width is cut).</summary>
    internal static void DrawWrapped(Vector2 at, string text, float width, float scale, ColorSRGB color)
    {
        var lines = Wrapped(text, width, scale);
        float lh = MapPipeline.MeasureText("Ag", scale).Y * 1.25f;
        if (!(lh > 0)) lh = 22f * scale;
        for (int n = 0; n < lines.Length; n++) MapPipeline.ScreenText(at + new Vector2(0, n * lh), lines[n], color, scale);
    }

    /// <summary>
    /// A hint line's lines (kept: laid out once per text, width and scale - it was split and measured every frame). Wrapped at
    /// its separators, BALANCED: as many lines as the greedy wrap needs, but each about as wide as the others (a last line
    /// of ", . / warp" alone under a full one - UI audit cosmetic).
    /// </summary>
    static string[] Wrapped(string text, float width, float scale)
    {
        var key = (text, (int)MathF.Round(width), (int)MathF.Round(scale * 1000f));
        if (_wrapped.TryGetValue(key, out var done)) return done;
        string sep = "   " + (char)0x00b7 + "   ";
        var parts = text.Split(new[] { sep }, StringSplitOptions.None);
        List<string> Greedy(float w)
        {
            var res = new List<string>(); string line = "";
            foreach (var p in parts)
            {
                string tryLine = line.Length == 0 ? p : line + sep + p;
                if (line.Length > 0 && MapPipeline.MeasureText(tryLine, scale).X > w) { res.Add(line); line = p; }
                else line = tryLine;
            }
            if (line.Length > 0) res.Add(line);
            return res;
        }
        var lines = Greedy(width);
        if (lines.Count > 1)
        {
            // the narrowest width that still needs no more lines than that
            float total = MapPipeline.MeasureText(text, scale).X, lo = total / lines.Count, hi = width;
            for (int k = 0; k < 12 && hi - lo > 4f; k++) { float mid = 0.5f * (lo + hi); if (Greedy(mid).Count <= lines.Count) hi = mid; else lo = mid; }
            lines = Greedy(hi);
        }
        var arr = new string[lines.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = Fit(lines[i], width, scale);
        if (!MapPipeline.CanMeasure) return arr;   // (an estimate: not kept)
        if (_wrapped.Count > 64) _wrapped.Clear();
        _wrapped[key] = arr;
        return arr;
    }
    static readonly Dictionary<(string, int, int), string[]> _wrapped = new Dictionary<(string, int, int), string[]>();

    /// <summary>
    /// A line of the controls, in the map's open area above the game's own hint bar: what the mouse can
    /// do where it is (on a maneuver, on the path, or on the map).
    /// </summary>
    static void Hints()
    {
        var scr = MapPipeline.ScreenSize;
        string h;
        bool undo = Maneuvers.UndoDepth > 0;
        // (the map's own keys - the game's W A S D line under the map is gone: it moved nothing here, UI audit C6)
        if (Maneuvers.EntryMarkHover) h = EntryHost.HeatHelp;   // (what the heat % means - UI audit U3)
        else if (Maneuvers.OnGizmo) h = undo ? HintGizmoUndo : HintGizmo;
        else if (!double.IsNaN(Maneuvers.HoverT)) h = "Click: add a maneuver here   \u00b7   Right-click: options   \u00b7   N: target what is under the cursor   \u00b7   Drag: pan";
        else if (TargetsUnder(Mouse).Count > 0) h = "N: target it   \u00b7   Right-click: options   \u00b7   Double-click: focus   \u00b7   Drag: pan";
        else h = undo ? HintMapUndo : HintMap;
        // (wrapped to the map's width at its separators: a long hint ran onto the right panel at 4:3 and 5:4; cut short, it
        //  lost its last hints)
        DrawWrapped(MapLayout.P(0.265f, 0.9f), h, MapLayout.X(0.775f) - MapLayout.X(0.265f), 0.74f * MapPipeline.MapText, Dim);
    }

    static readonly ColorSRGB TextPlate = new ColorSRGB(0.01f, 0.025f, 0.04f, 0.55f);
    /// <summary>Text on a dark backplate (UI audit cosmetic: the breadcrumb, the "Rendezvous" sub-line and the porkchop's sub-line
    /// were lost in the sun's glare).</summary>
    internal static void Plated(Vector2 at, string text, ColorSRGB c, float scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        var sz = MapPipeline.MeasureText(text, scale);
        MapPipeline.ScreenRect(at - new Vector2(6f * u, 2f * u), at + sz + new Vector2(6f * u, 2f * u), TextPlate);
        MapPipeline.ScreenText(at, text, c, scale);
    }

    // ── the map's notices (UI audit B3: on the map, "Target: X", "Nothing to target under the cursor", warp stops... were
    //    lost - fx-flight's Messages board; drawn here, a strip above the warp bar, newest at the bottom) ──
    static readonly SEAerospace.Flight.MessageBoard.Line[] _msg = new SEAerospace.Flight.MessageBoard.Line[SEAerospace.Flight.MessageBoard.Capacity];
    static readonly ColorSRGB MsgPlate = new ColorSRGB(0.02f, 0.045f, 0.07f, 0.78f);

    /// <summary>The kind's tag and colour (the strings constant: no garbage per frame).</summary>
    static string KindTag(string kind, out ColorSRGB c)
    {
        switch (kind)
        {
            case SEAerospace.Flight.MessageKinds.Target: c = TargetText; return "TARGET";
            case SEAerospace.Flight.MessageKinds.Burn: c = new ColorSRGB(0.35f, 0.90f, 1.00f, 1f); return "BURN";
            case SEAerospace.Flight.MessageKinds.Warp: c = new ColorSRGB(1.00f, 0.85f, 0.35f, 1f); return "WARP";
            case SEAerospace.Flight.MessageKinds.Contacts: c = EncColor; return "CONTACTS";
            case SEAerospace.Flight.MessageKinds.Entry: c = new ColorSRGB(1.00f, 0.50f, 0.30f, 1f); return "ENTRY";
            default: c = Dim; return "MAP";
        }
    }

    /// <summary>The board's showing lines over the map: a dark plate each, the kind as a coloured tag, the text; each fades
    /// out over its last half second. The map tab and the Rendezvous tab both.</summary>
    internal static void MessageStrip()
    {
        int n = Messages.MapLines(_msg);
        if (n == 0) return;
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        float ts = 0.72f * MapPipeline.MapText, ks = 0.58f * MapPipeline.MapText;
        float lh = MapPipeline.MeasureText("Ag", ts).Y + 8f * u;
        float x = MapLayout.X(0.265f), bottom = MapLayout.Y(0.812f), maxW = MapLayout.X(0.775f) - x;
        double now = Messages.Now;
        for (int i = 0; i < n; i++)
        {
            var l = _msg[i];
            float a = (float)Math.Clamp((l.Until - now) / 0.5, 0, 1);
            if (a <= 0) continue;
            string tag = KindTag(l.Kind, out var kc);
            float y = bottom - (i + 1) * lh;
            var tsz = MapPipeline.MeasureText(tag, ks);
            string text = Fit(l.Text, maxW - tsz.X - 30f * u, ts);
            var xsz = MapPipeline.MeasureText(text, ts);
            MapPipeline.ScreenRect(new Vector2(x, y), new Vector2(x + tsz.X + xsz.X + 30f * u, y + lh - 3f * u), HudPanel.Alpha(MsgPlate, 0.78f * a));
            MapPipeline.ScreenRect(new Vector2(x, y), new Vector2(x + 3f * u, y + lh - 3f * u), HudPanel.Alpha(kc, a));
            MapPipeline.ScreenText(new Vector2(x + 10f * u, y + (lh - 3f * u - tsz.Y) * 0.5f), tag, HudPanel.Alpha(kc, a), ks);
            MapPipeline.ScreenText(new Vector2(x + tsz.X + 22f * u, y + (lh - 3f * u - xsz.Y) * 0.5f), text, HudPanel.Alpha(Text, a), ts);
        }
    }

    const string HintGizmo ="Drag a handle: the burn (Shift: fine, Ctrl: snap)   ·   Drag the node along the path   ·   Right-click: options";
    const string HintGizmoUndo = HintGizmo + "   ·   Ctrl+Z: undo";
    const string HintMap = "Drag: pan   ·   Right-drag: orbit   ·   Wheel: zoom   ·   Double-click: focus   ·   N or right-click: target   ·   Home: your ship   ·   , . /: warp";
    const string HintMapUndo = HintMap + "   ·   Ctrl+Z: undo";

    // ───────────────────────────── encounters and GPS (both views) ─────────────────────────────

    private static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>A click is the map's: over its open area (not the panels beside it), with the terminal itself on top (not a
    /// dialog over it - its clicks fell through: a delta-v dialog's OK added a node; a GPS entry refocused the map).</summary>
    static bool MapClickable()
    {
        if (!MapLayout.InOpenArea(Mouse) && !InCrumbs(Mouse)) return false;
        var s = MapView.SessionForLayout;
        object top = s != null ? GameUi.TopScreenObject(s) : null;
        return top == null || top.GetType().Name == "TerminalScreenViewModel";
    }
    static bool InCrumbs(Vector2 m) { foreach (var c in _crumbs) if (c.box.Contains(m) == ContainmentType.Contains) return true; return false; }

    /// <summary>
    /// Right click on the map: what is under the mouse decides the menu. A node: edit its axes exactly,
    /// delete it (or it and the later ones). The trajectory: add a maneuver there. A sector (orbit,
    /// marker, list row): plan a route to it. Anywhere with a route: clear it.
    /// </summary>
    private static void ContextMenu(List<Band> bands, double t)
    {
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        bool rp = MapInput.RightPressed;
        if (MapMenu.Draw(Mouse, MapInput.LeftPressed, rp, u)) { Maneuvers.ClaimsMouse = true; return; }
        if (MapMenu.Open) { Maneuvers.ClaimsMouse = true; return; }
        if (!rp || !MapClickable()) return;
        var items = new List<MapMenu.Item>();
        string title = null;
        bool hasNodes = Maneuvers.Nodes.Count > 0;
        var node = Maneuvers.HoverNode;
        double ht = Maneuvers.HoverT;
        var session = _session;
        var under = TargetsUnder(Mouse);
        bool pinUnder = under.Exists(x => Targets.GridOf(x.key, out _));
        if (node != null)
        {
            title = "Maneuver  ·  in " + Maneuvers.Clock(node.T - t);
            Maneuvers.Selected = node;
            // Warp to it (as KSP): top warp; warp stops by itself ahead of the burn.
            if (node.T - t > Maneuvers.WarpLead + 10) items.Add(new MapMenu.Item("Warp to maneuver", () => WarpControl.SetLevel(WarpControl.Levels.Length - 1), FrameHost.PlayerFrame != null));
            // (each edit kept for Ctrl+Z first - UI audit C5; what auto-burn does, said under the menu - U2)
            items.Add(new MapMenu.Item("Edit exact values...", () => { Maneuvers.UndoMark(); Maneuvers.EditExact(node); }));   // (README promised it: audit B4, fx-flight's dialog)
            items.Add(new MapMenu.Item(node.Auto ? "Auto-burn: on" : "Auto-burn: off", () => { Maneuvers.UndoMark(); node.Auto = !node.Auto; }) { Tip = AutoBurn.Tooltip });
            items.Add(new MapMenu.Item("Remove maneuver", () => { Maneuvers.UndoMark(); Maneuvers.Delete(node, false); }));
        }
        else if (_youScreen.HasValue && (_youScreen.Value - Mouse).Length() <= 14f * u)
        {
            // Your ship's marker: the attitude hold (as KSP's SAS buttons) while seated, and the view onto it - only here now
            // (it opened on any empty spot of the map - UI audit C7)
            title = "Your ship";
            items.Add(new MapMenu.Item("Zoom to my ship", ZoomToShip));
            if (FrameHost.Seated)
                foreach (AttitudeHold.Mode m in Enum.GetValues(typeof(AttitudeHold.Mode)))
                {
                    var mm = m;
                    items.Add(new MapMenu.Item((AttitudeHold.Current == mm ? "• " : "") + (mm == AttitudeHold.Mode.Off ? "Hold off" : "Hold " + AttitudeHold.Label(mm)), () => AttitudeHold.Set(mm)));
                }
        }
        else if (!double.IsNaN(ht))
        {
            double T = ht;
            title = "Trajectory  ·  in " + Maneuvers.Clock(T - t);
            items.Add(new MapMenu.Item("Add maneuver", () => { Maneuvers.UndoMark(); Maneuvers.AddNodeAt(T); Maneuvers.FrameNode(Maneuvers.Selected); }));
            // Warp until the ship gets there (as KSP); other stops (burns, spheres) still come first.
            if (T - t > 30) items.Add(new MapMenu.Item("Warp here", () => { SystemHost.WarpStopAt = T; WarpControl.SetLevel(WarpControl.Levels.Length - 1); }, FrameHost.PlayerFrame != null));
            // (what lies under the path is still a target from here: the path claims the mouse)
            foreach (var x in under) items.Add(TargetItem(x.key, x.name, true));
            if (hasNodes) items.Add(RemoveAllItem());
        }
        else if (pinUnder || under.Count > 1)
        {
            // A ship, or several things under the cursor: one line each, so the one you mean is never a guess.
            title = under.Count == 1 ? under[0].name : "Under the cursor";
            foreach (var x in under) items.Add(TargetItem(x.key, x.name, under.Count > 1));
            if (Hovered != null && _markerAt.ContainsKey(Hovered)) { string sec = Hovered; items.Add(new MapMenu.Item("Centre on " + sec, () => CentreOn(sec))); }
            if (hasNodes) items.Add(RemoveAllItem());
        }
        else if (BodyAt(Mouse) is GravityBody bh && !bh.IsRoot)
        {
            // A planet or a moon: a target too (its transfer windows on the Rendezvous tab).
            string bn = bh.Name;
            title = SystemHost.DisplayName(bn);
            items.Add(Maneuvers.Target == bn ? new MapMenu.Item("Clear target", () => Maneuvers.Target = null)
                                             : new MapMenu.Item("Set as target", () => Maneuvers.Target = bn));
            if (hasNodes) items.Add(RemoveAllItem());
        }
        else if (Hovered != null)
        {
            string sec = Hovered;
            var hb = bands.Find(x => x.Name == sec);
            string shown = hb != null ? Label(hb) : sec;
            title = shown;
            items.Add(new MapMenu.Item("Centre on " + shown, () => CentreOn(sec)));
            // (a virtual zone: a planet's space targets the planet, a ring its rocks - not the ring)
            string tgt = hb != null && hb.Virtual && hb.Home.Kind == SectorHomes.Kind.Body ? hb.Home.Host
                       : hb != null && hb.Virtual && hb.Home.Kind == SectorHomes.Kind.Ring ? null : sec;
            if (tgt != null)
                items.Add(Maneuvers.Target == tgt ? new MapMenu.Item("Clear target", () => Maneuvers.Target = null)
                                                  : new MapMenu.Item("Set as target", () => Maneuvers.Target = tgt));
            if (hasNodes) items.Add(RemoveAllItem());
        }
        else
        {
            // Empty space: the view back onto your ship (the attitude hold is on your ship's marker now - UI audit C7)
            title = "Map";
            items.Add(new MapMenu.Item("Zoom to my ship   (Home)", ZoomToShip));
            if (hasNodes) items.Add(RemoveAllItem());
        }
        if (items.Count > 0) { MapMenu.Show(Mouse + new Vector2(4f * u, 4f * u), title, items); Maneuvers.ClaimsMouse = true; }
    }

    /// <summary>"Remove all maneuvers" - kept for Ctrl+Z first.</summary>
    static MapMenu.Item RemoveAllItem() => new MapMenu.Item("Remove all maneuvers", () => { Maneuvers.UndoMark(); Maneuvers.ClearAll(); });


    /// <summary>Home on the map: onto your ship (UI audit C5).</summary>
    static void ShipKey()
    {
        bool key = MapInput.KeyPressed(KeyboardInputs.Home) | MapInput.DevKeys.Remove("home");
        if (!key || MapMenu.Open) return;
        var s = MapView.SessionForLayout;   // (not while a dialog over the terminal has the keyboard)
        object top = s != null ? GameUi.TopScreenObject(s) : null;
        if (top == null || top.GetType().Name == "TerminalScreenViewModel") ZoomToShip();
    }
    /// <summary>The mouse (screen px) for this frame's editors.</summary>
    public static Vector2 Mouse;

    static readonly ColorSRGB EncColor = new ColorSRGB(1.00f, 0.55f, 0.25f, 0.85f);

    static string Km(double m) => Math.Abs(m) >= 10000 ? $"{m / 1000:N0} km" : $"{m / 1000:F1} km";

    /// <summary>
    /// Encounter frames (procedural spawns, authored sites; sector anchors are the sector orbits already):
    /// their orbit and where they are now. GPS markers: at their true place (frame-transferred), pinned to
    /// the edge when beyond the view. toLocal maps a sun-centred model position to the map.
    /// </summary>
    private static void Overlay(Func<Vector3D, Vector3D> toLocal, double limit, Func<Vector3D, Vector3D> W, double t, SystemRegistry reg)
    {
        long player = FrameHost.PlayerId;
        lock (ServerFrames.FramesLock)
        {
            if (SystemHost.Frames == null) return;
            foreach (var f in SystemHost.Frames.Frames)
            {
                if (!f.IsEncounter || f.HasMember(player)) continue;
                if (Targets.HasGrid(f)) continue;   // (its ships are pinned below, as targets)
                if (!Contacts.KnownFrame(f)) continue;   // not seen yet
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
                string label = site != null ? site.Label : $"Encounter ({f.Members.Count})";
                Pin(here, limit, W, label, EncColor);
            }
        }

        // Ships and stations: yours and the contacts you know of, where they are now; on rails, their orbit too.
        foreach (var e in Targets.ShipsCached())
        {
            if (!Targets.At(e.Key, t, out var par, out var rel)) continue;
            if (!(par.IsRoot || _detail.Contains(par.Name))) continue;
            Vector3D porg = par.OriginInRoot(t).Position;
            bool tgt = e.Key == Maneuvers.Target;
            var col = tgt ? TargetText : e.Yours ? OwnColor : EncColor;
            if (Targets.Orbit(e.Key, out var opar, out var el) && opar == par)
            {
                double R = reg.FindDefinition(par.Name)?.RadiusMeters ?? 0;
                if (!(el.IsElliptic && el.PeriapsisRadius < R))   // (a base on the ground: no orbit to draw)
                {
                    double T = el.IsElliptic && IsFinite(el.Period) ? el.Period : 6 * 3600.0;
                    Vector3D prev = default; bool havePrev = false;
                    for (int k = 0; k <= 120; k++)
                    {
                        var st = OrbitPropagation.StateAt(el, t + T * k / 120);
                        if (!IsFinite(st.Position.X) || !IsFinite(st.Position.Y)) { havePrev = false; continue; }
                        Vector3D pp = toLocal(porg + st.Position);
                        if (havePrev)
                        {
                            MapPipeline.Line(W(prev), W(pp), HudPanel.Alpha(col, tgt ? 0.7f : 0.45f), tgt ? 1.8f : 1.2f);
                            if (MapPipeline.ToScreen(W(prev), out var sa) && MapPipeline.ToScreen(W(pp), out var sb)) OrbitHover(e.Key, sa, sb);
                        }
                        prev = pp; havePrev = true;
                    }
                }
            }
            string label = e.Tracked ? e.Name : "\u2248 " + e.Name;
            _shipPins.Add((toLocal(porg + rel.Position), label, col, e.Key, e.Name, tgt ? 0 : e.Yours ? 1 : 2));
        }
        // Pins on one spot (a planet's ground stations seen from afar) are one pin, "Name +3": the target first, then yours.
        // Each still answers N and the right-click menu where it is (every one under the cursor is listed there).
        {
            float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
            _shipPins.Sort((a, b) => a.rank.CompareTo(b.rank));
            var leaders = new List<(int i, Vector2 s, int n)>();
            var follow = new List<(int i, Vector2 s)>();
            for (int i = 0; i < _shipPins.Count; i++)
            {
                if (!MapPipeline.ToScreen(W(_shipPins[i].p), out var s)) continue;
                int li = leaders.FindIndex(x => (x.s - s).Length() < 14f * u);
                if (li < 0) leaders.Add((i, s, 0));
                else { leaders[li] = (leaders[li].i, leaders[li].s, leaders[li].n + 1); follow.Add((i, s)); }
            }
            foreach (var ld in leaders)
            {
                var sp = _shipPins[ld.i];
                Pin(sp.p, limit, W, ld.n > 0 ? $"{sp.label}  +{ld.n}" : sp.label, sp.col, key: sp.key, name: sp.name);
            }
            foreach (var fl in follow)
                if (InOpenArea(fl.s)) _pins.Add((_shipPins[fl.i].key, _shipPins[fl.i].name, fl.s));
            _shipPins.Clear();
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
    private static void Pin(Vector3D p, double limit, Func<Vector3D, Vector3D> W, string label, ColorSRGB c, bool diamond = false, string key = null, string name = null)
    {
        if (!MapPipeline.ToScreen(W(p), out var s)) return;
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
        if (InOpenArea(s))
        {
            bool hot = key != null && (s - Mouse).Length() <= PinHit * u && !MapMenu.Open && !MapCamera.Dragging;
            if (hot) MapPipeline.ScreenCircle(s, 11f * u, HudPanel.Alpha(Text, 0.9f), 1.6f * u);   // (the one N or a click would take)
            if (diamond) MapPipeline.ScreenDiamond(W(p), 6f, c, 1.8f);
            else { MapPipeline.ScreenRing(W(p), 6f, c, 1.8f); MapPipeline.ScreenRing(W(p), 2.5f, c, 2.5f); }
            MapPipeline.TextScreen(s + new Vector2(0, 16f * u), label, hot ? Text : c, (hot ? 0.7f : 0.6f) * MapPipeline.MapText);
            if (key != null) _pins.Add((key, name ?? label, s));
            return;
        }
        // The open area's centre, and where the ray to the marker leaves it.
        var scr = MapPipeline.ScreenSize;
        Vector2 min = MapLayout.P(0.265f, 0.2f), max = MapLayout.P(0.765f, 0.82f);
        Vector2 c0 = (min + max) * 0.5f, d = s - c0;
        if (d.LengthSquared() < 1e-6f) return;
        float k = Math.Min(Math.Abs((d.X > 0 ? max.X - c0.X : c0.X - min.X) / (Math.Abs(d.X) + 1e-6f)), Math.Abs((d.Y > 0 ? max.Y - c0.Y : c0.Y - min.Y) / (Math.Abs(d.Y) + 1e-6f)));
        Vector2 e = c0 + d * k, n = Vector2.Normalize(d), side = new Vector2(-n.Y, n.X);
        float a = 9f * u;
        // The arrow only with its name (a bare arrow says nothing).
        if (!MapPipeline.TextScreen(e - n * (a + 14f * u), label, c, (0.6f) * MapPipeline.MapText)) return;
        if (!MapPipeline.ScreenArrow(e - n * a * 0.5f, n, a * 0.75f, c))
        {
            MapPipeline.ScreenLine(e, e - n * a + side * a * 0.6f, c, 1.8f * u);
            MapPipeline.ScreenLine(e, e - n * a - side * a * 0.6f, c, 1.8f * u);
        }
    }
}
