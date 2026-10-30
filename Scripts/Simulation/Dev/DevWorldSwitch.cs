#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;

namespace OrbitalMod;

/// <summary>
/// DEV (harness, 2026-10-20: the world-switch check, RUNBOOK "world switch"). `worldload &lt;save folder&gt;`: what a player does
/// through the menus, in this one game process - Escape -> Exit to main menu (the in-game menu's own call:
/// IGameApp.Disconnect, InGameMenuScreenViewModel.RequestReturnToMainMenu), then, from the main menu, Load (the main menu's own
/// call: IGameApp.LoadGame(WorldLoadArgs { SaveId }), MainMenuComponent.LoadGame). Both on the engine's main loop queue, as the menus call
/// them; the load DelaySeconds after the exit (the main menu up, this world's projects unloaded - the path the world-switch fix
/// is for). The world loaded is the next world's copy of this mod (its own harness answers again). Nothing is saved.
/// Everything by reflection (SharedUIComponent._gameApp, the game's IGameApp, WorldLoadArgs, ContainerId).
///
/// `worldreload &lt;save folder&gt;` (2026-10-30, the dev loop: edit scripts, reload, no game restart): a load of a world with the
/// SAME mods keeps them loaded - the game's world data (GameDataLoaderComponent) is swapped only when the required projects
/// differ from <c>_activeProjects</c>, so `worldload` of the same world runs the old compiled code. Reload clears
/// <c>_activeProjects</c> (what the game's own ClearStateUponFailure does after a failed load) and loads: the game's
/// project-swap path then exits to the menu itself, unloads every mod (Reload/ScriptResultsCleanup takes each out of the
/// script results) and compiles each again from its Scripts folder.
/// </summary>
public static class DevWorldSwitch
{
    public static double DelaySeconds = 12;
    /// <summary>Logged by every worldreload: a test edits it on disk between reloads and finds the new value in the log - the
    /// reload ran freshly compiled code.</summary>
    const string CodeMarker = "A";
    public static string Status = "-";

    public static string Load(Keen.VRage.Core.Game.Systems.Session session, string save) => Switch(session, save, recompile: false);

    public static string Reload(Keen.VRage.Core.Game.Systems.Session session, string save) => Switch(session, save, recompile: true);

    static string Switch(Keen.VRage.Core.Game.Systems.Session session, string save, bool recompile)
    {
        if (string.IsNullOrWhiteSpace(save)) return (recompile ? "worldreload" : "worldload") + " <save folder>";
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var shared = GameUi.SharedUi(session);
        var app = shared?.GetType().GetField("_gameApp", F)?.GetValue(shared);
        if (app == null) return "no IGameApp (SharedUIComponent._gameApp)";
        Type iApp = null, argsT = null, idT = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            iApp ??= asm.GetType("Keen.Game2.Simulation.RuntimeSystems.IGameApp");
            argsT ??= asm.GetType("Keen.Game2.Simulation.RuntimeSystems.WorldLoadArgs");
            idT ??= asm.GetType("Keen.VRage.Library.Filesystem.StorageManagers.ContainerId");
        }
        var disconnect = iApp?.GetMethod("Disconnect", Type.EmptyTypes);
        var load = argsT == null ? null : iApp?.GetMethod("LoadGameAsync", new[] { argsT });
        var idCtor = idT?.GetConstructor(new[] { typeof(string) });
        var saveField = argsT?.GetField("SaveId");
        if (disconnect == null || load == null || idCtor == null || saveField == null)
            return $"the game's API moved (IGameApp {iApp != null}, Disconnect {disconnect != null}, LoadGameAsync {load != null}, ContainerId(string) {idCtor != null}, SaveId {saveField != null})";
        object args = Activator.CreateInstance(argsT);
        saveField.SetValue(args, idCtor.Invoke(new object[] { save }));   // (boxed into ContainerId? by SetValue)
        var queue = Keen.VRage.Core.VRageCore.Instance?.UpdateQueue;
        if (queue == null) return "no engine update queue (VRageCore.UpdateQueue)";
        if (recompile)
        {
            // (GameAppComponent.GameData -> GameDataLoaderComponent._activeProjects: null = "no world data matches"; the load
            //  then transitions to the menu and swaps the world data itself - no Disconnect of ours)
            var gameData = app.GetType().GetProperty("GameData", F)?.GetValue(app);
            var active = gameData?.GetType().GetField("_activeProjects", F);
            if (active == null) return $"the game's API moved (GameAppComponent.GameData {gameData != null}, GameDataLoaderComponent._activeProjects {active != null})";
            Status = $"'{save}' loading with every mod compiled again";
            Log.Default?.Info($"[ORBIT] worldreload: '{save}' with every mod unloaded and compiled again (dev harness; code marker {CodeMarker})");
            queue.Enqueue(() =>
            {
                try { active.SetValue(gameData, null); load.Invoke(app, new[] { args }); Status = $"reloading '{save}'"; }
                catch (Exception e) { Status = "reload failed: " + (e.InnerException ?? e).Message; }
            });
            return Status;
        }
        double delay = DelaySeconds;
        Status = $"exit to the main menu, then '{save}' in {delay:F0} s";
        Log.Default?.Info($"[ORBIT] worldload: exit to the main menu, then load '{save}' in {delay:F0} s (dev harness)");
        // (on the engine's main loop queue, VRageCore.UpdateQueue - the thread's own SynchronizationContext, which the game's
        //  transitions await on (GameAppComponent.NextFrame: SynchronizationContext.Current.Queue). From Avalonia's dispatcher
        //  (UiThread.Post) that context was not current: Disconnect threw a NullReferenceException in NextFrame and the game's
        //  crash handler stopped it, in game 2026-10-20. Never from the harness's scene job: there Post runs it at once.)
        queue.Enqueue(() => { try { disconnect.Invoke(app, null); } catch (Exception e) { Status = "exit failed: " + (e.InnerException ?? e).Message; } });
        queue.Enqueue(() =>
        {
            try { load.Invoke(app, new[] { args }); Status = $"loading '{save}'"; }
            catch (Exception e) { Status = "load failed: " + (e.InnerException ?? e).Message; }
        }, TimeSpan.FromSeconds(delay));
        return Status;
    }
}
