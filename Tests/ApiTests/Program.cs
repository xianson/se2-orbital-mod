using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using SEAerospace.Api;
using SEAerospace.Frames;
using SEAerospace.Orbital;

namespace Orbital.ApiTests
{
    // The public API (OrbitalMod.OrbitalApi): its engine-free parts - the Peek snapshot (FrameTable) and the orbit queries - and
    // its SHAPE, checked from the source (the API compiles only in game). No test framework: PASS/FAIL lines, a summary, a
    // nonzero exit code on any failure.
    internal static class Program
    {
        static int _passed, _failed;
        const double Mu = 3.986004418e14, Deg = Math.PI / 180;

        static int Main()
        {
            Console.WriteLine("=== OrbitalApi tests ===\n");
            OrbitQueriesBasics();
            OrbitQueriesEvents();
            OrbitQueriesSampling();
            FrameTableReads();
            FrameTableRace();
            ApiShape();
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        static void Ok(string name, bool ok) { if (ok) _passed++; else { _failed++; Console.WriteLine("FAIL  " + name); } }
        static void Near(string name, double got, double want, double tol)
        {
            bool ok = Math.Abs(got - want) <= tol || got == want;
            if (ok) _passed++; else { _failed++; Console.WriteLine($"FAIL  {name}: got {got:R}, want {want:R} (tol {tol})"); }
        }

        // ── orbit queries ──

        static void OrbitQueriesBasics()
        {
            Console.WriteLine("-- orbit queries: state <-> elements");
            double r = 7.0e6, v = Math.Sqrt(Mu / r);
            Ok("a circular state converts", OrbitQueries.FromState(new Vector3D(r, 0, 0), new Vector3D(0, v, 0), Mu, 100, out var el));
            Near("... a = r", el.SemiMajorAxis, r, 1e-3);
            Near("... e = 0", el.Eccentricity, 0, 1e-9);
            Near("... epoch = the time given", el.Epoch, 100, 0);
            double T = 2 * Math.PI * Math.Sqrt(r * r * r / Mu);
            Ok("propagates", OrbitQueries.Propagate(el, 100 + T / 4, out var p, out var pv));
            Near("... a quarter period on: on +y", p.Y, r, 1e-2);
            Near("... moving -x", pv.X, -v, 1e-5);
            Ok("a whole period on: back where it was", OrbitQueries.Propagate(el, 100 + T, out p, out _) && (p - new Vector3D(r, 0, 0)).Length() < 1e-2);

            // round trip of an inclined eccentric orbit
            var e0 = OrbitQueries.Elements(9.0e6, 0.2, 30 * Deg, 40 * Deg, 50 * Deg, 60 * Deg, 0, Mu);
            OrbitQueries.Propagate(e0, 0, out var p0, out var v0);
            Ok("round trip: elements -> state -> elements", OrbitQueries.FromState(p0, v0, Mu, 0, out var e1));
            Near("... a", e1.SemiMajorAxis, 9.0e6, 1e-2); Near("... e", e1.Eccentricity, 0.2, 1e-9);
            Near("... i", e1.Inclination, 30 * Deg, 1e-9); Near("... raan", e1.Raan, 40 * Deg, 1e-9);
            Near("... argp", e1.ArgPeriapsis, 50 * Deg, 1e-9); Near("... nu", e1.TrueAnomaly, 60 * Deg, 1e-9);

            // a radial state (dropped from rest): a needle ellipse, never NaN
            Ok("a radial state still converts (capture nudge)", OrbitQueries.FromState(new Vector3D(r, 0, 0), Vector3D.Zero, Mu, 0, out var en) && OrbitQueries.Usable(en));
            Ok("... its periapsis is deep inside (a fall)", en.PeriapsisRadius < 1000);

            // refused
            Ok("refuses mu <= 0", !OrbitQueries.FromState(new Vector3D(r, 0, 0), new Vector3D(0, v, 0), 0, 0, out _));
            Ok("refuses a NaN state", !OrbitQueries.FromState(new Vector3D(double.NaN, 0, 0), new Vector3D(0, v, 0), Mu, 0, out _));
            Ok("refuses the centre", !OrbitQueries.FromState(Vector3D.Zero, new Vector3D(0, v, 0), Mu, 0, out _));
            Ok("not usable: e == 1", !OrbitQueries.Usable(OrbitQueries.Elements(1e7, 1.0, 0, 0, 0, 0, 0, Mu)));
            Ok("not usable: elliptic e with a < 0", !OrbitQueries.Usable(OrbitQueries.Elements(-1e7, 0.5, 0, 0, 0, 0, 0, Mu)));
            Ok("not usable: hyperbolic e with a > 0", !OrbitQueries.Usable(OrbitQueries.Elements(1e7, 1.5, 0, 0, 0, 0, 0, Mu)));
            Ok("not usable: NaN", !OrbitQueries.Usable(OrbitQueries.Elements(1e7, 0.1, double.NaN, 0, 0, 0, 0, Mu)));
            Ok("propagate refuses an unusable set", !OrbitQueries.Propagate(OrbitQueries.Elements(1e7, 1.0, 0, 0, 0, 0, 0, Mu), 0, out _, out _));
        }

        static void OrbitQueriesEvents()
        {
            Console.WriteLine("-- orbit queries: apsides and times");
            double a = 1.0e7, e = 0.3;
            double T = 2 * Math.PI * Math.Sqrt(a * a * a / Mu);
            var el = OrbitQueries.Elements(a, e, 0, 0, 0, 0, 0, Mu);   // at periapsis
            Ok("events of an ellipse", OrbitQueries.Events(el, out double pe, out double ap, out double per, out double tPe, out double tAp));
            Near("... periapsis radius a(1-e)", pe, a * (1 - e), 1e-3);
            Near("... apoapsis radius a(1+e)", ap, a * (1 + e), 1e-3);
            Near("... period", per, T, 1e-6);
            Near("... at periapsis: the next is now (0)", tPe, 0, 1e-6);
            Near("... apoapsis half a period on", tAp, T / 2, 1e-6);
            el.TrueAnomaly = Math.PI;   // at apoapsis
            OrbitQueries.Events(el, out _, out _, out _, out tPe, out tAp);
            Near("at apoapsis: periapsis half a period on", tPe, T / 2, 1e-6);
            Near("... the next apoapsis now", tAp, 0, 1e-6);
            Ok("a time to apoapsis lands on apoapsis", OrbitQueries.Propagate(OrbitQueries.Elements(a, e, 0, 0, 0, 1.0, 0, Mu), 0, out _, out _)
                && TimeLands(OrbitQueries.Elements(a, e, 0.3, 0.2, 0.1, 1.0, 50, Mu), Math.PI, a * (1 + e)));

            var h = OrbitQueries.Elements(-2.0e7, 1.5, 0, 0, 0, 0.5, 0, Mu);   // past periapsis
            Ok("events of a hyperbola", OrbitQueries.Events(h, out pe, out ap, out per, out tPe, out tAp));
            Near("... periapsis radius |a|(e-1)", pe, 2.0e7 * 0.5, 1e-3);
            Ok("... apoapsis and period infinite", double.IsPositiveInfinity(ap) && double.IsPositiveInfinity(per));
            Ok("... past periapsis: a negative time", tPe < 0);
            Ok("... no apoapsis (NaN)", double.IsNaN(tAp));
        }

        static bool TimeLands(KeplerianElements el, double nu, double radius)
        {
            OrbitQueries.Events(el, out _, out _, out _, out double tPe, out double tAp);
            double dt = nu == 0 ? tPe : tAp;
            OrbitQueries.Propagate(el, el.Epoch + dt, out var p, out _);
            return Math.Abs(p.Length() - radius) < 1.0;
        }

        static void OrbitQueriesSampling()
        {
            Console.WriteLine("-- orbit queries: sampling into a caller's buffer");
            double a = 1.0e7, e = 0.3;
            var el = OrbitQueries.Elements(a, e, 20 * Deg, 10 * Deg, 30 * Deg, 0.4, 0, Mu);
            var xyz = new double[3 * 33];
            Ok("by time: fills the buffer", OrbitQueries.SampleByTime(el, 0, 1000, xyz) == 33);
            OrbitQueries.Propagate(el, 0, out var p0, out _); OrbitQueries.Propagate(el, 1000, out var p1, out _);
            Near("... the first point at t0", new Vector3D(xyz[0], xyz[1], xyz[2]).X, p0.X, 1e-6);
            Near("... the last point at t1", xyz[3 * 32 + 1], p1.Y, 1e-6);
            Ok("by time: no room for 2 points -> 0", OrbitQueries.SampleByTime(el, 0, 1, new double[5]) == 0);
            Ok("by time: null buffer -> 0", OrbitQueries.SampleByTime(el, 0, 1, null) == 0);

            var shape = new double[3 * 64];
            Ok("shape of an ellipse: fills the buffer", OrbitQueries.SampleShape(el, 0, shape) == 64);
            bool inside = true;
            for (int k = 0; k < 64; k++)
            {
                double r = Math.Sqrt(shape[3 * k] * shape[3 * k] + shape[3 * k + 1] * shape[3 * k + 1] + shape[3 * k + 2] * shape[3 * k + 2]);
                if (r < a * (1 - e) - 1 || r > a * (1 + e) + 1) inside = false;
            }
            Ok("... every point between periapsis and apoapsis", inside);
            Near("... the first point is periapsis", Math.Sqrt(shape[0] * shape[0] + shape[1] * shape[1] + shape[2] * shape[2]), a * (1 - e), 1e-3);

            var h = OrbitQueries.Elements(-2.0e7, 1.5, 0, 0, 0, 0, 0, Mu);
            var hs = new double[3 * 21];
            Ok("shape of a hyperbola to a radius", OrbitQueries.SampleShape(h, 5.0e7, hs) == 21);
            Near("... its ends at that radius", Math.Sqrt(hs[0] * hs[0] + hs[1] * hs[1] + hs[2] * hs[2]), 5.0e7, 1.0);
            Near("... its middle at periapsis", Math.Sqrt(hs[30] * hs[30] + hs[31] * hs[31] + hs[32] * hs[32]), 1.0e7, 1.0);
        }

        // ── the Peek snapshot ──

        static KeplerianElements ElOf(long id) => OrbitQueries.Elements(7.0e6 + id * 1000, 0.01, 0.1, 0.2, 0.3, 0.4, 5, Mu);

        static void FrameTableReads()
        {
            Console.WriteLine("-- the Peek snapshot (FrameTable)");
            var reg = new FrameRegistry(new BerthAllocator(2000.0, 20000.0));
            var t = new FrameTable();
            Ok("empty: no frames", t.Count == 0 && t.CopyIds(new long[4]) == 0);
            Ok("empty: never copied (time NaN)", double.IsNaN(t.Time));
            var f1 = reg.CreateFrame("Verdure", ElOf(1), 1000000001);
            var f2 = reg.CreateFrame("Kemik", ElOf(2), 1000000002);
            reg.AddMember(f2, 1000000003); reg.AddMember(f2, 7);
            t.Refresh(reg, 123.5);
            int v1 = t.SetVersion;
            Ok("two frames", t.Count == 2);
            Near("its time", t.Time, 123.5, 0);
            var ids = new long[1];
            Ok("ids: the count even when the buffer is short", t.CopyIds(ids) == 2);
            Ok("... the buffer filled as far as it goes", ids[0] == f1.Id || ids[0] == f2.Id);
            Ok("a frame's row", t.TryGet(f2.Id, out var r) && r.Body == "Kemik" && r.MemberCount == 3 && r.Anchor == 1000000002);
            Near("... its elements", r.Elements.SemiMajorAxis, ElOf(2).SemiMajorAxis, 0);
            Near("... its epoch", r.Elements.Epoch, 5, 0);
            Ok("... its berth", (r.Berth - f2.BerthCenter).Length() == 0);
            Ok("no such frame", !t.TryGet(999, out _));
            var mem = new long[8];
            Ok("members: the count", t.CopyMembers(f2.Id, mem) == 3);
            Ok("... the ids", mem.Take(3).OrderBy(x => x).SequenceEqual(new long[] { 7, 1000000002, 1000000003 }));
            Ok("members of no frame: -1", t.CopyMembers(999, mem) == -1);
            Ok("frame of a member", t.FrameOf(1000000003) == f2.Id && t.FrameOf(7) == f2.Id && t.FrameOf(1000000001) == f1.Id);
            Ok("frame of no member: -1", t.FrameOf(42) == -1);
            Ok("berth at its centre", t.TryBerthAt(f1.BerthCenter + new Vector3D(10, 0, 0), 1000, out var b1) && b1.Id == f1.Id);
            Ok("no berth far off", !t.TryBerthAt(f1.BerthCenter + new Vector3D(1e9, 0, 0), 1000, out _));
            t.Refresh(reg, 124);
            Ok("same frames again: the set version holds", t.SetVersion == v1);
            reg.RemoveMember(7);
            t.Refresh(reg, 125);
            Ok("a member left: the set version moves", t.SetVersion != v1);
            int v2 = t.SetVersion;
            f1.Elements = ElOf(5);
            t.Refresh(reg, 126);
            Ok("only an orbit changed: the set version holds", t.SetVersion == v2);
            Ok("... the new orbit is read", t.TryGet(f1.Id, out r) && r.Elements.SemiMajorAxis == ElOf(5).SemiMajorAxis);
            // grows past its first arrays
            for (int i = 0; i < 40; i++) { var f = reg.CreateFrame("Verdure", ElOf(100 + i), 2000000000 + i); for (int k = 1; k < 5; k++) reg.AddMember(f, 3000000000L + i * 10 + k); }
            t.Refresh(reg, 127);
            Ok("grown: 42 frames", t.Count == 42 && t.CopyIds(new long[64]) == 42);
            Ok("... a late frame's members", t.FrameOf(3000000000L + 39 * 10 + 4) >= 0);
            t.Refresh(null, 128);
            Ok("no registry: empty", t.Count == 0 && t.FrameOf(7) == -1);
        }

        static void FrameTableRace()
        {
            Console.WriteLine("-- the Peek snapshot under a racing writer");
            // Two registries whose frames differ in a way a torn read would show: frame id k's a, anchor and members all say k.
            FrameRegistry Make(int n, int salt)
            {
                var reg = new FrameRegistry(new BerthAllocator(2000.0, 20000.0));
                for (int i = 0; i < n; i++)
                {
                    var f = reg.CreateFrame("B" + salt, OrbitQueries.Elements(1e7 + salt, 0.1, 0, 0, 0, 0, salt, Mu), 1000000000L + salt * 1000 + i);
                    for (int k = 0; k < salt % 7; k++) reg.AddMember(f, 5000000000L + salt * 100000 + i * 10 + k);
                }
                return reg;
            }
            var regs = new[] { Make(3, 1), Make(50, 2), Make(10, 3), Make(120, 4) };
            var t = new FrameTable();
            t.Refresh(regs[0], 0);
            bool stop = false; long torn = 0, reads = 0;
            // (the game refreshes at most once per tick; here as fast as a short spin lets it - every read races a write)
            var writer = new Thread(() => { int i = 0; while (!Volatile.Read(ref stop)) { t.Refresh(regs[++i % regs.Length], i); Thread.SpinWait(50); } });
            writer.Start();
            var ids = new long[256]; var mem = new long[16];
            var until = DateTime.UtcNow.AddSeconds(1.0);
            while (DateTime.UtcNow < until)
            {
                long id = 1 + (reads % 120);
                if (t.TryGet(id, out var r))
                {
                    int salt = r.Body[1] - '0';
                    if (Math.Abs(r.Elements.SemiMajorAxis - (1e7 + salt)) > 0 || r.Elements.Epoch != salt) torn++;
                    if (r.MemberCount != 1 + salt % 7) torn++;
                }
                int n = t.CopyIds(ids);
                if (n != 3 && n != 50 && n != 10 && n != 120) torn++;
                reads++;
            }
            Volatile.Write(ref stop, true); writer.Join();
            Ok($"{reads} reads against a writer: none torn", torn == 0 && reads > 1000);
        }

        // ── the API's shape (from source) ──

        static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));

