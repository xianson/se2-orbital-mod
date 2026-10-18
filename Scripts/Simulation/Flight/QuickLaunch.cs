using Keen.VRage.Core.Input;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// QUICK LAUNCH (2026-10-18, the user: "if the game is in creative, allow quick launch in cockpit as an actual keybind").
/// Seated, the key (Ctrl+T by default: QuickLaunch.Key / QuickLaunch.Modifier, client settings) asks the server to lift
/// your ship (its joined grids with it) to QuickLaunch.AltitudeM over the ground under it, turned level on its heading and
/// flying along the cockpit's nose at QuickLaunch.SpeedMps (0: cruise - the aero mod's top speed at 3/4 throttle, 150 m/s
/// without it). The server decides (ServerFrames.DoQuickLaunch, QuickLaunchRules): the world allows it
/// (QuickLaunch.Creative, a world setting), the pilot is in creative, the ship is the one it sees the player seated in,
/// dynamic, near a planet's surface; it moves the ship through MoveGrid (a long move: the teleport phase with its joined
/// set, statics refused - HavokMoves). Its answer comes back as a toast (PendingSay: written by the server, said here).
/// The dev fly test (FlyTest, harness key "flytest") is the same move without the checks; its Ctrl+T is now this.
/// </summary>
public static class QuickLaunch
{
    // ── the settings (OrbitalSettings: QuickLaunch.*) ──
    /// <summary>World: quick launch allowed in creative (the host can turn it off). Survival never has it.</summary>
    public static bool Creative = true;
    /// <summary>World: m over the ground (or, with no ground ray, over where the ship is) - clamped under the planet's space.</summary>
    public static double AltitudeM = 1000;
    /// <summary>World: the launch speed along the nose (m/s); 0: the ship's cruise speed.</summary>
    public static double SpeedMps = 0;
    /// <summary>Client: the key - 0 off, 1..26 the letters A..Z (Keys).</summary>
    public static int Key = 20;
    /// <summary>Client: the key's modifier - 0 none, 1 Ctrl, 2 Shift, 3 Alt.</summary>
    public static int Modifier = 1;

    /// <summary>The cruise throttle and the speed without the aero mod (FlyTest's).</summary>
    public const float CruiseThrottle = 0.75f, FallbackSpeed = 150f;

    /// <summary>The server's answer, said by the next client tick (null: none).</summary>
    public static volatile string PendingSay;
    public static string Status = "-";

    static DigitalInput[] _keys;
    static double _lastAsk = -10;

    /// <summary>The letter keys, in QuickLaunch.Key's order (1 = A).</summary>
    static DigitalInput[] Keys() => _keys ??= new[]
    {
        KeyboardInputs.A, KeyboardInputs.B, KeyboardInputs.C, KeyboardInputs.D, KeyboardInputs.E, KeyboardInputs.F, KeyboardInputs.G,
        KeyboardInputs.H, KeyboardInputs.I, KeyboardInputs.J, KeyboardInputs.K, KeyboardInputs.L, KeyboardInputs.M, KeyboardInputs.N,
        KeyboardInputs.O, KeyboardInputs.P, KeyboardInputs.Q, KeyboardInputs.R, KeyboardInputs.S, KeyboardInputs.T, KeyboardInputs.U,
        KeyboardInputs.V, KeyboardInputs.W, KeyboardInputs.X, KeyboardInputs.Y, KeyboardInputs.Z,
    };

    /// <summary>The binding went down this frame: its key pressed, its modifier held (none: no Ctrl, Shift or Alt held -
    /// so Ctrl+T bound elsewhere never also fires a bare T).</summary>
    static bool BindingPressed()
    {
        var keys = Keys();
        int k = Key;
        if (k < 1 || k > keys.Length || !MapInput.KeyPressed(keys[k - 1])) return false;
        bool ctrl = MapInput.KeyDown(KeyboardInputs.Control), shift = MapInput.KeyDown(KeyboardInputs.Shift), alt = MapInput.KeyDown(KeyboardInputs.Alt);
        switch (Modifier)
        {
            case 0: return !ctrl && !shift && !alt;
            case 1: return ctrl;
            case 2: return shift;
            case 3: return alt;
            default: return false;
        }
    }

    /// <summary>The binding as a player reads it ("Ctrl+T"; "off").</summary>
    public static string BindingText()
    {
        if (Key < 1 || Key > 26) return "off";
        string key = ((char)('A' + Key - 1)).ToString();
        return Modifier switch { 1 => "Ctrl+" + key, 2 => "Shift+" + key, 3 => "Alt+" + key, _ => key };
    }

    /// <summary>A new world: the last one's unanswered ask and unsaid answer dropped (WorldReset).</summary>
    internal static void ResetWorld()
    {
        while (ServerFrames.QuickLaunchRequests.TryDequeue(out _)) { }
        PendingSay = null; Status = "-"; _lastAsk = -10;
    }

    internal static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        var say = PendingSay;
        if (say != null) { PendingSay = null; Status = say; GameUi.Toast(session, "quicklaunch", "Quick launch", say, 3); }
        bool dev = MapInput.DevKeys.Remove("quicklaunch");   // (the harness: the player's path, the key's checks skipped)
        if (!dev)
        {
            if (!FrameHost.Seated || !BindingPressed()) return;
            if (GameUi.TopScreenNeedingInput(session) != null) return;   // (not while typing: a dialog, a terminal, a menu)
        }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _lastAsk < 1.0) return;   // (one ask a second: a held key or a double tap moves the ship once)
        _lastAsk = now;
        var grid = FrameHost.SeatGrid; var seat = FrameHost.SeatBlock;
        if (!FrameHost.Seated || grid == null || seat == null || FrameHost.SeatGridServerId == 0)
        { Status = QuickLaunchRules.Say(QuickLaunchRules.Verdict.NotSeated); GameUi.Toast(session, "quicklaunch", "Quick launch", Status, 3); return; }
        // the cockpit's nose and top in its grid's axes (the server's copy has the same; the server checks the seat itself)
        var gq = grid.Data.GetWorldTransform().Orientation; var sq = seat.Data.GetWorldTransform().Orientation;
        var inv = Quaternion.Inverse(gq);
        Vector3 nose = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, sq), inv);
        Vector3 top = Vector3.Transform(Vector3.Transform(Vector3.UnitY, sq), inv);
        ServerFrames.QuickLaunchRequests.Enqueue((FrameHost.SeatGridServerId, nose, top));
        Status = "asked";
    }
}
