using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Raw mouse buttons for the map's own editors (maneuver nodes). Mods cannot register key bindings
/// (those live in Keen.VRage.Input), but the platform's input devices can be polled directly: the
/// engine entity carries IPlatformInput, whose mouse device reports button states. Edges (pressed /
/// released this frame) are tracked here. Harness overrides stand in for a real mouse in tests.
/// </summary>
public static class MapInput
{
    private static IInputDevice _mouse, _keyboard;
    private static readonly Dictionary<int, bool> _keys = new Dictionary<int, bool>(), _keysPrev = new Dictionary<int, bool>();
    private static double _lastLookup = -10;
    private static bool _l, _r;
    public static bool Left, LeftPressed, LeftReleased, Right, RightPressed, Middle;
    /// <summary>The right button is orbiting the map camera: its release is not a click.</summary>
    public static bool RightDragged;
    public static string Status = "no mouse";

    /// <summary>DEV: button overrides (null = the real device).</summary>
    public static bool? DevLeft, DevRight;
    private static bool _devRightClick;
    public static void DevRightClick() => _devRightClick = true;
    private static int _devDbl;
    /// <summary>DEV: a left double-click (down, up, down, up on successive frames).</summary>
    public static void DevDoubleClick() => _devDbl = 1;

    /// <summary>Once per frame, before anything reads the buttons.</summary>
    public static void Poll()
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if ((_mouse == null || _keyboard == null) && now - _lastLookup > 2.0)
        {
            _lastLookup = now;
            try
            {
                var input = VRageCore.Instance?.Engine?.Single<IPlatformInput>();
                if (input != null)
                    foreach (var dev in input.ConnectedDevices)
                    {
                        if (_mouse == null && dev.Class.GenericClasses.Contains(GenericDeviceClass.Mouse)) { _mouse = dev; Status = "mouse: " + dev.Name; }
                        if (_keyboard == null && dev.Class.GenericClasses.Contains(GenericDeviceClass.Keyboard)) _keyboard = dev;
                    }
            }
            catch (Exception e) { Status = "no mouse: " + e.Message; }
        }
        bool l = false, r = false, mid = false;
        try { if (_mouse != null) { l = _mouse.GetDigitalState(MouseInputs.Left); r = _mouse.GetDigitalState(MouseInputs.Right); mid = _mouse.GetDigitalState(MouseInputs.Middle); } }
        catch { _mouse = null; }
        if (DevLeft.HasValue) l = DevLeft.Value;
        if (_devDbl > 0) { l = _devDbl % 2 == 1; if (++_devDbl > 5) _devDbl = 0; }
        if (DevRight.HasValue) r = DevRight.Value;
        if (_devRightClick) { r = !_r; _devRightClick = _r; }   // one frame down, then up
        LeftPressed = l && !_l; LeftReleased = !l && _l;
        // A right click is a release without an orbit drag (the press may start one).
        RightPressed = !r && _r && !RightDragged;
        if (!r) RightDragged = false;
        Left = l; Right = r; Middle = mid;
        _l = l; _r = r;
        _keysPrev.Clear(); foreach (var kv in _keys) _keysPrev[kv.Key] = kv.Value;
        _keys.Clear();
    }

    /// <summary>DEV: keys pressed by the harness this frame.</summary>
    public static readonly HashSet<string> DevKeys = new HashSet<string>();

    /// <summary>A key that went down this frame (polled on first ask each frame).</summary>
    public static bool KeyPressed(DigitalInput key)
    {
        int id = key.GetHashCode();
        if (!_keys.TryGetValue(id, out bool down))
        {
            try { down = _keyboard != null && _keyboard.GetDigitalState(key); } catch { _keyboard = null; }
            _keys[id] = down;
        }
        _keysPrev.TryGetValue(id, out bool was);
        return down && !was;
    }
}
