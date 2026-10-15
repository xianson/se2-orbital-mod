using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The map's targeting: N and right-click targets under the cursor, the pins, the target list.</summary>
public static partial class CleanMap
{
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
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
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
        float line = MapLayout.LenY(0.0275f) * k, scale = 0.78f * MapPipeline.MapText;
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
}
