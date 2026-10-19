using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>The map's focus (as KSP: double-click a body, or click the breadcrumb), the title path, the open area.</summary>
public static partial class CleanMap
{
    // ---- focus (as KSP: double-click a body, or click the breadcrumb) ----

    private static readonly List<(GravityBody b, Vector2 s, float r)> _hits = new List<(GravityBody, Vector2, float)>();
    private static readonly List<(GravityBody b, BoundingBox2 box)> _crumbs = new List<(GravityBody, BoundingBox2)>();
    private static double _lastClick;

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
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
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
        return MapPipeline.MeasureText(text, 1.05f * MapPipeline.MapText);   // (now never short)
    }

    private static Vector2 _titleAt;

    /// <summary>The title block's height: the path, your situation, and the burn and target lines when there are.</summary>
    static float TitleHeight(Vector2 sc)
    {
        return sc.Y * 0.045f;   // the path line only
    }

    /// <summary>
    /// Where you are looking and where you are, at the top of the map's open area: "Delfos › Kemik",
    /// then your ship's situation (the orbit's Pe / Ap, or on the ground).
    /// </summary>
    static void Title(GravityBody view, string playerPlanet, KeplerianElements? orbit)
    {
        var scr = MapPipeline.ScreenSize;
        var at = MapLayout.P(0.265f, 0.158f);
        // The breadcrumb: each part a click target (up to the star, down to what is in view).
        var chain = new List<GravityBody>();
        for (var b = view; b != null; b = b.Parent) chain.Insert(0, b);
        if (chain.Count == 0 && SystemHost.Registry?.Root != null) chain.Add(SystemHost.Registry.Root);
        float x = at.X;
        {
            // its backplate (UI audit cosmetic: the breadcrumb washed out in the sun's glare)
            float gap0 = 10f * MapPipeline.UiScale(scr.Y), w = 0, h = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                var sz = CrumbSize(chain[i].Parent == null ? StarName : chain[i].Name);
                w += sz.X + (i < chain.Count - 1 ? 2 * gap0 + CrumbSize(">").X : 0); h = Math.Max(h, sz.Y);
            }
            if (w > 0) MapPipeline.ScreenRect(at - new Vector2(gap0, 2f), at + new Vector2(w + gap0, h + 2f), TextPlate);
        }
        for (int i = 0; i < chain.Count; i++)
        {
            string name = chain[i].Parent == null ? StarName : chain[i].Name;
            var size = CrumbSize(name);
            var box = new BoundingBox2(new Vector2(x, at.Y), new Vector2(x + size.X, at.Y + size.Y));
            bool last = i == chain.Count - 1;
            bool hot = !last && box.Contains(Mouse) == ContainmentType.Contains;
            MapPipeline.ScreenText(new Vector2(x, at.Y), name, hot ? LineSel : last ? Text : Dim, 1.05f * MapPipeline.MapText);
            if (!last) _crumbs.Add((chain[i], box));
            x += size.X;
            if (!last)
            {
                float gap = 10f * MapPipeline.UiScale(scr.Y);   // the font drops leading spaces: gaps in pixels
                x += gap; MapPipeline.ScreenText(new Vector2(x, at.Y), ">", Dim, 1.05f * MapPipeline.MapText); x += CrumbSize(">").X + gap;
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
}
