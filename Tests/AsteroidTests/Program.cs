using System.IO;
using System.Text.RegularExpressions;
using OrbitalMod;

// Offline checks for Asteroids/AsteroidRules.cs (game-free): which generator bodies AsteroidBridge may delete. Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;

    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    /// <summary>An untouched procedural rock nobody is at: the one case that is deleted.</summary>
    static RockFacts Deletable() => new RockFacts { Alive = true, Procedural = true, Voxel = true, IdMatches = true, HasBox = true };

    static int Main()
    {
        Console.WriteLine("[the delete rule]");
        var d = Deletable();
        Check(AsteroidRules.Classify(true, true, true, in d) == RockVerdict.Delete, "untouched procedural voxel rock, nobody near: deleted");
        Check(AsteroidRules.Classify(false, true, true, in d) == RockVerdict.Off, "switch off: kept");
        Check(AsteroidRules.Classify(true, false, true, in d) == RockVerdict.NotServer, "not the server's generator: kept");
        Check(AsteroidRules.Classify(true, true, false, in d) == RockVerdict.NoScan, "the asteroid frames' scan failed: kept (a frame's rock may look like a stray)");

        // every single fact that is not proven keeps it
        var cases = new (string what, Func<RockFacts, RockFacts> change, RockVerdict want)[]
        {
            ("dead", f => { f.Alive = false; return f; }, RockVerdict.Dead),
            ("no procedural component (a player/scenario body)", f => { f.Procedural = false; return f; }, RockVerdict.NotProcedural),
            ("procedural but not the voxel component", f => { f.Voxel = false; return f; }, RockVerdict.NotVoxel),
            ("manual (player-edited / saved)", f => { f.Manual = true; return f; }, RockVerdict.Manual),
            ("excluded from generation", f => { f.Skipped = true; return f; }, RockVerdict.Skipped),
            ("its id not the generator's", f => { f.IdMatches = false; return f; }, RockVerdict.IdMismatch),
            ("no world box", f => { f.HasBox = false; return f; }, RockVerdict.NoBox),
            ("an asteroid frame's rock", f => { f.Ours = true; return f; }, RockVerdict.Ours),
            ("a grid near", f => { f.GridNear = true; return f; }, RockVerdict.GridNear),
            ("a player near", f => { f.PlayerNear = true; return f; }, RockVerdict.PlayerNear),
        };
        foreach (var c in cases)
        {
            var f = c.change(Deletable());
            var v = AsteroidRules.Classify(true, true, true, in f);
            Check(v == c.want, $"{c.what}: kept ({v})");
        }
        // manual wins over everything a manual body may also be (an edited rock in a frame, a grid on it)
        var m = Deletable(); m.Manual = true; m.Ours = true; m.GridNear = true; m.PlayerNear = true;
        Check(AsteroidRules.Classify(true, true, true, in m) == RockVerdict.Manual, "manual + ours + grid + player: manual");
        // the all-false facts (a read that failed) never delete
        var none = new RockFacts();
        Check(AsteroidRules.Classify(true, true, true, in none) != RockVerdict.Delete, "nothing known: never deleted");
        // exhaustive: only the one combination deletes (10 facts, 1024 combinations)
        int deletes = 0;
        for (int bits = 0; bits < 1024; bits++)
        {
            var f = new RockFacts
            {
                Alive = (bits & 1) != 0, Procedural = (bits & 2) != 0, Voxel = (bits & 4) != 0, Manual = (bits & 8) != 0,
                IdMatches = (bits & 16) != 0, Skipped = (bits & 32) != 0, HasBox = (bits & 64) != 0, Ours = (bits & 128) != 0,
                GridNear = (bits & 256) != 0, PlayerNear = (bits & 512) != 0,
            };
            if (AsteroidRules.Classify(true, true, true, in f) == RockVerdict.Delete) deletes++;
            if (AsteroidRules.Classify(false, true, true, in f) == RockVerdict.Delete || AsteroidRules.Classify(true, false, true, in f) == RockVerdict.Delete
                || AsteroidRules.Classify(true, true, false, in f) == RockVerdict.Delete) deletes += 100;
        }
        Check(deletes == 1, $"of 1024 fact combinations exactly one deletes, never with the switch off, off the server or without the frames' scan ({deletes})");

        Console.WriteLine("[kept bodies stay anchors]");
        foreach (RockVerdict v in Enum.GetValues(typeof(RockVerdict)))
            Check(AsteroidRules.Persists(v) == (v != RockVerdict.Delete && v != RockVerdict.Dead), $"{v}: {(AsteroidRules.Persists(v) ? "an anchor" : "not an anchor")}");
        Check(!AsteroidRules.LogKeep(RockVerdict.Manual) && !AsteroidRules.LogKeep(RockVerdict.Ours) && !AsteroidRules.LogKeep(RockVerdict.Off),
              "the normal keeps (manual, ours, off) are not logged per body");
        Check(AsteroidRules.LogKeep(RockVerdict.PlayerNear) && AsteroidRules.LogKeep(RockVerdict.GridNear) && AsteroidRules.LogKeep(RockVerdict.NotVoxel),
              "the unusual keeps are logged");
        foreach (RockVerdict v in Enum.GetValues(typeof(RockVerdict)))
            Check(!string.IsNullOrEmpty(AsteroidRules.Why(v)) && AsteroidRules.Why(v) != "kept", $"{v} has its own reason");

        Console.WriteLine("[near]");
        var rock = new BoundingBoxD(new Vector3D(0, 0, 0), new Vector3D(200, 200, 200));
        Check(AsteroidRules.Gap(rock, new BoundingBoxD(new Vector3D(150, 150, 150), new Vector3D(400, 400, 400))) == 0, "overlapping grid: gap 0");
        Check(Math.Abs(AsteroidRules.Gap(rock, new BoundingBoxD(new Vector3D(250, 0, 0), new Vector3D(300, 10, 10))) - 50) < 1e-9, "grid 50 m off a face: gap 50");
        Check(Math.Abs(AsteroidRules.Gap(rock, new BoundingBoxD(new Vector3D(230, 240, 200), new Vector3D(300, 300, 300))) - 50) < 1e-9, "grid off an edge: the diagonal gap (30, 40 -> 50)");
        Check(AsteroidRules.Gap(rock, new BoundingBoxD(new Vector3D(-500, 0, 0), new Vector3D(-301, 1, 1))) > AsteroidRules.GridMargin, "grid 301 m away: beyond the margin");

        Console.WriteLine("[log rate]");
        var lim = new LogLimiter(3, 30);
        int allowed = 0, held = 0;
        for (int i = 0; i < 10; i++) { if (lim.Allow(100 + i, out int h)) allowed++; held += h; }
        Check(allowed == 3 && held == 0, "3 lines in a window, the rest held");
        Check(lim.Allow(131, out int said) && said == 7, $"the next window: allowed, and the 7 held back are said once ({said})");
        Check(lim.Allow(132, out int again) && again == 0, "not said twice");
        lim.Reset();
        Check(lim.Allow(0, out int r0) && r0 == 0, "reset: a fresh window");

        Console.WriteLine("[pass line]");
        var n = new int[(int)RockVerdict.PlayerNear + 1];
        n[(int)RockVerdict.Delete] = 2; n[(int)RockVerdict.Manual] = 1; n[(int)RockVerdict.PlayerNear] = 3; n[(int)RockVerdict.NoBox] = 1;
        string pl = AsteroidRules.PassLine(true, true, true, n);
        Check(pl.Contains("2 deleted") && pl.Contains("1 manual") && pl.Contains("3 held") && pl.Contains("1 not provably procedural"), pl);
        Check(AsteroidRules.PassLine(false, true, true, n).Contains("OFF"), "off: said");

        Console.WriteLine("[the bridge uses the rule]");
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Scripts"))) root = Path.GetDirectoryName(root);
        string src = Regex.Replace(File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Asteroids", "AsteroidBridge.cs")), @"//.*", "");
        // (before 2026-10-15: a reflected TryGet with no parameters - never found, every body "manual" - and a second
        //  DeleteEntity for the encounter clean-up that asked nothing at all)
        Check(!Regex.IsMatch(src, @"""TryGet""[^\n]*GetParameters\(\)\.Length\s*==\s*0"), "no parameterless TryGet lookup (it never matched)");
        Check(Regex.Matches(src, @"\.DeleteEntity\(").Count == 1, "one DeleteEntity call");
        int del = src.IndexOf(".DeleteEntity(", StringComparison.Ordinal), cls = src.IndexOf("AsteroidRules.Classify(", StringComparison.Ordinal);
        Check(cls > 0 && cls < del, "it runs after the rule");
        Check(Regex.IsMatch(src, @"public static bool DeleteGenerated\s*=\s*true;"), "the switch defaults on");

        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
