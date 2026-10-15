#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV: the harness's work for the server thread, answered WITHOUT waiting for it (engine-free: Tests/HarnessTests).
/// The client's frame queues the work (<see cref="Queue"/> answers <see cref="Pending"/>), the server's tick runs it
/// (<see cref="Drain"/>, from GridGen.ServerTick), and a later client frame finishes the command with the answer - the client
/// side of a command that needs the answer goes in a continuation (<see cref="Then"/>). <see cref="Batch"/> keeps a command
/// file's lines in order: the line after a waiting one runs once that one is answered.
/// (2026-10-15: the harness WAITED, up to 3 s, inside the client's frame. The in-process server's next tick starts only
///  after the client's frame (GameAppComponent.GameUpdate: Server.Tick starts the server thread, Client.Tick - the harness -
///  then Server.AfterTick waits for it), so work queued after this tick's drain could only run once the wait gave up: every
///  such call was a 3 s freeze and "timed out waiting for the server thread" - a deadlock with a timeout, the log's
///  "server tick gap 3025 ms" right after each one. Heavy server load (a ship breaking up) made it most calls: the
///  integration test read 0 grids and failed "the ship came apart".)
/// </summary>
public static class ServerAsks
{
    /// <summary>The answer of a command whose work waits on the server thread (never a real answer: it starts with \u0001).</summary>
    public const string Pending = "\u0001asked the server thread";
    /// <summary>The answer when the server thread has not run the work in this long (as the old wait's).</summary>
    public const string TimedOut = "timed out waiting for the server thread";

    public sealed class Ask
    {
        internal Func<string> Work;
        internal string Result;
        internal volatile bool Done;
        /// <summary>The client side, in order, given the answer (each may ask the server again).</summary>
        internal readonly List<Func<string, string>> Thens = new List<Func<string, string>>();
        internal long Since;
    }

    static readonly System.Collections.Concurrent.ConcurrentQueue<Ask> _queue = new System.Collections.Concurrent.ConcurrentQueue<Ask>();
    static Ask _last;   // (the client thread's newest ask: Then and Take - one thread only, the harness's)

    /// <summary>Client: queue <paramref name="work"/> for the server thread; <see cref="Pending"/>.</summary>
    public static string Queue(Func<string> work)
    {
        var k = new Ask { Work = work, Since = System.Diagnostics.Stopwatch.GetTimestamp() };
        _last = k;
        _queue.Enqueue(k);
        return Pending;
    }

    /// <summary>Client: <paramref name="next"/> of the answer - now, or once the server thread has given it.</summary>
    public static string Then(string answer, Func<string, string> next)
    {
        if (answer != Pending) return next(answer);
        _last.Thens.Add(next);
        return Pending;
    }

    /// <summary>Client: the ask a <see cref="Pending"/> answer is waiting on (once).</summary>
    public static Ask Take() { var k = _last; _last = null; return k; }

    /// <summary>Server thread: run everything queued.</summary>
    public static void Drain()
    {
        while (_queue.TryDequeue(out var k))
        {
            try { k.Result = k.Work(); }
            catch (Exception e)
            {
                var x = e.InnerException ?? e;
                var st = (x.StackTrace ?? "").Split((char)10);
                var sb = new System.Text.StringBuilder("failed: ").Append(x.Message).Append(" @ ");
                for (int i = 0; i < st.Length && i < 4; i++) { if (i > 0) sb.Append(" < "); sb.Append(st[i].Trim()); }
                k.Result = sb.ToString();
            }
            k.Done = true;
        }
    }

    /// <summary>
    /// Client: a command file's lines, run in order; a line waiting on the server thread holds the ones after it (they may
    /// depend on it) until it is answered. <see cref="Pump"/> every client frame.
    /// </summary>
    public sealed class Batch
    {
        readonly Queue<string> _lines = new Queue<string>();
        Ask _wait; string _waitLine; int _then;
        public bool Busy => _wait != null || _lines.Count > 0;
        public void Add(string line) => _lines.Enqueue(line);
        /// <summary>A line run elsewhere that answered <see cref="Pending"/>: finished here, by <see cref="Pump"/> (false, and
        /// left alone, while another one waits here).</summary>
        public bool Hold(string line)
        {
            if (Busy) return false;
            _wait = Take(); _waitLine = line; _then = 0;
            return _wait != null;
        }

        /// <summary>Finish the waiting line if its answer is in (or it timed out), then run lines until one waits. Each
        /// finished line goes to <paramref name="done"/> with its answer.</summary>
        public void Pump(Func<string, string> execute, Action<string, string> done, double timeoutSeconds)
        {
            if (_wait != null)
            {
                string r;
                if (_wait.Done) r = _wait.Result;
                else if ((System.Diagnostics.Stopwatch.GetTimestamp() - _wait.Since) < timeoutSeconds * System.Diagnostics.Stopwatch.Frequency) return;
                else r = TimedOut;   // (the work stays queued and runs when the server ticks, as the old wait's did)
                if (!Continue(r)) return;
            }
            while (_lines.Count > 0)
            {
                string line = _lines.Dequeue();
                string r = execute(line);
                if (r == Pending)
                {
                    _wait = Take(); _waitLine = line; _then = 0;
                    if (_wait == null) { done(line, "ERROR: a server ask without its work"); continue; }
                    return;
                }
                done(line, r);
            }

            bool Continue(string r)
            {
                var k = _wait;
                for (int i = _then; i < k.Thens.Count; i++)
                {
                    try { r = k.Thens[i](r); }
                    catch (Exception e) { r = "ERROR " + e.Message; }
                    if (r != Pending) continue;
                    var k2 = Take();
                    if (k2 == null) { r = "ERROR: a server ask without its work"; continue; }
                    for (int j = i + 1; j < k.Thens.Count; j++) k2.Thens.Add(k.Thens[j]);   // (its own continuations first, then ours)
                    _wait = k2; _then = 0;
                    return false;
                }
                string line = _waitLine;
                _wait = null; _waitLine = null;
                done(line, r);
                return true;
            }
        }
    }
}
