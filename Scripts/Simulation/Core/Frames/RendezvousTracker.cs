namespace SEAerospace.Frames
{
    /// <summary>
    /// Stateful sticky merge gate for one frame pair (or one member-vs-frame). Feed it the
    /// instantaneous separation + relative speed each tick; it returns whether they should
    /// be merged, applying the <see cref="RendezvousParams"/> enter/exit hysteresis plus an
    /// enter dwell. Once merged it stays merged until the separation exceeds the exit range
    /// (DISTANCE-ONLY exit — there is no rel-speed exit; see RendezvousParams) — no thrash.
    /// </summary>
    public sealed class RendezvousTracker
    {
        private bool _merged;
        private double _dwell;

        public bool Merged { get { return _merged; } }

        /// <summary>Force the state (e.g. on load, or after an external merge/split).</summary>
        public void Reset(bool merged)
        {
            _merged = merged;
            _dwell = 0.0;
        }

        /// <summary>Advance the gate one step; returns the (possibly changed) merged state.</summary>
        public bool Update(double separation, double relSpeed, double dt, RendezvousParams p)
        {
            if (_merged)
            {
                // DISTANCE-ONLY exit (rob: "no speed exit") — a merged pair un-latches only when
                // it drifts clearly apart, never because the rel-speed rose. relSpeed still gates
                // the ENTER branch below.
                if (separation > p.ExitRangeMeters)
                {
                    _merged = false;
                    _dwell = 0.0;
                }
            }
            else
            {
                bool inGate = separation < p.EnterRangeMeters && relSpeed < p.EnterRelSpeedMps;
                if (inGate)
                {
                    _dwell += dt;
                    if (_dwell >= p.EnterDwellSeconds)
                    {
                        _merged = true;
                        _dwell = 0.0;
                    }
                }
                else
                {
                    _dwell = 0.0;   // conditions broke before dwell elapsed -> reset
                }
            }
            return _merged;
        }
    }
}
