using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// FrameHost (partial): the player in a frame - the anchor's treadmill (drain into the rails), the rider's relative
// motion about it, and the frame's SOI reparent.
public static partial class FrameHost
{
    // ───────────────────────────── treadmill (conjunction frame) ─────────────────────────────

    private static void UpdatePlayerFrame(Keen.VRage.Core.Game.Systems.Session session, Entity ch, ProximityFrame f,
                                          Vector3D pos, Vector3D vel, double t, double dt)
    {
        if (f.AnchorEntityId != _playerId || f.IsEncounter || DevRider)
        {
            UpdateRider(session, ch, f, pos, vel, t, dt);
            return;
        }
        // You anchor this frame: its orbit is yours (no rider state left from riding it a moment ago)
        _cur.AtAnchor = true; _riderN = 0;   // (not riding, no hold: the state's defaults)
        if (!_tpPending)
        {
            // Hard pin: the anchor never translates in world space.
            if ((pos - f.BerthCenter).Length() > PinTolerance)
            {
                StartTeleport(session, f.BerthCenter, Vector3D.Zero, t);
            }
            else if (IsFinite(vel))
            {
                // Force sensor: read the anchor's velocity, zero it, fold it into the orbit.
                f.PendingDrainDv += vel;
                SetVelocity(ch, Vector3D.Zero);
                // Zero the server copy too, or it re-supplies the same velocity for several ticks
                // (seen in game: a 10 m/s kick folded in ~17 times).
                if (vel.LengthSquared() > 1e-6) ServerPlanetBeacon.PendingPlayer = new PlayerRequest { Velocity = Vector3D.Zero };
            }
        }

        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        FrameRails.FoldDrain(f, t, FoldThreshold, MaxApparentAccel * dt, ref cur, out Vector3D slice);
        if (IsFinite(cur.Velocity)) f.VirtualVelocity = cur.Velocity;

        TryReparent(f, t);
        TryMaterialize(session, f, t);
    }

