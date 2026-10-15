#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// A grid's last position read from the engine, kept on its OrbitalGridComponent (one per grid, no allocation, gone with the
/// grid): what GridMembers.Position answers for a grid deleted before its Unregister ran. Engine-free: the rule is tested
/// offline (Tests/GridPositionTests).
/// (2026-10-15: Position read the transform with no guard - a dead grid threw, ~200 ms, into whatever handler its caller had,
///  ~70 callers. A NaN sentinel was no option: `d > R` and `d <= R` are both false for NaN - a dead grid joined every cluster
///  whose test was "skip if past R" - and a NaN broke the views' distance sort. The last known place reads as "where it was":
///  right for every distance test; the movers and framers ask TryPosition and leave a dead grid alone.)
/// </summary>
internal struct LastKnownPosition
{
    Vector3D _at;
    int _known;

    /// <summary>A read seen (only a live grid's: once it is dead nothing writes it any more - no torn reads after).</summary>
    public void Seen(Vector3D p) { _at = p; System.Threading.Volatile.Write(ref _known, 1); }
    /// <summary>Ever read (a grid deleted before any read answers the origin, and false).</summary>
    public bool Known => System.Threading.Volatile.Read(ref _known) != 0;
    public Vector3D At => _at;

    /// <summary>
    /// GridMembers.TryPosition's rule. pending: a move queued for the next physics step (its target is where the grid is
    /// going - answered as before, alive or not). Else a live read (alive and read) is the answer and remembered; anything
    /// else answers the last read. True only for a live grid.
    /// </summary>
    public static bool Pick(bool pending, Vector3D pendingAt, bool alive, bool read, Vector3D readAt, ref LastKnownPosition memo, out Vector3D p)
    {
        if (pending) { p = pendingAt; return alive; }
        if (alive && read) { memo.Seen(readAt); p = readAt; return true; }
        p = memo._at;
        return false;
    }
}
