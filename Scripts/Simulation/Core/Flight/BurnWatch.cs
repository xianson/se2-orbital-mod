namespace SEAerospace.Flight
{
    /// <summary>
    /// AUTO-BURN'S WATCH (engine-free; Tests/FlightTests). 2026-10-18, the UI audit (B2): an auto-burn on a ship whose
    /// thrust did nothing (atmospheric engines in vacuum: 0 kN) sat at "burning, 6.0 m/s left" forever, the card at
    /// "BURN 6.0 m/s · now" - no message, no end. The watch tells, from what the burn does each tick, when to give up:
    ///  - NoThrust: firing and lined up, yet what is left does not go down (or the thrust along the burn reads 0);
    ///  - Diverging: what is left grows well past its least (thrust pushing the wrong way);
    ///  - CantTurn: turning to the burn, yet the angle off it does not shrink;
    ///  - TimedOut: firing far longer than the burn should take;
    ///  - LeftSeat: the burn started from a seat and the pilot got out (a pause: it goes on when they sit back down).
    /// Times are wall-clock seconds (warp runs the game clock N times faster; the gyros and thrusters do not).
    /// </summary>
    public sealed class BurnWatch
    {
        public enum Verdict { Ok, NoThrust, Diverging, CantTurn, TimedOut, LeftSeat }

        /// <summary>Firing this long (s) without what is left going down by Progress: no thrust along the burn.</summary>
        public const double NoThrustSeconds = 6.0;
        /// <summary>The thrust along the burn reads 0 this long (s) while firing: no thrust (no need to wait for the window).</summary>
        public const double ZeroForceSeconds = 1.5;
        /// <summary>Turning this long (s) without the angle shrinking by TurnProgressDeg: it can't turn.</summary>
        public const double CantTurnSeconds = 20.0;
        public const double TurnProgressDeg = 1.0;
        /// <summary>Firing for longer than TimeOutFactor x the expected burn + TimeOutSlack (s), at least TimeOutMin: timed out.</summary>
        public const double TimeOutFactor = 3.0, TimeOutSlack = 20.0, TimeOutMin = 60.0;

        /// <summary>What counts as progress (m/s) on what is left: 0.5% of it, at least 0.02.</summary>
        public static double Progress(double left) => Math.Max(0.02, 0.005 * left);

        double _progressRef = double.NaN, _progressAt = double.NaN, _minLeft = double.NaN;
        double _turnRef = double.NaN, _turnAt = double.NaN, _zeroSince = double.NaN, _firingSince = double.NaN;
        bool _seatedAtStart, _started;

        /// <summary>A new burn (or a reset after a stop): nothing remembered.</summary>
        public void Reset()
        {
            _progressRef = _progressAt = _minLeft = _turnRef = _turnAt = _zeroSince = _firingSince = double.NaN;
            _seatedAtStart = false; _started = false;
        }

        /// <summary>Whether the burn started from a seat (a LeftSeat verdict pauses it).</summary>
        public bool StartedSeated => _seatedAtStart;

        /// <summary>
        /// One tick of an active auto-burn. now: wall clock (s). firing: thrust commanded (lined up, at or past the start, out
        /// of warp). turning: not lined up yet. offDeg: degrees between the ship's thrust and the burn (NaN unknown). left:
        /// m/s still to do. forceAlong: thrust (N) along the burn the ship can make (NaN unknown). seated: in a seat now.
        /// expectedBurn: how long the whole burn should take (s; NaN unknown).
        /// </summary>
        public Verdict Step(double now, bool firing, bool turning, double offDeg, double left, double forceAlong, bool seated, double expectedBurn)
        {
            if (!_started) { _started = true; _seatedAtStart = seated; }
            if (_seatedAtStart && !seated) { _progressAt = _turnAt = _zeroSince = _firingSince = double.NaN; return Verdict.LeftSeat; }

            if (firing)
            {
                _turnAt = _turnRef = double.NaN;
                if (double.IsNaN(_firingSince)) _firingSince = now;
                if (double.IsNaN(_progressAt) || double.IsNaN(_progressRef)) { _progressRef = left; _progressAt = now; }
                else if (left < _progressRef - Progress(_progressRef)) { _progressRef = left; _progressAt = now; }
                _minLeft = double.IsNaN(_minLeft) ? left : Math.Min(_minLeft, left);
                if (left > _minLeft + Math.Max(2.0, 0.2 * _minLeft)) return Verdict.Diverging;
                if (forceAlong <= 0) { if (double.IsNaN(_zeroSince)) _zeroSince = now; if (now - _zeroSince >= ZeroForceSeconds) return Verdict.NoThrust; }
                else _zeroSince = double.NaN;
                if (now - _progressAt >= NoThrustSeconds) return Verdict.NoThrust;
                double limit = double.IsNaN(expectedBurn) || double.IsInfinity(expectedBurn) ? TimeOutMin : Math.Max(TimeOutMin, TimeOutFactor * expectedBurn + TimeOutSlack);
                if (now - _firingSince >= limit) return Verdict.TimedOut;
                return Verdict.Ok;
            }

            // not firing: the progress window starts again when it fires again (a turn in between is no stall)
            _progressAt = double.NaN; _zeroSince = double.NaN; _firingSince = double.NaN;
            if (turning && !double.IsNaN(offDeg))
            {
                if (double.IsNaN(_turnAt) || double.IsNaN(_turnRef)) { _turnRef = offDeg; _turnAt = now; }
                else if (offDeg < _turnRef - TurnProgressDeg) { _turnRef = offDeg; _turnAt = now; }
                if (now - _turnAt >= CantTurnSeconds) return Verdict.CantTurn;
            }
            else { _turnAt = _turnRef = double.NaN; }
            return Verdict.Ok;
        }

        /// <summary>The player's words for a verdict ("Auto-burn stopped: no thrust along the burn"); null for Ok.</summary>
        public static string Message(Verdict v, double left)
        {
            switch (v)
            {
                case Verdict.NoThrust: return $"Auto-burn stopped: no thrust along the burn ({left:F1} m/s left)";
                case Verdict.Diverging: return $"Auto-burn stopped: the burn is going the wrong way ({left:F1} m/s left)";
                case Verdict.CantTurn: return "Auto-burn stopped: can't turn to the burn (gyros?)";
                case Verdict.TimedOut: return $"Auto-burn stopped: taking far too long ({left:F1} m/s left)";
                case Verdict.LeftSeat: return "Auto-burn paused: you left the seat";
                default: return null;
            }
        }

        /// <summary>The card's short word for a stop ("no thrust", "can't turn"...); null for Ok.</summary>
        public static string Short(Verdict v)
        {
            switch (v)
            {
                case Verdict.NoThrust: return "no thrust";
                case Verdict.Diverging: return "wrong way";
                case Verdict.CantTurn: return "can't turn";
                case Verdict.TimedOut: return "timed out";
                case Verdict.LeftSeat: return "paused";
                default: return null;
            }
        }
    }
}
