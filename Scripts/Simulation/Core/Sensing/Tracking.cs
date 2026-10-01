using System;

namespace SEAerospace.Sensing
{
    /// <summary>
    /// How well a contact is known (Contacts; game-free, tested offline in Tests/SensingTests). Seeing a thing
    /// is not knowing its orbit:
    ///  - DETECTED: seen at all: a bearing and a rough distance. Shown as "≈ name", its distance rounded; no
    ///    orbit, so no rendezvous or closest approach predicted for it.
    ///  - TRACKED: its orbit known. Eyes (it is right there) and radar (a range and a range-rate every ping)
    ///    track at once; a telescope alone needs <see cref="PassiveSeconds"/> of watching, as a real
    ///    angles-only orbit fit needs an arc of bearings. LIDAR: a telescope's laser rangefinder on your
    ///    target ranges it, <see cref="LidarFactor"/> times faster (and, like radar, gives you away).
    /// Watching accumulates while seen and is kept while not (a fit already made does not undo itself).
    /// </summary>
    public static class Tracking
    {
        public const double PassiveSeconds = 120;
        public const double LidarFactor = 12;

        public enum Quality { Unknown, Detected, Tracked }

        /// <summary>The watching after one more look of dt seconds by this sensor (targeted: your current target).</summary>
        public static double Watch(double dwell, SensorModel.Kind seenBy, bool targeted, double dt)
        {
            if (seenBy == SensorModel.Kind.Eyes || seenBy == SensorModel.Kind.Radar) return Math.Max(dwell, PassiveSeconds);
            return Math.Min(PassiveSeconds, dwell + dt * (targeted ? LidarFactor : 1));
        }

        public static Quality Of(bool detected, double dwell)
            => !detected ? Quality.Unknown : dwell >= PassiveSeconds ? Quality.Tracked : Quality.Detected;

        /// <summary>How far along a fit is (0..1), for "tracking 40%".</summary>
        public static double Progress(double dwell) => Math.Max(0, Math.Min(1, dwell / PassiveSeconds));

        /// <summary>A rough distance (two significant figures) for a contact not yet tracked.</summary>
        public static double Rough(double metres)
        {
            if (!(metres > 0)) return metres;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(metres)) - 1);
            return Math.Round(metres / mag) * mag;
        }
    }
}
