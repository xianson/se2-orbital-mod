using System;
using System.Threading;

namespace SEAerospace.Time
{
    /// <summary>
    /// Once-a-frame work started from several parallel jobs (every planet's beacon runs the same job): claimed by the
    /// GAME's frame time, not the wall clock. Every job of one frame reads the same game time, so exactly one of them
    /// claims it, however late it starts; a paused game (its time standing still) claims nothing. The claim also says how
    /// much game time passed since the last one - what the frame's work is for.
    ///
    /// (2026-10-15, WarpBill: a 4 ms wall gate let a planet's job starting 4-8 ms after the winner bill warp a second time
    ///  in the same frame - batteries drained 2x, warp cut early - and each bill was a fixed 1/60 s while the clock
    ///  advanced by the real frame time: at 30 fps half the warp went unbilled.)
    ///
    /// Game-free, lock-free; tested offline (Tests/TimeTests).
    /// </summary>
    public sealed class FrameClaim
    {
        // the game time (TimeSpan ticks) last claimed; long.MinValue: none yet
        private long _last = long.MinValue;
        /// <summary>A game time this far (or more) BEHIND the last claim is a new clock (a new session) and claims afresh;
        /// less is a straggler from an older frame and claims nothing.</summary>
        public const long NewClockTicks = TimeSpan.TicksPerSecond;

        /// <summary>
        /// True for exactly one caller per new game time. <paramref name="dtSeconds"/>: game seconds since the last claim,
        /// at most <paramref name="maxDt"/> (a stall must not bill minutes at once); 0 on the first claim and when the game
        /// time went BACK by NewClockTicks or more (a new session's clock starting again: claimed afresh, not held until it
        /// caught up). A smaller step back claims nothing and leaves the last claim where it was.
        /// (2026-10-15: any step back claimed and moved the claim backwards - a straggler from an older frame let the current
        ///  frame claim a second time.)
        /// </summary>
        public bool TryClaim(long gameTicks, double maxDt, out double dtSeconds)
        {
            dtSeconds = 0;
            long last = Interlocked.Read(ref _last);
            if (gameTicks == last) return false;
            if (last != long.MinValue && gameTicks < last && last - gameTicks < NewClockTicks) return false;
            if (Interlocked.CompareExchange(ref _last, gameTicks, last) != last) return false;
            if (last != long.MinValue && gameTicks > last)
                dtSeconds = Math.Min((gameTicks - last) / (double)TimeSpan.TicksPerSecond, maxDt);
            return true;
        }

        /// <summary>Forget the last claim (a new world).</summary>
        public void Reset() { Interlocked.Exchange(ref _last, long.MinValue); }
    }
}
