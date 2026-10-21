using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Library.UI.PropertyInspector;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// PROTOTYPE (untested in game): the attitude holds as cockpit toolbar actions (the user, 2026-10-08: "it should just be an
/// action, not a keybind"). SE2 builds a block's toolbar actions from the [Button] methods of its terminal panel models
/// ([BlockDetailProvider] classes anchored by a [Component] field - BlockDetailProviderIndexer); a model anchored to the
/// CockpitComponent puts "Hold prograde" ... in every cockpit's G-menu, and the number key runs the method ON THE SERVER
/// (DetailModelButtonToolbarAction.TryRun). Single player / listen host: the pilot is the local player, so the client's
/// hold is set directly.
/// RISKS to test first: the action stores the type by FullName and resolves it with Type.GetType (no assembly) - a mod
/// type needs the TypeResolve hook (HoldActions.Hook); and vanilla models carry [Replicate] code a mod cannot generate -
/// if the panel's replication refuses this one, cockpit terminals could break.
/// </summary>
// DISABLED 2026-10-08: CRASHED THE GAME. Opening a cockpit's control panel replicates every detail model; a mod type has no
// replication registration (vanilla models carry generated [Replicate] code mods cannot make): KeyNotFoundException in
// ObservableObjectReplicationSessionComponent.TranslateLocalToRemote from StreamedControlPanelSessionComponent.
// AttachBlockDetailModels - an unobserved exception, fatal. Never re-add [BlockDetailProvider] here.
// SE2 4.0 runs the serializer generators on mod scripts: a partial BlockDetailModel gets a generated named-object
// serializer whose code needs System.ObjectModel (INotifyPropertyChanged), which mod compiles do not reference (CS0012).
// [NoSerialize] turns those generators off for the model - mod models were never serialized (DetailModelPairing).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class OrbitalHoldDetailModel : BlockDetailModel
{
    [Component] private CockpitComponent _cockpit;

    [Button(null)] [Label("Hold prograde")] public void HoldPrograde() => HoldActions.Request(AttitudeHold.Mode.Prograde);
    [Button(null)] [Label("Hold retrograde")] public void HoldRetrograde() => HoldActions.Request(AttitudeHold.Mode.Retrograde);
    [Button(null)] [Label("Hold normal")] public void HoldNormal() => HoldActions.Request(AttitudeHold.Mode.Normal);
    [Button(null)] [Label("Hold anti-normal")] public void HoldAntiNormal() => HoldActions.Request(AttitudeHold.Mode.AntiNormal);
    [Button(null)] [Label("Hold radial out")] public void HoldRadialOut() => HoldActions.Request(AttitudeHold.Mode.RadialOut);
    [Button(null)] [Label("Hold radial in")] public void HoldRadialIn() => HoldActions.Request(AttitudeHold.Mode.RadialIn);
    [Button(null)] [Label("Hold level")] public void HoldLevel() => HoldActions.Request(AttitudeHold.Mode.Level);
    [Button(null)] [Label("Hold off")] public void HoldOff() => HoldActions.Request(AttitudeHold.Mode.Off);
}

public static class HoldActions
{
    /// <summary>The last action pressed (server), taken by the client tick: the same mode again turns the hold off.</summary>
    static volatile int _pending = -1;
    public static int Pressed;

    public static void Request(AttitudeHold.Mode m) { _pending = (int)m; Pressed++; }

    /// <summary>Client tick: a pressed action applied (a toggle: the hold already on that mode goes off).</summary>
    public static void ClientTick()
    {
        int p = System.Threading.Interlocked.Exchange(ref _pending, -1);
        if (p < 0) return;
        var m = (AttitudeHold.Mode)p;
        AttitudeHold.Set(m != AttitudeHold.Mode.Off && AttitudeHold.Current == m ? AttitudeHold.Mode.Off : m);   // (a mode set is selected too)
    }

    static int _hooked;
    static ResolveEventHandler _resolve;
    /// <summary>The AppDomain slot naming the copy that answers (the newest hooked).</summary>
    public const string Slot = "OrbitalMod.HoldActions.TypeOwner";
    static readonly System.Reflection.Assembly Own = typeof(OrbitalHoldDetailModel).Assembly;
    [ThreadStatic] static bool _inside;
    /// <summary>The toolbar resolves an action's model type by its full name alone (BlockToolbarServerComponent.TryGetDetailModelType:
    /// Type.GetType(name) - a mod type never found): point it at this assembly for any name in this mod's namespace it has (every
    /// panel model of ours - the hold actions', the radar's - 2026-10-21; before only the hold model's name). Unhooked when the game
    /// unloads this mod's project (ScriptResultsCleanup.OwnProjectUnloading, 2026-10-19). NEWEST COPY WINS (2026-10-21): the first
    /// handler that answers wins and a stale copy (world A's) stays loaded - each copy writes itself into one AppDomain slot when it
    /// hooks and answers only while it is the slot's (the Aerodynamics Mod's Blocks/ToolbarTypeResolve.cs is the same rule).</summary>
    public static void Hook()
    {
        if (System.Threading.Interlocked.Exchange(ref _hooked, 1) != 0) return;   // (once per load of this assembly: the load's post-processor, else the planets' tick)
        _resolve = OnTypeResolve;
        AppDomain.CurrentDomain.SetData(Slot, Own);
        AppDomain.CurrentDomain.TypeResolve += _resolve;
        ScriptResultsCleanup.OwnProjectUnloading += Unhook;
    }

    static System.Reflection.Assembly OnTypeResolve(object sender, ResolveEventArgs e)
    {
        string n = e?.Name;
        if (n == null || !n.StartsWith("OrbitalMod.", StringComparison.Ordinal) || _inside) return null;
        _inside = true;
        try { return ReferenceEquals(AppDomain.CurrentDomain.GetData(Slot), Own) && Own.GetType(n, false) != null ? Own : null; }
        catch { return null; }   // (never a throw into the game's type lookup)
        finally { _inside = false; }
    }

    static void Unhook()
    {
        var r = _resolve; _resolve = null;
        if (r == null) return;
        AppDomain.CurrentDomain.TypeResolve -= r;
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData(Slot), Own)) AppDomain.CurrentDomain.SetData(Slot, null);
    }
}
