using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// A right-click context menu on the map, in the look of the game's panels (dark slate, thin rule,
/// orange hover). The game has no general context menu a mod can drive (its only one is the building
/// radial menu), so this one is drawn. Items run on left click; a click elsewhere closes it.
/// </summary>
public static class MapMenu
{
    public sealed class Item
    {
        public string Text; public Action Run; public bool Enabled = true;
        public Item(string text, Action run, bool enabled = true) { Text = text; Run = run; Enabled = enabled; }
    }

    private static List<Item> _items;
    private static Vector2 _at;
    private static string _title;
    private static int _devChoose = -1;
    public static bool Open => _items != null;

    static readonly ColorSRGB Bg = new ColorSRGB(0.035f, 0.075f, 0.105f, 0.96f);
    static readonly ColorSRGB Rule = new ColorSRGB(0.42f, 0.52f, 0.62f, 0.55f);
    static readonly ColorSRGB Hover = new ColorSRGB(0.93f, 0.56f, 0.12f, 0.95f);
    static readonly ColorSRGB Text = new ColorSRGB(0.94f, 0.96f, 0.98f, 1f);
    static readonly ColorSRGB Dark = new ColorSRGB(0.04f, 0.05f, 0.06f, 1f);
    static readonly ColorSRGB Dim = new ColorSRGB(0.55f, 0.62f, 0.68f, 0.9f);

    private static string _openKey;

    public static void Show(Vector2 at, string title, List<Item> items)
    {
        _openKey = CleanMap.ViewKey;
        if (items == null || items.Count == 0) { Close(); return; }
        _at = at; _title = title; _items = items;
    }

    public static void Close() { _items = null; }

    /// <summary>DEV: choose item i on the next frame.</summary>
    public static void DevChoose(int i) => _devChoose = i;

    /// <summary>Draw and run the menu (inside the map's UI batch). True while it has the mouse.</summary>
    public static bool Draw(Vector2 mouse, bool leftPressed, bool rightPressed, float u)
    {
        if (_items == null) return false;
        if (MapCamera.Dragging || CleanMap.ViewKey != _openKey) { Close(); return false; }   // the view moved on
        if (MapInput.KeyPressed(Keen.VRage.Core.Input.KeyboardInputs.Escape) || MapInput.DevKeys.Remove("escape")) { Close(); return true; }   // Esc closes it
        var sz = MapPipeline.ScreenSize;
        float row = 30f * u, pad = 12f * u, ts = 0.58f * u, head = _title != null ? 26f * u : 0;
        float w = 0;
        foreach (var it in _items) w = Math.Max(w, MapPipeline.MeasureText(it.Text, ts).X);
        if (_title != null) w = Math.Max(w, MapPipeline.MeasureText(_title, 0.5f * u).X);
        w += pad * 2 + 8f * u;
        float h = head + row * _items.Count + 6f * u;
        Vector2 at = _at;
        // Inside the map's open area (not over the game's panels).
        var oa = MapLayout.OpenArea;   // (below the tab row at every screen shape)
        if (at.X + w > oa.Max.X) at.X = oa.Max.X - w;
        if (at.X < oa.Min.X) at.X = oa.Min.X;
        if (at.Y + h > oa.Max.Y) at.Y = oa.Max.Y - h;
        if (at.Y < oa.Min.Y) at.Y = oa.Min.Y;
        Vector2 max = at + new Vector2(w, h);

        MapPipeline.ScreenRect(at, max, Bg);
        MapPipeline.ScreenLine(at, new Vector2(max.X, at.Y), Rule, 1f * u);
        MapPipeline.ScreenLine(new Vector2(at.X, max.Y), max, Rule, 1f * u);
        if (_title != null) MapPipeline.ScreenText(at + new Vector2(pad, 6f * u), _title, Dim, 0.5f * u);

        int hover = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            Vector2 r0 = new Vector2(at.X + 3f * u, at.Y + head + 3f * u + i * row), r1 = new Vector2(max.X - 3f * u, r0.Y + row);
            bool hot = it.Enabled && mouse.X >= r0.X && mouse.X <= r1.X && mouse.Y >= r0.Y && mouse.Y <= r1.Y;
            if (hot) { hover = i; MapPipeline.ScreenRect(r0, r1, Hover); }
            var tsz = MapPipeline.MeasureText(it.Text, ts);
            MapPipeline.ScreenText(new Vector2(r0.X + pad - 3f * u, r0.Y + (row - tsz.Y) * 0.5f), it.Text, !it.Enabled ? Dim : hot ? Dark : Text, ts);
        }

        bool inside = mouse.X >= at.X && mouse.X <= max.X && mouse.Y >= at.Y && mouse.Y <= max.Y;
        if (_devChoose >= 0) { hover = _devChoose < _items.Count && _items[_devChoose].Enabled ? _devChoose : -1; leftPressed = true; inside = true; _devChoose = -1; }
        if (leftPressed)
        {
            var run = hover >= 0 ? _items[hover].Run : null;
            Close();
            try { run?.Invoke(); } catch (Exception e) { Log.Default?.Info("[ORBIT] menu: " + e.Message); }
            return true;
        }
        if (rightPressed && !inside) { Close(); return false; }   // a right click elsewhere opens a new one
        return true;
    }
}
