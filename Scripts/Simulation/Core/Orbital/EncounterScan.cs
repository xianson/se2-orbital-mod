using System;
using System.Collections.Generic;

namespace SEAerospace.Orbital
{
    /// <summary>What an encounter resolves to downstream — the two mechanisms unified here.</summary>
    public enum EncounterKind
    {
        SoiEntry,     // entered a body's SOI sphere -> a gravity reparent (SoiReparent)
        Conjunction,  // entered a clump's capture/merge sphere -> a conjunction-frame merge
    }

    /// <summary>
    /// One thing the subject's trajectory may run into: a moving center (the target's
    /// motion in the SUBJECT'S parent frame, via any <see cref="IEphemeris"/>) plus the
    /// sphere radius around it. Same shape for a real body (center = body's parent-relative
    /// ephemeris, radius = its SOI) and a tracked clump (center = the estimated orbit,
    /// radius = the capture/merge radius). This is the game-free seam: the scan CONSUMES
    /// orbits regardless of where they came from (registry truth or an earned track).
    /// </summary>
    public struct EncounterTarget
    {
        public string Id;             // caller's handle for this target (body name, clump id)
        public EncounterKind Kind;    // how a hit resolves downstream
        public IEphemeris Center;     // target's state over time, in the subject's parent frame
        public double SphereRadius;   // SOI (bodies) or capture/merge radius (clumps), meters

        /// <summary>Wrap a real body as a target: its motion in the parent frame is its
        /// own parent-relative ephemeris, the sphere is its SOI. Same math as the
        /// <see cref="PatchedConic"/> body-encounter case, now one row in a unified scan.</summary>
        public static EncounterTarget FromBody(GravityBody body)
        {
            EncounterTarget t = new EncounterTarget();
            t.Id = body.Name;
            t.Kind = EncounterKind.SoiEntry;
            t.Center = body.ParentRelative;     // body's state in ITS parent's frame
            t.SphereRadius = body.SoiRadius;
            return t;
        }

        /// <summary>Wrap a tracked clump as a target: an estimated orbit (about the same
        /// parent the subject orbits) and a capture/merge sphere.</summary>
        public static EncounterTarget FromOrbit(string id, KeplerianElements estOrbit, double mergeRadius)
        {
            EncounterTarget t = new EncounterTarget();
            t.Id = id;
            t.Kind = EncounterKind.Conjunction;
            t.Center = new KeplerianEphemeris(estOrbit);
            t.SphereRadius = mergeRadius;
            return t;
        }
    }

    /// <summary>
    /// One predicted encounter: the absolute time the subject's separation from a target
    /// dropped onto/below its sphere, which target, what it resolves to, the separation at
    /// that time, and the relative state (subject-relative-to-target) there.
    ///
    /// NOTE (v1 is DETERMINISTIC on the supplied orbits): combined-sigma widening of the
    /// prediction — the encounter is only as sharp as the COMBINED uncertainty of the
    /// subject's and the target's tracks — is a downstream tracking-layer concern and is
    /// NOT modeled here. This scan assumes the orbits it is handed are exact.
    /// </summary>
    public struct EncounterEvent
    {
        public double Time;             // absolute time of the sphere crossing / min separation (s)
        public string TargetId;         // the EncounterTarget.Id that was hit
        public EncounterKind Kind;      // SoiEntry | Conjunction
        public double MissDistance;     // separation at Time: ~SphereRadius at a crossing, the
                                        // true minimum if the pass never reaches the sphere is
                                        // NOT an event (no row); for an already-inside target at
                                        // t0 this is the actual separation there.
        public Vector3D RelativePosition; // r_subject - r_target at Time, in the parent frame (m)
        public Vector3D RelativeVelocity; // v_subject - v_target at Time, in the parent frame (m/s)
    }

