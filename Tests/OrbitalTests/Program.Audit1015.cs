using System;
using System.Collections.Generic;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.SystemDef;

namespace Orbital.Tests
{
    // 2026-10-15 audit fixes (AUDIT.md section 5, Orbital table): one section per bug id.
    internal static partial class Program
    {
        private static void AuditFixes1015()
        {
            ImpactOnLowPeriapsisPass();   // O15
            EncounterFlyThroughBetweenSamples();   // O16
        }

        // O15: an arrival whose periapsis is below a small body's surface. The map's CutAtImpact sampled the
        // hyperbolic leg at 240 points - ~100+ s apart while the surface pass lasts tens of seconds - so the
        // path was drawn through the planet and out as an escape. The impact is the inbound crossing of r = R.
        private static void ImpactOnLowPeriapsisPass()
        {
            Section("O15: impact on a low-periapsis pass is found analytically (no sampling gap)");
            double R = 50e3, mu = 5.0 * R * R, soi = 1714e3;   // a ~50 km moon (5 m/s2 at the surface), Kemik-sized SOI
            double rp = 0.97 * R, vInf = 30.0;
            double a = -mu / (vInf * vInf), e = 1.0 - rp / a;
            var el = new KeplerianElements { SemiMajorAxis = a, Eccentricity = e, Mu = mu, Epoch = 0.0 };
            // the leg: from the inbound SOI crossing toward the outbound one
            Ok("premise: TryTimeToRadius(SOI)", OrbitPropagation.TryTimeToRadius(el, soi, out double tOut, out double tIn));
            el.TrueAnomaly = -Math.Acos((el.SemiLatusRectum / soi - 1.0) / e);
            el.Epoch = 0.0;
            OrbitPropagation.TryTimeToRadius(el, soi, out tOut, out tIn);
            // a leg cut short by the next node, ending where the 240 samples straddle the ~50 s surface pass (k = 128.2 .. 128.8)
            OrbitPropagation.TryTimeToRadius(el, R, out _, out double tSurf);
            double T0 = 0.0, T1 = tSurf * 240.0 / 128.2;
            Near("premise: leg starts on the SOI", OrbitPropagation.StateAt(el, T0).Position.Length(), soi, 1.0);

            // the old 240-sample scan (CutAtImpact before the fix)
            bool sampledHit = false;
            for (int k = 1; k <= 240; k++)
                if (OrbitPropagation.StateAt(el, T0 + (T1 - T0) * k / 240).Position.Length() < R) { sampledHit = true; break; }
            Ok($"premise: 240 samples ({(T1 - T0) / 240:F0} s apart) miss the surface pass", !sampledHit);

            Ok("impact found", OrbitPropagation.TryFirstInboundCrossing(el, R, T0, T1, out double hit));
            Near("impact is on the surface", OrbitPropagation.StateAt(el, hit).Position.Length(), R, 1e-3);
            Ok("impact is inbound (before periapsis)", hit < OrbitPropagation.TimeToPeriapsis(el));
            Ok("no impact outside the window", !OrbitPropagation.TryFirstInboundCrossing(el, R, hit + 1.0, T1, out _));

            // elliptic: the FIRST pass at/after the window start, several revolutions on
            var ee = new KeplerianElements { SemiMajorAxis = 3 * R, Eccentricity = 0.8, Mu = mu, TrueAnomaly = 1.0, Epoch = 100.0 };
            double P = ee.Period;
            Ok("elliptic impact found", OrbitPropagation.TryFirstInboundCrossing(ee, R, 100.0 + 2.5 * P, 100.0 + 4 * P, out double he));
            Near("elliptic impact on the surface", OrbitPropagation.StateAt(ee, he).Position.Length(), R, 1e-3);
            Ok("elliptic impact is the first after the start", he >= 100.0 + 2.5 * P && he < 100.0 + 3.5 * P);
            Ok("elliptic impact inbound", Vector3D.Dot(OrbitPropagation.StateAt(ee, he).Position, OrbitPropagation.StateAt(ee, he).Velocity) < 0);
            Ok("periapsis above the radius: none", !OrbitPropagation.TryFirstInboundCrossing(ee, 0.5 * R, 0, 10 * P, out _));
        }

        // O16: PatchedConic only saw an SOI entry when a SAMPLE landed inside the child's sphere; the live callers use 256
        // samples over up to 10 days, so a quick fly-through of a small moon's sphere fell between two samples: no
        // encounter on the map, NextSoiChange NaN, warp did not stop.
        private static void EncounterFlyThroughBetweenSamples()
        {
            Section("O16: a fly-through of a child SOI between two samples is still an encounter");
            double muP = 1.0e14;
            double rp = 2.0e7, ra = 2.0e8, a = (rp + ra) / 2.0;
            double vp = Math.Sqrt(muP * (2.0 / rp - 1.0 / a));
            var craft = new StateVector(new Vector3D(rp, 0, 0), new Vector3D(0, vp, 0));
            var el = OrbitalMath.ToElements(craft, muP, 0.0);
            double H = el.Period;
            const int n = 256;
            double t0 = 1e-3, dt = (H - t0) / n, tm = t0 + dt * 20.5;   // the pass mid-way between samples 20 and 21
            var sm = OrbitPropagation.StateAt(el, tm);
            Vector3D side = Vector3D.Normalize(Vector3D.Cross(Vector3D.UnitZ, sm.Velocity));
            double soi = 1.0e6;
            Vector3D moonPos = sm.Position + side * (0.6 * soi);   // a pass 0.6 SOI off its centre: a ~1600 s chord
            var planet = GravityBody.Root("Planet", muP);
            var moon = planet.AddChild("Moon", 1.0e10, soi, new FixedEphemeris(moonPos));
            double d20 = (OrbitPropagation.StateAt(el, t0 + dt * 20).Position - moonPos).Length();
            double d21 = (OrbitPropagation.StateAt(el, t0 + dt * 21).Position - moonPos).Length();
            Ok($"premise: both neighbouring samples are outside the sphere ({d20 / soi:F2}, {d21 / soi:F2} SOI; {dt:F0} s apart)", d20 > soi && d21 > soi);

            var arcs = PatchedConic.Propagate(planet, craft, 0.0, H, 8, n);
            Ok("encounter found", arcs.Count >= 2 && arcs[0].EndReason == PatchType.Encounter && arcs[0].NextBody == moon);
            if (arcs.Count >= 2)
            {
                Near("patch on the Moon's SOI", (OrbitPropagation.StateAt(arcs[0].Elements, arcs[0].EndTime).Position - moonPos).Length(), soi, 100.0);
                Ok("patch before the closest approach", arcs[0].EndTime < tm + 100.0 && arcs[0].EndTime > t0 + dt * 20);
            }
            // the same miss in the unified scan's kernel (no live caller; it mirrors FindEarliestEncounter)
            Ok("EncounterScan kernel finds it too", EncounterScan.TryFindEarliest(el, 0.0, H, EncounterTarget.FromBody(moon), n, out var ev)
                && Math.Abs((OrbitPropagation.StateAt(el, ev.Time).Position - moonPos).Length() - soi) < 100.0);
        }
    }
}
