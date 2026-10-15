using SEAerospace;
using SEAerospace.Frames;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE PER-WORLD RESET: statics outlive a world (the mod's assembly stays loaded from one world to the next in a
/// process), so every static that belongs to one world is dropped HERE - one path, not a reset per site. A new
/// per-world static goes into the list below (2026-10-15: the clock, the system, contacts, tombstones, a pending
/// save, the player caches each carried into the next world, each missed by a reset of its own).
///
/// When (both run <see cref="ResetWorld"/>, once per world change):
///  - <see cref="Unloaded"/>: the last server planet leaves the scene (ServerPlanetBeacon) - the world unloads.
///  - <see cref="ServerSession"/>: a server planet joins the scene in a session not the last world's, with no unload
///    seen in between. The new world's saved state (SavedState, read at the planets' [Init], just before) is kept.
/// <see cref="SystemHost"/>.Build resets the system's own state again on every build (<see cref="ResetSystem"/>).
/// </summary>
public static class WorldReset
{
    static readonly object _lock = new object();
    /// <summary>The server session the world's statics belong to (null: reset since, none yet).</summary>
    static object _server;
    /// <summary>Diagnostics: resets so far, and the last one's reason.</summary>
    public static int Count;
    public static string Last = "none";

    /// <summary>A server planet joined the scene (server thread).</summary>
    internal static void ServerSession(Keen.VRage.Core.Game.Systems.Session session)
    {
        bool reset;
        lock (_lock) { if (session == null || ReferenceEquals(session, _server)) return; reset = _server != null; _server = session; }
        if (reset) ResetWorld("another world's planets (its unload not seen)", unloading: false);
    }

    /// <summary>The last server planet left the scene: the world unloaded (server thread).</summary>
    internal static void Unloaded()
    {
        lock (_lock) _server = null;
        ResetWorld("the world unloaded", unloading: true);
    }

    /// <summary>Everything of the last world. <paramref name="unloading"/>: at its unload (nothing of the next world exists yet).</summary>
    static void ResetWorld(string why, bool unloading)
    {
        Count++; Last = why;
        Log.Default?.Info($"[ORBIT] world reset: {why}");
        // the system: none until the next world's planets settle, so the server stops stepping this one's frames (it gates on
        // Built; only the client's EnsureBuilt reset it, ~2 s into the next world); the universe clock and warp from zero (a
        // world without a saved state started at the last one's time)
        Step("system", SystemHost.ResetWorld);
        Step("server clock", ServerFrames.ResetClock);
        Step("warp bill", WarpBill.ResetWorld);
        Step("system state", () => ResetSystem(build: false));
    }

    /// <summary>The system's state: the frames' server state, sites, rocks, entry, our edits; <paramref name="build"/>: for a
    /// build (SystemHost.Build, the client thread, every build - as it always did) the client's map and plans and the pinned
    /// cells too (the world reset, on the server thread, leaves those to the next build).</summary>
    internal static void ResetSystem(bool build)
    {
        AsteroidFrames.Reset();   // the belts' asteroid frames are built afresh for this system
        // (the sites and the frames' server state: another world's - under the lock, the server reads them)
        lock (ServerFrames.FramesLock) { EncounterFrames.ResetWorld(); ServerFrames.ResetWorld(); }
        // (a new world: the last one's berth rules and entry state must not apply - the planet slots are reserved after this,
        //  before this world's zones are set)
        BerthAllocator.IsForbidden = null; BerthAllocator.IsClear = null;
        EntryHost.ResetWorld();
        GridEdits.Clear();   // (what we wrote into the last world's grids: OrbitalApi.TryGetGridEdits)
        if (!build) return;
        Maneuvers.ResetWorld(); GameMap.ResetWorld(); CleanMap.Reset(); CleanMap.ResetView(); MapGlobes.ResetWorld();
        VoxelBerthRegistry.Clear();
    }

    // (each step on its own: one that throws leaves the rest done - not a per-tick path)
    static void Step(string name, Action a)
    {
        try { a(); }
        catch (Exception ex) { FrameHost.Fault("world reset: " + name, ex); }
    }
}
