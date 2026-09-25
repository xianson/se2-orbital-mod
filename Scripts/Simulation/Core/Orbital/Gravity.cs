using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// Our gravity model: a TRUE inverse-square point-mass field with NO cutoff.
    ///
    /// This is the deliberate fix versus the game:
    ///  - Vanilla SE: gravity ~ r^-7 inside the hill band, then HARD-cut to zero at the
    ///    gravity-limit radius (where it would fall to ~0.05 g). Deep space is zero-g.
    ///  - Real Orbits: inverse-square inside, but STILL hard-cut at 0.05 g.
    ///  - Us: inverse-square all the way out, no cutoff. Gravity never vanishes; orbits
    ///    are proper Keplerian conics at every distance.
    ///
    /// Rails/proximity-frame propagation already assumes this (a conic IS the inverse-
    /// square solution). The 0.05 g cutoff is removed in proximity-frame propagation
    /// by design.
    ///
    /// READOUT vs PHYSICS (the unifying proximity-frame principle — same trick as
    /// HighSpeed faking velocity):
    ///  - ProximityFrame (on rails, free-fall berth): the orbital field here is a
    ///    READOUT only — fed to the HUD / altimeter / "down" vector / gravity indicator
    ///    so the player sees the real field, but NOT applied as Havok acceleration
    ///    (the berth is in free fall; the frame's rails carry the orbital motion).
    ///    Only the small tidal/CW relative drift is physical, and even that is optional
    ///    in v1. So there is no orbital Coriolis to apply — the position-only-provider
    ///    limitation is moot in the berth.
    ///  - PlanetFrame (materialized, low/atmo flight): gravity is REAL — applied to
    ///    Havok (no cutoff) so ships actually fall/fly. This same field is the truth
    ///    the provider supplies.
    /// </summary>
    public static class Gravity
    {
        /// <summary>Acceleration from a point mass: -mu * r / |r|^3 (toward the body). No cutoff.</summary>
        public static Vector3D PointMassAcceleration(Vector3D rFromBody, double mu)
        {
            double r2 = rFromBody.LengthSquared();
            if (r2 <= 0.0) return Vector3D.Zero;
            double r = Math.Sqrt(r2);
            return rFromBody * (-mu / (r2 * r));
        }

        /// <summary>Gravity magnitude at distance r: mu / r^2. No cutoff (nonzero at any finite r).</summary>
        public static double Magnitude(double r, double mu) => r > 0 ? mu / (r * r) : 0.0;

        /// <summary>
        /// Classical Laplace sphere-of-influence radius — the patched-conic handoff
        /// boundary. NOTE: with our no-cutoff gravity this is a CHOSEN dominance-switch
        /// radius, not a physical edge: both wells are nonzero on each side. mu is
        /// proportional to mass, so the mass ratio is the mu ratio.
        /// r_SOI = a_orbit * (mu_body / mu_parent)^(2/5).
        /// </summary>
        public static double LaplaceSoiRadius(double orbitSemiMajorAxis, double muBody, double muParent)
            => orbitSemiMajorAxis * Math.Pow(muBody / muParent, 0.4);
    }
}
