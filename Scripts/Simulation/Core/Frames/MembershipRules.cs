using System;
using System.Collections.Generic;

namespace SEAerospace.Frames
{
    /// <summary>
    /// The world as the frames' membership rules see it - the game behind it in play (ServerFrames: GridWorld), a fake one
    /// offline (Tests/PlayerOrbitTests: MembershipCases). Grids by their mod id; a joined ship is the set of grids joined to
    /// one (sub-grids on hinges and rotors, docked craft).
    /// </summary>
    public interface IFrameWorld
    {
        /// <summary>A live server grid (registered now).</summary>
        bool IsLive(long id);
        /// <summary>Gone from the scene lately but may be coming back under the same id (an area respawn).</summary>
        bool MayReturn(long id);
        bool IsDynamic(long id);
        /// <summary>An NPC's grid: never an anchor.</summary>
        bool IsNpc(long id);
        double Mass(long id);
        /// <summary>The live grids joined to this one, itself included (just itself when it has no joints).</summary>
        IReadOnlyCollection<long> Joined(long id);
        /// <summary>The frame a grid is a member of, or -1.</summary>
        long FrameOf(long id);
    }

    /// <summary>
    /// THE FRAMES' MEMBERSHIP RULES, game-free and tested offline: who anchors a frame, who goes with a split, which members
    /// a step may still judge, who an arrival carries. (Each was a bug found in play: a light sub-grid kept as a ship's
    /// anchor; the player made anchor while the ship was only respawning; a split carrying a joined ship and then letting its
    /// grids go; an arrival moving a grid that had just split off.)
    /// </summary>
    public static class MembershipRules
    {
        /// <summary>A heavier joined grid takes the anchor from the current one only past this margin (no handover back and
        /// forth between twins).</summary>
        public const double HandoverMargin = 1.05;

        public enum AnchorChoice { Keep, Grid, Player, Wait, None }

        /// <summary>
        /// The anchor of a grid-anchored (or player-anchored) frame, from its members:
        ///  - the current grid anchor while it lives - unless a grid of its own ship, in the frame, is heavier (by the margin):
        ///    a ship is anchored by its heaviest grid;
        ///  - the current anchor gone but able to come back (an area respawn): Wait - nothing changes yet;
        ///  - else the heaviest live dynamic non-NPC member;
        ///  - none, and the player is a member: the player;
        ///  - else None (an empty frame: the prune's).
        /// </summary>
        public static AnchorChoice ElectAnchor(IFrameWorld w, long frameId, long currentAnchor, IEnumerable<long> members,
                                              long playerId, bool playerMember, out long anchor)
        {
            anchor = currentAnchor;
            bool gridAnchor = currentAnchor >= 0 && currentAnchor != playerId;
            if (gridAnchor && w.IsLive(currentAnchor) && !w.IsNpc(currentAnchor))
            {
                long best = currentAnchor; double bm = w.Mass(currentAnchor);
                foreach (long o in w.Joined(currentAnchor))
                {
                    if (o == currentAnchor || !w.IsLive(o) || w.IsNpc(o) || !w.IsDynamic(o) || w.FrameOf(o) != frameId) continue;
                    double m = w.Mass(o);
                    if (m > bm * HandoverMargin) { bm = m; best = o; }
                }
                if (best == currentAnchor) return AnchorChoice.Keep;
                anchor = best; return AnchorChoice.Grid;
            }
            if (gridAnchor && !w.IsLive(currentAnchor) && w.MayReturn(currentAnchor)) return AnchorChoice.Wait;
            long heavy = -1; double hm = -1;
            foreach (long m in members)
            {
                if (m == playerId || !w.IsLive(m) || w.IsNpc(m) || !w.IsDynamic(m) || w.FrameOf(m) != frameId) continue;
                double mass = w.Mass(m);
                if (mass > hm) { hm = mass; heavy = m; }
            }
            if (heavy >= 0) { anchor = heavy; return heavy == currentAnchor ? AnchorChoice.Keep : AnchorChoice.Grid; }
            if (playerMember && playerId >= 0) { anchor = playerId; return currentAnchor == playerId ? AnchorChoice.Keep : AnchorChoice.Player; }
            return AnchorChoice.None;
        }