    /// <summary>
    /// The UNIFIED ENCOUNTER SCAN (Pillar A): one scan over the subject's propagated
    /// trajectory that finds BOTH body-SOI crossings AND clump conjunctions as one ordered
    /// timeline. The math is identical for both — propagate the two orbits, find when the
    /// relative distance first drops onto a sphere of the target's radius:
    ///
    ///     gap(t) = |r_subject(t) - r_target(t)| - sphereRadius,   first t with gap &lt;= 0.
    ///
    /// This generalizes <see cref="PatchedConic"/>'s body-only FindEarliestEncounter (a
    /// moving SOI sphere) to any moving target sphere, and reuses the same coarse-sample +
    /// bisect-to-the-boundary kernel as <see cref="Rendezvous"/>'s closest-approach. Pure
    /// celestial-space orbital math — no Resolve, no game session, no FrameManager — so it is
    /// offline-provable like the rest of the core.
    ///
    /// The subject and every target's center are expressed in ONE common parent frame (the
    /// body the subject orbits): the subject as <see cref="KeplerianElements"/> about that
    /// parent, each target's center as an <see cref="IEphemeris"/> in that same frame. C# 6,
    /// VRage.Math + the orbital core only; out/ref multi-returns, no tuples.
    /// </summary>
    public static class EncounterScan
    {
        /// <summary>
        /// Scan the subject's orbit against every target over [t0, t0 + horizon], returning
        /// the EARLIEST sphere crossing per target as one list ordered by time. A target that
        /// is ALREADY inside its sphere at t0 yields an event at t0 (the encounter is now). A
        /// target whose closest pass never reaches the sphere yields no event (no false hit).
        /// <paramref name="samples"/> is the coarse bucket count across the window (more = less
        /// chance of skipping a brief deep pass between samples).
        /// </summary>
        public static List<EncounterEvent> Scan(KeplerianElements subject, double t0, double horizon,
            IList<EncounterTarget> targets, int samples = 1024)
        {
            List<EncounterEvent> events = new List<EncounterEvent>();
            if (targets == null || horizon <= 0.0) return events;

            for (int i = 0; i < targets.Count; i++)
            {
                EncounterEvent e;
                if (TryFindEarliest(subject, t0, horizon, targets[i], samples, out e))
                    events.Add(e);
            }

            // Order the interleaved bodies + clumps into one upcoming-encounters timeline.
            events.Sort(CompareByTime);
            return events;
        }

        /// <summary>
        /// The earliest crossing of one target's sphere in (t0, t0 + horizon], or an
        /// already-inside event at t0. Returns false (and leaves <paramref name="ev"/> default)
        /// when the trajectory never enters the sphere in the window. This is the single-target
        /// kernel <see cref="Scan"/> fans out over; it mirrors PatchedConic.FindEarliestEncounter's
        /// inward-crossing detection so the body case matches that result exactly.
        /// </summary>
        public static bool TryFindEarliest(KeplerianElements subject, double t0, double horizon,
            EncounterTarget target, int samples, out EncounterEvent ev)
        {
            ev = new EncounterEvent();
            if (horizon <= 0.0 || target.Center == null) return false;
            double radius = target.SphereRadius;
            if (radius <= 0.0 || double.IsInfinity(radius)) return false; // root / no sphere: never an event

            // Already inside at t0: the encounter is now. (Mirrors a target that starts within
            // the merge sphere — the merge gate is already satisfied.)
            double gap0 = Gap(subject, target, t0);
            if (gap0 <= 0.0)
            {
                ev = Evaluate(subject, target, t0);
                return true;
            }

            double tEnd = t0 + horizon;
            int n = Math.Max(8, samples);
            double dt = horizon / n;

            double prevT = t0;
            double prevF = gap0;
            for (int k = 1; k <= n; k++)
            {
                double tau = (k == n) ? tEnd : (t0 + dt * k);
                double f = Gap(subject, target, tau);
                if (prevF > 0.0 && f <= 0.0) // crossing inward = entering the sphere
                {
                    double root = BisectGap(subject, target, prevT, tau, 64);
                    ev = Evaluate(subject, target, root);
                    return true;
                }
                prevT = tau;
                prevF = f;
            }
            return false; // closest pass never reached the sphere -> no encounter
        }

        // Signed distance from the target's sphere: <0 inside, 0 on the boundary, >0 outside.
        private static double Gap(KeplerianElements subject, EncounterTarget target, double t)
        {
            Vector3D rSub = OrbitPropagation.StateAt(subject, t).Position;
            Vector3D rTgt = target.Center.PositionAt(t);
            return (rSub - rTgt).Length() - target.SphereRadius;
        }

        // Build the full event (separation + relative state) at a known time.
        private static EncounterEvent Evaluate(KeplerianElements subject, EncounterTarget target, double t)
        {
            StateVector sSub = OrbitPropagation.StateAt(subject, t);
            StateVector sTgt = target.Center.StateAt(t);
            Vector3D rRel = sSub.Position - sTgt.Position;
            Vector3D vRel = sSub.Velocity - sTgt.Velocity;
            EncounterEvent e = new EncounterEvent();
            e.Time = t;
            e.TargetId = target.Id;
            e.Kind = target.Kind;
            e.MissDistance = rRel.Length();
            e.RelativePosition = rRel;
            e.RelativeVelocity = vRel;
            return e;
        }

        // Bisection on the signed gap between an outside (gap>0) and inside-or-on (gap<=0)
        // sample, converging to the sphere boundary — same kernel shape as PatchedConic.Bisect.
        private static double BisectGap(KeplerianElements subject, EncounterTarget target,
            double lo, double hi, int iters)
        {
            double flo = Gap(subject, target, lo);
            for (int k = 0; k < iters; k++)
            {
                double mid = 0.5 * (lo + hi);
                double fmid = Gap(subject, target, mid);
                if ((flo < 0.0) == (fmid < 0.0)) { lo = mid; flo = fmid; }
                else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private static int CompareByTime(EncounterEvent a, EncounterEvent b)
        {
            return a.Time.CompareTo(b.Time);
        }
    }
}
