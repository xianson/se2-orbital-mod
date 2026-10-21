#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// BETWEEN THE SERVER'S TICKS (2026-10-21; the harness's since 2026-10-20 - Dev/GridGen.cs): work queued for the engine's main loop
/// (VRageCore.UpdateQueue - it runs after GameAppComponent.GameUpdate's Server.AfterTick: the server thread idle, the main thread the
/// server scene's own; the game's own server stories resume there - the in-process server thread's synchronization context IS the
/// main thread's, InProcessServerThread.RunSession). EVERY spawn of the mod's (IEntitySpawner.SpawnBundleAsync) and every block added
/// to a grid goes through here: from the client's frame job a spawn raced the server's tick (a native Havok crash, 2026-10-19); inside
/// the server's own job even the async spawn killed the server in the grid's first block commit (IndexOutOfRangeException in
/// InventoryComponent.DynamicMass - a block's inventory not there yet: 2 of 4 harness spawns, 2026-10-20). Tests/SceneTests holds
/// every SpawnBundleAsync / AddBlockToGrid / RequestSpawnTop in the mod to an argument of a BetweenTicks call.
/// </summary>
internal static class MainLoop
{
    /// <summary>The last failure of queued work ("" none).</summary>
    public static string LastError = "";

    /// <summary><paramref name="work"/> run on the engine's main loop between the server's ticks (a throw caught, logged with
    /// <paramref name="tag"/>). False: no queue - not run.</summary>
    internal static bool BetweenTicks(System.Action work, string tag = "between ticks")
    {
        var q = Keen.VRage.Core.VRageCore.Instance?.UpdateQueue;
        if (q == null || work == null) return false;
        q.Enqueue(() =>
        {
            try { work(); }
            catch (System.Exception e) { LastError = tag + ": " + (e.InnerException ?? e).Message; Log.Default?.Warning("[ORBIT] " + tag + ": " + e); }
        });
        return true;
    }
}
