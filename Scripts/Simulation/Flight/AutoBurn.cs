using Keen.VRage.Core;
using SEAerospace.Flight;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// AUTO-BURN: an armed maneuver flies itself. From its burn start (half the burn before the node) the
/// thrust is held along what is left of the burn, re-aimed every frame (closed loop), until under 0.1
/// m/s: the node then completes as usual. Seated, the ship's thrusters (all of them, translating);
/// on foot, the jetpack. The command is the same vector a pilot's keys write (ControlData.Movement).
/// Arm or disarm from the maneuver's right-click menu; warp stops before the burn start as ever.
/// It gives up with a reason (BurnWatch, 2026-10-18 - it sat at "burning, 6.0 m/s left" forever on a ship whose engines
/// made no thrust in vacuum): no thrust along the burn, the burn going the wrong way, can't turn to it, far too long - the
/// node is disarmed and kept; a burn started from a seat pauses while you are out of it.
/// </summary>
public static class AutoBurn
{
    public static string Status = "";
    public static bool Flying { get; private set; }
    public static double AlignLead = 30.0;   // s: turn to the burn this long before it starts (the setting Autopilot.BurnAlignLead)

    /// <summary>What "Auto-burn" does, for its menu item / tooltip (U2, 2026-10-18: it was never said).</summary>
    public const string Tooltip = "Auto-burn: the ship turns to this burn by itself and fires its thrusters when the burn starts, "
        + "until it is done. It stops and tells you why if it can't (no thrust along the burn, can't turn). Stay in the seat.";

    /// <summary>Why the last auto-burn stopped or paused (null while it is fine, or none ran): the card's word.</summary>
    public static string StopReason;
    /// <summary>The last verdict (BurnWatch.Verdict as an int: 0 fine) - the harness reads it.</summary>
    public static int LastVerdict;

