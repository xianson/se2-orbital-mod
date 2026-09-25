using System;

namespace SEAerospace.Persistence
{
    /// <summary>
    /// Game-free logging seam for the persistence layer (mirrors the XML-backend hooks on
    /// <see cref="FrameRegistrySnapshot"/>). The model + mapper files stay whitelist-clean and
    /// offline-testable by routing diagnostics through a pluggable <see cref="Sink"/> instead of
    /// referencing <c>VRage.Utils.MyLog</c> directly:
    ///   - In-game: the game-side host installs a sink that forwards to
    ///     <c>MyLog.Default.WriteLineAndConsole("[SEAerospace] " + msg)</c>.
    ///   - Offline: the sink is left null (a silent no-op), so the test harness needs no VRage
    ///     reference and load-anomaly logging never throws.
    /// Every call is exception-swallowing: a logging failure must never break a world load.
    /// </summary>
    public static class PersistenceLog
    {
        /// <summary>Installed by the game-side host; null = no-op (offline). Receives the raw
        /// message (without the "[SEAerospace] " prefix, which the host adds).</summary>
        public static Action<string> Sink;

        /// <summary>Log a non-fatal persistence anomaly (corrupt save, duplicate slot, etc.).
        /// Never throws — a missing/throwing sink is swallowed so world load continues.</summary>
        public static void Warn(string message)
        {
            Action<string> sink = Sink;
            if (sink == null) return;
            try
            {
                sink(message);
            }
            catch (Exception) { }
        }
    }
}
