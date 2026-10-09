using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.Frames
{
    /// <summary>
    /// THE RENDEZVOUS PICTURE (game-free; tested offline: Tests/PlayerOrbitTests). What the HUD plot, the map's
    /// Rendezvous tab, the closest-approach marker and the orbit card's target line show, from whose orbit, about what:
    ///  - <see cref="Curvilinear"/>: you in a target's own curvilinear frame (along its orbit, radial, out of its plane) -
    ///    the one definition both plots use (they had two radials).
    ///  - <see cref="Common"/>: the lowest body two things both go round.
    ///  - <see cref="Closest"/>: the closest approach of two motions over a span (sampled, refined).
    ///  - <see cref="Choose"/>: which plot the HUD shows (the anchor's, the target's, the disc) from who you are and what
    ///    you target.
    ///  - <see cref="OwnOrbit"/>: your own orbit while riding a frame - the frame's plus your offset and velocity in it -
    ///    kept while it still says where you are (it changes with a burn, not with every tick's measurement).
    /// </summary>
    public static class RendezvousPlot
    {
        /// <summary>b in a's curvilinear frame (both about the same body): (along a's orbit - the arc at a's radius,
        /// + ahead; radial - in a's plane, + away from the body; cross - along a's orbit normal). NaN: a has no orbit plane.</summary>
        public static Vector3D Curvilinear(StateVector a, StateVector b)
        {
            Vector3D h = Vector3D.Cross(a.Position, a.Velocity);
            double r0 = a.Position.Length();
            if (!(r0 > 0) || !(h.LengthSquared() > 0)) return new Vector3D(double.NaN, double.NaN, double.NaN);
            Vector3D N = Vector3D.Normalize(h), R = a.Position / r0, T = Vector3D.Cross(N, R);
            double cross = Vector3D.Dot(b.Position, N);
            Vector3D inPlane = b.Position - N * cross;
            double along = r0 * Math.Atan2(Vector3D.Dot(inPlane, T), Vector3D.Dot(inPlane, R));
            return new Vector3D(along, inPlane.Length() - r0, cross);
        }

        /// <summary>The period of a state's orbit about a body (vis-viva: an ellipse's own, not a circle's at its radius now);
        /// fallback when it is not bound (an escape) or not a state.</summary>
        public static double PeriodAbout(StateVector s, double mu, double fallback)
        {
            double r = s.Position.Length(), v2 = s.Velocity.LengthSquared();
            if (!(r > 0) || !(mu > 0)) return fallback;
            double inv = 2 / r - v2 / mu;   // 1/a
            if (!(inv > 0)) return fallback;
            double a = 1 / inv;
            return 2 * Math.PI * Math.Sqrt(a * a * a / mu);
        }

        /// <summary>
        /// What is left of a burn whose node is behind you (a late burn, one paused by warp): the target orbit's velocity
        /// minus yours AT THE NODE POINT (both orbits taken back to tNode) - not now, where the two have drifted apart (that
        /// grew with lateness, up to twice the orbital speed half an orbit on, and a burn chasing it never converged).
        /// </summary>
        public static Vector3D RemainingBurn(KeplerianElements current, KeplerianElements target, double tNode)
            => OrbitPropagation.StateAt(target, tNode).Velocity - OrbitPropagation.StateAt(current, tNode).Velocity;

        /// <summary>The lowest body both go round (null: none in common - different trees).</summary>
        public static GravityBody Common(GravityBody a, GravityBody b)
        {
            var up = new HashSet<GravityBody>();
            for (var x = a; x != null; x = x.Parent) up.Add(x);
            for (var y = b; y != null; y = y.Parent) if (up.Contains(y)) return y;
            return null;
        }

        public struct Approach { public bool Ok; public double T, Distance, RelSpeed; }

        /// <summary>The closest approach of you(t) and target(t) (same body; null: not defined then) over [t0, t1].</summary>
        public static Approach Closest(Func<double, StateVector?> you, Func<double, StateVector?> target, double t0, double t1, int samples = 0)
        {
            var best = new Approach { Distance = double.MaxValue };
            double span = t1 - t0;
            if (!(span > 0) || you == null || target == null) return best;
            int n = samples > 0 ? samples : Math.Min(800, Math.Max(120, (int)(span / 15)));
            double Dist(double tk)
            {
                var a = you(tk); var b = target(tk);
                return a.HasValue && b.HasValue ? (a.Value.Position - b.Value.Position).Length() : double.MaxValue;
            }
            double bt = double.NaN;
            var ds = new double[n + 1];
            for (int k = 0; k <= n; k++) { double tk = t0 + span * k / n, d = Dist(tk); ds[k] = d; if (d < best.Distance) { best.Distance = d; bt = tk; } }
            if (double.IsNaN(bt)) return best;
            // every sampled local minimum refined, not only the lowest sample: a sharp pass between two samples on one
            // revolution reads higher there than a broad one sampled dead on another - and was missed
            for (int k = 0; k <= n; k++)
            {
                if (ds[k] == double.MaxValue) continue;
                if ((k > 0 && ds[k - 1] < ds[k]) || (k < n && ds[k + 1] < ds[k])) continue;
                double lo = Math.Max(t0, t0 + span * (k - 1) / n), hi = Math.Min(t1, t0 + span * (k + 1) / n);
                for (int q = 0; q < 50; q++)
                {
                    double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                    if (Dist(m1) < Dist(m2)) hi = m2; else lo = m1;
                }
                double tr = 0.5 * (lo + hi), dr = Dist(tr);
                if (dr < best.Distance) { best.Distance = dr; bt = tr; }
            }
            best.Ok = true; best.T = bt;
            var ya = you(bt); var ta = target(bt);
            best.RelSpeed = ya.HasValue && ta.HasValue ? (ya.Value.Velocity - ta.Value.Velocity).Length() : double.NaN;
            return best;
        }

        public enum Plot { None, Disc, Anchor, Target }

        public struct Who
        {
            /// <summary>Seated, or in your suit with the jetpack on (walking: nothing is drawn).</summary>
            public bool Flying;
            /// <summary>You ride a frame anchored by something else (your motion about its anchor is a plot).</summary>
            public bool Riding;
            /// <summary>At the frame's anchor (in it, or beside it): no plot about it.</summary>
            public bool AtAnchor;
            /// <summary>A target that is not a body (a site, a rock, a station).</summary>
            public bool HasTarget;
            /// <summary>The target is the frame you are in (you are there already).</summary>
            public bool TargetIsOwnFrame;
            /// <summary>The target and you go round some body other than the star (a plot about it means something).</summary>
            public bool TargetSharesBody;
        }

        /// <summary>The HUD's plot: the target's (a target you are not already at, round your body) over the anchor's;
        /// the anchor's while riding away from it; else the disc. Nothing when walking.</summary>
        public static Plot Choose(Who w)
        {
            if (!w.Flying) return Plot.None;
            if (w.HasTarget && !w.TargetIsOwnFrame && w.TargetSharesBody) return Plot.Target;
            if (w.Riding && !w.AtAnchor) return Plot.Anchor;
            return Plot.Disc;
        }

        /// <summary>Elements for a state, also a radial one (zero angular momentum: a fall straight down, at rest over
        /// the body - nudged sideways by a hair, a needle-thin ellipse that follows the fall).</summary>
        public static KeplerianElements Capture(StateVector s, double mu, double t)
        {
            Vector3D r = s.Position, v = s.Velocity;
            double rm = r.Length(), vm = v.Length();
            double h = Vector3D.Cross(r, v).Length();
            if (rm > 1 && h < 1e-6 * rm * Math.Max(vm, 1.0))
            {
                Vector3D radial = r / rm;
                Vector3D axis = Math.Abs(radial.Z) < 0.9 ? Vector3D.UnitZ : Vector3D.UnitX;
                Vector3D perp = Vector3D.Normalize(Vector3D.Cross(axis, radial));
                v += perp * Math.Max(0.01, 1e-5 * vm);
            }
            return OrbitalMath.ToElements(new StateVector(r, v), mu, t);
        }

        /// <summary>
        /// Your own orbit while riding a frame: the frame's state plus your offset and velocity in it (celestial
        /// axes) - or, in a planet's space, your own state directly. Kept while it still says where you are, so a plan
        /// built on it is not re-solved every tick for the measurement's noise; a burn, a push off a wall, a new frame:
        /// new elements.
        ///
        /// SMOOTHED: the kept orbit is solved from a least-squares fit over the last few seconds of measurements, not
        /// from one. One measurement's velocity noise becomes an along-track error of ~3 T dv a revolution (the old
        /// single-measurement gate, 0.05 m/s, let the plan lag ~860 m a revolution), and a gate tighter than the noise
        /// re-solved every tick. Averaged over N measurements the noise is ~sqrt(N) smaller, so the gate can be that much
        /// tighter without churn.
        ///  - The fit: each measurement is kept as its residual from the kept orbit (position, velocity). Over a few
        ///    seconds the difference of two nearby Kepler orbits is linear (dr + dv tau: the gravity-gradient term,
        ///    n^2 tau^2 ~ 1e-5, is far under the noise), so the state at t is a 2-parameter weighted least squares per
        ///    axis (positions weighted 1/SigmaP^2, velocities 1/SigmaV^2) - one pass over a fixed ring, no allocation.
        ///  - The gate: re-solved when the fit leaves the kept orbit by more than K x the fit's own standard error
        ///    (scaled by the residuals' reduced chi-square, so a noisier feed than the nominal widens it by itself),
        ///    floored at FitPosTol / FitVelTol, never wider than PosTol / VelTol (the old gate).
        ///  - A real change (a burn, a bump, a teleport): a measurement further from the fit than PosTol / VelTol -
        ///    beyond anything the noise does - drops the window and starts over from that measurement, at once.
        ///  - A gap longer than the window (warp: game seconds per tick) drops the window too: a lone measurement is
        ///    gated exactly as before (PosTol / VelTol), so warp is no worse than unsmoothed.
        /// </summary>
        public sealed class OwnOrbit
        {
            /// <summary>A real change: a measurement this far from the fit (the single measurement's noise bound - the old gate).</summary>
            public double PosTol = 25, VelTol = 0.05;
            /// <summary>The smoothed gate's floors (with the window full the fit's noise is well under these).</summary>
            public double FitPosTol = 2, FitVelTol = 0.002;
            /// <summary>The measurement's nominal noise (1 sigma per axis): the fit's weights and its standard error's floor.</summary>
            public double SigmaP = 1, SigmaV = 0.01;
            /// <summary>Game seconds of measurements the fit spans (0: no smoothing - each measurement alone, the old gate).</summary>
            public double Window = 5;
            const double K = 4;          // standard errors the fit may leave the kept orbit by (noise, not a change)
            const int Cap = 256;         // ~4 s at 60 Hz
            readonly double[] _rt = new double[Cap];
            readonly Vector3D[] _rp = new Vector3D[Cap], _rv = new Vector3D[Cap];
            int _head, _n;   // ring: the newest at _head - 1, _n of them
            double _lastT;
            KeplerianElements _el; long _frame = long.MinValue; bool _has;
            public int Rebuilds { get; private set; }
            /// <summary>Measurements in the fit now (tests).</summary>
            public int WindowCount => _n;

            public KeplerianElements Get(long frameId, KeplerianElements frameEl, Vector3D offset, Vector3D velocity, double t)
            {
                var fc = OrbitPropagation.StateAt(frameEl, t);
                var mine = new StateVector(fc.Position + offset, fc.Velocity + velocity);
                var el = Observe(frameId, mine, frameEl.Mu, t, out bool ok);
                return ok ? el : frameEl;
            }

            /// <summary>As Get; false (and the frame's elements) when no orbit could be solved from the measurement.</summary>
            public bool TryGet(long frameId, KeplerianElements frameEl, Vector3D offset, Vector3D velocity, double t, out KeplerianElements el)
            {
                var fc = OrbitPropagation.StateAt(frameEl, t);
                el = Observe(frameId, new StateVector(fc.Position + offset, fc.Velocity + velocity), frameEl.Mu, t, out bool ok);
                if (!ok) el = frameEl;
                return ok;
            }

            /// <summary>As Get, from your own state directly (a planet's space: the local orbit measured each tick).</summary>
            public KeplerianElements GetState(long key, StateVector mine, double mu, double t)
                => Observe(key, mine, mu, t, out _);

            KeplerianElements Observe(long key, StateVector mine, double mu, double t, out bool ok)
            {
                ok = true;
                // (a new key, or the clock gone back more than the window - a reload: no history to fit)
                if (!_has || _frame != key || !(t >= _lastT - Window)) return Restart(key, mine, mu, t, out ok);
                if (t - _lastT > Window) { _n = 0; _head = 0; }   // (a gap: what the window held is too far back for the linear fit)
                var p = OrbitPropagation.StateAt(_el, t);
                Vector3D dr = mine.Position - p.Position, dv = mine.Velocity - p.Velocity;
                if (!Finite(dr) || !Finite(dv)) return Restart(key, mine, mu, t, out ok);
                // a real change: this measurement is not the motion the window has been seeing. The old gate's bound, fixed - not
                // widened by the window's scatter: a steady burn is a ramp the linear fit misreads as noise, and a widened bound
                // let the kept orbit trail a burn by tenths of a m/s (the burn readout stops at 0.1); fixed, it trails as before
                Fit(t, out Vector3D fr, out Vector3D fv, out _, out _);
                if ((dr - fr).Length() > PosTol || (dv - fv).Length() > VelTol) return Restart(key, mine, mu, t, out ok);
                // Several callers ask in one tick (the plan, the map, the HUD) with the same measurement: one sample per
                // moment, else the same tick is counted many times (and a caller a hair behind is only gated)
                if (t > _lastT || _n == 0) { Push(t, dr, dv); _lastT = Math.Max(_lastT, t); }
                Fit(t, out fr, out fv, out double posTol, out double velTol);
                if (fr.Length() <= posTol && fv.Length() <= velTol) return _el;
                // the fit has left the kept orbit: re-solved from the smoothed state, not from this measurement
                var el = Capture(new StateVector(p.Position + fr, p.Velocity + fv), mu, t);
                if (!Finite(new Vector3D(el.SemiMajorAxis, el.Eccentricity, el.Inclination))) return _el;
                // the window's residuals re-based on the new orbit: near t it is the old one plus (fr + fv tau, fv) - the fit's
                // own linear model - so the next fit starts at zero instead of solving the same difference again
                for (int k = 0; k < _n; k++)
                {
                    int i = (_head - 1 - k + Cap) % Cap;
                    double tau = _rt[i] - t;
                    _rp[i] -= fr + fv * tau; _rv[i] -= fv;
                }
                _el = el; Rebuilds++;
                return el;
            }

            /// <summary>Elements from this one measurement; the window starts over with it.</summary>
            KeplerianElements Restart(long key, StateVector mine, double mu, double t, out bool ok)
            {
                _n = 0; _head = 0;
                var el = Capture(mine, mu, t);
                if (!Finite(new Vector3D(el.SemiMajorAxis, el.Eccentricity, el.Inclination))) { _has = false; ok = false; return el; }
                _el = el; _frame = key; _has = true; _lastT = t; Rebuilds++; ok = true;
                Push(t, Vector3D.Zero, Vector3D.Zero);   // (the capture passes through it exactly)
                return el;
            }

            void Push(double t, Vector3D dr, Vector3D dv)
            {
                if (!(Window > 0)) { _n = 0; _head = 0; }   // (no smoothing: only the newest)
                _rt[_head] = t; _rp[_head] = dr; _rv[_head] = dv;
                _head = (_head + 1) % Cap;
                if (_n < Cap) _n++;
            }

            /// <summary>The window's least-squares state at t, relative to the kept orbit (fr, fv), and how far the kept orbit
            /// may be from it before that is more than noise (posTol, velTol). Measurements older than the window are dropped.</summary>
            void Fit(double t, out Vector3D fr, out Vector3D fv, out double posTol, out double velTol)
            {
                fr = Vector3D.Zero; fv = Vector3D.Zero; posTol = PosTol; velTol = VelTol;
                // (dropped from the old end: the ring's oldest are the furthest back)
                while (_n > 0 && t - _rt[(_head - _n + Cap) % Cap] > Window) _n--;
                if (_n == 0) return;
                double wp = 1 / (SigmaP * SigmaP), wv = 1 / (SigmaV * SigmaV);
                // the normal equations, one matrix for x, y and z: [A B; B C+V] [a; b] = [Sp; Stp + Sv]
                double A = 0, B = 0, C = 0, V = 0;
                Vector3D Sp = Vector3D.Zero, Stp = Vector3D.Zero, Sv = Vector3D.Zero;
                for (int k = 0; k < _n; k++)
                {
                    int i = (_head - 1 - k + Cap) % Cap;
                    double tau = _rt[i] - t;
                    A += wp; B += wp * tau; C += wp * tau * tau; V += wv;
                    Sp += _rp[i] * wp; Stp += _rp[i] * (wp * tau); Sv += _rv[i] * wv;
                }
                double det = A * (C + V) - B * B;
                if (!(det > 0)) return;
                Vector3D rb = Stp + Sv;
                fr = (Sp * (C + V) - rb * B) * (1 / det);
                fv = (rb * A - Sp * B) * (1 / det);
                // a lone measurement is no fit: the old gate (also what warp sees - a game second or more between ticks)
                if (_n < 2) return;
                // the residuals' scatter about the fit (reduced chi-square: 3 axes x 2n equations, 6 unknowns) - the real
                // noise as a multiple of the nominal - never below it (a clean feed keeps the floors)
                double chi = 0;
                for (int k = 0; k < _n; k++)
                {
                    int i = (_head - 1 - k + Cap) % Cap;
                    double tau = _rt[i] - t;
                    chi += wp * (_rp[i] - fr - fv * tau).LengthSquared() + wv * (_rv[i] - fv).LengthSquared();
                }
                double s2 = Math.Max(1.0, chi / (6 * _n - 6));
                // the fit's standard errors at t (the inverse's diagonal), as 3-D lengths
                double seP = Math.Sqrt(3 * s2 * (C + V) / det), seV = Math.Sqrt(3 * s2 * A / det);
                posTol = Math.Min(PosTol, Math.Max(FitPosTol, K * seP));
                velTol = Math.Min(VelTol, Math.Max(FitVelTol, K * seV));
            }

            public void Clear() { _has = false; _n = 0; _head = 0; }
        }

        static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
    }
}
