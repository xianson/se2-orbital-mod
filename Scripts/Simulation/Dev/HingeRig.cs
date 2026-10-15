#pragma warning disable
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.VRage.Core.Game.Components;

namespace OrbitalMod;

/// <summary>
/// DEV (harness 'hinge'): vanilla hinges on the server's grids, driven by code - the stiffness test for control
/// surfaces built as native subgrids (a flap on a hinge, aero commanding its angle). Lists hinges; sets their motor
/// (force, tau, damping, limits); holds one at a target angle with a P controller (velocity = Gain x error, clamped)
/// and records how well it holds: the error's mean and peak, and the flutter (the angle's frame-to-frame swing).
/// Everything through the hinge's public API (HingeComponent.Set*, HingeData) - no reflection.
/// </summary>
public static class HingeRig
{
    sealed class Rig
    {
        public long Grid; public Entity Block; public HingeComponent Hinge;
        public float Target = float.NaN, Gain = 4f;
        public float Last = float.NaN, ErrSum, ErrPeak, SwingSum, SwingPeak; public int Samples;
    }
    static readonly List<Rig> _rigs = new List<Rig>();

    /// <summary>Server tick (GridGen.ServerTick, the server thread): every driven hinge toward its target.</summary>
    public static void Tick()
    {
        if (_rigs.Count == 0) return;
        try
        {
            foreach (var r in _rigs)
            {
                if (float.IsNaN(r.Target) || r.Hinge == null) continue;
                if (!Angle(r, out float a)) continue;
                float err = r.Target - a;
                r.Hinge.SetVelocity(r.Gain * err);
                r.ErrSum += MathF.Abs(err); r.ErrPeak = MathF.Max(r.ErrPeak, MathF.Abs(err));
                if (!float.IsNaN(r.Last)) { float s = MathF.Abs(a - r.Last); r.SwingSum += s; r.SwingPeak = MathF.Max(r.SwingPeak, s); }
                r.Last = a; r.Samples++;
            }
        }
        catch (Exception e) { Status = "tick: " + e.Message; }
    }

    public static string Status = "";

    static bool Angle(Rig r, out float a)
    {
        a = 0f;
        if (r.Block == null || !r.Block.Data.TryGet<HingeComponent.HingeData>(out var d)) return false;
        a = d.CurrentAngle;
        return true;
    }

    static void Scan()
    {
        _rigs.Clear();
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || g.Entity == null) continue;
            var h = g.Entity.TryGet<HierarchyComponent>();
            if (h?.Children == null) continue;
            foreach (var child in h.Children)
            {
                var hinge = child?.TryGet<HingeComponent>();
                if (hinge != null) _rigs.Add(new Rig { Grid = g.Id, Block = child, Hinge = hinge });
            }
        }
    }

    static string Line(int i)
    {
        var r = _rigs[i];
        string ang = Angle(r, out float a) ? $"{a * 180f / MathF.PI:F2}°" : "?";
        string drive = float.IsNaN(r.Target) ? "free" : $"target {r.Target * 180f / MathF.PI:F1}° gain {r.Gain:F1}";
        string stats = r.Samples > 0
            ? $" | err mean {r.ErrSum / r.Samples * 180f / MathF.PI:F3}° peak {r.ErrPeak * 180f / MathF.PI:F3}° swing mean {r.SwingSum / Math.Max(1, r.Samples - 1) * 180f / MathF.PI:F4}° peak {r.SwingPeak * 180f / MathF.PI:F4}° ({r.Samples} ticks)"
            : "";
        return $"#{i} grid {r.Grid} angle {ang} vel {r.Hinge.Velocity:F3} force {r.Hinge.Force:F0} tau {r.Hinge.MotorTau:F2} damp {r.Hinge.MotorDamping:F2} {drive}{stats}";
    }

    /// <summary>hinge list | hinge set N force|tau|damp|min|max|limits V | hinge hold N DEG [GAIN] | hinge free N |
    /// hinge stats N (and reset them)</summary>
    public static string Command(string[] a) => GridGen.RunOnServer(() => CommandOnServer(a));

    static string CommandOnServer(string[] a)
    {
        string sub = a.Length > 1 ? a[1] : "list";
        if (sub == "list" || _rigs.Count == 0) Scan();
        if (sub == "list") return _rigs.Count == 0 ? "no hinges on the server's grids" : string.Join(" || ", Enumerable.Range(0, _rigs.Count).Select(Line));
        if (a.Length < 3 || !int.TryParse(a[2], out int n) || n < 0 || n >= _rigs.Count) return $"usage: hinge list|set|hold|free|stats N ... ({_rigs.Count} hinges)";
        var r = _rigs[n]; var ci = System.Globalization.CultureInfo.InvariantCulture;
        float V(int k) => a.Length > k ? float.Parse(a[k], ci) : 0f;
        switch (sub)
        {
            case "set":
                switch (a.Length > 3 ? a[3] : "")
                {
                    case "force": r.Hinge.SetForce(V(4)); break;
                    case "tau": r.Hinge.SetMotorTau(V(4)); break;
                    case "damp": r.Hinge.SetMotorDamping(V(4)); break;
                    case "limits": r.Hinge.SetEnableLocalLimits(V(4) != 0f); break;
                    default: return "set N force|tau|damp|limits V";
                }
                return Line(n);
            case "hold":
                r.Target = V(3) * MathF.PI / 180f; if (a.Length > 4) r.Gain = V(4);
                r.ErrSum = r.ErrPeak = r.SwingSum = r.SwingPeak = 0f; r.Samples = 0; r.Last = float.NaN;
                return Line(n);
            case "free":
                r.Target = float.NaN; r.Hinge.SetVelocity(0f);
                return Line(n);
            case "stats":
                string s = Line(n);
                r.ErrSum = r.ErrPeak = r.SwingSum = r.SwingPeak = 0f; r.Samples = 0; r.Last = float.NaN;
                return s;
        }
        return "hinge list|set|hold|free|stats";
    }
}
