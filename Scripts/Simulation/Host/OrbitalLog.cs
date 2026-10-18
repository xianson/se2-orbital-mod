#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// LOG SWITCHES (release prep, 2026-10-17: a player's log carried ~430 Orbital lines an hour, half of them one merge
/// refusal repeated every minute). Always logged: the startup summary (system built, each body and beacon, speed caps,
/// prewarm, settings, the restore summary), warnings and real failures ([ORBIT-FAULT], "... failed", lost save data,
/// [ORBIT-INV] violations, the [ORBIT-HAVOK] cluster audit's mismatches). Behind these switches - each ON only where
/// the dev flag is (<see cref="OrbitalConfig.DevHarness"/>), so tools/harness/*.sh, which always run with it, still
/// see every line they grep - the narration: what a frame, an encounter, a rock or a proxy did, one line per event.
/// Each is a plain static: a harness can flip one by reflection like any other knob.
/// </summary>
public static class OrbitalLog
{
    /// <summary>[ORBIT-FRAME] frame events (FrameHost.Event): merges, splits, stows, anchors, arrivals, entries, SOI changes,
    /// warp drops, each restored frame, warm berths; and the [ORBIT] crossing fade / observer lines.</summary>
    public static bool Frames = OrbitalConfig.DevHarness;
    /// <summary>[ORBIT-ENC] encounter sites, spawns, joins and dissolves (EncounterFrames.Event, SpawnGuard).</summary>
    public static bool Encounters = OrbitalConfig.DevHarness;
    /// <summary>[ORBIT-ROIDS] asteroid belt events and the generator's deletions (not its warnings).</summary>
    public static bool Asteroids = OrbitalConfig.DevHarness;
    /// <summary>[ORBIT] setup and render detail: each prefab injected, each planet's frame / proxy / real-planet state, rings,
    /// the sectorless map's steps. A failure among them is logged anyway.</summary>
    public static bool Setup = OrbitalConfig.DevHarness;
    /// <summary>[ORBIT-CONTACT] each newly tracked contact (the player sees the toast).</summary>
    public static bool Contacts = OrbitalConfig.DevHarness;
    /// <summary>[HITCH] server tick gaps and slow frame ticks.</summary>
    public static bool Perf = OrbitalConfig.DevHarness;
}
