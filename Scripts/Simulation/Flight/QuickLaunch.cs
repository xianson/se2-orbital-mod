#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// QUICK LAUNCH - the player feature (creative only, Ctrl+T seated: lifted over the ground, level, at cruise speed) is the
/// AERO MOD'S since 2026-10-18 (the user: "ctrl+T should be in aero creative"; this mod's 1.0.1 had it - its key, settings,
/// toasts and rules went there: Aerodynamics Mod Launch/QuickLaunch*.cs). What stays here:
///  - the move, for aero: OrbitalApi.TryQuickMove (ServerFrames.QuickMove - this mod's safe move path and space border, so
///    no other mod teleports a grid behind this mod's frames);
///  - the DEV harness key "quicklaunch" (dev flag only): the player's path, asked of the aero mod (AeroApi.RequestQuickLaunch,
///    found by AeroLink - the last loaded copy); its answer is aero's toast and aeroget QuickLaunch.Status.
/// The dev fly test (FlyTest, harness key "flytest") is this mod's own level-flight move, unchecked.
/// </summary>
public static class QuickLaunch
{
    /// <summary>The harness key's last outcome (DevHarness reads statics by name).</summary>
    public static string Status = "-";

    static System.Reflection.MethodInfo _request; static int _gen = -1;

    internal static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!OrbitalConfig.DevHarness) return;
        if (!MapInput.DevKeys.Remove("quicklaunch")) return;
        int gen = AeroLink.Generation;
        if (gen != _gen)
        {
            _gen = gen;
            // (explicit parameter types: an aero overload added later must not make the lookup ambiguous - API.md)
            _request = AeroLink.Find("AeroMod.AeroApi")?.GetMethod("RequestQuickLaunch", System.Type.EmptyTypes);
        }
        if (_request == null) { Status = "no aero mod with AeroApi.RequestQuickLaunch (version 9)"; return; }
        try { _request.Invoke(null, null); Status = "asked the aero mod (its answer: aeroget QuickLaunch.Status)"; }
        catch (System.Exception e) { Status = "aero's RequestQuickLaunch threw: " + (e.InnerException ?? e).GetType().Name; }
    }
}
