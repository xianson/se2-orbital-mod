using System.Collections.Generic;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// Converts states / positions / directions between any two frames in the same tree,
    /// at a given time. Routes through the LOWEST COMMON ANCESTOR (not the root) — the
    /// same precision discipline as keeping berths near the world origin: never lift a
    /// conversion up to a distant frame (e.g. a Sun at 1.5e11 m) when the two frames
    /// already share a closer ancestor, or float cancellation eats ~1e-5 m. Velocity
    /// terms are carried through every rotating frame, so a state expressed in (say)
    /// LVLH comes back with the correct relative velocity.
    /// </summary>
    public static class FrameConvert
    {
        /// <summary>Full state (position + velocity) from one frame to another at time t.</summary>
        public static StateVector State(StateVector s, Frame from, Frame to, double t)
        {
            if (from == to) return s;
            Frame lca = LowestCommonAncestor(from, to);
            for (var f = from; f != lca; f = f.Parent) s = f.PoseAt(t).ToParent(s);
            foreach (var f in DownChain(lca, to)) s = f.PoseAt(t).ToLocal(s);
            return s;
        }

        /// <summary>Position only (velocity ignored).</summary>
        public static Vector3D Position(Vector3D p, Frame from, Frame to, double t)
            => State(new StateVector(p, Vector3D.Zero), from, to, t).Position;

        /// <summary>A free direction (rotation only — no translation, no velocity). For
        /// "down" vectors, thrust directions, prograde markers.</summary>
        public static Vector3D Direction(Vector3D dir, Frame from, Frame to, double t)
        {
            if (from == to) return dir;
            Frame lca = LowestCommonAncestor(from, to);
            for (var f = from; f != lca; f = f.Parent) dir = f.PoseAt(t).DirToParent(dir);
            foreach (var f in DownChain(lca, to)) dir = f.PoseAt(t).DirToLocal(dir);
            return dir;
        }

        // ---- internals ----

        private static Frame LowestCommonAncestor(Frame a, Frame b)
        {
            var ancestors = new HashSet<Frame>();
            for (var f = a; f != null; f = f.Parent) ancestors.Add(f);
            for (var f = b; f != null; f = f.Parent)
                if (ancestors.Contains(f)) return f;
            return null; // not in the same tree (shouldn't happen for one celestial root)
        }

        // Frames strictly below `top`, down to and including `target` (top..target exclusive of top).
        private static IEnumerable<Frame> DownChain(Frame top, Frame target)
        {
            var chain = new List<Frame>();
            for (var f = target; f != top && f != null; f = f.Parent) chain.Add(f);
            chain.Reverse();
            return chain;
        }
    }
}
