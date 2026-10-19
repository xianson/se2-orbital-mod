using SEAerospace.Flight;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE PLAYER'S NOTICES, one channel for the map and flight (2026-10-18, the UI audit B3): WarpControl.Say toasted only
/// with the map closed and on the map kept its text in a field nothing drew - "Target: X", "Nothing to target under the
/// cursor", every Contacts message and the warp stops were lost there; in flight every notice came titled "Time warp".
/// Now a notice has a KIND (MessageKinds: Target, Burn, Warp, Contacts, Entry, Map): in flight it is a toast titled with
/// its kind; on the map it is a line of the board the map draws (MapLines) - always both kept, so closing or opening the
/// map mid-notice loses nothing. Client thread; the server's notices go through Post (a queue drained by Tick).
/// </summary>
public static class Messages
{
    static readonly MessageBoard _board = new MessageBoard();
    static readonly System.Collections.Concurrent.ConcurrentQueue<(string kind, string text)> _posted = new System.Collections.Concurrent.ConcurrentQueue<(string, string)>();
    /// <summary>How long a notice shows by default (s).</summary>
    public const double DefaultSeconds = 5.0;

    /// <summary>The clock the lines' At / Until are on (wall seconds).</summary>
    public static double Now => Wall();

    /// <summary>Say a notice (client thread): a toast titled `kind` in flight, a line on the map's board always.</summary>
    public static void Say(string kind, string text, double seconds = DefaultSeconds)
    {
        if (string.IsNullOrEmpty(text)) return;
        kind ??= MessageKinds.Of(text);
        _board.Post(kind, text, Wall(), seconds);
        var session = WarpControl.Session;
        // (no toast over the map: the toast sat on its close button - the map draws the board's lines instead)
        if (session != null && !MapView.Visible) GameUi.Toast(session, ToastKey(kind), kind == MessageKinds.Map ? "Notice" : kind, text, Math.Min(seconds, 4));
    }

    /// <summary>Say a notice from any thread (the server's): said on the next client tick.</summary>
    public static void Post(string kind, string text) { if (!string.IsNullOrEmpty(text)) _posted.Enqueue((kind, text)); }

    /// <summary>Client tick: what other threads posted is said now.</summary>
    public static void Tick()
    {
        while (_posted.TryDequeue(out var m)) Say(m.kind, m.text);
    }

    /// <summary>The map's lines (newest first, up to into.Length): how many show now.</summary>
    public static int MapLines(MessageBoard.Line[] into) => _board.Showing(Wall(), into);

    /// <summary>The newest line showing (false: none).</summary>
    public static bool MapLatest(out MessageBoard.Line line) => _board.Latest(Wall(), out line);

    /// <summary>The newest line of a kind showing (false: none) - e.g. the warp bar's own notice.</summary>
    public static bool Latest(string kind, out MessageBoard.Line line) => _board.LatestOf(kind, Wall(), out line);

    /// <summary>A new world: no notices.</summary>
    public static void ResetWorld() { _board.Clear(); while (_posted.TryDequeue(out _)) { } }

    // one toast slot per kind (a new one of a kind replaces the last), its key made once
    static string ToastKey(string kind)
    {
        switch (kind)
        {
            case MessageKinds.Target: return "msg-target";
            case MessageKinds.Burn: return "msg-burn";
            case MessageKinds.Warp: return "warp";   // (the warp toast's own key, as before)
            case MessageKinds.Contacts: return "msg-contacts";
            case MessageKinds.Entry: return "msg-entry";
            default: return "msg";
        }
    }
}
