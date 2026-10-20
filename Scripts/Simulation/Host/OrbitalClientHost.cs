using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE CLIENT'S SESSION TICK (2026-10-19, as the Aerodynamics Mod's AeroClientHost): injected into the client's render
/// camera prefab (the one entity with the game's CameraComponent - the client scene always has it: on foot, seated, in
/// spectator, with no planet and no grid anywhere). Its job runs the client's session-level work every frame
/// (OrbitalClientHost.Frame). Before, that work ran only from the client planets' job (PlanetFrameComponent -&gt;
/// FrameHost.Tick): in a world with no planet the Orbital Mod alone showed no section on Options &gt; Mods and its settings
/// files were never read.
/// </summary>
public partial class OrbitalClientHostComponent : Component, IInSceneListener
{
    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    [After(typeof(RenderSubmissionBegin))]
    private class OnOrbitalClientFrame : JobGroup;

    [OnOrbitalClientFrame]
    [MustHave(typeof(OrbitalClientHostComponent))]
    private static void ClientFrameJob(OrbitalClientHostComponent c)
    {
        Session session = null;
        try { session = c.Entity?.GetSession(); } catch { }
        OrbitalClientHost.Frame(session, fromHost: true);
    }
}

/// <summary>
/// The client's session-level work, once a frame, from the render camera (OrbitalClientHostComponent) - not from a planet:
/// the settings (SawClient, the files' poll ~2 s) and the Options screen's Mods tab (Ui/SettingsTab.cs: one caller per 250 ms).
/// Fallback: the planets' FrameHost.Tick calls Frame too (fromHost: false); it does the work only while the host has not
/// ticked for a second (the camera prefab not injected - a game update renamed something): the old behaviour, never twice.
/// Left to FrameHost.Tick (a planet's): WorldReset.ClientSession - the client's planet and frame statics, reset at the first
/// tick of a world with planets (a planetless world uses none of them, the next planet world resets them before use).
/// </summary>
public static class OrbitalClientHost
{
    /// <summary>Diagnostics: frames run by the host / by a planet's fallback; the last failure.</summary>
    public static long HostFrames, FallbackFrames;
    public static string Status = "not ticked";
    static long _hostAt;

    internal static void Frame(Session session, bool fromHost)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (fromHost) System.Threading.Volatile.Write(ref _hostAt, now);
        else if (now - System.Threading.Volatile.Read(ref _hostAt) < System.Diagnostics.Stopwatch.Frequency) return;   // (the host runs it)
        if (session == null) return;
        try
        {
            if (fromHost) HostFrames++; else FallbackFrames++;
            if (!OrbitalSettings.SawClient) OrbitalSettings.SawClient = true;
            OrbitalSettings.Poll();   // (the settings files, every ~2 s)
            SettingsTab.Tick(session);   // (the Options screen's Mods tab, Ui/SettingsTab.cs: a timestamp compare; 4 Hz a UI-thread check)
        }
        catch (Exception e)
        {
            // (an exception escaping into the engine's job is fatal; this is not a path that throws by design)
            string why = "client frame failed: " + e.GetType().Name + ": " + e.Message;
            if (why != Status) { Status = why; Log.Default?.Warning("[ORBIT] " + why); }
        }
    }
}
