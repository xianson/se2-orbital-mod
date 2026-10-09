using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Library.UI.PropertyInspector;

#pragma warning disable
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
public class OrbitalHoldDetailModel : BlockDetailModel
{
    [Component] private CockpitComponent _cockpit;

    [Button(null)] [Label("Hold prograde")] public void HoldPrograde() => HoldActions.Request(AttitudeHold.Mode.Prograde);
    [Button(null)] [Label("Hold retrograde")] public void HoldRetrograde() => HoldActions.Request(AttitudeHold.Mode.Retrograde);
    [Button(null)] [Label("Hold normal")] public void HoldNormal() => HoldActions.Request(AttitudeHold.Mode.Normal);
    [Button(null)] [Label("Hold anti-normal")] public void HoldAntiNormal() => HoldActions.Request(AttitudeHold.Mode.AntiNormal);
    [Button(null)] [Label("Hold radial out")] public void HoldRadialOut() => HoldActions.Request(AttitudeHold.Mode.RadialOut);
    [Button(null)] [Label("Hold radial in")] public void HoldRadialIn() => HoldActions.Request(AttitudeHold.Mode.RadialIn);
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
        AttitudeHold.Set(m != AttitudeHold.Mode.Off && AttitudeHold.Current == m ? AttitudeHold.Mode.Off : m);
    }

    static bool _hooked;
    /// <summary>The toolbar resolves an action's model type by its full name alone: point it at this assembly.</summary>
    public static void Hook()
    {
        if (_hooked) return; _hooked = true;
        AppDomain.CurrentDomain.TypeResolve += (s, e) =>
            e.Name == typeof(OrbitalHoldDetailModel).FullName ? typeof(OrbitalHoldDetailModel).Assembly : null;
    }
}
