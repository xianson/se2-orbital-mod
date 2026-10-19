using SEAerospace.MapUi;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE MANEUVER EDITOR'S UNDO (2026-10-18, UI audit C5: "no undo for node edits"). Before each edit - a node added (a click
/// on the path, the menu, a porkchop cell), a handle or a node dragged, a node removed, auto-burn flipped, exact values
/// typed - the plan as it was is kept (SEAerospace.MapUi.EditHistory: at most 50, the same plan twice kept once); Ctrl+Z on
/// the map puts the last one back. Client only: the plan is this player's (Nodes).
/// </summary>
public static partial class Maneuvers
{
    static readonly EditHistory _history = new EditHistory();
    static readonly List<NodeSnap> _snapNow = new List<NodeSnap>();

    /// <summary>The plan now, as the undo keeps it (into a reused list).</summary>
    static List<NodeSnap> PlanNow()
    {
        _snapNow.Clear();
        lock (Nodes) foreach (var n in Nodes) _snapNow.Add(new NodeSnap(n.T, n.Pro, n.Nor, n.Rad, n.Auto));
        return _snapNow;
    }

    /// <summary>Keep the plan as it is now: call BEFORE changing it (safe to call when nothing changes after).</summary>
    public static void UndoMark() => _history.Mark(PlanNow());

    /// <summary>Plans kept to go back to.</summary>
    public static int UndoDepth => _history.Count;

    /// <summary>Back to the plan before the last edit (true: changed). The node selected keeps its selection when one is at
    /// its time in the plan put back.</summary>
    public static bool Undo()
    {
        if (!_history.Undo(PlanNow(), out var prev)) return false;
        double selT = Selected?.T ?? double.NaN;
        Node sel = null;
        lock (Nodes)
        {
            Nodes.Clear();
            foreach (var s in prev)
            {
                var n = new Node { T = s.T, Pro = s.Pro, Nor = s.Nor, Rad = s.Rad, Auto = s.Auto };
                Nodes.Add(n);
                if (s.T == selT) sel = n;
            }
        }
        Selected = sel;
        _drag = Drag.None;
        return true;
    }

    /// <summary>A new world: no undo into the last one's plan.</summary>
    public static void UndoClear() => _history.Clear();
}
