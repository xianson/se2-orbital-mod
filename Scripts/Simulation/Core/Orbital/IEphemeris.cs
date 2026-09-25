
namespace SEAerospace.Orbital
{
    /// <summary>
    /// Time -> state seam. Anything that knows where it is over time implements this:
    /// a body on Keplerian rails (<see cref="KeplerianEphemeris"/>), a tabulated
    /// ephemeris later, a barycenter, a ProximityFrame on rails. Frames consume this
    /// for "where is X at time t" without caring how the motion is computed.
    /// Position/velocity are in the parent BodyInertialFrame, SI units.
    /// </summary>
    public interface IEphemeris
    {
        StateVector StateAt(double time);
        Vector3D PositionAt(double time);
    }

    /// <summary>A body that does not move in its parent frame (vanilla SE: voxel planets
    /// are static). Constant position, zero velocity.</summary>
    public sealed class FixedEphemeris : IEphemeris
    {
        private readonly Vector3D _position;
        public FixedEphemeris(Vector3D position) { _position = position; }
        public StateVector StateAt(double time) => new StateVector(_position, Vector3D.Zero);
        public Vector3D PositionAt(double time) => _position;
    }

    /// <summary>Analytic two-body ephemeris: a fixed element set propagated on rails.</summary>
    public sealed class KeplerianEphemeris : IEphemeris
    {
        private readonly KeplerianElements _epochElements;

        public KeplerianEphemeris(KeplerianElements epochElements)
        {
            _epochElements = epochElements;
        }

        /// <summary>The raw element set at its baked epoch (true anomaly NOT advanced). A consumer
        /// that needs to ride the SAME orbit on its own rails (e.g. a ProximityFrame whose
        /// <see cref="KeplerianElements"/> is propagated from epoch each tick) seeds itself with
        /// this — so <c>OrbitPropagation.StateAt(EpochElements, t)</c> reproduces <see cref="PositionAt"/>
        /// exactly. KeplerianElements is a value type, so this hands back a copy (no aliasing).</summary>
        public KeplerianElements EpochElements => _epochElements;

        /// <summary>The element set with true anomaly advanced to absolute time t.</summary>
        public KeplerianElements ElementsAt(double time)
            => OrbitPropagation.AtTime(_epochElements, time);

        public StateVector StateAt(double time)
            => OrbitalMath.ToState(ElementsAt(time));

        public Vector3D PositionAt(double time)
            => StateAt(time).Position;
    }
}
