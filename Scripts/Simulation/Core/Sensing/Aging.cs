using System;

namespace SEAerospace.Sensing
{
    /// <summary>
    /// CONTACTS AGE (Contacts; game-free, tested offline in Tests/SensingTests). 2026-10-19: a contact, once seen, was
    /// known for good and its orbit trusted for ever - a ship seen once a week ago was still predicted to the second.
    /// Now each contact remembers when it was last seen (game time):
    ///  - IN SIGHT: seen within <see cref="InSightSeconds"/> (a sensor has it now);
    ///  - RECENT: not in sight, but its orbit still trusted;
    ///  - STALE: not seen for the stale time (a setting): it may have burned since - its orbit is shown as an estimate
    ///    and it fades on the map;
    ///  - LOST: a MOVING ship not seen for the forget time (a setting; 0 never) is forgotten - find it again. Stations,
    ///    wrecks and rocks do not move off their orbits: never forgotten.
    /// </summary>
    public static class Aging
    {
        public const double InSightSeconds = 5;

        public enum State { InSight, Recent, Stale, Lost }

        /// <summary>The state after age seconds unseen (NaN: never stamped - counted in sight). Stale / forget seconds of 0
        /// or less: never.</summary>
        public static State Of(double age, double staleSeconds, double forgetSeconds, bool moving)
        {
            if (double.IsNaN(age) || age <= InSightSeconds) return State.InSight;
            if (moving && forgetSeconds > 0 && age >= forgetSeconds) return State.Lost;
            if (moving && staleSeconds > 0 && age >= staleSeconds) return State.Stale;
            return State.Recent;
        }

        /// <summary>How bright to draw it: 1 in sight or recent, fading over the stale span down to 0.4.</summary>
        public static double Fade(double age, double staleSeconds, double forgetSeconds, bool moving)
        {
            var s = Of(age, staleSeconds, forgetSeconds, moving);
            if (s != State.Stale) return s == State.Lost ? 0 : 1;
            if (!(forgetSeconds > staleSeconds)) return 0.4;
            double k = (age - staleSeconds) / (forgetSeconds - staleSeconds);
            return 1 - 0.6 * Math.Max(0, Math.Min(1, k));
        }

        /// <summary>"in sight", "40 s ago", "12 min ago", "3 h ago", "2 d ago".</summary>
        public static string Ago(double age)
        {
            if (double.IsNaN(age) || age <= InSightSeconds) return "in sight";
            if (age < 60) return $"{age:F0} s ago";
            if (age < 3600) return $"{Math.Floor(age / 60):F0} min ago";
            if (age < 86400) return $"{Math.Floor(age / 3600):F0} h ago";
            return $"{Math.Floor(age / 86400):F0} d ago";
        }
    }

    /// <summary>
    /// ONE TOAST PER PASS (Contacts' new-rock announcements; game-free, tested offline). 2026-10-19: rocks came into
    /// sight a few at a time as you went round, and each batch toasted ("Oblivara: a rock spotted" 7 times in 90 s). Now
    /// counts gather per group (a ring) and go out as one summary ("Oblivara: 7 rocks spotted") once nothing new came for
    /// <see cref="Quiet"/> seconds (or the batch is <see cref="MaxHold"/> old), never sooner than <see cref="MinGap"/>
    /// after the last summary. Wall-clock seconds (a toast is for the player, not the sim).
    /// </summary>
    public sealed class Batcher
    {
        public double Quiet = 10, MaxHold = 60, MinGap = 45;
        readonly System.Collections.Generic.Dictionary<string, int> _counts = new System.Collections.Generic.Dictionary<string, int>();
        readonly System.Collections.Generic.List<string> _order = new System.Collections.Generic.List<string>();
        double _first = double.NaN, _last = double.NaN, _sent = double.NegativeInfinity;

        public int Pending { get { int n = 0; foreach (var kv in _counts) n += kv.Value; return n; } }

        public void Add(string group, int n, double now)
        {
            if (n <= 0 || group == null) return;
            if (!_counts.TryGetValue(group, out int c)) _order.Add(group);
            _counts[group] = c + n;
            if (double.IsNaN(_first)) _first = now;
            _last = now;
        }

        /// <summary>A summary is due now (held: the caller cannot show it yet - the map is open - and asks again later).</summary>
        public bool Due(double now)
            => _counts.Count > 0 && now - _sent >= MinGap && (now - _last >= Quiet || now - _first >= MaxHold);

        /// <summary>The summary's lines, one per group, and the batch emptied.</summary>
        public System.Collections.Generic.List<string> Take(double now)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var g in _order) { int n = _counts[g]; lines.Add(n == 1 ? $"{g}: a rock spotted" : $"{g}: {n} rocks spotted"); }
            _counts.Clear(); _order.Clear();
            _first = _last = double.NaN; _sent = now;
            return lines;
        }

        public void Clear() { _counts.Clear(); _order.Clear(); _first = _last = double.NaN; _sent = double.NegativeInfinity; }
    }
}
