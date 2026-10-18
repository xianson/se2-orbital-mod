using System.IO;
using System.Text.RegularExpressions;
using OrbitalMod;

// Offline checks for Flight/QuickLaunchRules.cs - the server's decisions on a seated pilot's quick launch (2026-10-18: the dev
// fly test made a player key, creative only) - and a scan of the engine side for the rules it must keep (the server's own seat
// and game mode, no static ever moved, the dev fly test's Ctrl+T gone). Exit 0 = all pass.
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
        Decide();
        Where();
        Speed();
        Say();
        SourceScan();
        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static QuickLaunchRules.Verdict C(bool enabled = true, bool creative = true, long seat = 7, long asked = 7, bool alive = true,
                                      bool isStatic = false, bool rails = false, bool planet = true)
        => QuickLaunchRules.Check(enabled, creative, seat, asked, alive, isStatic, rails, planet);

    static void Decide()
    {
        Console.WriteLine("[may it go]");
        Check(C() == QuickLaunchRules.Verdict.Go, "creative, seated in the asked grid, dynamic, at a planet: go");
        Check(C(creative: false) == QuickLaunchRules.Verdict.NotCreative, "survival: refused (creative only)");
        Check(C(creative: false, enabled: false) == QuickLaunchRules.Verdict.Off, "the world turned it off: said first (survival too)");
        Check(C(enabled: false) == QuickLaunchRules.Verdict.Off, "creative, the world's switch off: refused");
        Check(C(seat: 0) == QuickLaunchRules.Verdict.NotSeated, "the server sees nobody seated: refused");
        Check(C(asked: 8) == QuickLaunchRules.Verdict.NotYourSeat, "the client asked for another grid than the server's seat: refused (a stale or forged id moves nothing)");
        Check(C(alive: false) == QuickLaunchRules.Verdict.Gone, "the grid is gone: refused");
        Check(C(isStatic: true) == QuickLaunchRules.Verdict.Static, "a static grid (a station): refused");
        Check(C(rails: true) == QuickLaunchRules.Verdict.OnRails, "on rails: refused");
        Check(C(planet: false) == QuickLaunchRules.Verdict.NotAtPlanet, "no planet cell (deep space): refused");
        Check(C(creative: false, isStatic: true) == QuickLaunchRules.Verdict.NotCreative, "survival is said before anything about the ship");
    }

    const double R = 60000, Shell = R + 9500;   // (a Verdure-like planet: radius, the space border ~9.5 km up)

    static void Where()
    {
        Console.WriteLine("[where it ends]");
        var go = QuickLaunchRules.Verdict.Go;
        Check(QuickLaunchRules.Radius(R + 20, R, Shell, 1000, 15, out double r) == go && Math.Abs(r - (R + 1000)) < 1e-6, "parked on the ground: 1000 m over it");
        Check(QuickLaunchRules.Radius(R + 820, R + 800, Shell, 1000, 15, out r) == go && Math.Abs(r - (R + 1800)) < 1e-6, "on a hill: 1000 m over the hill, not over sea level");
        Check(QuickLaunchRules.Radius(R + 3000, R, Shell, 1000, 15, out r) == go && Math.Abs(r - (R + 3000)) < 1e-6, "already higher than the launch altitude: never dropped (stays at its height)");
        Check(QuickLaunchRules.Radius(R + 50, double.NaN, Shell, 1000, 15, out r) == go && Math.Abs(r - (R + 1050)) < 1e-6, "no ground ray: lifted 1000 m from where it is (never into an unmeasured hill)");
        Check(QuickLaunchRules.Radius(R + 50, R + 400, Shell, 1000, 15, out r) == go && Math.Abs(r - (R + 1050)) < 1e-6, "a ground ray above the ship (a cave, a bad ray): the ship's own place stands in");
        Check(QuickLaunchRules.Radius(R + 20, R, Shell, 100, 300, out r) == go && r >= R + 300 + QuickLaunchRules.MinClearance - 1e-6, "a long ship and a low setting: its radius plus the clearance over the ground");
        Check(QuickLaunchRules.Radius(R + 20, R, Shell, 1e6, 15, out r) == go && Math.Abs(r - (Shell - QuickLaunchRules.BorderMargin)) < 1e-6, "a launch past the planet's space: clamped under its border");
        Check(QuickLaunchRules.Radius(R + 9200, R + 9150, Shell, 1000, 15, out r) == QuickLaunchRules.Verdict.NoRoom, "a mountain right under the border: no room");
        Check(QuickLaunchRules.Radius(R + 9200, double.NaN, Shell, 1000, 15, out r) == QuickLaunchRules.Verdict.NoRoom, "between the cap and the border, no ground known: no room to lift by the clearance");
        Check(QuickLaunchRules.Radius(R + 9200, R, Shell, 1000, 15, out r) == go && Math.Abs(r - (R + 9200)) < 1e-6, "between the cap and the border, high over the ground: no lift, kept where it is");
        Check(QuickLaunchRules.Radius(Shell + 10, R, Shell, 1000, 15, out r) == QuickLaunchRules.Verdict.AboveAir, "already past the border: refused");
        Check(QuickLaunchRules.Radius(double.NaN, R, Shell, 1000, 15, out r) == QuickLaunchRules.Verdict.NotAtPlanet, "no position: refused");
        Check(QuickLaunchRules.Radius(R + 20, R, 0, 1e5, 15, out r) == go && Math.Abs(r - (R + 1e5)) < 1e-6, "no border known (0): no clamp");
        // never into the ground, over a sweep of places and settings
        bool ok = true;
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            double ground = R + rnd.NextDouble() * 8000, now = ground + rnd.NextDouble() * 3000, alt = 100 + rnd.NextDouble() * 4900, ship = rnd.NextDouble() * 200;
            bool known = rnd.Next(3) > 0;
            var v = QuickLaunchRules.Radius(now, known ? ground : double.NaN, Shell, alt, ship, out r);
            if (v == go && (r < now - 1e-6 || r > Math.Max(now, Shell - QuickLaunchRules.BorderMargin) + 1e-6 || r < (known ? ground : now) + ship + QuickLaunchRules.MinClearance - 1e-6)) ok = false;
            if (now >= Shell && v == go) ok = false;
        }
        Check(ok, "2000 random places: never lower, never under the ship's clearance over the ground, never over the border's cap");
    }

    static void Speed()
    {
        Console.WriteLine("[speed]");
        Check(QuickLaunchRules.Speed(0, 331, 150, 1000) == 331, "0: the cruise speed (the aero mod's)");
        Check(QuickLaunchRules.Speed(0, double.NaN, 150, 1000) == 150, "0, no aero mod: the fallback");
        Check(QuickLaunchRules.Speed(0, 0.5, 150, 1000) == 150, "0, a cruise under 1 m/s (no thrust that way): the fallback");
        Check(QuickLaunchRules.Speed(0, double.PositiveInfinity, 150, 1000) == 150, "0, an infinite cruise: the fallback");
        Check(QuickLaunchRules.Speed(250, 331, 150, 1000) == 250, "a set speed wins over cruise");
        Check(QuickLaunchRules.Speed(1500, 331, 150, 1000) == 1000, "never over the world's speed cap");
        Check(QuickLaunchRules.Speed(0, 5000, 150, 1000) == 1000, "a cruise over the cap: the cap");
        Check(QuickLaunchRules.Speed(400, 331, 150, 0) == 400, "no cap known (0): as set");
    }

    static void Say()
    {
        Console.WriteLine("[what the player reads]");
        Check(QuickLaunchRules.Say(QuickLaunchRules.Verdict.Go) == null, "go: no refusal line");
        bool all = true;
        foreach (QuickLaunchRules.Verdict v in Enum.GetValues(typeof(QuickLaunchRules.Verdict)))
            if (v != QuickLaunchRules.Verdict.Go && string.IsNullOrEmpty(QuickLaunchRules.Say(v))) all = false;
        Check(all, "every refusal has a line");
        Check(QuickLaunchRules.Say(QuickLaunchRules.Verdict.NotCreative).Contains("creative"), "survival says creative only");
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Scripts", "Simulation"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no mod root above " + AppContext.BaseDirectory);
    }
    static string Src(string rel) => Regex.Replace(File.ReadAllText(Path.Combine(Root(), "Scripts", "Simulation", rel)), @"//.*", "");

    static void SourceScan()
    {
        Console.WriteLine("[the engine side]");
        string srv = Src(Path.Combine("Server", "ServerFrames.QuickLaunch.cs"));
        string body = Regex.Match(srv, @"static void DoQuickLaunch\(.*?\n    \}", RegexOptions.Singleline).Value;
        Check(body.Length > 0, "DoQuickLaunch found");
        Check(body.Contains("PlayerSeatGrid") && body.Contains("QuickLaunchRules.Check("), "the server judges by its own seat (PlayerSeatGrid), through the rules");
        Check(body.Contains("GameModeSessionComponent") && body.Contains("IsCreative("), "creative is the server's game mode (IsCreative of its character)");
        Check(body.Contains("QuickLaunch.Creative"), "the world's switch is read");
        Check(body.Contains("IsDynamicNow(g)"), "static asked live (not the 0.25 s cache)");
        Check(body.Contains("QuickLaunchRules.Radius(") && body.Contains("PlanetBerths.ShellRadius("), "where: the rules, with the planet's space border");
        string fly = Regex.Match(srv, @"static bool FlyLevel\(.*?\n    \}", RegexOptions.Singleline).Value;
        Check(fly.Contains("MoveGrid(") && !fly.Contains("allowStatic"), "the move goes through MoveGrid, statics never opted in (HavokMoves: the teleport phase with the joined set)");
        string sf = Src(Path.Combine("Server", "ServerFrames.cs"));
        Check(Regex.IsMatch(sf, @"QuickLaunchRequests\.TryDequeue\(out var \w+\)\) DoQuickLaunch\("), "the requests are drained on the server's locked step");
        string host = Src(Path.Combine("Host", "FrameHost.cs"));
        Check(host.Contains("QuickLaunch.ClientTick("), "the client tick reads the key");
        string ft = Src(Path.Combine("Dev", "FlyTest.cs"));
        Check(!ft.Contains("KeyboardInputs.") && ft.Contains("DevKeys.Remove(\"flytest\")") && ft.Contains("OrbitalConfig.DevHarness"), "the dev fly test: harness key only, still dev-gated (Ctrl+T is quick launch)");
        string cl = Src(Path.Combine("Flight", "QuickLaunch.cs"));
        Check(cl.Contains("TopScreenNeedingInput") && cl.Contains("FrameHost.Seated"), "the key: seated only, never while typing");
        Check(Regex.IsMatch(cl, @"public static int Key = 20;") && Regex.IsMatch(cl, @"public static int Modifier = 1;"), "the default binding is Ctrl+T (Key 20 = T, Modifier 1 = Ctrl)");
    }
}
