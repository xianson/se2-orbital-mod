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
    }
}
