using SEAerospace.Frames;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The map's places on screen at any screen shape: fractions of the terminal's 1920 x 1080 design cell (the numbers the
/// map was laid out with, measured at 16:9) turned into screen pixels through the game's own design scale
/// (UIEngineComponent.DesignResolutionScale, by reflection; unknown: the cell fitted whole). TerminalLayout (Core) does the
/// arithmetic and is tested offline. Refreshed once a frame.
/// </summary>
public static class MapLayout
{
    static TerminalLayout _l = TerminalLayout.For(1920, 1080);
    static double _at = -1;
    static object _engine; static System.Reflection.PropertyInfo _scaleProp; static bool _looked;

    /// <summary>The terminal's tab row's bottom, as a fraction of the cell (the map is clipped below it).</summary>
    public const float TabRowBottom = (float)TerminalLayout.TabRowBottom;

    public static TerminalLayout Current
    {
        get
        {
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (now - _at > 0.25) { _at = now; Refresh(); }
            return _l;
        }
    }

    static void Refresh()
    {
        var scr = MapPipeline.ScreenSize;
        double sx = double.NaN, sy = double.NaN;
        try
        {
            if (!_looked || _engine == null)
            {
                _looked = true;
                var session = MapView.SessionForLayout;
                var shared = session != null ? GameUi.SharedUi(session) : null;
                _engine = shared?.GetType().GetField("_ui", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(shared);
                _scaleProp = _engine?.GetType().GetProperty("DesignResolutionScale");
                if (_engine == null) _looked = false;   // (not ready yet: looked for again)
            }
            if (_scaleProp?.GetValue(_engine) is Vector2 v) { sx = v.X; sy = v.Y; }
        }
        catch { _engine = null; _looked = false; }
        _l = TerminalLayout.For(scr.X, scr.Y, sx, sy);
    }

    public static float X(float fx) => (float)Current.X(fx);
    public static float Y(float fy) => (float)Current.Y(fy);
    public static float LenX(float fx) => (float)Current.LenX(fx);
    public static float LenY(float fy) => (float)Current.LenY(fy);
    public static Vector2 P(float fx, float fy) => new Vector2(X(fx), Y(fy));

    /// <summary>The map's open area between the terminal's panels, below its tab row.</summary>
    public static BoundingBox2 OpenArea => new BoundingBox2(P(0.255f, TabRowBottom), P(0.775f, 0.84f));
    public static bool InOpenArea(Vector2 s) { var b = OpenArea; return s.X >= b.Min.X && s.X <= b.Max.X && s.Y >= b.Min.Y && s.Y <= b.Max.Y; }
}
