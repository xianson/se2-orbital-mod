using System;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.Rendezvous;

namespace RendezvousTest
{
    // 2026-10-15 audit fixes (AUDIT.md section 5, Orbital table).
    internal static partial class Program
    {
        // O24: a phasing plan reported its arrival relative speed as 0 ("after the exit burn we're back on the target's
        // orbit") - the exit burn restores MY orbit, which can be ~2% off the target's: report what flying it gives.
        private static void PhasingReportsFlownRelSpeed()
        {
            Section("O24 phasing: the reported arrival rel speed is the flown one (after the exit burn), not 0");
            double Rt = 8000e3, vt = Math.Sqrt(MuEarth / Rt);
            var target = OrbitalMath.ToElements(new StateVector(new Vector3D(Rt, 0, 0), new Vector3D(0, vt, 0)), MuEarth, 0);
            double Rm = Rt * (1.0 - 0.019), vm = Math.Sqrt(MuEarth / Rm), phi = 0.35;   // (the M3 case)
            var myState = new StateVector(new Vector3D(Rm * Math.Cos(-phi), Rm * Math.Sin(-phi), 0),
                new Vector3D(-vm * Math.Sin(-phi), vm * Math.Cos(-phi), 0));
            var opts = new InterceptOptions(); opts.MaxPhasingRevs = 6;
            var plan = InterceptPlanner.Plan(myState, target, MuEarth, 0.0, RendezvousParams.Default, opts);
            Ok("premise: a phasing plan", plan.Status == PlanStatus.Ok && plan.Kind == PlanKind.Phasing && plan.Maneuvers.Count == 2);
            if (plan.Kind != PlanKind.Phasing || plan.Maneuvers.Count != 2) return;
            var orbit = OrbitalMath.ToElements(new StateVector(myState.Position, myState.Velocity + plan.Maneuvers[0].DeltaV), MuEarth, 0.0);
            double tx = plan.Maneuvers[1].TimeFromNowSeconds;
            var me = OrbitPropagation.StateAt(orbit, tx);
            var tg = OrbitPropagation.StateAt(target, tx);
            double flown = (me.Velocity + plan.Maneuvers[1].DeltaV - tg.Velocity).Length();
            Console.WriteLine($"   flown rel speed after the exit burn {flown:F2} m/s, reported {plan.Arrival.RelativeSpeed:F2}");
            Ok("premise: the orbits differ (rel speed > 1 m/s)", flown > 1.0);
            Ok("reported == flown", Math.Abs(plan.Arrival.RelativeSpeed - flown) < 1e-6);
        }
    }
}
