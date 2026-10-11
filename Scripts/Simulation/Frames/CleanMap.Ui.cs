using Keen.VRage.Core;
using Keen.VRage.Core.Input;
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
        _listBox = new BoundingBox2(MapLayout.P(0.777f, 0.27f), MapLayout.P(0.777f, 0.27f));   // (none this frame until drawn)
        Vector2 scr = MapPipeline.ScreenSize;
        float k = MapPipeline.TextScale;   // rows and columns grow with the text
        float x = MapLayout.X(0.782f), y = MapLayout.Y(0.27f), line = MapLayout.LenY(0.0275f) * k, scale = 0.78f;
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
            MapPipeline.ScreenText(new Vector2(x, r.y), b.Number.ToString(), Dim, scale);
            MapPipeline.ScreenDot(new Vector2(x + MapLayout.LenY(0.030f) * k, r.y + line * 0.42f), 4.5f, StateColor(b));
            MapPipeline.ScreenText(new Vector2(x + MapLayout.LenY(0.045f) * k, r.y), b.Name == Maneuvers.Target ? Label(b) + "  · target" : Label(b), b.Name == Maneuvers.Target ? TargetText : c, scale);
            MapPipeline.ScreenText(new Vector2(x + MapLayout.LenY(0.19f) * k, r.y), Fit(Where(b), x1 - (x + MapLayout.LenY(0.19f) * k) - 6f, scale * 0.85f), Dim, scale * 0.85f);
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
    /// <summary>A hint line wrapped at its separators to a width (each piece kept whole; one wider than the width is cut).</summary>
    internal static void DrawWrapped(Vector2 at, string text, float width, float scale, ColorSRGB color)
    {
        string sep = "   " + (char)0x00b7 + "   ";
        var parts = text.Split(new[] { sep }, StringSplitOptions.None);
        string line = "";
        float lh = MapPipeline.MeasureText("Ag", scale).Y * 1.25f;
        if (!(lh > 0)) lh = 22f * scale;
        int n = 0;
        foreach (var p in parts)
        {
            string tryLine = line.Length == 0 ? p : line + sep + p;
            if (line.Length > 0 && MapPipeline.MeasureText(tryLine, scale).X > width)
            {
                MapPipeline.ScreenText(at + new Vector2(0, n++ * lh), Fit(line, width, scale), color, scale);
                line = p;
            }
            else line = tryLine;
        }
        if (line.Length > 0) MapPipeline.ScreenText(at + new Vector2(0, n * lh), Fit(line, width, scale), color, scale);
    }

    static void Hints()
    {
        var scr = MapPipeline.ScreenSize;
        string h;
        if (Maneuvers.OnGizmo) h = "Drag a handle to change the burn   \u00b7   Drag the node along the path   \u00b7   Right-click: options";
        else if (!double.IsNaN(Maneuvers.HoverT)) h = "Click: add a maneuver here   \u00b7   Right-click: options   \u00b7   N: target what is under the cursor   \u00b7   Drag: pan";
        else if (TargetsUnder(Mouse).Count > 0) h = "N: target it   \u00b7   Right-click: options   \u00b7   Double-click: focus   \u00b7   Drag: pan";
        else h = "Drag: pan   \u00b7   Right-drag: orbit   \u00b7   Wheel: zoom   \u00b7   Double-click: focus   \u00b7   N or right-click: target   \u00b7   . , /  warp";   // (two lines at most: the game's text is wide)
        // (wrapped to the map's width at its separators: a long hint ran onto the right panel at 4:3 and 5:4; cut short, it
        //  lost its last hints)
        DrawWrapped(MapLayout.P(0.265f, 0.9f), h, MapLayout.X(0.775f) - MapLayout.X(0.265f), 0.74f, Dim);
    }

    // ───────────────────────────── encounters and GPS (both views) ─────────────────────────────

    private static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>
    /// Right click on the map: what is under the mouse decides the menu. A node: edit its axes exactly,
    /// delete it (or it and the later ones). The trajectory: add a maneuver there. A sector (orbit,
    /// marker, list row): plan a route to it. Anywhere with a route: clear it.
    /// </summary>
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

    private static void ContextMenu(List<Band> bands, double t)
    {
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
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
            // (what lies under the path is still a target from here: the path claims the mouse)
            foreach (var x in under) items.Add(TargetItem(x.key, x.name, true));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (pinUnder || under.Count > 1)
        {
            // A ship, or several things under the cursor: one line each, so the one you mean is never a guess.
            title = under.Count == 1 ? under[0].name : "Under the cursor";
            foreach (var x in under) items.Add(TargetItem(x.key, x.name, under.Count > 1));
            if (Hovered != null && _markerAt.ContainsKey(Hovered)) { string sec = Hovered; items.Add(new MapMenu.Item("Centre on " + sec, () => CentreOn(sec))); }
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else if (BodyAt(Mouse) is GravityBody bh && !bh.IsRoot)
        {
            // A planet or a moon: a target too (its transfer windows on the Rendezvous tab).
            string bn = bh.Name;
            title = SystemHost.DisplayName(bn);
            items.Add(Maneuvers.Target == bn ? new MapMenu.Item("Clear target", () => Maneuvers.Target = null)
                                             : new MapMenu.Item("Set as target", () => Maneuvers.Target = bn));
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
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
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        else
        {
            // Empty space: the attitude hold (as KSP's SAS buttons), while seated
            if (FrameHost.Seated)
            {
                title = "Attitude hold";
                foreach (AttitudeHold.Mode m in Enum.GetValues(typeof(AttitudeHold.Mode)))
                {
                    var mm = m;
                    items.Add(new MapMenu.Item((AttitudeHold.Current == mm ? "• " : "") + (mm == AttitudeHold.Mode.Off ? "Hold off" : "Hold " + AttitudeHold.Label(mm)), () => AttitudeHold.Set(mm)));
                }
            }
            if (hasNodes) items.Add(new MapMenu.Item("Remove all maneuvers", Maneuvers.ClearAll));
        }
        if (items.Count > 0) { MapMenu.Show(Mouse + new Vector2(4f * u, 4f * u), title, items); Maneuvers.ClaimsMouse = true; }
    }
    /// <summary>The mouse (screen px) for this frame's editors.</summary>
    public static Vector2 Mouse;

    // ───────────────────────────── targeting ─────────────────────────────

    /// <summary>How near a ship's pin the cursor must be to take it (px at 1080p, scaled with the screen).</summary>
    public const float PinHit = 16f;
    private static readonly List<(string key, string name, Vector2 s)> _pins = new List<(string, string, Vector2)>();
    private static List<(string key, string name, Vector2 s)> _pinsLast = new List<(string, string, Vector2)>();

    /// <summary>A new frame of pins (the last frame's kept for this frame's input: input runs before the overlay draws).</summary>
    static void SwapPins() { _pinsLast = new List<(string, string, Vector2)>(_pins); _pins.Clear(); }

    /// <summary>What can be targeted under the cursor: ship pins (nearest first), the body there, the sector or rock there.</summary>
    static List<(string key, string name)> TargetsUnder(Vector2 m)
    {
        var l = new List<(string key, string name)>();
        if (!MapClickable()) return l;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        var pins = new List<(string key, string name, Vector2 s)>(_pins.Count > 0 ? _pins : _pinsLast);
        pins.Sort((a, b) => (a.s - m).Length().CompareTo((b.s - m).Length()));
        foreach (var pn in pins)
            if ((pn.s - m).Length() <= PinHit * u && !l.Exists(x => x.key == pn.key)) l.Add((pn.key, pn.name));
        if (BodyAt(m) is GravityBody b && !b.IsRoot && !l.Exists(x => x.key == b.Name)) l.Add((b.Name, SystemHost.DisplayName(b.Name)));
        if (Hovered != null)
        {
            // (a virtual zone: a planet's space targets the planet, a ring its rocks - not the ring)
            var hb = _bandsLast?.Find(x => x.Name == Hovered);
            string tgt = hb != null && hb.Virtual && hb.Home.Kind == SectorHomes.Kind.Body ? hb.Home.Host
                       : hb != null && hb.Virtual && hb.Home.Kind == SectorHomes.Kind.Ring ? null : Hovered;
            if (tgt != null && !l.Exists(x => x.key == tgt)) l.Add((tgt, hb != null && tgt == Hovered ? Label(hb) : Targets.Name(tgt)));
        }
        return l;
    }
    private static List<Band> _bandsLast;

    static MapMenu.Item TargetItem(string key, string name, bool named)
        => Maneuvers.Target == key ? new MapMenu.Item(named ? "Clear target: " + name : "Clear target", () => SetTarget(null))
                                   : new MapMenu.Item(named ? "Target " + name : "Set as target", () => SetTarget(key));

    /// <summary>Set (or clear) the target, and say so.</summary>
    public static void SetTarget(string key)
    {
        Maneuvers.Target = key;
        WarpControl.Say(key == null ? "Target cleared" : "Target: " + Targets.Name(key));
    }

    /// <summary>N on the map: target what is under the cursor (again: the next thing there; on the target alone: clear it).</summary>
    static void TargetKey()
    {
        bool n = MapInput.KeyPressed(KeyboardInputs.N) | MapInput.DevKeys.Remove("n");
        if (!n || MapMenu.Open) return;
        var under = TargetsUnder(Mouse);
        if (under.Count == 0) { WarpControl.Say("Nothing to target under the cursor"); return; }
        int i = under.FindIndex(x => x.key == Maneuvers.Target);
        if (i < 0) SetTarget(under[0].key);
        else if (under.Count == 1) SetTarget(null);
        else SetTarget(under[(i + 1) % under.Count].key);
    }

    /// <summary>DEV: the targeting state, for the harness.</summary>
    public static string DevTargets()
    {
        var sb = new System.Text.StringBuilder();
        string tk = Maneuvers.Target;
        sb.Append($"target {(tk == null ? "none" : $"'{Targets.Name(tk)}' [{tk}]")} | line {Maneuvers.TargetLine(SystemHost.Now) ?? "-"}");
        var scr = MapPipeline.ScreenSize;
        sb.Append(" | ships");
        foreach (var e in Targets.ShipsCached()) sb.Append($" [{e.Key} '{e.Name}' {(e.Yours ? "yours" : "contact")}{(e.Tracked ? "" : " untracked")}]");
        sb.Append(" | pins");
        foreach (var pn in _pinsLast) sb.Append($" [{pn.key} '{pn.name}' at {pn.s.X / scr.X:F3},{pn.s.Y / scr.Y:F3}]");
        sb.Append(" | list");
        foreach (var r in _tlRows) if (r.key != null) sb.Append($" [{r.key} at {(r.box.Min.X + r.box.Max.X) * 0.5f / scr.X:F3},{(r.box.Min.Y + r.box.Max.Y) * 0.5f / scr.Y:F3}]");
        sb.Append(" | bodies");
        foreach (var h in _hits) sb.Append($" [{h.b.Name} at {h.s.X / scr.X:F3},{h.s.Y / scr.Y:F3}]");
        if (Maneuvers.Target != null)
        {
            var pi = OrbitInfo.Panel(Maneuvers.Target, SystemHost.Now, _panelMore);
            sb.Append($" | panel {(TargetPanelShown ? "shown" : "lists")} '{pi.Title}' ({pi.Sub})");
            foreach (var sec in pi.Sections) { sb.Append($" / {sec.Head ?? "-"}{(sec.Folded ? " (folded)" : "")}:"); if (!sec.Folded) foreach (var r in sec.Rows) sb.Append($" {r.K}={r.V}"); }
        }
        sb.Append(" | under");
        foreach (var x in TargetsUnder(Mouse)) sb.Append($" [{x.key}]");
        return sb.ToString();
    }

    private static readonly List<(Vector3D p, string label, ColorSRGB col, string key, string name, int rank)> _shipPins = new List<(Vector3D, string, ColorSRGB, string, string, int)>();
    static readonly ColorSRGB OwnColor = new ColorSRGB(0.45f, 0.85f, 1.00f, 0.9f);
    private static readonly List<(string key, BoundingBox2 box)> _tlRows = new List<(string, BoundingBox2)>();
    private static BoundingBox2 _tlBox;

    /// <summary>
    /// The target list, under the sector list: your ships and the contacts you know (nearest first), then the planets
    /// and moons, each with its distance. A click targets (again: clears); the target is marked.
    /// </summary>
    static void TargetList(SystemRegistry reg, double t, bool ui)
    {
        _tlRows.Clear();
        float k = MapPipeline.TextScale;
        float line = MapLayout.LenY(0.0275f) * k, scale = 0.78f;
        float x0 = MapLayout.X(0.777f), x1 = MapLayout.X(0.975f), x = MapLayout.X(0.782f);
        float top = Math.Max(_listBox.Max.Y, MapLayout.Y(0.27f) - line) + line * 0.7f;
        float bottom = MapLayout.Y(0.80f);   // (above the game's Fast Travel button, which sits over the panel's foot)
        int room = (int)((bottom - top) / line) - 1;
        if (room < 2) return;
        Vector3D you = default; bool haveYou = Maneuvers.Trajectory(t, out var legs, out _) && legs.Count > 0;
        if (haveYou) you = Maneuvers.RootAt(legs[0], t);
        double Dist(string key) => haveYou && Targets.RootAt(key, t, out var r) ? (r.Position - you).Length() : double.NaN;
        var ships = new List<(string key, string name, double d, ColorSRGB c)>();
        foreach (var e in Targets.ShipsCached())
            ships.Add((e.Key, e.Tracked ? e.Name : "\u2248 " + e.Name, Dist(e.Key), e.Yours ? OwnColor : EncColor));
        ships.Sort((a, b) => (double.IsNaN(a.d) ? double.MaxValue : a.d).CompareTo(double.IsNaN(b.d) ? double.MaxValue : b.d));
        var bodies = new List<(string key, string name, double d, ColorSRGB c)>();
        foreach (var b in reg.Bodies)
            if (!b.IsRoot && SystemHost.BeaconOf.ContainsKey(b.Name)) bodies.Add((b.Name, SystemHost.DisplayName(b.Name), Dist(b.Name), Text));
        bodies.Sort((a, b) => (double.IsNaN(a.d) ? double.MaxValue : a.d).CompareTo(double.IsNaN(b.d) ? double.MaxValue : b.d));
        // Ships first (rendezvous is what the list is for), nearest first, then the planets and moons; what does not fit
        // is paged (a last row, "more", turns the page).
        var all = new List<(string key, string name, double d, ColorSRGB c, bool header)>();
        all.Add((null, ships.Count == 0 ? "SHIPS  (none known)" : "SHIPS AND STATIONS", 0, Dim, true));
        foreach (var sh in ships) all.Add((sh.key, sh.name, sh.d, sh.c, false));
        if (bodies.Count > 0) all.Add((null, "PLANETS AND MOONS", 0, Dim, true));
        foreach (var b in bodies) all.Add((b.key, b.name, b.d, b.c, false));
        var rows = all;
        if (all.Count > room)
        {
            int per = Math.Max(1, room - 1), pages = (all.Count + per - 1) / per;
            _tlPage %= pages;
            rows = all.GetRange(_tlPage * per, Math.Min(per, all.Count - _tlPage * per));
            rows.Add((MoreKey, $"more  ({_tlPage + 1} / {pages})", double.NaN, Dim, false));
        }
        else _tlPage = 0;
        float y = top + line * 0.25f;
        float h = rows.Count * line + line * 0.5f;
        MapPipeline.ScreenRect(new Vector2(x0, top), new Vector2(x1, top + h), PanelFill);
        _tlBox = new BoundingBox2(new Vector2(x0, top), new Vector2(x1, top + h));
        MapPipeline.Reserve(new Vector2(x0, top), new Vector2(MapPipeline.ScreenSize.X, top + h));
        bool inBox = _tlBox.Contains(Mouse) == ContainmentType.Contains;
        string clicked = null; bool click = ui && inBox && MapInput.LeftReleased && !MapCamera.DragEnded && !MapMenu.Open;
        foreach (var r in rows)
        {
            if (r.header) { MapPipeline.ScreenText(new Vector2(x, y), r.name, Dim, scale * 0.85f); y += line; continue; }
            var rowMin = new Vector2(x0, y - line * 0.12f); var rowMax = new Vector2(x1, y + line * 0.88f);
            bool hot = inBox && Mouse.Y >= rowMin.Y && Mouse.Y < rowMax.Y;
            bool tgt = r.key == Maneuvers.Target;
            if (hot) MapPipeline.ScreenRect(rowMin, rowMax, RowHover);
            if (tgt) MapPipeline.ScreenRect(rowMin, new Vector2(x0 + 3f, rowMax.Y), TargetText);
            if (r.key != MoreKey) MapPipeline.ScreenDot(new Vector2(x + 5f, y + line * 0.42f), 4f, r.c);
            float nx = x + MapLayout.LenY(0.02f) * k, dx = x + MapLayout.LenY(0.19f) * k;
            MapPipeline.ScreenText(new Vector2(nx, y), Fit(tgt ? r.name + "  \u00b7 target" : r.name, dx - nx - 6f, scale), tgt ? TargetText : Text, scale);
            if (!double.IsNaN(r.d)) MapPipeline.ScreenText(new Vector2(dx, y), Fit(HudPanel.Km(r.d), x1 - dx - 6f, scale * 0.85f), Dim, scale * 0.85f);
            if (hot && click || DevRowClick == _tlRows.Count) clicked = r.key;
            _tlRows.Add((r.key, new BoundingBox2(rowMin, rowMax)));
            y += line;
        }
        DevRowClick = -1;
        if (clicked == MoreKey) _tlPage++;
        else if (clicked != null) SetTarget(clicked == Maneuvers.Target ? null : clicked);
    }
    private static int _tlPage;
    const string MoreKey = "\u0001more";
    /// <summary>DEV: click the target list's row i (0: its first target) on the next frame.</summary>
    public static int DevRowClick = -1;
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

    /// <summary>The body drawn under a screen point this frame (within its drawn size or ring), or null.</summary>
    static GravityBody BodyAt(Vector2 m)
    {
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - m).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        return best;
    }

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
        if (_markerAt.ContainsKey(name)) { CentreOn(name); return "centre on sector " + name; }   // a sector: centre on it
        var reg = SystemHost.Registry; var b = reg?.Find(name) ?? (name == StarName ? reg?.Root : null);
        if (b == null || _lastW == null) return "no body / map not drawn";
        FocusOn(b, reg, SystemHost.Now, _lastW, _lastSolar);
        return "focus " + name;
    }

    /// <summary>
    /// Hovering something: a small panel by the cursor with what it is and its orbit (apoapsis, periapsis, inclination,
    /// period), so the map can be read by pointing at it. What is under the cursor, first found: a ship's pin, a body, an
    /// orbit line (a ship's, a planet's, a moon's), your own path (that arc's orbit), a sector.
    /// </summary>
    static void HoverCard(SystemRegistry reg, string playerPlanet, KeplerianElements? playerOrbit, double t)
    {
        if (MapCamera.Dragging || MapMenu.Open || Maneuvers.OnGizmo || !InOpenArea(Mouse)) return;
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
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
        float kS = 0.74f * k, vS = 0.8f * k, hS = 0.6f * k, tS = 1.0f * k, sS = 0.72f * k;
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
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_panel == null || now - _panelAt > 0.5) { _panel = OrbitInfo.Panel(key, t, _panelMore); _panelAt = now; }
        float x0 = MapLayout.X(0.777f), x1 = MapLayout.X(0.975f), top = MapLayout.Y(0.268f);   // (clear of the game's index box above)
        float bs = 0.72f, bh = Math.Max(MapPipeline.MeasureText("Ag", bs).Y, 14f * u) + 14f * u;
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

    static void FocusInput(SystemRegistry reg, double t, Func<Vector3D, Vector3D> W, bool solar)
    {
        if (MapInput.LeftReleased)
        {
            float nd = float.MaxValue; string nn = null;
            foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d < nd) { nd = d; nn = h.b.Name + $"(r{h.r:F0})"; } }
            ClickDebug = $"release at {Mouse} drag={MapCamera.DragEnded} gizmo={Maneuvers.OnGizmo} menu={MapMenu.Open} hits={_hits.Count} nearest {nn} {nd:F0}px";
        }
        if (!MapInput.LeftReleased || MapCamera.DragEnded || Maneuvers.OnGizmo || MapMenu.Open) return;
        if (!MapClickable()) return;
        foreach (var c in _crumbs)
            if (c.box.Contains(Mouse) == ContainmentType.Contains) { FocusOn(c.b, reg, t, W, solar); return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        bool dbl = now - _lastClick < 0.4 && (Mouse - _lastClickAt).Length() < 10f;
        _lastClick = dbl ? 0 : now; _lastClickAt = Mouse;
        if (!dbl) return;
        GravityBody best = null; float bd = float.MaxValue;
        foreach (var h in _hits) { float d = (h.s - Mouse).Length(); if (d <= h.r && d < bd) { bd = d; best = h.b; } }
        if (best != null) FocusOn(best, reg, t, W, solar);
        else FocusLens(Mouse);   // a Lagrange zone: centre on it and frame it
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
        var at = MapLayout.P(0.265f, 0.158f);
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
        MapPipeline.Reserve(at - new Vector2(4, 4), at + new Vector2(MapLayout.LenX(0.3f), MapLayout.LenY(0.035f)));
        _titleAt = at;
        // (Only the path: the title box and the you / burn / target lines were removed on request.)
    }

    /// <summary>The map's open area on screen (between the game's panels, above the warp bar).</summary>
    public static bool InOpenArea(Vector2 s)
    {
        var scr = MapPipeline.ScreenSize;
        return MapLayout.InOpenArea(s);   // (below the tab row - it was 0.1 here while the clip was 0.148: pins drew their names in the tabs)
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
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
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
        float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
        if (InOpenArea(s))
        {
            bool hot = key != null && (s - Mouse).Length() <= PinHit * u && !MapMenu.Open && !MapCamera.Dragging;
            if (hot) MapPipeline.ScreenCircle(s, 11f * u, HudPanel.Alpha(Text, 0.9f), 1.6f * u);   // (the one N or a click would take)
            if (diamond) MapPipeline.ScreenDiamond(W(p), 6f, c, 1.8f);
            else { MapPipeline.ScreenRing(W(p), 6f, c, 1.8f); MapPipeline.ScreenRing(W(p), 2.5f, c, 2.5f); }
            MapPipeline.TextScreen(s + new Vector2(0, 16f * u), label, hot ? Text : c, hot ? 0.7f : 0.6f);
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
        if (!MapPipeline.TextScreen(e - n * (a + 14f * u), label, c, 0.6f)) return;
        if (!MapPipeline.ScreenArrow(e - n * a * 0.5f, n, a * 0.75f, c))
        {
            MapPipeline.ScreenLine(e, e - n * a + side * a * 0.6f, c, 1.8f * u);
            MapPipeline.ScreenLine(e, e - n * a - side * a * 0.6f, c, 1.8f * u);
        }
    }
}
