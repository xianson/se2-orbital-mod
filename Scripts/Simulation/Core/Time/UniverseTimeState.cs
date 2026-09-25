
namespace SEAerospace.Time
{
    /// <summary>
    /// Serializable snapshot of the universe clock: just the epoch (universe seconds) and the
    /// warp multiplier. A plain POCO (public fields, parameterless ctor) so the persistence layer
    /// can save/restore it with the game's XML/protobuf serializer without touching
    /// <see cref="UniverseTime"/> internals. Produced by <see cref="UniverseTime.Capture"/> and
    /// consumed by <see cref="UniverseTime.Apply"/>.
    ///
    /// The warp ladder itself is NOT persisted — it is world/config-defined, so a save restores
    /// the time and warp value but always lands on the active world's ladder (Apply clamps).
    /// C# 6, game-free.
    /// </summary>
    public class UniverseTimeState
    {
        /// <summary>Universe time at save, in seconds.</summary>
        public double EpochSeconds;

        /// <summary>Warp multiplier at save (>= 1). Clamped to the active ladder on Apply.</summary>
        public double Timescale = 1.0;

        /// <summary>Parameterless ctor for the serializer.</summary>
        public UniverseTimeState()
        {
        }
    }
}
