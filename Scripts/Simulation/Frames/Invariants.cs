#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// INVARIANTS, CHECKED ALL THE TIME (in play too, not only in tests): the rules the frames must keep, each judged once a
/// second, a violation logged ("[ORBIT-INV] VIOLATION rule: detail" - once per 10 s per case), counted, and listed by the
/// harness ('inv'). Every in-game suite ends by requiring none. The rules (server side, ServerFrames.CheckInvariants):
///  - anchor-alive: a frame's grid anchor is a live grid (or one about to come back: an area respawn);
///  - past-edge: no dynamic member stays past the frame's edge (20 km; 10 km from a static anchor) unsplit;
///  - ship-together: every grid of a joined ship (sub-grids, docked craft) is in the same frame;
///  - warp-state: no member is steered (warp's kept state) at x1 once its settle is over;
///  - on-rails: a player ship in open space is in a frame (one let go was left with no orbit at all).
/// and client side:
///  - seated-frame: seated in a framed ship, the HUD's frame is that ship's (or its anchor's);
///  - prediction: where the plot said you would be 30 s (game time) later is where you are (within 50 m + 3 % of the
///    distance travelled; not across a split, a merge or a re-pin).
/// </summary>
public static class Invariants
{
    public static bool Enabled = true;
    static readonly object _gate = new object();
    static readonly Dictionary<string, int> _count = new Dictionary<string, int>();
    static readonly Dictionary<string, double> _lastLog = new Dictionary<string, double>();
    static readonly List<string> _recent = new List<string>();
    public static int Total;

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>A violation of <paramref name="rule"/>; <paramref name="key"/> names the case (rate limit: once per 10 s each).</summary>
    public static void Report(string rule, string key, string detail)
    {
        if (!Enabled) return;
        double now = Wall();
        lock (_gate)
        {
            _count.TryGetValue(rule, out int n); _count[rule] = n + 1; Total++;
            string k = rule + "|" + key;
            if (_lastLog.TryGetValue(k, out double last) && now - last < 10) return;
            _lastLog[k] = now;
            string line = $"{rule}: {detail}";
            _recent.Add(System.DateTime.Now.ToString("HH:mm:ss") + " " + line);
            if (_recent.Count > 30) _recent.RemoveAt(0);
            Log.Default?.Info("[ORBIT-INV] VIOLATION " + line);
        }
    }

    /// <summary>Harness: the counts per rule and the latest violations; 'inv reset' clears them.</summary>
    public static string Describe(bool reset = false)
    {
        lock (_gate)
        {
            string s = (Total == 0 ? "inv: none" : $"inv: {Total} violation(s) | " + string.Join(", ", System.Linq.Enumerable.Select(_count, kv => $"{kv.Key} {kv.Value}")) + " | latest: " + string.Join(" || ", _recent.GetRange(System.Math.Max(0, _recent.Count - 5), System.Math.Min(5, _recent.Count)))) + $" | predictions judged {PredictionsJudged}";
            if (reset) { _count.Clear(); _lastLog.Clear(); _recent.Clear(); Total = 0; }
            return s;
        }
    }

    // ── client side ──

    static double _nextClient;
    /// <summary>Client tick: the seated-frame rule (once a second).</summary>
    public static void ClientCheck()
    {
        if (!Enabled) return;
        double now = Wall();
        if (now < _nextClient) return;
        _nextClient = now + 1.0;
        try
        {
            if (!FrameHost.Seated || FrameHost.SeatGridServerId == 0 || SystemHost.Frames == null) return;
            SEAerospace.Frames.ProximityFrame shipFrame; lock (ServerFrames.FramesLock) shipFrame = SystemHost.Frames.FindByMember(FrameHost.SeatGridServerId);
            if (shipFrame == null) return;   // (an unframed seat grid near a framed one: its own rule, on-rails)
            var hud = FrameHost.PlayerFrame;
            if (hud == null || hud.Id != shipFrame.Id)
                Report("seated-frame", "f" + shipFrame.Id, $"seated in grid {FrameHost.SeatGridServerId} of frame #{shipFrame.Id}, the HUD's frame is {(hud == null ? "none" : "#" + hud.Id)}");
        }
        catch { }
    }

    /// <summary>OrbitDisplay's 30 s check: predicted against actual offset (m), the distance the plot said you would move,
    /// and whether the frame stayed the same frame throughout (a split, merge or re-pin in between: not judged).</summary>
    public static int PredictionsJudged;
    public static void Prediction(double error, double travelled, bool sameFrame, long frameId, double warp)
    {
        if (!sameFrame) return;
        PredictionsJudged++;
        double bound = 50 + 0.03 * travelled;
        if (error > bound) Report("prediction", "f" + frameId, $"30 s on, {error:F0} m from where the plot said (bound {bound:F0} m; moved {travelled:F0} m; warp x{warp:F0}) in frame #{frameId}");
    }
}
