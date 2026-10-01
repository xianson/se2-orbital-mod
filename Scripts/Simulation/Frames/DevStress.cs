using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// HARNESS: load tests for big ships (the aero mod's cost on giant grids). gridhold keeps a grid moving forward at
/// a speed (so per-frame aero runs on it); breakblocks destroys its blocks at a steady rate through the game's own
/// damage (as combat or a crash does). Server side; read with the status line 'frame' (worst / mean tick).
/// </summary>
public static class DevStress
{
    static readonly Dictionary<long, double> _hold = new Dictionary<long, double>();
    static (long id, int left, double every, double next) _break;
    static readonly Random _rng = new Random(7);
    public static string Status = "-";

    public static string Hold(long id, double speed)
    {
        lock (_hold) { if (speed <= 0) _hold.Remove(id); else _hold[id] = speed; }
        return speed <= 0 ? $"grid {id} released" : $"grid {id} held at {speed:F0} m/s forward";
    }

    public static string Break(long id, int n, double every)
    {
        _break = (id, n, Math.Max(0.0, every), 0);
        return $"breaking {n} block(s) of grid {id}, one every {every:F2} s";
    }

    /// <summary>Server tick.</summary>
    public static void Tick()
    {
        lock (_hold)
            foreach (var kv in _hold)
            {
                var g = GridMembers.Get(kv.Key);
                if (g == null || !g.IsServer) continue;
                var q = (QuaternionD)g.Entity.Data.GetWorldTransform().Orientation;
                GridMembers.SetVelocity(g, q * Vector3D.Forward * kv.Value);
            }
        if (_break.left <= 0) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _break.next) return;
        var bg = GridMembers.Get(_break.id);
        if (bg == null || !bg.IsServer) { Status = "break: no such grid"; _break.left = 0; return; }
        int per = _break.every <= 0 ? _break.left : 1;   // (every 0: all at once)
        int done = 0;
        try
        {
            var grid = bg.Entity.TryGet<CubeGridComponent>();
            var recv = bg.Entity.TryGet<GridDamageReceiverComponent>();
            var all = new List<CubeBlockComponent>();
            grid?.VisitAllBlocksWithComponent<CubeBlockComponent>(b => { if (b != null) all.Add(b); }, false);
            for (int i = 0; i < per && all.Count > 0; i++)
            {
                int k = _rng.Next(all.Count);
                recv?.DealDamage(all[k], 1e9f);
                all.RemoveAt(k);
                done++;
            }
            Status = $"broke {done} (left {_break.left - done}) of '{bg.DisplayName}', {all.Count} block(s) remain";
        }
        catch (Exception e) { Status = "break failed: " + e.Message; }
        _break.left -= Math.Max(done, 1);
        _break.next = now + _break.every;
    }
}
