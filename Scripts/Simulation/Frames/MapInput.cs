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
    private static IInputDevice _mouse;
    private static double _lastLookup = -10;
    private static bool _l, _r;
    public static bool Left, LeftPressed, LeftReleased, Right, RightPressed;
    public static string Status = "no mouse";

    /// <summary>DEV: button overrides (null = the real device).</summary>
    public static bool? DevLeft, DevRight;
    private static bool _devRightClick;
    public static void DevRightClick() => _devRightClick = true;

    /// <summary>Once per frame, before anything reads the buttons.</summary>
    public static void Poll()
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_mouse == null && now - _lastLookup > 2.0)
        {
            _lastLookup = now;
            try
            {
                var input = VRageCore.Instance?.Engine?.Single<IPlatformInput>();
                if (input != null)
                    foreach (var dev in input.ConnectedDevices)
                        if (dev.Class.GenericClasses.Contains(GenericDeviceClass.Mouse)) { _mouse = dev; Status = "mouse: " + dev.Name; break; }
            }
            catch (Exception e) { Status = "no mouse: " + e.Message; }
        }
        bool l = false, r = false;
        try { if (_mouse != null) { l = _mouse.GetDigitalState(MouseInputs.Left); r = _mouse.GetDigitalState(MouseInputs.Right); } }
        catch { _mouse = null; }
        if (DevLeft.HasValue) l = DevLeft.Value;
        if (DevRight.HasValue) r = DevRight.Value;
        if (_devRightClick) { r = !_r; _devRightClick = _r; }   // one frame down, then up
        LeftPressed = l && !_l; LeftReleased = !l && _l;
        RightPressed = r && !_r;
        Left = l; Right = r;
        _l = l; _r = r;
    }
}
