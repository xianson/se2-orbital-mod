using System;

namespace SEAerospace.Frames
{
    /// <summary>
    /// THE CROSSING FADE'S STATE (game-free; tested offline: Tests/PlayerOrbitTests). The view goes black across a seated
    /// player's arrival into a planet's frame and comes back once the engine has stopped stalling (OrbitalMod.CrossingFade
    /// draws it). Inputs each frame: the server's counters - arrivals announced (ahead of the move), crossings done (the
    /// move) - whether this client has seen its ship moved, whether the player is seated, the frame time. Out -> Black ->
    /// (the move, seen here) -> Settle (steady frames again) -> In -> Idle; the move may land while still fading out (it is
    /// announced ahead, the fade may be slow); no move within MaxBlackSeconds: back in anyway.
    /// Time: the caller's game-active clock - a pause (no ticks) does not count (a pause mid-fade timed it out, and the move
    /// came unfaded); a stall's long frame is capped to MaxStep for the fade's alpha but still breaks the calm count.
    /// </summary>
    public sealed class CrossingFadeMachine
    {
        public enum Phase { Idle, Out, Black, Settle, In }
        public Phase State { get; private set; }
        public double Alpha { get; private set; }
        /// <summary>Set for the one step the view is about to come back (the chase camera reset there, still black).</summary>
        public bool ResetCameraNow { get; private set; }
        public string Status { get; private set; } = "idle";

        public double OutSeconds = 0.2, InSeconds = 0.35, MaxBlackSeconds = 4, CalmFrameSeconds = 0.05, MaxStep = 0.05;
        /// <summary>After the server's move, how long to wait for this client to see it before settling anyway (s).</summary>
        public double ClientWaitSeconds = 1.5;
        public int CalmFrames = 6;

        int _notices, _crossings, _calm;
        bool _crossed, _seen;
        double _since, _movedAt, _typical = 1.0 / 60;

        /// <summary>A new session: nothing pending; the counters as they stand now are not news.</summary>
        public void Reset(int notices, int crossings)
        {
            State = Phase.Idle; Alpha = 0; ResetCameraNow = false; Status = "idle";
            _notices = notices; _crossings = crossings; _crossed = false; _seen = false; _typical = 1.0 / 60;
        }

        public void Step(double now, double dt, int notices, int crossings, bool seated, bool enabled, bool clientMoved = true)
        {
            ResetCameraNow = false;
            if (double.IsNaN(dt) || dt < 0) dt = 0;
            double step = Math.Min(dt, MaxStep);   // (a stalled frame does not jump the fade)
            // the usual frame time, from quiet frames: "calm" is relative to it (at 20 fps a 0.05 s threshold never calmed)
            if ((State == Phase.Idle || State == Phase.Out) && dt > 0 && dt < 0.5) _typical += (dt - _typical) * 0.1;
            if (notices != _notices)
            {
                _notices = notices;
                if (enabled && seated && State == Phase.Idle) { State = Phase.Out; _since = now; _crossings = crossings; _crossed = false; _seen = false; Status = "fading out"; }
            }
            if ((State == Phase.Out || State == Phase.Black) && crossings != _crossings && !_crossed) { _crossed = true; _movedAt = now; }
            if (_crossed && clientMoved) _seen = true;
            switch (State)
            {
                case Phase.Out:
                    Alpha = Math.Min(1, Alpha + step / OutSeconds);
                    if (Alpha >= 1) { State = _crossed ? Phase.Settle : Phase.Black; _since = now; _calm = 0; Status = _crossed ? "moved: waiting for steady frames" : "black: waiting for the move"; }
                    break;
                case Phase.Black:
                    if (_crossed) { State = Phase.Settle; _since = now; _calm = 0; Status = "moved: waiting for steady frames"; }
                    else if (now - _since > MaxBlackSeconds) { State = Phase.In; _since = now; Status = "no move came: fading in"; }
                    break;
                case Phase.Settle:
                    // (the server moved; this client's copy follows up to a second later - calm frames counted only once it has)
                    bool seenOrLate = _seen || now - _movedAt > ClientWaitSeconds;
                    double calmBelow = Math.Max(CalmFrameSeconds, 2.5 * _typical);
                    _calm = seenOrLate && dt > 0 && dt < calmBelow ? _calm + 1 : 0;
                    if (_calm >= CalmFrames || now - _since > MaxBlackSeconds) { ResetCameraNow = true; State = Phase.In; _since = now; Status = "fading in"; }
                    break;
                case Phase.In:
                    Alpha = Math.Max(0, Alpha - step / InSeconds);
                    if (Alpha <= 0) { State = Phase.Idle; Status = "idle"; }
                    break;
            }
            _crossings = crossings;
        }
    }

    /// <summary>The fade's clock: real time while the game runs - a gap between ticks (a pause, the menu) counts as at most
    /// MaxGap, not its length (a pause mid-fade timed the fade out on the wall clock).</summary>
    public sealed class FadeClock
    {
        public double MaxGap = 0.25;
        public double Now { get; private set; }
        public double Last { get; private set; }
        double _wall = double.NaN;

        /// <summary>A tick at this wall time: the clock advanced by the gap since the last tick, capped. Returns the step.</summary>
        public double Tick(double wall)
        {
            double d = double.IsNaN(_wall) ? 0 : Math.Max(0, wall - _wall);
            _wall = wall;
            Last = Math.Min(d, MaxGap);
            Now += Last;
            return d;   // (the true gap: a stall's long frame still reads long to the calm count)
        }
    }

    /// <summary>
    /// WHEN AN ARRIVAL IS ANNOUNCED (the server, for a frame with a piloted ship): NoticeSeconds of real time ahead of the
    /// crossing (game time ahead = that x the warp), once - and again when the last notice is older than RearmSeconds of
    /// real time (under warp it came minutes of game time early, warp then stopped short of the crossing for other reasons,
    /// the fade timed out, and the real crossing came unfaded: no second notice).
    /// </summary>
    public static class ArrivalNotice
    {
        public const double NoticeSeconds = 0.4, RearmSeconds = 5.0;

        /// <summary>lastWall: the real time of this frame's last notice (NaN: none).</summary>
        public static bool Due(double tCross, double t, double timescale, double lastWall, double nowWall)
        {
            if (double.IsNaN(tCross) || tCross < t) return false;
            if (tCross - t > NoticeSeconds * Math.Max(1.0, timescale)) return false;
            return double.IsNaN(lastWall) || nowWall - lastWall > RearmSeconds;
        }
    }
}
