using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// FrameHost (partial): moving the player - a teleport and its settling (client and server writes).
public static partial class FrameHost
{
    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>The player moved: keeps their facing (turned with the chart when they cross between a spinning planet's
    /// frame and the rails) - every move snapped the view to one fixed world direction.</summary>
    private static void StartTeleport(Keen.VRage.Core.Game.Systems.Session session, Vector3D target, Vector3D velocity, double t, Quaternion? turn = null)
    {
        // (moving with something - an arrival with its ship at up to the speed cap: aimed where it will be when the teleport
        //  lands, by the measured landing delay - aimed where it was, the player trailed the ship by delay x speed, ~100 m)
        if (velocity.LengthSquared() > 1 && IsFinite(velocity)) target += velocity * Math.Min(_tpLatency, 0.5);
        _tpPending = true;
        _tpTarget = target;
        _tpVelocity = velocity;
        _tpStarted = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        _tpRetried = false;
        Quaternion q = Quaternion.Identity;
        try
        {
            var ch = PlayerCharacter(session);
            if (ch != null) q = ch.Data.GetWorldTransform().Orientation;
            if (turn.HasValue) q = Quaternion.Normalize(turn.Value * q);
            if (!q.IsValidAndRotationIsNormalized()) q = Quaternion.Identity;
        }
        catch { q = Quaternion.Identity; }
        _tpOrient = q;
        PlanetRenderBridge.TeleportPlayer(session, new WorldTransform(target, q), clearMotion: true);
    }
    private static Quaternion _tpOrient = Quaternion.Identity;
    /// <summary>How long a teleport takes to land (s, learned); the first guess a few frames.</summary>
    private static double _tpLatency = 0.08;
    private static bool _tpRetried;

    private static void SettleTeleport(Keen.VRage.Core.Game.Systems.Session session, Entity ch, Vector3D pos, double t)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if ((pos - _tpTarget).Length() < 200.0)
        {
            // the landing delay, learned (not from a retried teleport: that one restarted the clock)
            if (!_tpRetried) _tpLatency += (Math.Clamp(now - _tpStarted, 0, 1) - _tpLatency) * 0.3;
            SetVelocity(ch, _tpVelocity);
            ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Velocity = _tpVelocity };
            _tpPending = false;
        }
        else if (now - _tpStarted > TeleportSettle)
        {
            Event($"teleport did not land within {TeleportSettle:F0} s (at {ServerPlanetBeacon.Fmt(pos)}); retrying");
            PlanetRenderBridge.TeleportPlayer(session, new WorldTransform(_tpTarget, _tpOrient), clearMotion: true);
            _tpStarted = now; _tpRetried = true;
        }
    }

    private static Vector3D ReadVelocity(Entity ch) =>
        ch.Data.TryGet<RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero;
}
