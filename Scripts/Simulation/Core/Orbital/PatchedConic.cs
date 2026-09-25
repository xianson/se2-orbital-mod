using System;
using System.Collections.Generic;

namespace SEAerospace.Orbital
{
    /// <summary>Why a conic arc ends.</summary>
    public enum PatchType
    {
        Horizon,    // ran out the propagation window (no transition)
        Escape,     // left this body's SOI -> continue in the parent frame
        Encounter,  // entered a child body's SOI -> continue in the child frame
    }

    /// <summary>One conic segment of a patched trajectory, in its body's inertial frame.</summary>
    public struct ConicArc
    {
        public GravityBody Body;            // the primary for this arc
        public KeplerianElements Elements;  // in Body's frame; epoch = StartTime
        public double StartTime;
        public double EndTime;
        public PatchType EndReason;
        public GravityBody NextBody;        // parent (Escape) / child (Encounter) / null
    }

    /// <summary>
    /// Patched-conic propagator: walks a trajectory through the SOI tree, switching the
    /// primary body at SOI boundaries. Exit (escape) is the analytic
    /// <see cref="OrbitPropagation.TryTimeToRadius"/> on the body's SOI; entry
    /// (encounter) is a numeric search of the relative distance to each child crossing
    /// its SOI (handles static AND moving children uniformly via IEphemeris). State is
    /// reparented Galilean across each patch (position continuous, v_rel = v - v_body).
    /// </summary>
    public static class PatchedConic
    {
        /// <summary>
        /// Propagate from (state in startBody's frame at t0) for up to `horizon` seconds,
        /// producing the sequence of conic arcs with their patch points.
        /// </summary>
        public static List<ConicArc> Propagate(GravityBody startBody, StateVector state,
            double t0, double horizon, int maxPatches = 16, int scanSamples = 1024)
        {
            var arcs = new List<ConicArc>();
            var body = startBody;
            var s = state;
            double t = t0;
            double tHorizon = t0 + horizon;

            for (int i = 0; i <= maxPatches; i++)
            {
                var el = OrbitalMath.ToElements(s, body.Mu, t);

                // --- escape: next outbound crossing of this body's SOI ---
                double tEscape = double.PositiveInfinity;
                if (!double.IsPositiveInfinity(body.SoiRadius))
                {
                    // Skip the escape test only when the orbit provably never reaches the SOI:
                    // an ELLIPTIC orbit whose apoapsis is inside it. For hyperbolic, bounded is
                    // false (apoapsis = +inf) and we rely on the now-asymptote-correct
                    // TryTimeToRadius (audit H1). NOTE: energetic capture AFTER a child
                    // encounter (hyperbolic-relative -> elliptic post-periapsis) is not re-
                    // evaluated here — a known v0 patched-conic limitation (audit H3).
                    bool bounded = el.IsElliptic && el.ApoapsisRadius <= body.SoiRadius;
                    double tOut, tInUnused;
                    if (!bounded &&
                        OrbitPropagation.TryTimeToRadius(el, body.SoiRadius, out tOut, out tInUnused) &&
                        tOut > 1e-6)
                    {
                        tEscape = t + tOut;
                    }
                }

                // --- encounter: earliest child SOI entry before escape/horizon ---
                double encLimit = Math.Min(tEscape, tHorizon);
                double tEncounter;
                GravityBody child;
                FindEarliestEncounter(body, el, t, encLimit, scanSamples,
                    out tEncounter, out child);

                // --- pick the first event ---
                double tEnd;
                PatchType reason;
                GravityBody next;
                if (tEncounter < encLimit) { tEnd = tEncounter; reason = PatchType.Encounter; next = child; }
                else if (tEscape <= tHorizon) { tEnd = tEscape; reason = PatchType.Escape; next = body.Parent; }
                else { tEnd = tHorizon; reason = PatchType.Horizon; next = null; }

                arcs.Add(new ConicArc
                {
                    Body = body,
                    Elements = el,
                    StartTime = t,
                    EndTime = tEnd,
                    EndReason = reason,
                    NextBody = next,
                });

                if (reason == PatchType.Horizon || next == null) break;

                var sEnd = OrbitPropagation.StateAt(el, tEnd);     // craft in `body` frame at tEnd
                s = body.ConvertStateTo(next, sEnd, tEnd);          // reparent into next body's frame
                body = next;
                t = tEnd;
            }

            return arcs;
        }

        // Earliest time in (tStart, tEnd) at which the craft enters a child's SOI.
        private static void FindEarliestEncounter(GravityBody parent, KeplerianElements craftEl,
            double tStart, double tEnd, int samples, out double tEncounter, out GravityBody child)
        {
            tEncounter = double.PositiveInfinity;
            child = null;
            if (tEnd <= tStart || parent.Children.Count == 0) return;

            double t0 = tStart + 1e-3;
            if (t0 >= tEnd) return;
            int n = Math.Max(8, samples);
            double dt = (tEnd - t0) / n;

            foreach (var c in parent.Children)
            {
                GravityBody cc = c; // capture for the closure (C# 6: no local functions)
                Func<double, double> soiGap = tau =>
                {
                    Vector3D craft = OrbitPropagation.StateAt(craftEl, tau).Position;  // in parent frame
                    Vector3D childPos = cc.StateInParentAt(tau).Position;
                    return (craft - childPos).Length() - cc.SoiRadius;
                };

                double prevT = t0, prevF = soiGap(t0);
                for (int k = 1; k <= n; k++)
                {
                    double tau = t0 + dt * k;
                    double f = soiGap(tau);
                    if (prevF > 0.0 && f <= 0.0) // crossing inward = entering the SOI
                    {
                        double root = Bisect(soiGap, prevT, tau, 64);
                        if (root < tEncounter) { tEncounter = root; child = cc; }
                        break; // earliest entry for this child
                    }
                    prevT = tau;
                    prevF = f;
                }
            }
        }

        private static double Bisect(Func<double, double> f, double lo, double hi, int iters)
        {
            double flo = f(lo);
            for (int k = 0; k < iters; k++)
            {
                double mid = 0.5 * (lo + hi);
                double fmid = f(mid);
                if ((flo < 0) == (fmid < 0)) { lo = mid; flo = fmid; }
                else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// Sample a whole patched trajectory into one chosen frame for drawing — e.g. an
        /// Earth-frame view of an Earth->Moon transfer including the lunar-SOI arc. Points
        /// per arc are apportioned by duration.
        /// </summary>
        public static Vector3D[] SamplePath(List<ConicArc> arcs, GravityBody frame, int totalCount)
        {
            double total = 0.0;
            foreach (var a in arcs) total += Math.Max(0.0, a.EndTime - a.StartTime);
            if (total <= 0.0) return new Vector3D[0];

            var pts = new List<Vector3D>(totalCount + arcs.Count);
            foreach (var a in arcs)
            {
                double dur = Math.Max(0.0, a.EndTime - a.StartTime);
                int n = Math.Max(2, (int)Math.Round(totalCount * (dur / total)));
                for (int k = 0; k < n; k++)
                {
                    double tau = a.StartTime + dur * (k / (double)(n - 1));
                    var sBody = OrbitPropagation.StateAt(a.Elements, tau);
                    pts.Add(a.Body.ConvertStateTo(frame, sBody, tau).Position);
                }
            }
            return pts.ToArray();
        }
    }
}
