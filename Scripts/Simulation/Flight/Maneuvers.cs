using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// MANEUVER NODES (docs/design-maneuver-nodes.md), manual editor.
///
/// A node is a burn at an absolute time on the player's trajectory: delta-v in prograde / normal /
/// radial. Nodes chain: each applies to the trajectory the earlier ones produced, and the trajectory
/// is patched-conic (it follows SOI changes). On the map:
///  - click your trajectory to add a node there;
///  - drag a node's handles (P / R prograde-retrograde, N / AN normal, RO / RI radial out-in): the
///    further you pull, the faster the delta-v grows;
///  - drag the node itself to slide it along the trajectory; right-click it to delete it;
///  - the planned trajectory is drawn dashed; with a sector selected, its closest approach to that
///    sector's site is marked.
/// In flight, the next node is a HUD marker in the burn direction with the delta-v still to do,
/// counting down as you thrust (planned velocity minus actual, both at the current time); the node
/// completes below 0.1 m/s. Warp stops ahead of a node.
/// </summary>
public static partial class Maneuvers
{
    /// <summary>
    /// A burn. Pro/Nor/Rad are what the player set, relative to the trajectory at the time they set it;
    /// that fixes a TARGET (the post-burn orbit). Burning does not move the target: what is left is the
    /// target's velocity minus the current orbit's, at the node point (as KSP). Editing re-targets.
    /// </summary>
    public sealed class Node
    {
        public double T; public double Pro, Nor, Rad;
        public bool Dirty = true;               // re-target on the next trajectory pass
        public bool Auto;                       // fly it automatically at the burn start
        public string TBody; public KeplerianElements TAfter;
        /// <summary>The acceleration its target was planned with (a finite burn's shape depends on it).</summary>
        public double TAccel;
        public void Edit() => Dirty = true;
    }

    public static readonly List<Node> Nodes = new List<Node>();   // lock(Nodes)
    public static Node Selected;
    /// <summary>Under the mouse this frame (for the context menu): a node, or a time on the trajectory.</summary>
    public static Node HoverNode; public static double HoverT = double.NaN;

    public static void AddNodeAt(double T)
    {
        var n = new Node { T = T };
        lock (Nodes) Nodes.Add(n);
        Selected = n;
    }

    public static void Delete(Node n, bool andLater)
    {
        lock (Nodes)
        {
            if (andLater) Nodes.RemoveAll(x => x.T >= n.T); else Nodes.Remove(n);
        }
        if (Selected != null && !Nodes.Contains(Selected)) Selected = null;
    }

    public static void ClearAll() { lock (Nodes) Nodes.Clear(); Selected = null; }
    /// <summary>Your own orbit while riding: none kept (a new session, a new world).</summary>
    public static void ResetOwnOrbit() { lock (_own) _own.Clear(); lock (_ownDrawn) _ownDrawn.Clear(); }

    /// <summary>A new world: the last one's nodes (a reload doubled the saved ones), target, Lagrange plan and path cache.</summary>
    public static void ResetWorld()
    {
        ResetOwnOrbit();
        ClearAll();
        Target = null; _lag = null; Targets.ResetWorld(); ContactsTab.ResetWorld();
        _cT = double.NaN; _cLegs = null; _cApplied = null; _cOk = false;
    }

    /// <summary>The next burn, one line for the orbit card (null when none).</summary>
    public static string BurnLine;
    /// <summary>Seconds until the next burn starts (0: now) - with BurnLeft, the plot's short tag.</summary>
    public static double BurnIn;
    /// <summary>The next burn's direction (world axes) and what is left (m/s), from the last HUD tick.</summary>
    public static Vector3D BurnDirWorld; public static double BurnLeft;
    public static double WarpLead = 30.0;     // s: warp stops this long before a burn (time to line up; the setting Warp.StopLead)
    public const double AutoWarpLead = 5.0;   // s: before an auto-burn (it turns and fires by itself)

    /// <summary>How long before the next burn warp stops: shorter when that node flies itself.</summary>
    public static double WarpLeadAt(double t0)
    {
        double tn = NextNodeTime(t0);
        if (double.IsNaN(tn)) return WarpLead;
        bool auto = false;
        lock (Nodes) foreach (var n in Nodes) if (n.T == tn && n.Auto) auto = true;
        // An auto-burn turns to its burn during warp, so it needs no time to line up after; unless it
        // is still well off (warp faster than the gyros can follow the burn round): then the full lead.
        return auto && DevFlight.OffBurnDeg <= 5 ? AutoWarpLead : WarpLead;
    }
    public const double DoneDv = 0.1;         // m/s
    public static string Status = "";

    static readonly ColorSRGB YouColor = new ColorSRGB(0.35f, 0.88f, 1.00f, 1f);   // your orbit: cyan (as KSP)
    static readonly ColorSRGB PlanColor = new ColorSRGB(0.35f, 0.90f, 1.00f, 0.9f);
    static readonly ColorSRGB NodeColor = new ColorSRGB(0.35f, 0.90f, 1.00f, 1f);
    static readonly ColorSRGB ProColor = new ColorSRGB(0.85f, 0.95f, 0.30f, 1f);
    static readonly ColorSRGB NorColor = new ColorSRGB(0.85f, 0.45f, 1.00f, 1f);
    static readonly ColorSRGB RadColor = new ColorSRGB(0.35f, 0.85f, 1.00f, 1f);

}
