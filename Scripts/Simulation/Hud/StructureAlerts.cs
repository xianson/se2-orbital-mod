using Keen.VRage.Core;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE SHIP YOU FLY BREAKING (2026-10-15): the Aerodynamics Mod's structure failures (AeroApi version 7's polled event queue,
/// ReadStructureEvents) on the seated grid, as a toast - "Structural failure: 2 sections, 41 blocks" / "Burn-through: 3
/// blocks" - and a log line. The game removes the blocks with no word; aero decides them in its background evaluation.
/// Polled twice a second (client, FrameHost's tick; no lock of ours held - the lock rule), into arrays kept here: no
/// allocation unless something failed. Events of other grids are skipped. Bound through AeroLink (the last loaded aero, bound
/// again after a reload); no aero mod or an older one (version under 7): nothing, Status says why.
/// </summary>
public static class StructureAlerts
{
    public static bool On = true;
    public static double PollSeconds = 0.5;
    public static string Status = "not looked up";
    /// <summary>Failures seen on the seated grid this session (dev: the harness reads it).</summary>
    public static int Sections, Blocks, Burnt;

    delegate long SequenceFn();
    delegate int ReadFn(long fromSequence, long[] sequences, int[] kinds, Entity[] grids, Entity[] blocks, Vector3[] pointsLocal, Vector3[] directionsLocal, float[] values, out long nextSequence);
    static SequenceFn _seq; static ReadFn _read;
    static int _gen = -1;
    static long _next = -1;
    static double _pollAt, _faultUntil;
    static Entity _cur, _prev; static double _prevUntil;
    const int Batch = 64, MaxBatches = 8;   // (the queue keeps 512: eight reads catch up with all of it)
    static readonly long[] _sq = new long[Batch];
    static readonly int[] _kinds = new int[Batch];
    static readonly Entity[] _grids = new Entity[Batch];

    /// <summary>Client, every frame (FrameHost): cheap until the poll is due.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!On) return;
        double now = Wall();
        if (now < _pollAt) return;
        _pollAt = now + PollSeconds;
        if (now < _faultUntil) return;
        // (the server copy of the grid you sit in: aero's events name the server grid. The one before is kept 3 s - a
        //  failure that splits the ship may seat you on the new piece, or out, before its events are read)
        var g = FrameHost.Seated && FrameHost.SeatGridServerId != 0 ? GridMembers.Get(FrameHost.SeatGridServerId) : null;
        Entity seat = g != null && g.IsServer ? g.Entity : null;
        if (!ReferenceEquals(seat, _cur)) { if (_cur != null) { _prev = _cur; _prevUntil = now + 3; } _cur = seat; }
        if (now > _prevUntil) _prev = null;
        // (2026-10-15: asked unseated too - aero records events only once someone asks, and our asking from the menu on held
        //  its ring of up to 512 destroyed blocks and grids alive all session. Now only seated (or the 3 s after); a new
        //  seat reads from its own now)
        if (_cur == null && _prev == null) { _next = -1; return; }
        if (!Bind()) return;
        try
        {
            if (_next < 0) { _next = _seq(); return; }   // (from now: what failed before we looked is not news)
            int cuts = 0, blocks = 0, burnt = 0;
            for (int b = 0; b < MaxBatches; b++)
            {
                int n = _read(_next, _sq, _kinds, _grids, null, null, null, null, out long next);
                _next = next;
                for (int i = 0; i < n; i++)
                {
                    var eg = _grids[i];
                    if (eg == null || !ReferenceEquals(eg, _cur) && !ReferenceEquals(eg, _prev)) continue;
                    if (_kinds[i] == 3) cuts++; else if (_kinds[i] == 2) burnt++; else if (_kinds[i] == 1) blocks++;
                }
                System.Array.Clear(_grids, 0, n);   // (no destroyed grid kept alive by us)
                if (n < Batch) break;
            }
            if (cuts + blocks + burnt == 0) return;
            Sections += cuts; Blocks += blocks; Burnt += burnt;
            string text = blocks > 0
                ? (cuts > 0 ? $"{cuts} section{(cuts == 1 ? "" : "s")} broke away, {blocks} block{(blocks == 1 ? "" : "s")}" : $"{blocks} block{(blocks == 1 ? "" : "s")} failed")
                  + (burnt > 0 ? $"; {burnt} burnt through" : "")
                : $"{burnt} block{(burnt == 1 ? "" : "s")} burnt through";
            Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[ORBIT-STRUCT] seated ship: {text}");
            if (!MapView.Visible) GameUi.Toast(session, "structure", blocks > 0 ? "Structural failure" : "Burn-through", text, 5);
        }
        catch (System.Exception e)
        {
            // (a poll, not a hot path: dropped, bound again after a minute - an exception costs ~200 ms in SE2)
            Status = "aero's structure events threw: " + e.GetType().Name;
            _seq = null; _read = null; _gen = -1; _next = -1; _faultUntil = now + 60;
        }
    }

    /// <summary>A new world (WorldReset.ClientSession, the client thread): the last world's grids and queue position go.</summary>
    internal static void ResetWorld()
    {
        _next = -1; _cur = _prev = null; _prevUntil = 0;
        Sections = Blocks = Burnt = 0;
    }

    /// <summary>The two members, once per aero assembly (AeroLink.Generation): typed delegates, looked up by name and parameter
    /// types. A new assembly starts the queue from its own now.</summary>
    static bool Bind()
    {
        int gen = AeroLink.Generation;
        if (gen == _gen) return _read != null;
        _gen = gen; _seq = null; _read = null; _next = -1;
        var t = AeroLink.Find("AeroMod.AeroApi");
        if (t == null) { Status = "no aero mod"; return false; }
        // (explicit parameter types: an aero overload added later must not make a lookup ambiguous)
        var seq = t.GetMethod("GetStructureEventSequence", System.Type.EmptyTypes);
        var read = t.GetMethod("ReadStructureEvents", new[] { typeof(long), typeof(long[]), typeof(int[]), typeof(Entity[]), typeof(Entity[]), typeof(Vector3[]), typeof(Vector3[]), typeof(float[]), typeof(long).MakeByRefType() });
        if (seq == null || read == null) { Status = "aero's API has no structure events (older than version 7)"; return false; }
        _seq = (SequenceFn)System.Delegate.CreateDelegate(typeof(SequenceFn), seq, false);
        _read = (ReadFn)System.Delegate.CreateDelegate(typeof(ReadFn), read, false);
        if (_seq == null || _read == null) { _seq = null; _read = null; Status = "aero's structure events: unexpected signature"; return false; }
        Status = "bound to AeroApi.ReadStructureEvents";
        return true;
    }
}
