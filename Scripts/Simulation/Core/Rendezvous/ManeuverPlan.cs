using System.Collections.Generic;

namespace SEAerospace.Rendezvous
{
    /// <summary>Outcome status of an intercept solve.</summary>
    public enum PlanStatus
    {
        Ok,          // a usable plan was found
        NoSolution   // no feasible intercept in the searched window
    }

    /// <summary>Whether the chosen plan is a direct Lambert intercept or a phasing drift.</summary>
    public enum PlanKind
    {
        Direct,
        Phasing,
        TransferPhase   // Hohmann to the target's orbit, then phase to the target (cheap)
    }

    /// <summary>
    /// One scheduled burn: a world-frame Δv to apply at a time offset from "now". The
    /// optional prograde/radial/normal components are a display convenience (the same Δv
    /// decomposed in the ship's velocity frame) so a UI can say "X m/s prograde, Y radial".
    /// </summary>
    public struct Maneuver
    {
        public double TimeFromNowSeconds; // when to burn, relative to the plan's "now"
        public Vector3D DeltaV;           // world-frame impulsive Δv (m/s)
        public double Magnitude;          // |DeltaV|

        public double Prograde;           // Δv components in the velocity LVLH-ish frame (display)
        public double Radial;
        public double Normal;

        public Maneuver(double timeFromNow, Vector3D dv)
        {
            TimeFromNowSeconds = timeFromNow;
            DeltaV = dv;
            Magnitude = dv.Length();
            Prograde = 0.0;
            Radial = 0.0;
            Normal = 0.0;
        }
    }

    /// <summary>
    /// Honest prediction of where the plan puts you relative to the target at arrival.
    /// Targets "within capture range at low relative speed", NOT an exact rendezvous —
    /// the forgiving auto-merge finishes the job from here.
    /// </summary>
    public struct ArrivalPrediction
    {
        public double ArrivalTime;        // absolute time of arrival
        public double TimeFromNowSeconds; // arrival relative to "now"
        public double MissDistance;       // predicted |r_target - r_me| at arrival (m)
        public Vector3D RelativeVelocity; // v_target - v_me at arrival (m/s), before any matching burn
        public double RelativeSpeed;      // |RelativeVelocity|
    }

    /// <summary>
    /// The result of an intercept solve: a small POCO a UI turns into "burn X m/s in T
    /// seconds → arrive HERE in this long, this close, at this relative speed". Holds the
    /// ordered maneuvers (1 for a flyby intercept, 2 if a velocity-matching arrival burn
    /// is included), the arrival prediction, and the total Δv used to rank candidates.
    /// </summary>
    public class ManeuverPlan
    {
        public PlanStatus Status;
        public PlanKind Kind;
        public List<Maneuver> Maneuvers;
        public ArrivalPrediction Arrival;
        public double TotalDeltaV;        // sum of maneuver magnitudes (m/s) — the cost score
        public string Note;               // human-readable description / reason for NoSolution

        public ManeuverPlan()
        {
            Status = PlanStatus.NoSolution;
            Kind = PlanKind.Direct;
            Maneuvers = new List<Maneuver>();
            TotalDeltaV = double.PositiveInfinity;
            Note = null;
        }

        public static ManeuverPlan NoSolution(string note)
        {
            ManeuverPlan p = new ManeuverPlan();
            p.Status = PlanStatus.NoSolution;
            p.Note = note;
            return p;
        }
    }
}
