using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV (2026-10-26, the shared slot broker - Aerodynamics Mod/tools/shared/slot.py, SLOTS_DESIGN.md): records when the
/// USER last did anything in the game - any key, button, mouse movement or gamepad input on any connected device
/// (IInputDevice.HasChanged) - into %LOCALAPPDATA%\OrbitalModBuild\user_input.stamp. slot.py reads it: while the user had
/// input in the last 2 min, no session restarts or closes the game. Input the harness injects (key.ps1 / mouse.ps1 stamp
/// injected.stamp just before sending) is not the user's; harness cmd.txt commands never touch the devices.
/// Only where the dev harness runs (OrbitalConfig.DevHarness): a player's install does nothing here.
/// </summary>
public static class UserInput
{
    private static IInputDevice[] _devices = Array.Empty<IInputDevice>();
    private static double _lastLookup = -10, _lastWrite = -10, _pendingInput = -1;
    private static string _dir;
    public static double LastInputUnix;
    public static string Status = "-";

    /// <summary>Client frame (MapInput.Poll). Cheap: one flag per device; the file is written at most every 5 s, off the tick.</summary>
    public static void Poll()
    {
        if (!OrbitalConfig.DevHarness) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _lastLookup > 10.0)   // (devices come and go: a gamepad plugged in)
        {
            _lastLookup = now;
            try
            {
                var input = VRageCore.Instance?.Engine?.Single<IPlatformInput>();
                if (input != null) { _devices = input.ConnectedDevices.ToArray(); Status = $"{_devices.Length} device(s)"; }
            }
            catch (Exception e) { Status = "no input: " + e.Message; }
        }
        bool changed = false;
        for (int i = 0; i < _devices.Length; i++)
        {
            try { if (_devices[i].HasChanged) { changed = true; break; } }
            catch { _lastLookup = -10; }
        }
        if (changed) _pendingInput = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (_pendingInput > 0 && now - _lastWrite > 5.0)
        {
            _lastWrite = now; double t = _pendingInput; _pendingInput = -1;
            System.Threading.Tasks.Task.Run(() => Write(t));
        }
    }

    private static void Write(double t)
    {
        try
        {
            _dir ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitalModBuild");
            string inj = System.IO.Path.Combine(_dir, "injected.stamp");
            // (the harness's own SendInput: stamped just before it sends; input within 3 s of the stamp isn't the user's)
            if (System.IO.File.Exists(inj) && Math.Abs((System.IO.File.GetLastWriteTimeUtc(inj) - DateTime.UnixEpoch).TotalSeconds - t) < 3) return;
            System.IO.Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(_dir, "user_input.stamp"), t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            LastInputUnix = t;
        }
        catch (Exception e) { Status = "stamp failed: " + e.Message; }
    }
}
