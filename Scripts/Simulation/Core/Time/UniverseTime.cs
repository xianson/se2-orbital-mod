using System;

namespace SEAerospace.Time
{
    /// <summary>
    /// The universe clock (the architecture's <c>UniverseTime</c>): a single persisted global
    /// time that everything analytic propagates from, plus a time-warp <see cref="Timescale"/>.
    /// It replaces <c>FrameManager</c>'s ad-hoc <c>_t</c> accumulator with a real, warpable,
    /// serializable clock owned by the session component.
    ///
    /// Model only — game-free (VRageMath / System.* only). The caller drives it: each real frame
    /// it calls <see cref="Advance"/> with the real wall-clock dt; the universe advances by
    /// <c>dt * Timescale</c>. Time-skipping long orbits (the headline feature Real Orbits can't do)
    /// is just running at a large timescale while everything is on rails — see <see cref="WarpPolicy"/>
    /// for the only-while-on-rails rule (docs/architecture-proximity-frames.md, "Time warp policy").
    ///
    /// C# 6 throughout (SE mod compiler): no out-var, tuples, local functions, switch expressions,
    /// or pattern matching.
    /// </summary>
    public class UniverseTime
    {
        /// <summary>The default warp ladder. 1 == real-time; each step is the unrestricted
        /// (all-on-rails) cap the caller may request. Configurable via the ctor.</summary>
        public static readonly double[] DefaultSteps = { 1.0, 2.0, 5.0, 10.0, 50.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0 };

        // The allowed timescale ladder, ascending. Always contains at least {1}. Cloned on
        // construction so the caller can't mutate our ladder out from under us.
        private readonly double[] _steps;

        // Current universe time (seconds) and current warp multiplier (one of _steps).
        private double _epochSeconds;
        private double _timescale;

        /// <summary>New clock at epoch 0, real-time (x1), with the default warp ladder.</summary>
        public UniverseTime()
        {
            _steps = NormalizeSteps(null);
            _epochSeconds = 0.0;
            _timescale = 1.0;
        }

        /// <summary>New clock with an explicit start epoch and warp ladder. Null/empty steps fall
        /// back to <see cref="DefaultSteps"/>. The starting timescale is the lowest step (x1).</summary>
        public UniverseTime(double epochSeconds, double[] steps)
        {
            _steps = NormalizeSteps(steps);
            _epochSeconds = epochSeconds;
            _timescale = _steps[0];
        }

        /// <summary>The current universe time, in seconds. This is the clock every analytic
        /// propagator should read (instead of a private frame accumulator).</summary>
        public double EpochSeconds
        {
            get { return _epochSeconds; }
            set { _epochSeconds = value; }
        }

        /// <summary>The current warp multiplier (always one of <see cref="AllowedSteps"/>, >= 1).
        /// Setting clamps to the nearest allowed step at or below the requested value (and never
        /// below x1).</summary>
        public double Timescale
        {
            get { return _timescale; }
            set { _timescale = ClampToLadder(value); }
        }

        /// <summary>The configured warp ladder (a copy; ascending, starts at 1).</summary>
        public double[] AllowedSteps
        {
            get { return (double[])_steps.Clone(); }
        }

        /// <summary>The largest configured step (the warp ceiling while on rails).</summary>
        public double MaxStep
        {
            get { return _steps[_steps.Length - 1]; }
        }

        /// <summary>The smallest configured step — always 1 (real-time).</summary>
        public double MinStep
        {
            get { return _steps[0]; }
        }

        /// <summary>
        /// Advance the universe by one real frame: adds <c>dt * Timescale</c> seconds to
        /// <see cref="EpochSeconds"/>. <paramref name="realDtSeconds"/> is the real wall-clock
        /// delta for the frame (e.g. 1/60). Non-positive dt is a no-op (paused / bad frame).
        /// </summary>
        public void Advance(double realDtSeconds)
        {
            if (realDtSeconds <= 0.0) return;
            _epochSeconds += realDtSeconds * _timescale;
        }

        /// <summary>
        /// Advance the universe by one real frame at an EXPLICIT timescale, WITHOUT changing the
        /// stored <see cref="Timescale"/>. This is how the warp-policy lock works: while anything is
        /// materialized the caller advances at the policy cap (x1) but the player's chosen warp stays
        /// stored, so it resumes automatically once everything is back on rails. Non-positive dt or a
        /// timescale below x1 is floored (x1 is real-time; the clock never runs backward or pauses
        /// the universe mid-session). Returns the seconds actually added.
        /// </summary>
        public double AdvanceScaled(double realDtSeconds, double timescale)
        {
            if (realDtSeconds <= 0.0) return 0.0;
            double ts = timescale < 1.0 ? 1.0 : timescale;
            double add = realDtSeconds * ts;
            _epochSeconds += add;
            return add;
        }

