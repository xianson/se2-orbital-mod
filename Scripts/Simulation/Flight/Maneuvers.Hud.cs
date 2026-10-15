using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ───────────────────────────── flying a node (HUD) ─────────────────────────────

    private static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>The next node and the delta-v still to do (world-axis direction), or false.</summary>
    public static bool NextBurn(double t, out Node node, out Vector3D remaining, out GravityBody body)
    {
        node = null; remaining = default; body = null;
        if (Nodes.Count == 0 || !Trajectory(t, out _, out var applied) || applied.Count == 0) return false;
        var a = applied[0];
        remaining = a.Dv;   // target minus my trajectory, at the node point
        node = a.Node; body = a.Body;
        return IsFinite(remaining.X) && IsFinite(remaining.Y) && IsFinite(remaining.Z);
    }

    /// <summary>World view: the next burn as a HUD marker; completes the node when done.</summary>
    public static void HudTick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        _session = session;
        if (Nodes.Count == 0) { BurnLine = null; BurnLeft = 0; return; }
        // Nodes left in the past without being flown stay until deleted; a flown one completes.
        if (!NextBurn(t, out var node, out var rem, out var body)) { BurnLeft = 0; BurnLine = null; return; }
        double left = rem.Length();
        double planned = Math.Sqrt(node.Pro * node.Pro + node.Nor * node.Nor + node.Rad * node.Rad);
        if (planned > DoneDv && left < DoneDv)
        {
            lock (Nodes) Nodes.Remove(node);
            if (Selected == node) Selected = null;
            return;
        }
        if (!(left > 1e-9)) { BurnLine = null; return; }   // (a fresh node with no delta-v: no direction - it was NaN)
        Vector3D dirW = rem / left;
        if (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanet != null) dirW = Chart.Of(FrameHost.ObserverPlanet, t).FromInertial(dirW);
        BurnDirWorld = dirW; BurnLeft = left;
        Accel = MaxAccel(session);
        double burn = Accel > 1e-3 ? left / Accel : double.NaN;
        double start = BurnStart(node);   // half the burn before the node (as KSP)
        BurnIn = start > t ? start - t : 0;   // (the plot's short tag: OrbitHud)
        BurnLine = (AutoBurn.Flying ? "Auto-burning " : node.Auto ? "Auto-burn " : "Next burn ") + $"{left:F1} m/s" + (IsFinite(burn) ? (burn < 60 ? $" ({Math.Max(1, burn):F0} s)" : $" ({Clock(burn)})") : "")
                   + (start > t ? $"   ·   in {Clock(start - t)}" : "   ·   now");
        if (MapView.Visible) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
            HudPanel.BeginLabels();
            if (!MapPipeline.HudPoint(camera.Position + dirW * 1e4, out var s, out bool edge)) return;
            Icon("P", s, PlanColor, true, u);
            string when = start > t ? "in " + Clock(start - t) : "now";
            string dur = IsFinite(burn) ? (burn < 60 ? $"  ·  {Math.Max(1, burn):F0} s burn" : $"  ·  {Clock(burn)} burn") : "";
            HudPanel.LabelAt(s + new Vector2(16f * u, 0), $"{left:F1} m/s", PlanColor, u);
        }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>
    /// The burn as it really flies: constant thrust at the ship's acceleration along the planned delta-v
    /// (fixed in inertial space, as KSP), centred on the node, integrated through the body's gravity
    /// (RK4). Short or unknown-thrust burns are impulsive (the difference is negligible).
    /// </summary>
    private static KeplerianElements FiniteBurn(GravityBody body, KeplerianElements coast, double tc, double Tn, StateVector atNode, Vector3D dv)
    {
        double mag = dv.Length(), a = Accel;
        double tau = a > 0.01 ? mag / a : 0;
        if (tau < 2.0 || mag < 1e-6)
            return OrbitalMath.ToElements(new StateVector(atNode.Position, atNode.Velocity + dv), body.Mu, Tn);
        double t0 = Math.Max(tc, Tn - tau / 2);
        var s = OrbitPropagation.StateAt(coast, t0);
        Vector3D d = dv / mag;
        double mu = body.Mu;
        Vector3D Acc(Vector3D r) { double rr = r.Length(); return r * (-mu / (rr * rr * rr)) + d * a; }
        int steps = Math.Max(20, Math.Min(2000, (int)Math.Ceiling(tau / 0.5)));
        double h = tau / steps;
        Vector3D x = s.Position, v = s.Velocity;
        for (int i = 0; i < steps; i++)
        {
            Vector3D k1v = Acc(x), k1x = v;
            Vector3D k2v = Acc(x + k1x * (h / 2)), k2x = v + k1v * (h / 2);
            Vector3D k3v = Acc(x + k2x * (h / 2)), k3x = v + k2v * (h / 2);
            Vector3D k4v = Acc(x + k3x * h), k4x = v + k3v * h;
            x += (k1x + 2 * k2x + 2 * k3x + k4x) * (h / 6);
            v += (k1v + 2 * k2v + 2 * k3v + k4v) * (h / 6);
        }
        return OrbitalMath.ToElements(new StateVector(x, v), mu, t0 + tau);
    }

    /// <summary>The node's burn is over and done with (its second half ended a minute ago): not the next one to fly.</summary>
    public static bool BurnOver(Node n, double t)
    {
        double mag = Math.Sqrt(n.Pro * n.Pro + n.Nor * n.Nor + n.Rad * n.Rad);
        return n.T + (Accel > 0.01 ? 0.5 * mag / Accel : 0) + 60 < t;
    }

    /// <summary>When the next burn starts: half its duration before the node.</summary>
    public static double BurnStart(Node n)
    {
        double mag = Math.Sqrt(n.Pro * n.Pro + n.Nor * n.Nor + n.Rad * n.Rad);
        return Accel > 0.01 ? n.T - 0.5 * mag / Accel : n.T;
    }

    /// <summary>The acceleration the player can make (m/s²): the best-facing max thrust over the mass of
    /// the grid you sit in, or of the character (jetpack).</summary>
    public static double Accel = 0;

    private static double MaxAccel(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            Keen.VRage.DCS.Components.Entity e = null; double mass = 0;
            var sg = FrameHost.Seated ? FrameHost.SeatGrid : null;
            if (sg != null && sg.Data.Has<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>() && sg.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var smp) && smp.InvMass > 0)
            { e = sg; mass = 1.0 / smp.InvMass; }   // (the ship you fly - not the nearest grid: a docked fighter's thrust and mass)
            else if (FrameHost.Seated)
            {
                OrbitalGridComponent best = null; double bd = 300;
                foreach (var g in GridMembers.All())
                {
                    if (!g.IsServer) continue;
                    double d = (GridMembers.Position(g) - FrameHost.PlayerPosition).Length();
                    if (d < bd) { bd = d; best = g; }
                }
                if (best != null) { e = best.Entity; mass = GridMembers.Mass(best); }
            }
            else
            {
                e = FrameHost.PlayerCharacter(session);
                if (e != null && e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) && mp.InvMass > 0) mass = 1.0 / mp.InvMass;
            }
            if (e == null || mass <= 0 || !e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt)) return Accel;
            var p = mt.Regular.Positive; var n = mt.Regular.Negative;
            double f = Math.Max(Math.Max(Math.Max(p.X, p.Y), p.Z), Math.Max(Math.Max(n.X, n.Y), n.Z));
            return f > 0 ? f / mass : Accel;
        }
        catch { return Accel; }
    }

    /// <summary>Warp: when the next burn starts (half the burn before its node), NaN if none.</summary>
    public static double NextBurnStart(double t0)
    {
        double tn = NextNodeTime(t0);
        if (double.IsNaN(tn)) return tn;
        double dv = 0;
        lock (Nodes) foreach (var n in Nodes) if (n.T == tn) dv = Math.Sqrt(n.Pro * n.Pro + n.Nor * n.Nor + n.Rad * n.Rad);
        return Accel > 1e-3 ? tn - 0.5 * dv / Accel : tn;
    }

    /// <summary>Warp: the next sphere-of-influence change on the path after t0 (NaN if none).</summary>
    public static double NextSoiChange(double t0)
    {
        // Last frame's path when there is one (its times are absolute): not a second full solve per warp frame.
        List<Leg> legs = _cLegs;
        if (legs == null || !_cOk || t0 - _cT > 1.0 || Signature(t0) != _cSig) { if (!Trajectory(t0, out legs, out _)) return double.NaN; }
        for (int i = 1; i < legs.Count; i++)
            if (legs[i].Body != legs[i - 1].Body && legs[i].T0 > t0) return legs[i].T0;
        return double.NaN;
    }

    /// <summary>Warp: the next node time after t0 (NaN if none).</summary>
    public static double NextNodeTime(double t0)
    {
        double best = double.NaN;
        lock (Nodes) foreach (var n in Nodes) if (n.T > t0 && (double.IsNaN(best) || n.T < best)) best = n.T;
        return best;
    }
}
