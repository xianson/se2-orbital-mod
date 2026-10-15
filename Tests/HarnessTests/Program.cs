using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OrbitalMod;

// HarnessTests - the dev harness's server asks (Scripts/Simulation/Dev/ServerAsks.cs) under the game's tick order, game-free.
// SE2 (GameAppComponent.GameUpdate): Server.Tick() starts the in-process server's tick on its own thread, Client.Tick() runs
// the client's frame on the main thread - the harness polls there - and Server.AfterTick() waits for the server's tick. The
// NEXT server tick starts only after the client's frame. 2026-10-15: the harness waited (3 s) in the client's frame for work it
// had queued for the server; queued after this tick's drain, it could run only once the wait gave up - "timed out waiting
// for the server thread" and a 3025 ms server tick gap, every time (a ship breaking up: nearly every call).
// Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;
    static void Check(bool ok, string what) { if (ok) { _pass++; Console.WriteLine("  PASS  " + what); } else { _fail++; Console.WriteLine("  FAIL  " + what); } }

    /// <summary>The game's two threads, as InProcessServerThread runs them.</summary>
    sealed class Game : IDisposable
    {
        readonly ManualResetEventSlim _start = new(false), _finished = new(false), _drained = new(false);
        readonly Thread _server;
        volatile bool _stop;
        public int ServerThreadId;
        public bool TickServer = true;
        public double WorstClientMs;
        public Game()
        {
            _server = new Thread(() =>
            {
                ServerThreadId = Environment.CurrentManagedThreadId;
                while (true)
                {
                    _start.Wait(); _start.Reset();
                    if (_stop) return;
                    ServerAsks.Drain();   // (the mod's server job: early in the tick - the client's harness comes after it)
                    _drained.Set();
                    Thread.Sleep(2);      // (the rest of the server's tick)
                    _finished.Set();
                }
            }) { IsBackground = true };
            _server.Start();
            while (ServerThreadId == 0) Thread.Sleep(1);
        }
        /// <summary>One GameUpdate: the server's tick started, the client's frame (after this tick's drain: the losing race,
        /// every time), the server's tick waited for.</summary>
        public void Frame(Action client)
        {
            if (TickServer) { _drained.Reset(); _start.Set(); _drained.Wait(); }
            var sw = Stopwatch.StartNew();
            client();
            WorstClientMs = Math.Max(WorstClientMs, sw.Elapsed.TotalMilliseconds);
            if (TickServer) { _finished.Wait(); _finished.Reset(); }
        }
        public void Dispose() { _stop = true; _start.Set(); _server.Join(1000); }
    }

    static int Main()
    {
        using var game = new Game();

        Console.WriteLine("[the old way: wait in the client's frame]");
        {
            var done = new ManualResetEventSlim(false);
            bool answered = true; var sw = new Stopwatch();
            game.Frame(() =>
            {
                ServerAsks.Queue(() => { done.Set(); return "ok"; });
                ServerAsks.Take();
                sw.Start(); answered = done.Wait(300); sw.Stop();
            });
            Check(!answered && sw.ElapsedMilliseconds >= 290, $"queued after this tick's drain, a wait in the frame is never answered: it only times out ({sw.ElapsedMilliseconds} ms)");
            game.Frame(() => { });
            Check(done.IsSet, "and the work runs the tick after the frame lets go");
        }

        Console.WriteLine("[a batch: queued, answered a frame later, in order]");
        {
            var batch = new ServerAsks.Batch();
            var got = new List<(string line, string r, int frame)>();
            int frame = 0, ranOn = 0;
            string Execute(string line) => line switch
            {
                "a" => "A",
                "srv" => ServerAsks.Then(ServerAsks.Queue(() => { ranOn = Environment.CurrentManagedThreadId; return "grids: 1=Ship"; }), r => r + " (client side)"),
                "b" => "B",
                _ => "?"
            };
            foreach (var l in new[] { "a", "srv", "b" }) batch.Add(l);
            game.WorstClientMs = 0;
            for (frame = 1; frame <= 5 && (frame == 1 || batch.Busy); frame++)
                game.Frame(() => batch.Pump(Execute, (l, r) => got.Add((l, r, frame)), 3));
            Check(got.Count == 3 && got[0].line == "a" && got[1].line == "srv" && got[2].line == "b", $"every line answered, in the file's order ({string.Join(", ", got.ConvertAll(g => g.line))})");
            Check(got.Count == 3 && got[0].frame == 1 && got[0].r == "A", "a line with no server work: answered at once");
            Check(got.Count == 3 && got[1].r == "grids: 1=Ship (client side)" && got[1].frame == 2, $"the server's line: the server's answer, then its client side, the next frame ({(got.Count > 1 ? got[1].r + " @" + got[1].frame : "-")})");
            Check(got.Count == 3 && got[2].frame == 2, "the line after it waits for it (it may depend on it), then runs");
            Check(ranOn == game.ServerThreadId, "the work ran on the server's thread");
            Check(game.WorstClientMs < 50, $"the client's frame never waits ({game.WorstClientMs:F1} ms worst)");
            Check(!batch.Busy, "the batch is done");
        }

        Console.WriteLine("[chained: a client side that asks the server again]");
        {
            var batch = new ServerAsks.Batch();
            string res = null; int at = 0, frame;
            string Two(string line) => ServerAsks.Then(
                ServerAsks.Then(ServerAsks.Queue(() => "p"), r => ServerAsks.Then(ServerAsks.Queue(() => r + "q"), r2 => r2 + "r")),
                r3 => r3 + "s");
            batch.Add("two");
            for (frame = 1; frame <= 6 && (frame == 1 || batch.Busy); frame++)
                game.Frame(() => batch.Pump(Two, (l, r) => { res = r; at = frame; }, 3));
            Check(res == "pqrs", $"inner continuations first, then the outer ones ({res})");
            Check(at == 3, $"two server ticks: answered the third frame ({at})");
        }

        Console.WriteLine("[an answer already in: Then runs at once]");
        Check(ServerAsks.Then("not run: the server thread is not ticking", r => "x " + r) == "x not run: the server thread is not ticking", "a refusal goes straight through the client side");

        Console.WriteLine("[the server thread never runs it: timed out, the frame free]");
        {
            var batch = new ServerAsks.Batch();
            string res = null;
            game.TickServer = false; game.WorstClientMs = 0;
            batch.Add("srv"); batch.Add("next");
            var sw = Stopwatch.StartNew();
            while (batch.Busy && sw.ElapsedMilliseconds < 2000)
            {
                game.Frame(() => batch.Pump(l => l == "srv" ? ServerAsks.Then(ServerAsks.Queue(() => "late"), r => "c: " + r) : "N", (l, r) => { if (l == "srv") res = r; }, 0.2));
                Thread.Sleep(10);
            }
            Check(res == "c: " + ServerAsks.TimedOut, $"after its time: the old wait's text, through the client side ({res})");
            Check(!batch.Busy, "and the lines after it run");
            Check(game.WorstClientMs < 50, $"no frame waited meanwhile ({game.WorstClientMs:F1} ms worst)");
            game.TickServer = true;
            game.Frame(() => { });   // (the stale work drains; nobody reads it)
        }

        Console.WriteLine("[work that throws]");
        {
            var batch = new ServerAsks.Batch();
            string res = null;
            batch.Add("boom");
            for (int i = 0; i < 3 && (i == 0 || batch.Busy); i++)
                game.Frame(() => batch.Pump(l => ServerAsks.Queue(() => throw new InvalidOperationException("no grid")), (l, r) => res = r, 3));
            Check(res != null && res.StartsWith("failed: no grid @ "), $"its answer is the failure, as before ({res})");
        }

        Console.WriteLine("[a command run elsewhere (the debug panel), finished by a batch]");
        {
            var panel = new ServerAsks.Batch();
            string res = null;
            bool held = false;
            game.Frame(() => { string r = ServerAsks.Queue(() => "panel"); held = r == ServerAsks.Pending && panel.Hold("warm clear"); });
            Check(held && panel.Busy, "held");
            game.Frame(() => { ServerAsks.Queue(() => "other"); Check(!panel.Hold("x"), "a second one is refused while it waits"); ServerAsks.Take(); });
            for (int i = 0; i < 3 && panel.Busy; i++) game.Frame(() => panel.Pump(l => "?", (l, r) => res = l + " => " + r, 3));
            Check(res == "warm clear => panel", $"answered ({res})");
        }

        Console.WriteLine($"HarnessTests: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
