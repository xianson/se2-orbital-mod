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
        /// axes). Kept while it still says where you are - within PosTol and VelTol of the measurement - so a plan
        /// built on it is not re-solved every tick for the measurement's noise; a burn, a push off a wall, a new
        /// frame: new elements.
        /// </summary>
        public sealed class OwnOrbit
        {
            public double PosTol = 25, VelTol = 0.05;
            KeplerianElements _el; long _frame = long.MinValue; bool _has;
            public int Rebuilds { get; private set; }

            public KeplerianElements Get(long frameId, KeplerianElements frameEl, Vector3D offset, Vector3D velocity, double t)
            {
                var fc = OrbitPropagation.StateAt(frameEl, t);
                var mine = new StateVector(fc.Position + offset, fc.Velocity + velocity);
                if (_has && _frame == frameId)
                {
                    var p = OrbitPropagation.StateAt(_el, t);
                    if ((p.Position - mine.Position).Length() <= PosTol && (p.Velocity - mine.Velocity).Length() <= VelTol && Finite(p.Position)) return _el;
                }
                var el = Capture(mine, frameEl.Mu, t);
                if (!Finite(new Vector3D(el.SemiMajorAxis, el.Eccentricity, el.Inclination))) { _has = false; return frameEl; }
                _el = el; _frame = frameId; _has = true; Rebuilds++;
                return el;
            }

            /// <summary>As Get, from your own state directly (a planet's space: the local orbit measured each tick) - kept while
            /// it still says where you are, so the plan is not re-solved every frame for the measurement's noise.</summary>
            public KeplerianElements GetState(long key, StateVector mine, double mu, double t)
            {
                if (_has && _frame == key)
                {
                    var p = OrbitPropagation.StateAt(_el, t);
                    if ((p.Position - mine.Position).Length() <= PosTol && (p.Velocity - mine.Velocity).Length() <= VelTol && Finite(p.Position)) return _el;
                }
                var el = Capture(mine, mu, t);
                if (!Finite(new Vector3D(el.SemiMajorAxis, el.Eccentricity, el.Inclination))) { _has = false; return el; }
                _el = el; _frame = key; _has = true; Rebuilds++;
                return el;
            }

            public void Clear() { _has = false; }
        }

        static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
    }
}
