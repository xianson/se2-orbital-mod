using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// WARP IS NOT FREE (SE1 WarpResourceDrain). Warp runs the orbits' clock faster, but the game simulates
/// the grids at x1, so their batteries and tanks would age at x1: an hour of warp for a second of power.
/// Every warped frame, each battery and tank on a grid ON RAILS (in a frame) is given the rest of that
/// frame's time: its current flow (units per second, as the game's own container job integrates it)
/// times the surplus (warp - 1) / 60 s. A draining store drains at the warp rate, one being filled (solar,
/// a generator) fills at it; the game's own clamps (empty, full) hold. When a store on YOUR frame's grids
/// runs dry, warp drops to x1 with a notice. Grids off the rails (a base on a planet) run at x1 and are
/// not billed, nor NPC grids. Runs in the pre-physics phase (ServerPlanetBeacon's teleport job), server side.
/// </summary>
public static class WarpBill
{
    public static bool Enabled = true;
    public static string Status = "";
    private static long _last;

    /// <summary>Harness: every store on a railed grid: its grid, fill and flow.</summary>
    public static string Describe(Keen.VRage.Core.Game.Systems.Session session)
    {
        var names = new Dictionary<Entity, string>();
        lock (ServerFrames.FramesLock)
            foreach (var g in GridMembers.All())
                if (g.IsServer && g.Entity != null && SystemHost.Frames?.FindByMember(g.Id) != null) names[g.Entity] = g.DisplayName;
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var e in session.GetEntitiesOfType<ResourceContainerComponent>())
        {
            var top = e.GetTopLevelParent();
            if (top == null || !names.TryGetValue(top, out var gname)) continue;
            var c = e.TryGet<ResourceContainerComponent>();
            if (c == null) continue;
            n++;
            string type = "?"; try { type = c.ResourceType?.Name.ToString() ?? "?"; } catch { }
            sb.Append($" [{gname}: {type} {(float)c.CurrentChargeValue:G5}/{(float)c.MaxChargeValue:G5} flow {(float)c.CurrentChargeFlowRate:G4}]");
        }
        return $"{n} store(s) on railed grids:" + sb;
    }

    public static void Run(Keen.VRage.Core.Game.Systems.Session session)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_last != 0 && (now - _last) / (double)System.Diagnostics.Stopwatch.Frequency < 0.004) return;   // once a frame (one job per beacon)
        _last = now;
        double warp = SystemHost.Timescale;
        if (!Enabled || session == null || !(warp > 1.0) || !SystemHost.Built || SystemHost.Frames == null) return;
        double surplus = (warp - 1.0) / 60.0;   // s of game time this frame did not simulate

        // The grids on rails, and which of them are yours (in the player's frame).
        var railed = new Dictionary<Entity, (string name, bool yours)>();
        var pf = FrameHost.PlayerFrame;
        lock (ServerFrames.FramesLock)
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer || g.Entity == null || EncounterFrames.IsNpc(g)) continue;   // (NPC stations: the game's, left alone)
                var f = SystemHost.Frames.FindByMember(g.Id);
                if (f == null) continue;
                railed[g.Entity] = (g.DisplayName, pf != null && f.Id == pf.Id);
            }
        if (railed.Count == 0) { Status = $"x{warp:F0}: nothing on rails"; return; }

        int billed = 0; string dry = null;
        foreach (var e in session.GetEntitiesOfType<ResourceContainerComponent>())
        {
            var top = e.GetTopLevelParent();
            if (top == null || !railed.TryGetValue(top, out var owner)) continue;
            var c = e.TryGet<ResourceContainerComponent>();
            if (c == null) continue;
            try
            {
                FixedPoint flow = c.CurrentChargeFlowRate;
                if (flow == 0) continue;
                ref var data = ref c.ResourceEntity.GetWritePtr<ResourceContainerComponent.ResourceContainerData>();
                if (data.MaxCapacity == 0) continue;
                // (the bill itself: SensorModel.AgeStore, tested offline in Tests/SensingTests)
                double v = SEAerospace.Sensing.SensorModel.AgeStore((double)data.CurrentChargeValue, (double)flow, (double)data.MaxCapacity, surplus, out bool ranDry);
                if (ranDry && owner.yours && dry == null) dry = owner.name;
                data.CurrentChargeValue = (FixedPoint)v;
                billed++;
            }
            catch (Exception ex) { FrameHost.Fault("WarpBill", ex); }
        }
        Status = $"x{warp:F0}: {billed} store(s) on {railed.Count} railed grid(s) aged {surplus:F2} s a frame";
        if (dry != null)
        {
            SystemHost.Timescale = 1.0;
            SystemHost.WarpStopAt = double.NaN;
            WarpControl.Say($"Warp stopped: '{dry}' ran out of power or fuel");
            Log.Default?.Info($"[ORBIT-FRAME] warp -> x1: a store on '{dry}' ran dry");
        }
    }
}
