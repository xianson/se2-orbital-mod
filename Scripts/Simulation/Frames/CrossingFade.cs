using System;
using Keen.VRage.Core;
using SEAerospace.Frames;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The view faded across a seated player's arrival into a planet's frame (rails -> physics). Measured (DevReentry, engine
/// screenshots): the frame the turned ship arrives in is drawn before any mod code runs - the chase camera springs from
/// the previous frame's orientation (ThirdPersonCameraComponent), so the ship showed side-on - and the engine stalls
/// ~1 s right after a crossing (physics meeting the grid at its new place: not this mod's code, not Aero's), holding that
/// frame on screen. The server announces the arrival ahead (ServerFrames.ArrivalNotices, NoticeSeconds); the view fades
/// to black, stays black through the move and the stall, the chase camera is put back behind the ship, and the view
/// comes back. The states: CrossingFadeMachine (tested offline). Harness: crossfade on|off.
/// </summary>
public static class CrossingFade
{
    public static bool Enabled = true;
    public static string Status => _m.Status;
    static readonly CrossingFadeMachine _m = new CrossingFadeMachine();
    /// <summary>How black the screen is now (0..1): the reentry recorder judges only what can be seen.</summary>
    public static double Alpha => _m.Alpha;
    static FadeClock _clock = new FadeClock();
    static object _session;
    static Vector3D _seatLast, _footLast; static bool _seatKnown, _footKnown;
    static string _lastStatus = "idle";

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        int notices = System.Threading.Volatile.Read(ref ServerFrames.ArrivalNotices);
        int crossings = System.Threading.Volatile.Read(ref ServerFrames.ChartCrossings);
        if (!ReferenceEquals(session, _session)) { _session = session; _m.Reset(notices, crossings); _clock = new FadeClock(); _seatKnown = false; _footKnown = false; }
        // (the game-active clock: a pause between ticks counts as a quarter second at most - FadeClock)
        double dt = _clock.Tick(Wall()), now = _clock.Now;
        // this client has seen its ship moved (its copy jumps a kilometre and more at a crossing, up to a second after the server)
        bool moved = false;
        var seat = FrameHost.SeatGrid;
        if (seat != null)
        {
            Vector3D p = seat.Data.GetWorldTransform().Position;
            moved = _seatKnown && (p - _seatLast).Length() > 1000;
            _seatLast = p; _seatKnown = true;
            _footKnown = false;   // (standing up later: no stale place to compare with)
        }
        else
        {
            // on foot (riding a frame): your own place jumps at the crossing
            _seatKnown = false;
            Vector3D p = FrameHost.PlayerPosition;
            moved = _footKnown && (p - _footLast).Length() > 1000;
            _footLast = p; _footKnown = true;
        }
        // (only your own frame's arrival: another ship arriving anywhere is not yours to black out)
        bool mine = FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == System.Threading.Interlocked.Read(ref ServerFrames.ArrivalNoticeFrame);
        // (seated, or riding the frame on foot: the move and the engine's stall after it are the same)
        _m.Step(now, dt, notices, crossings, mine, Enabled, moved);
        if (_m.Status != _lastStatus) { _lastStatus = _m.Status; Log.Default?.Info($"[ORBIT] crossing fade: {_m.Status} (alpha {_m.Alpha:F2})"); }   // (the tests read its phases)
        if (_m.ResetCameraNow) ResetChaseCamera(session);
        if (_m.Alpha > 0) Draw(session, (float)_m.Alpha);
    }

    /// <summary>The chase camera put back behind the ship (the game's own ResetCamera), while the view is still black: it
    /// springs from the previous frame's orientation, which a crossing turned out from under it.</summary>
    static void ResetChaseCamera(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent>())
                e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent>()?.ResetCamera(true, true);
        }
        catch { }
    }

    static void Draw(Keen.VRage.Core.Game.Systems.Session session, float alpha)
    {
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            var size = MapPipeline.ScreenSize;
            MapPipeline.ScreenRect(new Vector2(-8, -8), new Vector2(size.X + 8, size.Y + 8), new ColorSRGB(0f, 0f, 0f, alpha));
        }
        finally { MapPipeline.UiEnd(); }
    }

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