        /// <summary>Set the universe time directly (e.g. on load / restore). Drops a non-finite input
        /// and keeps the prior epoch — defense-in-depth so no caller can poison the clock with NaN/Inf
        /// (which would then propagate via AdvanceScaled's `+=` with no recovery).</summary>
        public void SetTime(double epochSeconds)
        {
            if (double.IsNaN(epochSeconds) || double.IsInfinity(epochSeconds)) return;
            _epochSeconds = epochSeconds;
        }

        /// <summary>Request a warp multiplier; it is clamped to the nearest allowed step at or
        /// below the request (and to >= 1). Returns the applied timescale.</summary>
        public double SetTimescale(double requested)
        {
            _timescale = ClampToLadder(requested);
            return _timescale;
        }

        /// <summary>Step up one rung on the warp ladder (no-op at the top). Returns the new
        /// timescale.</summary>
        public double StepUp()
        {
            int i = IndexOfCurrentStep();
            if (i < _steps.Length - 1) _timescale = _steps[i + 1];
            return _timescale;
        }

        /// <summary>Step down one rung on the warp ladder (no-op at x1). Returns the new
        /// timescale.</summary>
        public double StepDown()
        {
            int i = IndexOfCurrentStep();
            if (i > 0) _timescale = _steps[i - 1];
            return _timescale;
        }

        /// <summary>Drop straight back to real-time (x1) — the action a materialization triggers.</summary>
        public void ResetToRealtime()
        {
            _timescale = _steps[0];
        }

        // ---- persistence bridge -----------------------------------------------

        /// <summary>Snapshot epoch + timescale into a serializable POCO for the persistence layer.</summary>
        public UniverseTimeState Capture()
        {
            UniverseTimeState s = new UniverseTimeState();
            s.EpochSeconds = _epochSeconds;
            s.Timescale = _timescale;
            return s;
        }

        /// <summary>Restore epoch + timescale from a saved POCO. The timescale is clamped to the
        /// configured ladder (a save made under a different ladder still lands on a legal step).
        /// A null state is ignored.</summary>
        public void Apply(UniverseTimeState state)
        {
            if (state == null) return;
            _epochSeconds = state.EpochSeconds;
            _timescale = ClampToLadder(state.Timescale);
        }

        // ---- internals --------------------------------------------------------

        // Index of the current timescale on the ladder (the highest step <= _timescale).
        private int IndexOfCurrentStep()
        {
            int idx = 0;
            for (int i = 0; i < _steps.Length; i++)
            {
                if (_steps[i] <= _timescale + 1e-9) idx = i;
                else break;
            }
            return idx;
        }

        // Clamp an arbitrary value to the highest ladder step at or below it, floored at the
        // lowest step. (So a request between two rungs snaps DOWN to the legal lower rung.)
        private double ClampToLadder(double value)
        {
            if (value <= _steps[0]) return _steps[0];
            double chosen = _steps[0];
            for (int i = 0; i < _steps.Length; i++)
            {
                if (_steps[i] <= value + 1e-9) chosen = _steps[i];
                else break;
            }
            return chosen;
        }

        // Build a clean ascending ladder that always starts at 1 (real-time). Sorts, dedupes,
        // drops anything < 1, and guarantees 1 is present. Falls back to DefaultSteps if empty.
        private static double[] NormalizeSteps(double[] steps)
        {
            double[] src = (steps != null && steps.Length > 0) ? steps : DefaultSteps;

            // Copy + sort ascending (selection sort; tiny arrays, C# 6, no LINQ dependency).
            double[] work = new double[src.Length];
            Array.Copy(src, work, src.Length);
            for (int i = 0; i < work.Length; i++)
            {
                int min = i;
                for (int j = i + 1; j < work.Length; j++)
                    if (work[j] < work[min]) min = j;
                if (min != i) { double tmp = work[i]; work[i] = work[min]; work[min] = tmp; }
            }

            // Dedupe + drop < 1, force 1 to be present.
            double[] tmp2 = new double[work.Length + 1];
            int n = 0;
            bool hasOne = false;
            for (int i = 0; i < work.Length; i++)
            {
                double v = work[i];
                if (v < 1.0) continue;
                if (n > 0 && Math.Abs(tmp2[n - 1] - v) < 1e-9) continue; // dedupe
                if (Math.Abs(v - 1.0) < 1e-9) hasOne = true;
                tmp2[n++] = v;
            }
            if (!hasOne)
            {
                // Prepend 1.0.
                for (int i = n; i > 0; i--) tmp2[i] = tmp2[i - 1];
                tmp2[0] = 1.0;
                n++;
            }

            double[] result = new double[n];
            Array.Copy(tmp2, result, n);
            return result;
        }
    }
}
