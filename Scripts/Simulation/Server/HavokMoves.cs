#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// How a grid move may reach Havok's large-world manager without corrupting its cluster bookkeeping (engine-free: the
/// rules are tested offline, Tests/HavokMoveTests; the engine calls are ServerFrames.MoveGrid / RunDeferredMoves).
///
/// (2026-10-17: the game died twice on "There are still active bodies in native world N" -
/// HavokLargeWorldManager.RemoveCluster, from ReorderClusters / UpdateAddedEntityCluster. The engine removes a cluster once
/// its last dynamic body leaves, after destroying the statics its static tree finds INSIDE THE CLUSTER'S BOX. A static
/// body moved by SetWorldTransform goes through UpdateStaticEntityCluster: still touching its old box it stays (fine, the
/// box still finds it); clear of it with no world at the new place it leaves the world (fine); clear of it with ONE world
/// that CONTAINS it, it migrates (fine); clear of it next to a world that only overlaps it, it stays in the OLD world while
/// its tree entry moves away - the old box no longer finds it, and the cluster's removal asserts. Statics are therefore
/// never moved straight (MoveGrid refuses them unless the caller opts in); an opted-in static goes to the teleport phase,
/// and a move that would land in that last case goes in two hops: first clear of every world (it leaves its world for
/// good), the next frame to its place (a static in no world is added only by a world that contains it).)
/// </summary>
public static class HavokMoves
{
    /// <summary>m: a dynamic body's step the engine treats as ordinary movement - LargeWorldManagerSettings.WorldMargin
    /// (100 m: "no smaller than the maximum distance any one entity can traverse in a single simulation step"; the motion
    /// prediction box reaches MaximumMotionPredictionRange, 200 m). A per-tick placement within it stays a direct write;
    /// anything longer is a teleport and goes to the teleport phase, where the game's fast travel moves ships.</summary>
    public const double OrdinaryStep = 100.0;

    /// <summary>m added to a static's radius in the cluster tests: our box (the grid's AABB, any orientation) against the
    /// engine's (its collider's AABB) - judged conservatively, so a borderline case is a park, never a leak.</summary>
    public const double BoundsMargin = 10.0;

    public enum Route { Direct, Deferred, RefusedStatic }

    /// <summary>
    /// Where a move goes. A static: refused unless the caller opted in (allowStatic), then always the teleport phase (any
    /// static move, however short, can end in the leaking case). A dynamic grid with joints: the teleport phase with its
    /// joined set (as before). A dynamic grid on its own: direct within OrdinaryStep, the teleport phase beyond.
    /// A non-finite distance is a teleport (never a direct write of a value nobody checked).
    /// </summary>
    public static Route Choose(bool isStatic, bool allowStatic, bool constrained, double distance)
    {
        if (isStatic) return allowStatic ? Route.Deferred : Route.RefusedStatic;
        if (constrained) return Route.Deferred;
        return distance <= OrdinaryStep ? Route.Direct : Route.Deferred;
    }

    /// <summary>
    /// True when moving a static body (a sphere: centre and radius, BoundsMargin added here) from <paramref name="from"/> to
    /// <paramref name="to"/> cannot leave it in a world whose box no longer reaches it, given the physics worlds' boxes
    /// (IPhysicsWorldManager.GetWorlds; worlds[0..n)). Its own world is not known publicly, so every world its old place
    /// touches is taken as possibly its own (W), and each must be safe:
    ///  - the new centre inside W: the new box surely still touches it - it stays, and W's box still finds it;
    ///  - otherwise the engine may find the new box clear of W (surely so when ours is; maybe so when ours only grazes it -
    ///    staying is safe then too): it is safe only if it lands clean of the OTHER worlds - none touches the new box (it
    ///    leaves for none) or exactly one does and contains it whole (it migrates there).
    /// Touching no world now: safe (in no world, it is added only to a world that contains it).
    /// </summary>
    public static bool StaticMoveSafe(Vector3D from, Vector3D to, double radius, BoundingBoxD[] worlds, int n)
    {
        double r = Math.Max(0, radius) + BoundsMargin;
        var fromBox = BoundingBoxD.CreateFromHalfExtent(from, r);
        var toBox = BoundingBoxD.CreateFromHalfExtent(to, r);
        for (int i = 0; i < n; i++)
        {
            if (!worlds[i].Intersects(in fromBox)) continue;      // (not a world it can be in)
            if (worlds[i].Contains(in to)) continue;              // stays in it, still found by its box
            if (!LandsClean(toBox, worlds, n, i)) return false;   // leaves it - beside a world that only overlaps it
        }
        return true;
    }

    /// <summary>A box leaving world <paramref name="skip"/> lands clean: no other world touches it, or exactly one and that
    /// one contains it.</summary>
    static bool LandsClean(BoundingBoxD box, BoundingBoxD[] worlds, int n, int skip)
    {
        int touching = 0; bool contained = false;
        for (int i = 0; i < n; i++)
        {
            if (i == skip || !worlds[i].Intersects(in box)) continue;
            touching++;
            contained = worlds[i].Contains(in box) == ContainmentType.Contains;
        }
        return touching == 0 || (touching == 1 && contained);
    }

    /// <summary>
    /// A place clear of every world for a static's first hop (it leaves its world there for none): beside the destination
    /// along the first of the six axes (+Y, -Y, +X, -X, +Z, -Z) whose box at a distance of the largest world's size plus
    /// the body and a 5 km gap touches no world. False (none found, or a non-finite input): no park - the caller logs it.
    /// </summary>
    public static bool TryParkPoint(Vector3D to, double radius, BoundingBoxD[] worlds, int n, out Vector3D park)
    {
        park = to;
        double r = Math.Max(0, radius) + BoundsMargin;
        if (!(r < 1e9) || double.IsNaN(to.X + to.Y + to.Z) || double.IsInfinity(to.X + to.Y + to.Z)) return false;
        double big = 0;
        for (int i = 0; i < n; i++) { var s = worlds[i].Size; big = Math.Max(big, Math.Max(s.X, Math.Max(s.Y, s.Z))); }
        double d = big + 2 * r + 5000.0;
        for (int a = 0; a < 6; a++)
        {
            Vector3D axis = a switch { 0 => Vector3D.UnitY, 1 => -Vector3D.UnitY, 2 => Vector3D.UnitX, 3 => -Vector3D.UnitX, 4 => Vector3D.UnitZ, _ => -Vector3D.UnitZ };
            Vector3D p = to + axis * d;
            var box = BoundingBoxD.CreateFromHalfExtent(p, r);
            bool clear = true;
            for (int i = 0; i < n && clear; i++) if (worlds[i].Intersects(in box)) clear = false;
            if (clear) { park = p; return true; }
        }
        return false;
    }

    /// <summary>
    /// The cluster audit's test (HavokAudit): bodies in a native world beyond what the engine can account for when it removes
    /// that cluster - its dynamic list plus every static its tree finds in the cluster's box (an upper bound: a static in the
    /// box may be in no world, or a neighbour's). Positive: that many bodies the removal would not clean up (the assertion's
    /// precondition, once the last dynamic leaves). Ragdoll bodies, if any, also count here.
    /// </summary>
    public static long Unaccounted(long nativeBodies, int dynamicTracked, int staticsInBox)
        => nativeBodies - (long)dynamicTracked - staticsInBox;
}
