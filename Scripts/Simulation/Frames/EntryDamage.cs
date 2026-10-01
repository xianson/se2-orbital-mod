using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.VRage.Core;
using SEAerospace.Entry;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Reentry's damage (server): heat past a grid's tolerance wears down its FORWARD LAYER, the blocks facing the
/// air (within <see cref="LayerDepth"/> of its leading face). Gentle by rule: each block loses health in
/// proportion to the overshoot, and an entry never takes more than <see cref="MaxPerEntry"/> of any block's
/// health, so the rule alone never destroys a ship. Heavy armour as most of that layer doubles the tolerance.
/// Through the game's own damage receiver (its game-mode rules apply: creative takes none) and synced as usual.
/// </summary>
public static class EntryDamage
{
    public const double LayerDepth = 1.5;     // m behind the leading face
    public const double MaxPerEntry = 0.5;    // of a block's health, per entry
    public static string Status = "-";

    /// <summary>A grid's forward layer: the blocks furthest along its motion through the air (motion, world axes).</summary>
    static List<CubeBlockComponent> Forward(OrbitalGridComponent g, Vector3D motion)
    {
        var grid = g.Entity.TryGet<CubeGridComponent>();
        var all = new List<(CubeBlockComponent b, double d)>();
        if (grid == null) return new List<CubeBlockComponent>();
        var wt = g.Entity.Data.GetWorldTransform();
        grid.VisitAllBlocksWithComponent<CubeBlockComponent>(b =>
        {
            if (b == null) return;
            Vector3 local = (CubeGridCoords.GridToLocal(b.AABB.Min) + CubeGridCoords.GridToLocal(b.AABB.Max)) * 0.5f;
            all.Add((b, Vector3D.Dot(CubeGridCoords.LocalToWorld(local, wt), motion)));
        }, false);
        double front = double.MinValue;
        foreach (var x in all) if (x.d > front) front = x.d;
        var l = new List<CubeBlockComponent>();
        foreach (var x in all) if (x.d >= front - LayerDepth) l.Add(x.b);
        return l;
    }

    /// <summary>Harness: the blocks' health (lowest, mean) on a grid.</summary>
    public static string Health(OrbitalGridComponent g)
    {
        var grid = g.Entity.TryGet<CubeGridComponent>();
        if (grid == null) return "no grid";
        float lo = 1, sum = 0; int n = 0;
        grid.VisitAllBlocksWithComponent<CubeBlockComponent>(b => { if (b == null) return; lo = Math.Min(lo, b.HealthIntegrity); sum += b.HealthIntegrity; n++; }, false);
        return n == 0 ? "no blocks" : $"{n} block(s), health lowest {lo:P0}, mean {sum / n:P0}";
    }

    public static bool Damaged(OrbitalGridComponent g)
    {
        var grid = g.Entity.TryGet<CubeGridComponent>();
        bool d = false;
        grid?.VisitAllBlocksWithComponent<CubeBlockComponent>(b => { if (b != null && b.HealthIntegrity < 0.999f) d = true; }, false);
        return d;
    }

    static bool Heavy(CubeBlockComponent b)
    {
        try { return (b.Entity?.DebugName ?? "").IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; }
    }

    /// <summary>Most of the grid's forward layer is heavy armour (a heat shield: tolerance x2).</summary>
    public static bool Shielded(OrbitalGridComponent g, Vector3D motion)
    {
        try
        {
            var f = Forward(g, motion);
            int heavy = 0; foreach (var b in f) if (Heavy(b)) heavy++;
            return f.Count > 0 && heavy * 2 >= f.Count;
        }
        catch { return false; }
    }

    /// <summary>
    /// Wears the forward layer by fraction of each block's health (already capped per entry by the caller).
    /// Returns how many blocks were damaged.
    /// </summary>
    public static int Apply(OrbitalGridComponent g, Vector3D motion, double fraction)
    {
        if (!(fraction > 0) || g == null || !g.IsServer) return 0;
        try
        {
            var recv = g.Entity.TryGet<GridDamageReceiverComponent>();
            if (recv == null) { Status = "no damage receiver"; return 0; }
            int n = 0;
            foreach (var b in Forward(g, motion))
            {
                // (its full health: the definition's MaxHealth is not reachable from mod scripts)
                float max = b.HealthIntegrity > 1e-3f ? b.AbsoluteHealth / b.HealthIntegrity : 0;
                float hp = (float)(fraction * max);
                if (hp <= 0) continue;
                recv.DealDamage(b, hp);
                n++;
            }
            Status = $"{n} block(s) of '{g.DisplayName}' worn {fraction:P0}";
            return n;
        }
        catch (Exception e) { Status = "failed: " + e.Message; FrameHost.Fault("entry damage", e); return 0; }
    }
}
