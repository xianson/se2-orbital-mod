
namespace SEAerospace.Orbital
{
    /// <summary>
    /// A Cartesian orbital state: position + velocity, expressed in some
    /// body-centered inertial frame (the celestial realm's BodyInertialFrame).
    /// SI units throughout: meters, meters/second. The frame and its mu are
    /// supplied alongside (see <see cref="KeplerianElements"/>), not stored here.
    /// </summary>
    public struct StateVector
    {
        public Vector3D Position;   // r, meters, from the central body's center
        public Vector3D Velocity;   // v, meters/second, inertial

        public StateVector(Vector3D position, Vector3D velocity)
        {
            Position = position;
            Velocity = velocity;
        }

        public static readonly StateVector Zero = new StateVector(Vector3D.Zero, Vector3D.Zero);

        /// <summary>Galilean frame shift: add a frame's state to express this state in the parent frame.</summary>
        public static StateVector operator +(StateVector a, StateVector b)
            => new StateVector(a.Position + b.Position, a.Velocity + b.Velocity);

        public static StateVector operator -(StateVector a, StateVector b)
            => new StateVector(a.Position - b.Position, a.Velocity - b.Velocity);

        public double Radius => Position.Length();
        public double Speed => Velocity.Length();

        public override string ToString()
            => $"r={Position} ({Radius:F1} m), v={Velocity} ({Speed:F2} m/s)";
    }
}
