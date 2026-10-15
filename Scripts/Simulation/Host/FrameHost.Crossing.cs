using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

// FrameHost (partial): the client's side of a chart crossing (the ship turned and its speed stepped in one tick) -
// the first-person camera, the acceleration shake, the cockpit's target turned with its grid.
public static partial class FrameHost
{
    // ── the first-person camera across a crossing ──
    // (FirstPersonCameraComponent springs the render camera toward the cockpit: Slerp(last render orientation, the
    //  cockpit's, stiffness). A crossing turns the ship by the chart's angle (~100 deg) in one tick: the view lagged 84 deg
    //  off the cockpit and swung back over ~1.5 s - DevReentry. When the piloted grid the camera rides turns more than 20
    //  deg in one frame - no physics does that - the render camera is turned with it: the spring sees no change.)
    static Entity _snapGrid; static Quaternion _snapQ; static int _snaps;
    private static void SnapCameraToGridTurn(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera)
    {
        if (!_cur.Seated) { _snapGrid = null; return; }
        Entity grid = SeatGrid; double bd = 100;   // (the ship you sit in: a big ship's origin is far from its cockpit)
        if (grid == null)
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
        {
            if (!e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) continue;
            double d = (e.Data.GetWorldTransform().Position - camera.Position).Length();
            if (d < bd) { bd = d; grid = e; }
        }
        if (grid == null) { _snapGrid = null; return; }
        var q = grid.Data.GetWorldTransform().Orientation;
        if (grid == _snapGrid)
        {
            var dq = Quaternion.Normalize(q * Quaternion.Inverse(_snapQ));
            double ang = 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)dq.W))) * 180 / Math.PI;
            if (ang > 20)
            {
                var cam = SpecCam.CameraOf(session)?.Entity;
                if (cam != null)
                {
                    var wt = cam.Data.GetWorldTransform();
                    cam.Data.SetWorldTransform(new WorldTransform(wt.Position, Quaternion.Normalize(dq * wt.Orientation)));
                    if (++_snaps <= 20) Event($"camera turned with its grid ({ang:F1} deg in one frame)");
                }
                // the seated character's copy too: the camera sits where the character's head is, and the character reaches
                // the client a frame after its grid - on that frame the view stood 12 m outside the cockpit (the seat's offset
                // unturned), and the engine's ~1 s stall that follows a crossing held that frame on screen
                var ch = PlayerCharacter(session);
                if (ch != null)
                {
                    var cwt = ch.Data.GetWorldTransform();
                    var gp = grid.Data.GetWorldTransform().Position;
                    Vector3D rel = cwt.Position - gp;
                    // (only when it is still where the unturned seat put it: its offset is off the turned one by about the turn)
                    if (rel.Length() < 100)
                        ch.Data.SetWorldTransform(new WorldTransform(gp + Vector3D.Transform(rel, (QuaternionD)dq), Quaternion.Normalize(dq * cwt.Orientation)));
                }
            }
        }
        _snapGrid = grid; _snapQ = q;
    }

    // ── the game's acceleration camera shake across a chart crossing ──
    // (AccelerationCameraShakeComponent: the observed grid's speed, averaged over 0.16 s, compared tick to tick - a change
    //  over 0.9 m/s shakes the camera. A crossing steps the speed 0 <-> ~1 km/s in one tick: a full shake, 19 deg and 13 m
    //  in one frame, 0.4 s after the handover - DevReentry. Its samples are emptied with no previous average (null: the
    //  first full window compares with itself) for half a second, so the step never registers; real acceleration still
    //  shakes. ClearSamples alone leaves the previous average at 0: a full shake again at speed. REFLECTION: the private
    //  _previousSpeedAverage - docs/REFLECTION_AND_WHITELIST.md.)
    static int _crossingsSeen;
    static long _quietUntil, _quietMax;   // (Stopwatch: quiet until the client's speed has settled for 0.5 s, at most 5 s)
    static float _quietLastSpeed = -1f;
    static System.Reflection.FieldInfo _shakePrev;
    static bool _shakeLooked;

    private static void QuietCrossingShake(Keen.VRage.Core.Game.Systems.Session session)
    {
        int c = System.Threading.Volatile.Read(ref ServerFrames.ChartCrossings);
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        if (c != _crossingsSeen) { _crossingsSeen = c; _quietUntil = now + f / 2; _quietMax = now + 5 * f; _quietLastSpeed = -1f; }
        if (now > _quietUntil || now > _quietMax) return;
        if (!_shakeLooked)
        {
            _shakeLooked = true;
            _shakePrev = typeof(Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent)
                .GetField("_previousSpeedAverage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (_shakePrev == null) Event("camera shake: _previousSpeedAverage not found (the game changed) - crossings may shake the camera");
        }
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent>())
        {
            var s = e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent>();
            if (s == null) continue;
            s.ClearSamples();
            _shakePrev?.SetValue(s, null);
        }
        // (the client's copy gets the new speed by replication - up to a second after the server's move: quiet until the
        //  speed the camera's grid shows has stopped stepping)
        float sp = -1f;
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
        {
            if (!e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) continue;
            if (e.Data.TryGet<RigidBodyData>(out var rb)) { sp = rb.LinearVelocity.Length(); break; }
        }
        if (sp >= 0f)
        {
            if (_quietLastSpeed >= 0f && Math.Abs(sp - _quietLastSpeed) > 5f) _quietUntil = now + f / 2;
            _quietLastSpeed = sp;
        }
    }

    /// <summary>A crossing between a planet's rotating chart and the inertial frames turns the grids (ServerFrames.MoveGrid);
    /// the cockpit's target orientation - what the pilot steers to, held on the client's copy - must turn with them, or
    /// the gyros swing the ship back toward the old one and the controls fight it. Every piloted grid within 300 m.</summary>
    /// <summary>Client: the turns the server gave piloted grids (ServerFrames.TargetTurns) - each one's cockpit target turned
    /// with it, once: the piloted client grid nearest where that grid was or now is (within 300 m).</summary>
    private static void ApplyTargetTurns(Keen.VRage.Core.Game.Systems.Session session)
    {
        while (ServerFrames.TargetTurns.TryDequeue(out var turn))
        {
            if (turn.rot == Quaternion.Identity) continue;
            try
            {
                Entity best = null; double bd = 300;
                foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
                {
                    if (!e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>()) continue;
                    var p = e.Data.GetWorldTransform().Position;
                    double d = Math.Min((p - turn.from).Length(), (p - turn.to).Length());
                    if (d < bd) { bd = d; best = e; }
                }
                if (best == null) { Event("cockpit target turn: no piloted grid near (client)"); continue; }
                var tcd = best.Data.Get<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>();
                tcd.TargetOrientation = Quaternion.Normalize(turn.rot * tcd.TargetOrientation);
                best.Data.Set(tcd);
                Event($"cockpit target turned with its grid ({2 * Math.Acos(Math.Min(1.0, Math.Abs((double)turn.rot.W))) * 180 / Math.PI:F1} deg)");
            }
            catch (Exception ex) { Event("cockpit target turn failed: " + ex.Message); }
        }
    }
}