        /// <summary>
        /// A grid split off from frame <paramref name="fromFrame"/> into a new frame, its joined ship moved with it: which grids
        /// of that ship the new frame takes - the old frame's, and any in no frame (carried to the new berth, left out they were
        /// on no rails). Grids in a third frame stay where they are.
        /// </summary>
        public static List<long> SplitTakes(IFrameWorld w, long fromFrame, long splitGrid, IEnumerable<long> movedWith)
        {
            var take = new List<long>();
            foreach (long o in movedWith)
            {
                if (o == splitGrid || !w.IsLive(o)) continue;
                long f = w.FrameOf(o);
                if (f == fromFrame || f < 0) take.Add(o);
            }
            return take;
        }

        /// <summary>
        /// A step of frame <paramref name="frameId"/> may judge (split, let go, steer, carry on arrival) only grids still its
        /// own: one taken by a split or a merge earlier the same tick has its queued place at another berth.
        /// </summary>
        public static bool StillJudged(IFrameWorld w, long frameId, long id) => w.IsLive(id) && w.FrameOf(id) == frameId;

        /// <summary>What an arrival of frame <paramref name="frameId"/> carries into the planet's space: its live grids still its own.</summary>
        public static List<long> ArrivalCarries(IFrameWorld w, long frameId, IEnumerable<long> snapshot)
        {
            var l = new List<long>();
            foreach (long id in snapshot) if (StillJudged(w, frameId, id)) l.Add(id);
            return l;
        }

        /// <summary>
        /// A member found missing: prune it only when it cannot come back (an area respawn keeps its id) and it has been
        /// missing past the grace.
        /// </summary>
        public static bool Prune(IFrameWorld w, long id, double missingSeconds, double grace) => !w.IsLive(id) && !w.MayReturn(id) && missingSeconds > grace;

        /// <summary>Periapsis this far over the planet's frame border (m) for an orbit to count as stable.</summary>
        public const double StableMargin = 5000;

        /// <summary>
        /// A frame's orbit stays clear of its planet's border: bound (an escape is no place for a station either) and its
        /// periapsis over the border with <see cref="StableMargin"/> to spare. <paramref name="borderRadius"/>: the planet's
        /// frame border (its shell). (A body with no planet frame - the star - is always stable: the caller's to say.)
        /// </summary>
        public static bool OrbitStable(SEAerospace.Orbital.KeplerianElements el, double borderRadius)
        {
            if (double.IsNaN(el.SemiMajorAxis) || double.IsInfinity(el.SemiMajorAxis) || !(el.Eccentricity < 1.0)) return false;
            return el.PeriapsisRadius > borderRadius + StableMargin;
        }

        public enum StaticKind { None, Grid, Rock }

        /// <summary>
        /// What fixed thing anchors a (non-encounter) frame:
        ///  - a static grid in it (a station, a base): always - on an unstable orbit it is made a ship first (the game's own
        ///    static/dynamic switch), and anchors as one from then on;
        ///  - else a rock near its centre - ONLY on a stable orbit. A rock cannot be made a ship and cannot move: a frame
        ///    decaying into an atmosphere (or escaping) anchored on one was "the rock's orbit" going down, while the rock stayed
        ///    in its berth for good (and every frame passing it was captured onto that orbit). Unstable, the rock is left out of
        ///    the frame: it is anchored by its heaviest grid, or the player (ElectAnchor), and the rock is just scenery;
        ///  - else none (the grids' or the player's election).
        /// (No hysteresis needed: a rock-anchored frame's orbit is fixed - nothing folds into a static anchor's frame - so only a
        ///  grid-anchored frame crosses the line, and once rock-anchored it stays on its stable orbit.)
        /// </summary>
        public static StaticKind StaticAnchorKind(bool staticGridIn, bool rockNear, bool orbitStable)
        {
            if (staticGridIn) return StaticKind.Grid;
            if (rockNear && orbitStable) return StaticKind.Rock;
            return StaticKind.None;
        }
    }
}
