namespace SEAerospace.Flight
{
    /// <summary>
    /// The kinds of the player's notices: each is the title of its toast in flight and the tag of its line on the map
    /// (2026-10-18, the UI audit: every notice - a target set, a contact hidden - came as a toast titled "Time warp", and
    /// none at all with the map open). Engine-free: tested offline (Tests/FlightTests).
    /// </summary>
    public static class MessageKinds
    {
        public const string Target = "Target", Burn = "Burn", Warp = "Warp", Contacts = "Contacts", Entry = "Entry", Map = "Map";

        /// <summary>The kind of a notice said without one (the older one-argument callers, WarpControl.Say): by its first
        /// words. Unknown words: Map (a plain notice).</summary>
        public static string Of(string text)
        {
            if (string.IsNullOrEmpty(text)) return Map;
            if (Starts(text, "Target") || Starts(text, "Nothing to target") || Starts(text, "Not on this view") || Starts(text, "Can't target")
                || Starts(text, "Cannot target")) return Target;
            if (Starts(text, "Contact") || Starts(text, "GPS point") || Starts(text, "Asteroid") || Starts(text, "Tracked") || Starts(text, "Rock")) return Contacts;
            if (Starts(text, "Auto-burn") || Starts(text, "Burn") || Starts(text, "Maneuver")) return Burn;
            if (Starts(text, "Warp") || Starts(text, "Needs rails")) return Warp;
            if (Starts(text, "Entry") || Starts(text, "No air")) return Entry;
            return Map;
        }

        static bool Starts(string s, string w) => s.StartsWith(w, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE PLAYER'S NOTICES as lines with an expiry: one line per kind (a new notice replaces the showing one of its kind -
    /// "Warp ×5" then "Warp ×10" is one line, not two), newest first, at most Capacity. The map draws the showing lines in its
    /// strip; in flight each notice is also a toast (the host's part). No allocation per notice: a fixed ring.
    /// Engine-free (Tests/FlightTests). Not thread-safe: the client thread posts (the server's go through a queue).
    /// </summary>
    public sealed class MessageBoard
    {
        public struct Line
        {
            public string Kind, Text;
            /// <summary>When it was said and until when it shows (the caller's clock, seconds).</summary>
            public double At, Until;
            /// <summary>Counts up with every notice (a renderer can tell a new one from the same one).</summary>
            public int Serial;
            public bool Showing(double now) => Text != null && now < Until;
        }

        public const int Capacity = 4;
        readonly Line[] _lines = new Line[Capacity];   // newest first
        int _serial;

        /// <summary>The last notice's serial (0: none yet).</summary>
        public int Serial => _serial;

        /// <summary>Say a notice: it replaces the showing line of its kind (or the oldest line), and goes first.</summary>
        public Line Post(string kind, string text, double now, double seconds)
        {
            kind ??= MessageKinds.Of(text);
            int at = Capacity - 1;   // (the oldest, when no line of this kind)
            for (int i = 0; i < Capacity; i++) if (_lines[i].Text != null && _lines[i].Kind == kind) { at = i; break; }
            for (int i = at; i > 0; i--) _lines[i] = _lines[i - 1];
            _lines[0] = new Line { Kind = kind, Text = text, At = now, Until = now + Math.Max(0.5, seconds), Serial = ++_serial };
            return _lines[0];
        }

        /// <summary>The showing lines, newest first, into `into` (up to its length): how many.</summary>
        public int Showing(double now, Line[] into)
        {
            int n = 0;
            for (int i = 0; i < Capacity && n < into.Length; i++) if (_lines[i].Showing(now)) into[n++] = _lines[i];
            return n;
        }

        /// <summary>The newest showing line; false when none shows.</summary>
        public bool Latest(double now, out Line line)
        {
            for (int i = 0; i < Capacity; i++) if (_lines[i].Showing(now)) { line = _lines[i]; return true; }
            line = default; return false;
        }

        /// <summary>The newest line of a kind still showing (false: none).</summary>
        public bool LatestOf(string kind, double now, out Line line)
        {
            for (int i = 0; i < Capacity; i++) if (_lines[i].Showing(now) && _lines[i].Kind == kind) { line = _lines[i]; return true; }
            line = default; return false;
        }

        public void Clear() { for (int i = 0; i < Capacity; i++) _lines[i] = default; }
    }
}
