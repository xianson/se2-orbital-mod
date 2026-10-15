using System.Collections.Concurrent;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// ServerFrames (partial): the forces the frames add or skip - a spinning chart's Coriolis + centrifugal, and members
// holding station (thrust instead of the relative pull; StationKeepCharge bills it).
public static partial class ServerFrames
{
    /// <summary>Free-flying grids in a spinning planet's cell (the rotating chart): Coriolis + centrifugal.</summary>
    private static void ApplyFictitious(double dt)
    {
        var reg = SystemHost.Registry;
        double t = SystemHost.Now;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || _gridHighSpeed.ContainsKey(g.Id) || !GridMembers.IsDynamic(g)) continue;
            // (a framed grid first - one lookup - before the cell search: both only skip, so their order is free; most grids
            //  are framed and were cell-searched first, every tick, 2026-10-15)
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) continue;
            if (EncounterFrames.IsNpc(g)) continue;   // NPCs: no relative-motion terms
            Chart c = Chart.Of(body, t);
            if (!c.Spin) continue;
            Vector3D r = pos - cell, v = GridMembers.Velocity(g);
            double alt = r.Length() - (reg.FindDefinition(body)?.RadiusMeters ?? 0);
            if (v.Length() < OrbitalConfig.FictitiousMinSpeed && alt < OrbitalConfig.FictitiousMinAltitude) continue;
            Vector3D dv = c.Fictitious(r, v) * dt;
            if (IsFinite(dv)) GridMembers.AddVelocity(g, dv);
        }
    }

    // ───────────────────────────── DEV: station-keeping test grids ─────────────────────────────

    /// <summary>DEV: NPC grids feel relative motion too (to test station-keeping on the wrecks a test world has).</summary>
    public static bool DevNpcRelative;

    /// <summary>Grids holding station, for the harness: id -> name.</summary>
    public static readonly Dictionary<long, string> Holding = new Dictionary<long, string>();

    /// <summary>
    /// A grid holding station (SE1: dampeners + thrust authority): its dampeners on, and thrust on every side
    /// the force that cancels the pull (−m·a, in the grid's own axes) points to. The game's MaxThrustData:
    /// Positive = the push along each +axis, Negative = along each −axis (Thrust6Directions.Clamp).
    /// A ship turned so it cannot push against the pull drifts until it can.
    /// </summary>
    static bool StationKeeping(OrbitalGridComponent g, Vector3D accel, double N = 1.0)
    {
        bool on = false;
        try
        {
            var e = g.Entity;
            if (e != null && IsFinite(accel) && GridMembers.Mass(g) > 0 && e.Data.Has<Keen.Game2.Simulation.WorldObjects.Movement.DampeningData>()
                && e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt))
            {
                Vector3D need = -accel * GridMembers.Mass(g);   // world
                var q = (QuaternionD)e.Data.GetWorldTransform().Orientation;
                Vector3D l = QuaternionD.Inverse(q) * need;       // the grid's axes
                var P = mt.Regular.Positive; var Ng = mt.Regular.Negative;
                bool Ok(double c, float pos, float neg) => Math.Abs(c) < 1e-6 || (c > 0 ? pos >= c : neg >= -c);   // (no pull along an axis needs no thrust there)
                on = Ok(l.X, P.X, Ng.X) && Ok(l.Y, P.Y, Ng.Y) && Ok(l.Z, P.Z, Ng.Z);
                // (not free: what making the true force would cost, charged to its thrusters, x N in warp)
                if (on) StationKeepCharge.ChargeGrid(e, l, mt, N);
            }
        }
        catch { }
        if (on != Holding.ContainsKey(g.Id))
        {
            if (on) Holding[g.Id] = g.DisplayName; else Holding.Remove(g.Id);
            Event($"grid {g.Id} '{g.DisplayName}' {(on ? "holds station (dampeners, thrust): no relative pull" : "free: the relative pull is on it again")}");
        }
        return on;
    }
}
