using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OrbitalMod;

// Offline checks for Server/HavokMoves.cs - how a grid move may reach Havok's large-world manager (game-free), and a scan of
// the mod's sources for the bug class (2026-10-17, "There are still active bodies in native world N": a static body moved
// by SetWorldTransform stayed in its old physics world while its box left it). The cluster rules mirror
// HavokLargeWorldManager.UpdateStaticEntityCluster (decompiled 2.4.0.95, identical to .77). Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;
    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static BoundingBoxD Cube(Vector3D c, double half) => BoundingBoxD.CreateFromHalfExtent(c, half);

    static int Main()
    {
        Route();
        StaticLanding();
        Park();
        DriftAcrossABorder();
        Audit();
        SourceScan();
        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void Route()
    {
        Console.WriteLine("[route: straight, teleport phase, refused]");
        var R = HavokMoves.Route.Direct; var D = HavokMoves.Route.Deferred; var X = HavokMoves.Route.RefusedStatic;
        Check(HavokMoves.Choose(true, false, false, 1) == X, "a static is refused by default, however short the move (MoveGrid / SetPosition)");
        Check(HavokMoves.Choose(true, false, true, 5e6) == X, "a static with joints too");
        Check(HavokMoves.Choose(true, true, false, 0.5) == D, "a meant static move (allowStatic) goes to the teleport phase even for half a metre (any static move can land in the leaking case)");
        Check(HavokMoves.Choose(true, true, false, 5e6) == D, "and a long one");
        Check(HavokMoves.Choose(false, false, true, 1) == D, "a dynamic grid with joints: the teleport phase with its joined set (as before)");
        Check(HavokMoves.Choose(false, false, false, 16.5) == R, "a dynamic grid's per-tick step (990 m/s at 60 Hz = 16.5 m): straight - ordinary motion");
        Check(HavokMoves.Choose(false, false, false, HavokMoves.OrdinaryStep) == R, "up to the engine's world margin (100 m) straight");
        Check(HavokMoves.Choose(false, false, false, HavokMoves.OrdinaryStep + 0.1) == D, "past it: a teleport, the teleport phase");
        Check(HavokMoves.Choose(false, false, false, 7500) == D, "a merge's 7.5 km: the teleport phase");
        Check(HavokMoves.Choose(false, false, false, double.NaN) == D, "a NaN distance is never a straight write");
        Check(HavokMoves.OrdinaryStep == 100.0, "the step is LargeWorldManagerSettings.WorldMargin (100 m)");
    }

    static void StaticLanding()
    {
        Console.WriteLine("[a static's landing against the physics worlds]");
        // worlds as the engine makes them: 20 km cubes at least (MinimumWorldRadius 10 km)
        var A = Cube(new Vector3D(0, 0, 0), 10000);
        var B = Cube(new Vector3D(30000, 0, 0), 10000);    // a neighbour, 10 km of empty space between
        var w = new[] { A, B };
        double r = 50;   // a 100 m station
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(200, 0, 0), r, w, 2), "a short move inside its world: stays, still in its box");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(9990, 0, 0), r, w, 2), "out to its world's edge, centre still inside: stays - the box still finds it");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(1e7, 0, 0), r, w, 2), "far into empty space: it leaves its world for none");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(30000, 0, 0), r, w, 2), "into a world that contains it whole: migrates there");
        // THE CRASH CASE: clear of its own world, landing across a neighbour's edge - the engine keeps it in the old world
        // (UpdateStaticEntityCluster: Disjoint, the one world found does not contain it, nothing done) while its tree entry
        // moves: the old cluster's removal cannot find it. Moved straight before 2026-10-17.
        Check(!HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(20000, 0, 0), r, w, 2), "LEAK CASE: clear of its world, across a neighbour's edge - not safe");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(10030, 0, 0), r, w, 2), "grazing its world's edge from outside, nothing else near: safe either way the engine calls it (stays, or leaves for none)");
        var E = Cube(new Vector3D(20080, 0, 0), 10000);   // a neighbour whose edge (x = 10.08 km) is right there
        Check(!HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(10030, 0, 0), r, new[] { A, E }, 2), "grazing its world's edge beside a neighbour that only overlaps it: not safe (if the engine calls it clear, it is the leak)");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(15000, 0, 0), new Vector3D(20000, 0, 0), r, w, 2), "in no world now (between them): safe anywhere - added only to a world that contains it");
        // in two boxes now (straddling a seam two worlds share): each must be safe
        var C = Cube(new Vector3D(20000, 0, 0), 10000);   // touches A at x = 10 km
        var w2 = new[] { A, C };
        Check(HavokMoves.StaticMoveSafe(new Vector3D(10000, 0, 0), new Vector3D(1e7, 0, 0), r, w2, 2), "on a seam, off to empty space: safe from either world");
        Check(!HavokMoves.StaticMoveSafe(new Vector3D(10000, 0, 0), new Vector3D(-10000, 0, 0), r, new[] { A, C, Cube(new Vector3D(-20000, 0, 0), 10000) }, 3),
              "on a seam, to another seam: not safe (from C it lands across two worlds' edge)");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(20000, 0, 0), r, w, 0), "no worlds at all: safe");
        // B's edge at x = 20 km: our sphere's box (radius + BoundsMargin) is what counts
        Check(!HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(20000 - r - HavokMoves.BoundsMargin + 1, 0, 0), r, w, 2), "within the margin of a neighbour's edge counts as touching it: not safe");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), new Vector3D(20000 - r - HavokMoves.BoundsMargin - 1, 0, 0), r, w, 2), "just clear of the margin: it lands in no world - safe");
    }

    static void Park()
    {
        Console.WriteLine("[the first hop: clear of every world]");
        var A = Cube(new Vector3D(0, 0, 0), 10000);
        var B = Cube(new Vector3D(30000, 0, 0), 10000);
        var w = new[] { A, B };
        double r = 50;
        var dest = new Vector3D(20000, 0, 0);
        Check(HavokMoves.TryParkPoint(dest, r, w, 2, out var park), "a park point is found");
        var pbox = Cube(park, r + HavokMoves.BoundsMargin);
        Check(!A.Intersects(in pbox) && !B.Intersects(in pbox), "it touches no world");
        Check(HavokMoves.StaticMoveSafe(new Vector3D(0, 0, 0), park, r, w, 2), "hop 1 (its world -> the park) is safe: it leaves its world for none");
        Check(HavokMoves.StaticMoveSafe(park, dest, r, w, 2), "hop 2 (the park -> its landing) is safe: in no world, added only where contained");
        Check(HavokMoves.TryParkPoint(dest, r, w, 0, out var p0) && (p0 - dest).Length() > 0, "no worlds: still a point (trivially clear)");
        Check(!HavokMoves.TryParkPoint(new Vector3D(double.NaN, 0, 0), r, w, 2, out _), "a NaN landing: no park");
        // a crowd of worlds round the landing: the first clear axis is taken
        var crowd = new[] { A, B, Cube(dest + new Vector3D(0, 60000, 0), 10000) };
        Check(HavokMoves.TryParkPoint(dest, r, crowd, 3, out var pc) && crowd.All(c => !c.Intersects(Cube(pc, r + HavokMoves.BoundsMargin))), "a world where +Y would park: another axis");
    }

    static void DriftAcrossABorder()
    {
        Console.WriteLine("[a static placed each tick along its conic (HighSpeed) - the per-tick drift out of its world]");
        // 2 km/s at 60 Hz: 33 m a tick, from inside A towards a neighbour that overlaps the gap - before 2026-10-17 every tick
        // was a straight write; the tick it leaves A next to B's edge is the leak.
        var A = Cube(new Vector3D(0, 0, 0), 10000);
        var B = Cube(new Vector3D(20060, 0, 0), 10000);   // a neighbour across a 60 m gap (a reorder splits worlds side by side)
        var w = new[] { A, B };
        double r = 50, step = 2000.0 / 60;
        int unsafeTicks = 0, firstUnsafe = -1;
        var at = new Vector3D(9000, 0, 0);
        for (int k = 0; k < 120; k++)
        {
            var next = at + new Vector3D(step, 0, 0);
            if (!HavokMoves.StaticMoveSafe(at, next, r, w, 2)) { unsafeTicks++; if (firstUnsafe < 0) firstUnsafe = k; }
            at = next;
        }
        Check(unsafeTicks > 0, $"the drift has unsafe ticks ({unsafeTicks} of 120, the first at tick {firstUnsafe}) - each would be parked, not written");
        Check(firstUnsafe > 0, "not from the start: inside its world it moves as before (through the teleport phase)");
        // with nothing next door: never unsafe
        int none = 0; at = new Vector3D(9000, 0, 0);
        for (int k = 0; k < 120; k++) { var next = at + new Vector3D(step, 0, 0); if (!HavokMoves.StaticMoveSafe(at, next, r, new[] { A }, 1)) none++; at = next; }
        Check(none == 0, "the same drift with no neighbour: never parked (it leaves its world for none)");
    }

    static void Audit()
    {
        Console.WriteLine("[the cluster audit's count]");
        Check(HavokMoves.Unaccounted(5, 3, 2) == 0, "bodies = dynamics + statics in its box: consistent");
        Check(HavokMoves.Unaccounted(4, 3, 2) < 0, "fewer bodies (a static in the box but in no world, or a neighbour's): not a leak");
        Check(HavokMoves.Unaccounted(6, 3, 2) == 1, "one more body than the removal can find: 1 unaccounted (the assertion's precondition)");
        Check(HavokMoves.Unaccounted(1, 0, 0) == 1, "an empty cluster with a body left in it (the crash state)");
    }

    // ── the bug class in the sources: every transform write and every opted-in static mover is a vetted one ──
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Server", "HavokMoves.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Server/HavokMoves.cs above " + AppContext.BaseDirectory);
    }

    static void SourceScan()
    {
        Console.WriteLine("[sources: transform writes and static movers]");
        string root = Path.Combine(Root(), "Scripts");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        int Count(string file, Regex re)
        {
            int n = 0;
            foreach (var line in File.ReadLines(file))
            {
                int c = line.IndexOf("//", StringComparison.Ordinal);
                string code = c >= 0 ? line.Substring(0, c) : line;
                n += re.Matches(code).Count;
            }
            return n;
        }
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');

        // every SetWorldTransform: GridMembers.MoveTo / Rotate (dynamic only, MoveGrid's straight path), the teleport phase's
        // two writes (a static's park hop and the move), WarmBerths.Pin (an ordinary step of a dynamic placeholder), and the
        // client's own camera and character turned in place (FrameHost.Crossing - client entities, never a server grid)
        var vetted = new Dictionary<string, int>
        {
            ["Simulation/Server/GridMembers.cs"] = 2,
            ["Simulation/Server/ServerFrames.Moves.cs"] = 2,
            ["Simulation/Server/WarmBerths.cs"] = 1,
            ["Simulation/Host/FrameHost.Crossing.cs"] = 2,
        };
        var swt = new Regex(@"\.SetWorldTransform\(");
        var seen = new Dictionary<string, int>();
        foreach (var f in files) { int n = Count(f, swt); if (n > 0) seen[Rel(f)] = n; }
        foreach (var kv in seen) Check(vetted.TryGetValue(kv.Key, out int want) && want == kv.Value, $"SetWorldTransform in {kv.Key}: {kv.Value} (vetted: {(vetted.TryGetValue(kv.Key, out int v2) ? v2 : 0)})");
        foreach (var kv in vetted) Check(seen.ContainsKey(kv.Key), $"the vetted write in {kv.Key} is still there (else update this list)");
        // a WorldTransform written by Data.Set bypasses every check here: only the player's character (a dynamic body, moved
        // as the game's admin teleport moves it - client shift, the server's copy from the player's request) and a planet
        // ring's render entity (no physics body)
        var set = new Regex(@"\b(Data\.Set|\.Set)\(\s*new\s+WorldTransform\b|Set<WorldTransform>");
        var setVetted = new Dictionary<string, int>
        {
            ["Simulation/Host/FrameHost.Arrival.cs"] = 1,
            ["Simulation/Server/ServerPlanetBeacon.cs"] = 1,
            ["Simulation/Render/PlanetRings.cs"] = 1,
        };
        foreach (var f in files)
        {
            int n = Count(f, set);
            if (n > 0) Check(setVetted.TryGetValue(Rel(f), out int want) && want == n, $"WorldTransform by Data.Set in {Rel(f)}: {n} (vetted: a character or a render entity)");
        }

        // no grid moved by GridMembers.SetPosition any more (MoveGrid / PlaceGrid decide); its definition aside
        var sp = new Regex(@"GridMembers\.SetPosition\(");
        Check(files.All(f => Count(f, sp) == 0), "no caller of GridMembers.SetPosition (every grid move goes through MoveGrid / PlaceGrid)");

        // the callers allowed to move a static (allowStatic: true) - a new one is a decision, made here
        var allowed = new Dictionary<string, int>
        {
            ["Simulation/Server/ServerFrames.Arrival.cs"] = 2,       // a static arriving at a planet; its HighSpeed conic
            ["Simulation/Server/ServerFrames.GridFrame.cs"] = 2,     // the re-pin of a frame's members; a static member split off
            ["Simulation/Server/ServerFrames.cs"] = 1,               // a restored frame's grids following its fresh berth
            ["Simulation/Server/ServerFrames.Moves.cs"] = 1,         // PlaceGrid: a per-tick placement on its own orbit
            ["Simulation/Encounters/EncounterFrames.Spawns.cs"] = 1, // a spawn moved off a planet's border (or far), whole
        };
        var al = new Regex(@"allowStatic:\s*true");
        var got = new Dictionary<string, int>();
        foreach (var f in files) { int n = Count(f, al); if (n > 0) got[Rel(f)] = n; }
        foreach (var kv in got) Check(allowed.TryGetValue(kv.Key, out int want) && want == kv.Value, $"allowStatic: true in {kv.Key}: {kv.Value}");
        foreach (var kv in allowed) Check(got.ContainsKey(kv.Key), $"the static mover in {kv.Key} is still there (else update this list)");

        // queued moves outside MoveGrid: only the vetted ones (they reach RunDeferredMoves, which checks a static's landing)
        var dq = new Regex(@"_deferred\.Add\(");
        var dqWant = new Dictionary<string, int>
        {
            ["Simulation/Server/ServerFrames.Moves.cs"] = 3,     // MoveGrid single, MoveGrid's joined set, QueueEntityMove
            ["Simulation/Server/ServerFrames.Arrival.cs"] = 1,   // a static arriving with its joints (its set, or alone)
            ["Simulation/Server/ServerFrames.cs"] = 1,           // DEV gridmove
        };
        var dqGot = new Dictionary<string, int>();
        foreach (var f in files) { if (!Rel(f).StartsWith("Simulation/Server/")) continue; int n = Count(f, dq); if (n > 0) dqGot[Rel(f)] = n; }   // (ServerFrames' queue; the map has a _deferred of its own)
        foreach (var kv in dqGot) Check(dqWant.TryGetValue(kv.Key, out int want) && want == kv.Value, $"a queued move in {kv.Key}: {kv.Value}");
    }
}
