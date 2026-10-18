using Keen.VRage.Core;
using Keen.VRage.Core.Input;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// FLY TEST (DEV; 2026-10-12, the user: "a way to tp me to an altitude of 1km and at cruise speed level, like a test
/// hotkey"). Seated, Ctrl+T: your ship (its joined grids with it) straight up or down to AltitudeM over the ground under it
/// (the aero mod's ground ray; sea level when it has none), turned level - the cockpit's nose horizontal on its heading,
/// its top up - and flying along that nose at cruise speed: the aero mod's top speed that way at CruiseThrottle (the
/// build boxes' cruise; 150 m/s when it has none). Planet-fixed velocity, so that is your airspeed (wind aside).
/// Ctrl+T and the harness key "flytest" both need the dev flag (release prep, 2026-10-17: it was live in every game - a
/// seated player's Ctrl+T teleported the ship; the developer's machine has the flag, so it works there as before).
/// </summary>
public static class FlyTest
{
    public static double AltitudeM = 1000;
    public static float CruiseThrottle = 0.75f, FallbackSpeed = 150f;
    public static string Status = "-";

    internal static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!OrbitalConfig.DevHarness) return;
        bool ctrlT = MapInput.KeyPressed(KeyboardInputs.T) && MapInput.KeyDown(KeyboardInputs.Control);
        if (!(ctrlT | MapInput.DevKeys.Remove("flytest"))) return;
        var grid = FrameHost.SeatGrid; var seat = FrameHost.SeatBlock;
        if (!FrameHost.Seated || grid == null || seat == null || FrameHost.SeatGridServerId == 0) { Status = "not seated in a ship"; return; }
        // the cockpit's nose and top in its grid's axes (the server's copy has the same)
        var gq = grid.Data.GetWorldTransform().Orientation; var sq = seat.Data.GetWorldTransform().Orientation;
        var inv = Quaternion.Inverse(gq);
        Vector3 nose = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, sq), inv);
        Vector3 top = Vector3.Transform(Vector3.Transform(Vector3.UnitY, sq), inv);
        ServerFrames.FlyTest.Enqueue((FrameHost.SeatGridServerId, nose, top));
        Status = "queued";
    }
}