        static readonly HashSet<string> Allowed = new HashSet<string>
        {
            "bool", "int", "long", "float", "double", "string", "void", "Vector3", "Vector3D", "Entity", "Delegate",
            "List<string>", "List<long>", "double[]", "long[]", "string[]",   // (string[]: TryGetSettingUi's choices, v3 - as AeroApi's)
        };

        static void ApiShape()
        {
            Console.WriteLine("-- the API's shape (OrbitalApi, every partial file)");
            string root = Root();
            var files = Directory.GetFiles(Path.Combine(root, "Scripts"), "OrbitalApi*.cs", SearchOption.AllDirectories);
            Ok("found the API's files", files.Length >= 2);
            var decl = new Regex(@"^\s*public static (?!readonly|class|partial)([A-Za-z0-9_<>\[\], ]+?) ([A-Za-z0-9_]+)\(", RegexOptions.Multiline);
            var names = new Dictionary<string, string>();
            string doc = File.ReadAllText(Path.Combine(root, "docs", "API.md"));
            int members = 0;
            foreach (var f in files)
            {
                string src = File.ReadAllText(f);
                if (!Regex.IsMatch(src, @"public static partial class OrbitalApi\b")) continue;
                bool v2File = Path.GetFileName(f) != "OrbitalApi.cs";   // (version 1 is OrbitalApi.cs itself; every OrbitalApi.<Area>.cs came later)
                foreach (Match m in decl.Matches(src))
                {
                    members++;
                    string ret = m.Groups[1].Value.Trim(), name = m.Groups[2].Value;
                    Ok($"{name}: never overloaded (also in {(names.TryGetValue(name, out var o) ? Path.GetFileName(o) : "-")})", !names.ContainsKey(name));
                    names[name] = f;
                    Ok($"{name}: returns an allowed type ({ret})", Allowed.Contains(ret));
                    int open = m.Index + m.Length, depth = 1, i = open;
                    while (i < src.Length && depth > 0) { if (src[i] == '(') depth++; else if (src[i] == ')') depth--; i++; }
                    string ps = src.Substring(open, i - open - 1);
                    foreach (var raw in SplitTop(ps))
                    {
                        string p = Regex.Replace(raw.Trim(), @"\s+", " ");
                        if (p.Length == 0) continue;
                        p = Regex.Replace(p, @"^(out|ref|in|params) ", "");
                        int sp = p.LastIndexOf(' ');
                        string type = sp > 0 ? p.Substring(0, sp) : p;
                        Ok($"{name}: parameter '{raw.Trim()}' has an allowed type", Allowed.Contains(type));
                    }
                    Ok($"{name}: documented in docs/API.md", doc.Contains("`" + name + "(") || doc.Contains(" " + name + "(") || doc.Contains("`" + name + "`"));
                    if (v2File)
                    {
                        int at = src.LastIndexOf("<summary>", m.Index, StringComparison.Ordinal);
                        Ok($"{name}: its summary says the version it came in", at >= 0 && src.Substring(at, Math.Min(16, src.Length - at)).Contains("(v"));
                    }
                }
            }
            Ok($"{members} public members found", members > 40);
            var ver = Regex.Match(string.Join("\n", files.Select(File.ReadAllText)), @"public const int Version = (\d+);");
            Ok("Version is a const int", ver.Success);
            Ok("docs/API.md has a section for the current version", ver.Success && doc.Contains("## Version " + ver.Groups[1].Value));
            Ok("Version is 3 (the settings' UI metadata and authority)", ver.Success && ver.Groups[1].Value == "3");
            // version 3: the settings members a settings screen binds (the same names and shapes as AeroApi version 8)
            foreach (var want in new[] {
                "bool TryGetSettingUi(string name, out string label, out string group, out string tier, out string unit, out double step, out string[] choices)",
                "bool IsSettingForced(string name)", "bool CanEditWorldSettings()",
                "int ResetSettingsGroup(string scope, string group, out string why)", "bool AreWorldSettingsUnsaved()" })
                Ok($"v3: public static {want}", files.Any(f => File.ReadAllText(f).Contains("public static " + want)));
        }

        static IEnumerable<string> SplitTop(string s)
        {
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<' || s[i] == '(' || s[i] == '[') depth++;
                else if (s[i] == '>' || s[i] == ')' || s[i] == ']') depth--;
                else if (s[i] == ',' && depth == 0) { yield return s.Substring(start, i - start); start = i + 1; }
            }
            yield return s.Substring(start);
        }
    }
}
