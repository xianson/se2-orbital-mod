#pragma warning disable
namespace OrbitalMod;

/// <summary>Capture helpers that need the ported core (kept out of FrameMath.cs, which the tests compile alone).</summary>
public static class CaptureMath
{
    /// <summary>
    /// Elements for CAPTURING a state (stow, split, arrival, fold). A radial state (zero angular
    /// momentum: at rest relative to the body, or moving straight toward/away from it) makes the
    /// classical conversion divide by zero (NaN inclination). Nudge it with a tiny perpendicular
    /// velocity: the result is a needle-thin ellipse that follows the radial fall to well within
    /// a metre over the fall, and whose inbound shell crossing (the arrival) is well defined.
    /// </summary>
    public static SEAerospace.Orbital.KeplerianElements CaptureElements(SEAerospace.Orbital.StateVector s, double mu, double t)
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
        return SEAerospace.Orbital.OrbitalMath.ToElements(new SEAerospace.Orbital.StateVector(r, v), mu, t);
    }
}

/// <summary>
/// A spinning planet's ROTATING SURFACE CHART (SE-Aerospace's chart, applied to the whole cell).
/// SE2 voxel planets cannot rotate, so the planet cell itself is the rotating frame: the terrain is
/// at rest in it. Inertial states (rails, HighSpeed conics) convert to and from the chart only where
/// objects leave or enter the voxel world (stow, arrival, HighSpeed placement), and free flight in
/// the cell gets the fictitious forces (Coriolis, centrifugal) so it stays physically right.
/// Identity when the body does not spin.
/// </summary>
public struct Chart
{
    public bool Spin;
    public Vector3D Axis;
    public double Theta, Omega;

    public static Chart Of(string body, double t)
    {
        var b = SystemHost.Registry?.Find(body);
        if (b == null || !SEAerospace.PlanetBerths.TryBodySpin(b, out Vector3D axis, out double omega)) return default;
        return new Chart { Spin = true, Axis = axis, Omega = omega, Theta = b.RotationAngleAt(t) };
    }

    public Vector3D W => Axis * Omega;
    /// <summary>Chart (world-relative-to-cell) position -> inertial.</summary>
    public Vector3D ToInertial(Vector3D r) => Spin ? SEAerospace.PlanetBerths.RotateAboutAxis(r, Axis, Theta) : r;
    /// <summary>Inertial position (or any vector) -> chart axes.</summary>
    public Vector3D FromInertial(Vector3D p) => Spin ? SEAerospace.PlanetBerths.RotateAboutAxis(p, Axis, -Theta) : p;
    /// <summary>Chart velocity at chart position r -> inertial velocity.</summary>
    public Vector3D VelToInertial(Vector3D r, Vector3D v) => Spin ? ToInertial(v + Vector3D.Cross(W, r)) : v;
    /// <summary>Inertial state -> chart velocity at the matching chart position.</summary>
    public Vector3D VelFromInertial(Vector3D p, Vector3D vi) => Spin ? FromInertial(vi) - Vector3D.Cross(W, FromInertial(p)) : vi;
    /// <summary>Fictitious acceleration in the chart: Coriolis + centrifugal.</summary>
    public Vector3D Fictitious(Vector3D r, Vector3D v)
    {
        if (!Spin) return Vector3D.Zero;
        Vector3D w = W;
        return -2.0 * Vector3D.Cross(w, v) - Vector3D.Cross(w, Vector3D.Cross(w, r));
    }
}