    static readonly BurnWatch _watch = new BurnWatch();
    static Maneuvers.Node _watched;   // the node the watch is on
    static bool _paused;              // out of the seat (LeftSeat): nothing commanded until seated again

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        WatchCompletion();
        var node = NextArmed();
        if (!ReferenceEquals(node, _watched)) { _watched = node; _watch.Reset(); _paused = false; if (node != null) { StopReason = null; LastVerdict = 0; } }
        double start = node != null ? Maneuvers.BurnStart(node) : double.NaN;
        // Turning is allowed in warp (the ship is simulated as usual inside its frame; only the orbit
        // runs fast): in warp it keeps pointing at the burn, so it is lined up when warp drops out.
        // Thrust only out of warp.
        bool warping = SystemHost.Timescale > 1.0;
        bool active = node != null && (t >= start - AlignLead || warping) && Maneuvers.BurnLeft >= Maneuvers.DoneDv
                      && !DevFlight.Busy;
        bool fly = active;
        if (fly)
        {
            bool burning = t >= start && !warping;
            // the watch: what the burn does, each tick (wall clock - warp runs the game clock fast, not the gyros)
            bool seated = FrameHost.Seated;
            bool aligned = !seated || DevFlight.Aligned;   // (on foot the jetpack translates any way: always lined up)
            // (no turn judged in warp: the burn's direction sweeps round with the orbit N times faster than gyros can follow)
            var verdict = _watch.Step(Wall(), burning && aligned, !aligned && !warping, seated ? DevFlight.OffBurnDeg : double.NaN,
                                      Maneuvers.BurnLeft, seated ? DevFlight.AxisForce : double.NaN, seated, Maneuvers.BurnSeconds);
            if (verdict == BurnWatch.Verdict.LeftSeat)
            {
                if (!_paused) { _paused = true; Stop(session, verdict); }
                Status = "paused: you left the seat";
                return;
            }
            if (_paused) { _paused = false; StopReason = null; LastVerdict = 0; Messages.Say(MessageKinds.Burn, "Auto-burn: back in the seat, going on"); }
            if (verdict != BurnWatch.Verdict.Ok)
            {
                node.Auto = false;   // (disarmed and kept: fix the ship and arm it again, or fly it by hand)
                _watched = null;
                Stop(session, verdict);
                return;
            }
            if (!Flying) Flying = true;   // (no toast: the orbit card and the map title say it)
            // Turn first (seated: the gyros bring the main thrust axis onto the burn), thrust from the start.
            DevFlight.Command(session, Maneuvers.BurnDirWorld, burning ? Math.Max(0.25, Math.Min(1.0, Maneuvers.BurnLeft / 5.0)) : 0.0);
            Status = burning ? $"burning, {Maneuvers.BurnLeft:F1} m/s left" : warping ? $"lined up in warp, burn in {Maneuvers.Clock(start - t)}" : $"turning, burn in {Maneuvers.Clock(start - t)}";
        }
        else if (Flying)
        {
            Flying = false;
            DevFlight.Command(session, Vector3D.Zero, 0);
            Status = node == null ? "burn complete" : "paused";
            // (the "Burn done" notice: WatchCompletion, for manual and auto burns alike)
        }
    }

    /// <summary>Stop flying with a reason: thrust off, the reason on the card and as a notice.</summary>
    static void Stop(Keen.VRage.Core.Game.Systems.Session session, BurnWatch.Verdict v)
    {
        Flying = false;
        DevFlight.Command(session, Vector3D.Zero, 0);
        LastVerdict = (int)v;
        StopReason = BurnWatch.Short(v);
        string msg = BurnWatch.Message(v, Maneuvers.BurnLeft);
        Status = msg;
        if (msg != null) Messages.Say(MessageKinds.Burn, msg, 8);
    }

    // ── a burn done (flown by hand or by the auto-burn): said, with what was left over ──
    static Maneuvers.Node _nextSeen; static double _nextLeft = double.NaN;

    /// <summary>The next node of last tick gone from the list without the player removing it: the burn completed
    /// (Maneuvers.HudTick removes a node under DoneDv). Said: "Burn done, 0.04 m/s left over" (2026-10-18, U2: a burn
    /// flown by hand vanished without a word).</summary>
    static void WatchCompletion()
    {
        var seen = _nextSeen;
        if (seen != null)
        {
            bool still; lock (Maneuvers.Nodes) still = Maneuvers.Nodes.Contains(seen);
            if (!still)
            {
                // (completed: HudTick removes a node only under DoneDv - the last left seen is a tick before that, so
                //  anything well over it was a removal of another kind: the harness, the API)
                double planned = Math.Sqrt(seen.Pro * seen.Pro + seen.Nor * seen.Nor + seen.Rad * seen.Rad);
                if (!Maneuvers.TakeRemovedByPlayer(seen) && planned > Maneuvers.DoneDv && _nextLeft < Math.Max(1.0, 0.05 * planned))
                {
                    Messages.Say(MessageKinds.Burn, _nextLeft < Maneuvers.DoneDv ? $"Burn done, {_nextLeft:F2} m/s left over"
                                                                                  : $"Burn done, under {Maneuvers.DoneDv:F1} m/s left over");
                    if (StopReason == null) Status = "burn complete";
                }
                _nextSeen = null;
                StopReason = null; LastVerdict = 0;   // (the burn is gone: no reason left on the card)
            }
        }
        // the next node now, and what is left of it (HudTick's, this tick)
        Maneuvers.Node next = null; double tn = double.MaxValue, now = SystemHost.Now;
        lock (Maneuvers.Nodes) foreach (var n in Maneuvers.Nodes) if (!Maneuvers.BurnOver(n, now) && n.T < tn) { tn = n.T; next = n; }
        if (next != null) { _nextSeen = next; _nextLeft = Maneuvers.BurnLeft; }
    }

    /// <summary>A new world: no watch, no reason.</summary>
    public static void ResetWorld() { _watch.Reset(); _watched = null; _paused = false; StopReason = null; LastVerdict = 0; _nextSeen = null; _nextLeft = double.NaN; Status = ""; }

    private static Maneuvers.Node NextArmed()
    {
        Maneuvers.Node best = null;
        // (the earliest node whose burn is not over: a past manual node left in the list hid every later armed one - warp
        //  still stopped for them and nothing fired)
        double t = SystemHost.Now;
        lock (Maneuvers.Nodes) foreach (var n in Maneuvers.Nodes) if (!Maneuvers.BurnOver(n, t) && (best == null || n.T < best.T)) best = n;
        return best != null && best.Auto ? best : null;
    }
}
