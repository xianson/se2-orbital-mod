using System;
using OrbitalMod;

// Offline checks for Server/LastKnownPosition.cs - GridMembers.Position / TryPosition's rule for a dead grid (game-free).
// Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;

    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static int Main()
    {
        var a = new Vector3D(1000, -2000, 3000);
        var b = new Vector3D(1500, -2000, 3000);
        var q = new Vector3D(9e6, 0, 0);

        Console.WriteLine("[live grid]");
        var m = new LastKnownPosition();
        Check(!m.Known, "a fresh memo knows nothing");
        bool live = LastKnownPosition.Pick(false, default, true, true, a, ref m, out var p);
        Check(live && p == a, "a live read is the answer, true");
        Check(m.Known && m.At == a, "and is remembered");
        live = LastKnownPosition.Pick(false, default, true, true, b, ref m, out p);
        Check(live && p == b && m.At == b, "each live read replaces the last");

        Console.WriteLine("[dead grid]");
        live = LastKnownPosition.Pick(false, default, false, false, default, ref m, out p);
        Check(!live, "dead: false (TryPosition's callers leave it alone)");
        Check(p == b, "dead: the last known place (Position's distance tests see it where it was)");
        Check(IsFinite(p), "never a NaN (a NaN admitted a dead grid to every 'skip past R' cluster, and broke the sort)");
        live = LastKnownPosition.Pick(false, default, false, true, q, ref m, out p);
        Check(!live && p == b && m.At == b, "a read claimed for a dead grid is not taken, nor remembered");
        live = LastKnownPosition.Pick(false, default, true, false, q, ref m, out p);
        Check(!live && p == b && m.At == b, "alive but the read failed (deleted between the check and the read): the last one, false");

        Console.WriteLine("[queued move]");
        live = LastKnownPosition.Pick(true, q, true, false, default, ref m, out p);
        Check(live && p == q, "alive with a move queued: its target (as before), true");
        Check(m.At == b, "a queued target is not a read: not remembered");
        live = LastKnownPosition.Pick(true, q, false, false, default, ref m, out p);
        Check(!live && p == q, "dead with a move queued: Position still answers the target, TryPosition says false");

        Console.WriteLine("[never read]");
        var n = new LastKnownPosition();
        live = LastKnownPosition.Pick(false, default, false, false, default, ref n, out p);
        Check(!live && !n.Known && p == Vector3D.Zero, "deleted before any read: the origin, false, and not Known");

        Console.WriteLine("[per grid]");
        var g1 = new LastKnownPosition(); var g2 = new LastKnownPosition();
        LastKnownPosition.Pick(false, default, true, true, a, ref g1, out _);
        LastKnownPosition.Pick(false, default, true, true, b, ref g2, out _);
        LastKnownPosition.Pick(false, default, false, false, default, ref g1, out var p1);
        LastKnownPosition.Pick(false, default, false, false, default, ref g2, out var p2);
        Check(p1 == a && p2 == b, "each grid keeps its own last place");

        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static bool IsFinite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
}
