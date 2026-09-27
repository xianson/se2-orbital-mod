using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// AUTO-BURN: an armed maneuver flies itself. From its burn start (half the burn before the node) the
/// thrust is held along what is left of the burn, re-aimed every frame (closed loop), until under 0.1
/// m/s: the node then completes as usual. Seated, the ship's thrusters (all of them, translating);
/// on foot, the jetpack. The command is the same vector a pilot's keys write (ControlData.Movement).
/// Arm or disarm from the maneuver's right-click menu; warp stops before the burn start as ever.
/// </summary>
public static class AutoBurn
{
    public static string Status = "";
    public static bool Flying { get; private set; }
    public const double AlignLead = 30.0;   // s: turn to the burn this long before it starts

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        var node = NextArmed();
        double start = node != null ? Maneuvers.BurnStart(node) : double.NaN;
        bool active = node != null && t >= start - AlignLead && Maneuvers.BurnLeft >= Maneuvers.DoneDv
                      && SystemHost.Timescale <= 1.0 && !DevFlight.Busy;
        bool fly = active;
        if (fly)
        {
            bool burning = t >= start;
            if (!Flying) { Flying = true; GameUi.Toast(session, "burn", "Auto-burn", burning ? $"Burning {Maneuvers.BurnLeft:F1} m/s" : "Turning to the burn", 4); }
            // Turn first (seated: the gyros bring the main thrust axis onto the burn), thrust from the start.
            DevFlight.Command(session, Maneuvers.BurnDirWorld, burning ? Math.Max(0.25, Math.Min(1.0, Maneuvers.BurnLeft / 5.0)) : 0.0);
            Status = burning ? $"burning, {Maneuvers.BurnLeft:F1} m/s left" : $"turning, burn in {Maneuvers.Clock(start - t)}";
        }
        else if (Flying)
        {
            Flying = false;
            DevFlight.Command(session, Vector3D.Zero, 0);
            Status = node == null ? "burn complete" : "paused";
            if (node == null) GameUi.Toast(session, "burn", "Auto-burn", "Burn complete", 3);
        }
    }

    private static Maneuvers.Node NextArmed()
    {
        Maneuvers.Node best = null;
        lock (Maneuvers.Nodes) foreach (var n in Maneuvers.Nodes) if (best == null || n.T < best.T) best = n;
        return best != null && best.Auto ? best : null;
    }
}
