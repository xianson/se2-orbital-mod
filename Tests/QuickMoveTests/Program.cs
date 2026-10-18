using System.IO;
using System.Text.RegularExpressions;
using OrbitalMod;

// Offline checks for Server/QuickMoveRules.cs - OrbitalApi.TryQuickMove's space border clamp (2026-10-18: creative quick launch
// moved to the aero mod - the user, "ctrl+T should be in aero creative"; this mod only moves the ship for it) - and a scan of
// the engine side: the move goes through MoveGrid under the frame lock (no static ever opted in), aero told after the lock,
// the player feature (key, settings, toasts, the server's queue) gone from this mod, the dev keys kept. The decisions, the
// placement over the ground, the level turn and the speed are tested in the aero mod's Tests/QuickLaunchTests. Exit 0 = all pass.
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
        Border();
        Reasons();
        SourceScan();
        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    const double R = 60000, Shell = R + 9500;   // (a Verdure-like planet: radius, the space border ~9.5 km up)
    const double Clear = 15 + 100;   // (a 15 m ship and aero's 100 m clearance)

    static void Border()
    {
        Console.WriteLine("[under the space border]");
        var go = QuickMoveRules.Verdict.Go;
        double cap = Shell - QuickMoveRules.BorderMargin;
        Check(QuickMoveRules.UnderBorder(R + 20, R + 1000, Shell, 1000 - Clear, out double r) == go && Math.Abs(r - (R + 1000)) < 1e-6, "under the cap: as aero asked");
        Check(QuickMoveRules.UnderBorder(R + 20, R + 1e5, 0, 0, out r) == go && Math.Abs(r - (R + 1e5)) < 1e-6, "no border known (0): no clamp");
        Check(QuickMoveRules.UnderBorder(R + 20, R + 20000, Shell, 20000 - Clear, out r) == go && Math.Abs(r - cap) < 1e-6, "asked past the planet's space with room: clamped under its border");
        // a mountain right under the border: ground R+9150, ship R+9200, aero asks 1000 m over the ground with 885 m of room
        Check(QuickMoveRules.UnderBorder(R + 9200, R + 10150, Shell, 10150 - 9150 - Clear, out r) == QuickMoveRules.Verdict.NoRoom, "a mountain right under the border: no room");
        Check(QuickMoveRules.UnderBorder(R + 9200, R + 10200, Shell, 1000 - Clear, out r) == QuickMoveRules.Verdict.NoRoom, "between the cap and the border, no ground known: no room to lift by the clearance");
        Check(QuickMoveRules.UnderBorder(R + 9200, R + 9200, Shell, 9200 - Clear, out r) == go && Math.Abs(r - (R + 9200)) < 1e-6, "between the cap and the border, high over the ground: no lift, kept where it is (never lowered)");
        Check(QuickMoveRules.UnderBorder(Shell + 10, Shell + 1010, Shell, 1000, out r) == QuickMoveRules.Verdict.AboveAir, "already past the border: refused");
        Check(QuickMoveRules.UnderBorder(double.NaN, R, Shell, 1000, out r) == QuickMoveRules.Verdict.NotAtPlanet, "no position: refused");
        Check(QuickMoveRules.UnderBorder(R, double.PositiveInfinity, Shell, 1000, out r) == QuickMoveRules.Verdict.NotAtPlanet, "no finite place asked: refused");
        Check(QuickMoveRules.UnderBorder(R + 8000, R + 9000.0000001, Shell, 0, out r) == go, "a hair over the cap with no room: kept (a rounding is no refusal)");
        // a sweep: never past the cap (unless it already was, then never moved up), never lowered below where it is, never
        // further down than the room aero gave
        bool ok = true;
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            double now = R + rnd.NextDouble() * 9800, asked = now + rnd.NextDouble() * 6000, room = rnd.NextDouble() * 3000;
            var v = QuickMoveRules.UnderBorder(now, asked, Shell, room, out r);
            if (now >= Shell) { if (v != QuickMoveRules.Verdict.AboveAir) ok = false; continue; }
            if (v != go) { if (v != QuickMoveRules.Verdict.NoRoom || asked - Math.Max(now, cap) <= room) ok = false; continue; }
            if (r > Math.Max(now, cap) + 1e-6 || r < now - 1e-6 || r < asked - room - 1e-6 || r > asked + 1e-6) ok = false;
        }
        Check(ok, "2000 random asks: never past the cap, never lowered, never more than aero's room under the ask; refused only when it would be");
    }

    static void Reasons()
    {
        Console.WriteLine("[what aero says]");
        Check(QuickMoveRules.Reason(QuickMoveRules.Verdict.Go) == null, "go: no reason");
        bool all = true;
        foreach (QuickMoveRules.Verdict v in Enum.GetValues(typeof(QuickMoveRules.Verdict)))
            if (v != QuickMoveRules.Verdict.Go && string.IsNullOrEmpty(QuickMoveRules.Reason(v))) all = false;
        Check(all, "every refusal has a reason");
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
        string body = Regex.Match(srv, @"internal static bool QuickMove\(.*?\n    \}", RegexOptions.Singleline).Value;
        Check(body.Length > 0, "QuickMove found");
        int lk = body.IndexOf("lock (FramesLock)"), mv = body.IndexOf("MoveGrid(g, placed, velocity, out with, default, turn)"), fl = body.IndexOf("FlushAeroNoteMoved();");
        Check(lk > 0 && mv > lk && fl > mv && !body.Contains("allowStatic"), "the move: MoveGrid under the frame lock, statics never opted in (HavokMoves), aero told after the lock");
        Check(body.Contains("IsDynamicNow(g)") && body.Contains("og.IsServer"), "a server grid, static asked live (not the 0.25 s cache)");
        string place = Regex.Match(srv, @"static bool QuickMovePlace\(.*?\n    \}", RegexOptions.Singleline).Value;
        Check(place.Contains("FindByMember(g.Id)") && place.Contains("TryCellContaining(") && place.Contains("QuickMoveRules.UnderBorder(") && place.Contains("PlanetBerths.ShellRadius("),
              "refused on rails and outside a planet's cell; clamped by the rules under the planet's own border");
        string api = Src(Path.Combine("Api", "OrbitalApi.Grids.cs"));
        Check(Regex.IsMatch(api, @"public static bool TryQuickMove\(Entity grid, Vector3D position, Quaternion orientation, Vector3D velocity, double maxDrop, out Vector3D placed, out string reason\)")
              && api.Contains("ServerFrames.QuickMove("), "OrbitalApi.TryQuickMove (the shape aero's OrbitalLink binds) goes to QuickMove");
        // the player feature is the aero mod's now
        string set = Src(Path.Combine("Host", "OrbitalSettings.cs"));
        Check(!set.Contains("\"QuickLaunch."), "no QuickLaunch.* setting here (the aero mod's registry has them)");
        string sf = Src(Path.Combine("Server", "ServerFrames.cs"));
        Check(!sf.Contains("QuickLaunchRequests") && !srv.Contains("QuickLaunchRequests"), "no client -> server quick launch queue here");
        string cl = Src(Path.Combine("Flight", "QuickLaunch.cs"));
        Check(!cl.Contains("KeyboardInputs") && !cl.Contains("Toast(") && cl.Contains("OrbitalConfig.DevHarness") && cl.Contains("DevKeys.Remove(\"quicklaunch\")") && cl.Contains("\"RequestQuickLaunch\""),
              "Flight/QuickLaunch.cs: no key, no toast - only the dev key quicklaunch, asked of the aero mod");
        string host = Src(Path.Combine("Host", "FrameHost.cs"));
        Check(host.Contains("QuickLaunch.ClientTick("), "the dev key is still read by the client tick");
        string ft = Src(Path.Combine("Dev", "FlyTest.cs"));
        Check(!ft.Contains("KeyboardInputs.") && ft.Contains("DevKeys.Remove(\"flytest\")") && ft.Contains("OrbitalConfig.DevHarness"), "the dev fly test: harness key only, still dev-gated");
    }
}
