namespace SEAerospace.Frames
{
    /// <summary>
    /// THE PLAYER, AS OF ONE TICK: which frame, riding it or not, where and how fast in it, at its anchor or not, seated,
    /// the suit's station-keeping. Built fresh every tick from these defaults (no frame, not riding, nowhere) and published
    /// whole (OrbitalMod.FrameHost): a branch that does not know a fact leaves its default - never the last tick's (the
    /// separate statics this replaces went stale whenever one branch forgot one: the wrong speed after a walk, a hold
    /// claimed while seated, a frame kept after leaving it) - and a reader on another thread sees one tick's state, never
    /// half of two.
    /// </summary>
    public sealed class PlayerState
    {
        /// <summary>No player yet (before the first tick, a new session).</summary>
        public static readonly PlayerState None = new PlayerState();

        /// <summary>The frame whose orbit is the player's (null: none - a planet's cell, HighSpeed, nothing near).</summary>
        public ProximityFrame Frame;
        /// <summary>The frame the player rides (anchored by something else), -1: not riding (its anchor, or no frame).</summary>
        public long RiderFrame = -1;
        /// <summary>Riding: the offset from the berth's centre and the velocity in the frame (window axes; velocity true, not warped).</summary>
        public Vector3D RiderOffset, RiderVelocity;
        /// <summary>At the frame's anchor (in it, beside it, within its box): no plot about it.</summary>
        public bool AtAnchor;
        /// <summary>The suit's dampeners: station-keeping in the frame (on foot only).</summary>
        public bool Dampeners;
        public bool Seated;
    }
}