    /// <summary>
    /// RIDER: the player in a frame anchored by a grid (SE1 CharacterRider / loose-object feed). Not pinned,
    /// not drained: the player floats freely relative to the anchor and feels the berth's differential
    /// gravity minus the frame acceleration A the server folded this tick. Past the slot radius the
    /// player splits into a frame of their own.
    /// </summary>
    private static void UpdateRider(Keen.VRage.Core.Game.Systems.Session session, Entity ch, ProximityFrame f,
                                    Vector3D pos, Vector3D vel, double t, double dt)
    {
        if (_tpPending) return;
        // Warp N: the rails run N times faster than real time, so relative motion must too. Forces scale by
        // N^2 (velocities come out N times the true ones, covering N times the ground per real second); on a
        // change of warp the velocity is rescaled so the motion carries on; readouts divide by N.
        // Physics warp limit (as KSP's): relative motion in warp needs N times the true speed, which must stay
        // under the world's speed cap (1000 m/s): riding free (dampeners off), warp is held to N x |v_rel| <= 900
        // m/s and x25 at most. (x10 is exact: 2 m off over 30 s; past the cap the motion was lost.)
        // (the jetpack's dampeners read first: read below, after this, the limit always saw them off - x25 even holding)
        try { _cur.Dampeners = ch.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>(); } catch { }
        if (SystemHost.Timescale > 1.0 && !_cur.Dampeners && _riderN > 0)
        {
            double vTrue = vel.Length() / _riderN;
            double nMax = Math.Min(MaxRiderWarp, RiderWarpSpeed / Math.Max(1.0, vTrue));
            if (SystemHost.Timescale > nMax)
            {
                SystemHost.Timescale = Math.Max(1.0, Math.Floor(nMax));
                RiderWarpLimit = $"warp held to x{SystemHost.Timescale:F0}: relative motion at {vTrue:F0} m/s";
            }
        }
        double N = Math.Max(1.0, SystemHost.Timescale);
        if (_riderN > 0 && Math.Abs(N - _riderN) > 1e-9 && _riderFrame == f.Id)
        {
            vel *= N / _riderN;
            SetVelocity(ch, vel);
            ServerPlanetBeacon.SetRiderVelocity(vel);
        }
        _riderN = N; _riderFrame = f.Id;
        _cur.RiderFrame = f.Id; _cur.RiderOffset = pos - f.BerthCenter; _cur.RiderVelocity = vel / N;
        // At the anchor (inside its bounding box, touching it, or within 100 m of it): no rendezvous plot.
        _cur.AtAnchor = AnchorDistance(f, pos) <= 100.0;
        // Dampeners: station-keeping. As SE1 (a station-keeping member holds its offset with the relative force
        // nulled): with them on no relative force is applied at all, so they only null your own motion and you
        // hold EXACTLY (the anchor is pinned at the berth). Fighting the force each tick left a lag, a drift.
        try { _cur.Dampeners = ch.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>(); } catch { }
        Vector3D A = !DevRider && ServerFrames.AnchorAccel.TryGetValue(f.Id, out var a) ? a : Vector3D.Zero;
        StateVector cur = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D rRel = pos - f.BerthCenter;   // the anchor is pinned at the berth
        double mu = f.Elements.Mu;
        bool isLag = EncounterFrames.LagrangeDynamics(f.Id, t, out var lag);
        // A static anchor (a station, an asteroid): you leave it past the capture radius, measured from it.
        bool isStatic = ServerFrames.StaticAnchorView(f, out Vector3D anchorAt);
        double leave = ServerFrames.LeaveRadius(isStatic);
        double away = isStatic ? (pos - anchorAt).Length() : rRel.Length();
        // The relative force (true acceleration at the true velocity), applied as a force: x N^2 in warp.
        Vector3D acc = isLag ? lag(rRel, vel / N)   // a Lagrange site's own dynamics
                     : (Grav(cur.Position + rRel, mu) - Grav(cur.Position, mu)) - A;
        // DEV check (dampeners off, not thrusting): the measured relative acceleration against the model's.
        {
            double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (_diagWall > 0 && wall - _diagWall >= 1.0 && !_cur.Dampeners)
            {
                Vector3D meas = (vel / N - _diagVel) / ((t - _diagT) > 0 ? (t - _diagT) : 1);
                Vector3D model = 0.5 * (acc + _diagAcc);
                double ratio = model.LengthSquared() > 1e-12 ? Vector3D.Dot(meas, model) / model.LengthSquared() : double.NaN;
                RiderDiag = $"rider: |a| model {model.Length():F3} measured {meas.Length():F3} m/s2, along-model ratio {ratio:F2}, warp x{N:F0}, off {rRel.Length() / 1000:F2} km, world |v| {vel.Length():F0} m/s, dv/tick {acc.Length() * dt * N * N:F1} m/s, ticks/s {_diagTicks:F0}";
                _diagTicks = 0;
            }
            _diagTicks++;
            if (_diagWall <= 0 || wall - _diagWall >= 1.0) { _diagWall = wall; _diagVel = vel / N; _diagT = t; _diagAcc = acc; }
        }
        if (!_cur.Dampeners && IsFinite(acc) && acc.LengthSquared() > 1e-12)
        {
            Vector3D dv = acc * (dt * N * N);
            SetVelocity(ch, vel + dv);
            ServerPlanetBeacon.AddRiderDv(dv);   // the server's copy too (it would overwrite the client's)
        }
        else if (_cur.Dampeners && IsFinite(acc) && acc.LengthSquared() > 1e-12)
        {
            // Holding station is not free: the jetpack is charged for the force it would make against the pull - the
            // fraction of its thrust that face needs, for this tick's game time (x N in warp; the server bills the suit).
            try
            {
                var q = (QuaternionD)ch.Data.GetWorldTransform().Orientation;
                Vector3D need = QuaternionD.Inverse(q) * (-acc);   // (per kg, the character's axes)
                double m = ch.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) && mp.InvMass > 0 ? 1.0 / mp.InvMass : 0;
                if (m > 0 && ch.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt))
                {
                    var P = mt.Regular.Positive; var Ng = mt.Regular.Negative;
                    double Face(double c, float pos, float neg) => c > 0 ? pos : -neg;   // (the face that pushes along +c or -c)
                    double frac = 0;
                    double F(double c, float pos, float neg) { double fm = Math.Abs(Face(c, pos, neg)); return Math.Abs(c) < 1e-12 ? 0 : fm > 0 ? Math.Abs(c) * m / fm : 1; }
                    frac = Math.Min(1.0, Math.Max(F(need.X, P.X, Ng.X), Math.Max(F(need.Y, P.Y, Ng.Y), F(need.Z, P.Z, Ng.Z))));
                    ServerPlanetBeacon.AddRiderHold(frac * dt * N);
                }
            }
            catch { }
        }
        if (away > leave)
        {
            var el = CaptureMath.CaptureElements(new StateVector(cur.Position + rRel, cur.Velocity + vel / N), mu, t);
            if (!IsFinite(el.SemiMajorAxis)) return;
            var nf = SystemHost.Frames.SplitOff(f, _playerId, f.ParentBodyName, el);
            if (nf == null) return;
            _riderN = 0;
            StartTeleport(session, nf.BerthCenter, Vector3D.Zero, t);
            Event($"SPLIT player from frame #{f.Id} at {away / 1000:F1} km -> frame #{nf.Id}");
        }
    }

    /// <summary>DEV: the rider force check (measured vs model relative acceleration).</summary>
    public static string RiderDiag = "-";
    private static double _diagWall, _diagT, _diagTicks; private static Vector3D _diagVel, _diagAcc;

    /// <summary>Riding free, warp is held so relative motion (N x its true speed) stays under the world cap.</summary>
    public const double MaxRiderWarp = 25, RiderWarpSpeed = 900;
    public static string RiderWarpLimit;

    /// <summary>DEV: ride your own frame (its centre a virtual fixed anchor), to test relative motion.</summary>
    public static bool DevRider;

    /// <summary>Riding, at the anchor itself (within 100 m of its bounding box): no rendezvous plot.</summary>
    public static bool AtAnchor => _pub.AtAnchor;

    /// <summary>Riding a frame (not its anchor) and holding station: no relative force on you - on foot your jetpack's
    /// dampeners, seated the ship's (the server holds it when its thrust can cancel the pull: StationKeeping).</summary>
    public static bool HoldingStation => _pub.RiderFrame >= 0 && (_pub.Seated ? ServerFrames.View(SeatGridServerId, out var sv) && sv.Holding : _pub.Dampeners);

    /// <summary>Seated on a grid joined to the anchor: your orbit from where you sit, no drift plot (PlayerState.AnchorRigid).</summary>
    public static bool AnchorRigid => _pub.AnchorRigid;

    /// <summary>Your jetpack's dampeners (riding: on = station-keeping with the anchor).</summary>
    public static bool Dampeners => _pub.Dampeners;

    /// <summary>The warp the rider's velocity was last scaled for (and in which frame).</summary>
    private static double _riderN;
    private static long _riderFrame = -1;

    private static Vector3D Grav(Vector3D r, double mu)
    {
        double d = r.Length();
        return d > 1 ? r * (-mu / (d * d * d)) : Vector3D.Zero;
    }

    /// <summary>Patched conics: leave the parent's SOI or enter a child's -> re-express the orbit there.
    /// (2026-10-15: through SoiReparent.Repatch - the escape pad, a sphere skipped between two polls at high warp
    /// sub-stepped, and VirtualVelocity re-expressed in the new parent: it was left in the old one, and saved so.)</summary>
    internal static void TryReparent(ProximityFrame f, double t)
    {
        var reg = SystemHost.Registry;
        GravityBody parent = reg.Find(f.ParentBodyName);
        if (parent == null) return;
        double from = f.SoiCheckedT; f.SoiCheckedT = t;
        if (!SoiReparent.Repatch(parent, f.Elements, from, t, out GravityBody now, out KeplerianElements el, out Vector3D vel)) return;
        Event($"SOI {f.ParentBodyName} -> {now.Name} (frame #{f.Id}, a={el.SemiMajorAxis / 1000:F0} km e={el.Eccentricity:F3})");
        f.ParentBodyName = now.Name;
        f.Elements = el;
        if (IsFinite(vel)) f.VirtualVelocity = vel;
    }
}
