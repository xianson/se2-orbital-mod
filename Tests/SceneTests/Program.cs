// SceneTests - ONE SCENE PER THREAD, checked from source (dotnet run -c Release; exit 0 = all pass).
// SE2 runs a CLIENT scene (the main thread) and a SERVER scene (the in-process server's thread) in one process. A component's
// IInSceneListener.OnAddedToScene fires for BOTH copies of an entity, so a registry filled there holds both scenes' entities;
// a loop over it on one thread that reads the other scene's entities is a cross-thread read - the game's DataGuard asserts
// ("Concurrent access (read) ... Reason: IsEntityAlive, Last Writer: SyncEntities on thread 'Main Thread'", found in game
// 2026-10-15 in StationKeepCharge.RebuildByGrid) and it is a data race.
// 1. THE REGISTRIES: every static collection a component adds itself to in OnAddedToScene (Add(this) / Register(this)) is
//    classed below - both scenes' copies, or one scene's (and why). A new one fails here until it is classed.
// 2. THE LOOPS over GridMembers.All() / AllCopy() (both scenes' grids) that read a grid's engine data (GridMembers' physics
//    accessors, its entity's data or components): on the CLIENT side (Flight, Hud, Render, Map, Ui, Host, Frames) they keep to
//    the caller's own scene - ReferenceEquals(g.Entity.Data.Scene, ...) - never "IsServer" (that is the server's copy, read
//    from the client's thread); on the SERVER side (Server, Encounters, Entry, Asteroids) they keep to the server copies -
//    IsServer or the scene - before the first read. (Dev/ is checked on its own, 4.)
// 3. THE SITES: StationKeepCharge's rebuild keeps to the server scene; OrbitalApi.GetMemberId answers a server copy's id only.
// 4. THE HARNESS (Dev/, and Render/SpecCam's dev camera): it runs on the client's frame job, so every read of a server entity -
//    a server grid's engine data (GridMembers' physics accessors, its entity's data or components), the server session's
//    entities, the server's sensor blocks, a helper that reads them - happens on the server thread: inside a GridGen
//    OnServerThread / RunOnServer call, in a method only the server thread runs (its entry points listed below; a method
//    every caller of which is such a place counts as one), in a `gen` verb GridGen.Command sends there, or in a loop that
//    keeps to the caller's own scene first. Spawning (GridGen's measured exception) is listed with why. (2026-10-15: the
//    harness's leftover cross-scene reads - findgrids, exitcheck, seat, thrust, cam grid, ... - the DataGuard class.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }

    /// <summary>Registries filled in OnAddedToScene: true = both scenes' copies land there.</summary>
    static readonly Dictionary<string, (bool both, string why)> Registries = new()
    {
        ["GridMembers.Register"] = (true, "every CubeGridComponent composition, client and server (OrbitalGridComponent.IsServer tells them apart)"),
        ["StationKeepCharge.All"] = (true, "every thruster composition, client and server"),
        ["SensorBlocks.Telescopes"] = (false, "the telescope's SERVER prefab only (SensorBlocks.TelescopePrefab, gen_blocks.py SPREF)"),
        ["SensorBlocks.Radars"] = (false, "the radar's SERVER prefab only (SensorBlocks.RadarPrefab)"),
        ["PlanetRings.Register"] = (false, "the CLIENT procedural volume prefab only (ProceduralVolumeRenderComponent)"),
    };

    static readonly string[] ClientDirs = { "Flight", "Hud", "Render", "Map", "Ui", "Host", "Frames" };
    static readonly string[] ServerDirs = { "Server", "Encounters", "Entry", "Asteroids" };

    static string Root()
    {
        // (SCENE_ROOT: another checkout's tree - how the checks were seen failing on the code before the fixes)
        var env = Environment.GetEnvironmentVariable("SCENE_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Api", "OrbitalApi.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Api/OrbitalApi.cs above " + AppContext.BaseDirectory);
    }

    // ── 4: what the harness may not do from the client thread, and where it may ──
    /// <summary>The calls that run their argument on the server thread (GridGen: queued to ServerPlanetBeacon's job, waited for).</summary>
    static readonly string[] OnServerCalls = { "OnServerThread", "RunOnServer", "OnServer" };
    /// <summary>Dev methods the server thread calls (from ServerFrames / ServerPlanetBeacon / GridGen.ServerTick).</summary>
    static readonly string[] ServerEntries = { "DevFlight.ServerTick", "DevFlight.ServerCommandTick", "DevReentry.ServerTick", "DevReentry.Expect", "DevStress.Tick", "GridGen.ServerTick", "HingeRig.Tick" };
    /// <summary>Spawning and growing blocks: measured to work only from the client thread (GridGen.Command's notes: a new
    /// battery's inventory is not readable inside the server's job, "Data not found").</summary>
    static readonly Dictionary<string, string> SpawnPath = new()
    {
        ["GridGen.Tick"] = "the client tick: hinge heads spawned and flaps grown (GridBuilder.AddBlockToGrid), as spawning works there",
        ["GridGen.AddTo"] = "gen add: blocks grown on a grid as the flaps are (GridBuilder.AddBlockToGrid)",
    };
    /// <summary>Helpers outside Dev/ (or taking a server entity as a parameter) that read server entities.</summary>
    static readonly string[] ServerHelpers = { "StationKeepCharge.Describe", "EntryDamage.Damaged", "EntryDamage.Health", "EncounterFrames.Describe", "WarmBerths.Clear", "SensorBlocks.Describe", "Contacts.Command", "WarpBill.Describe", "ServerFrames.StaticAnchorOf", "GridGen.AeroReadout" };
    static readonly string[] PhysicsReads = { "Position", "TryPosition", "Velocity", "Mass", "IsDynamic", "IsDynamicNow", "ThrustDv", "IsConstrained", "MoveSet", "SetVelocity", "AddVelocity", "SetPosition", "Rotate", "Unmanage" };
    /// <summary>Where a server grid or entity comes from: a local or loop variable from one of these holds one.</summary>
    static readonly Regex ServerSource = new(@"GridMembers\.(Get|All|AllCopy)\(|\bWorldGrid\(|\bGrid\(a\b|\b_spawned\b|\b_measured\b|\b_hinges\b|ServerPlanetBeacon\.ServerSession");
    static readonly Regex SensorSource = new(@"SensorBlocks\.(Radars|Telescopes)\b");

    static string CalleeName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => ""
    };
    static bool InServerArg(SyntaxNode n) => n.Ancestors().OfType<ArgumentSyntax>().Any(arg => arg.Parent?.Parent is InvocationExpressionSyntax inv && OnServerCalls.Contains(CalleeName(inv)));
    static string MethodKey(SyntaxNode n) { var m = n.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault(); return m == null ? "" : ClassOf(m) + "." + m.Identifier.Text; }

    static void DevScan(Dictionary<string, SyntaxNode> trees, Func<string, string> rel)
    {
        var methods = new HashSet<string>(trees.Values.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>()).Select(m => ClassOf(m) + "." + m.Identifier.Text));
        foreach (var k in ServerEntries.Concat(SpawnPath.Keys)) Check(methods.Contains(k), $"harness: {k} (a server-thread or spawn entry) still exists");

        // the gen verbs GridGen.Command sends to the server thread (its `sub == "..."` tests before an OnServer call)
        var routed = new HashSet<string>();
        var cmd = trees.Values.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>()).FirstOrDefault(m => ClassOf(m) == "GridGen" && m.Identifier.Text == "Command");
        if (cmd != null)
            foreach (var ifs in cmd.DescendantNodes().OfType<IfStatementSyntax>())
                if (ifs.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => OnServerCalls.Contains(CalleeName(i))))
                    foreach (var lit in ifs.Condition.DescendantNodes().OfType<LiteralExpressionSyntax>()) if (lit.IsKind(SyntaxKind.StringLiteralExpression)) routed.Add(lit.Token.ValueText);
        Check(routed.Contains("clear") && routed.Contains("steer"), $"harness: GridGen.Command's server verbs found ({string.Join(",", routed)})");
        bool InRoutedGen(SyntaxNode n) => MethodKey(n) == "GridGen.CommandOnServer"
            && n.Ancestors().OfType<SwitchSectionSyntax>().LastOrDefault() is { } sec
            && sec.Labels.OfType<CaseSwitchLabelSyntax>().Any(l => l.Value is LiteralExpressionSyntax lv && routed.Contains(lv.Token.ValueText));

        // a loop over GridMembers' grids that keeps to the caller's own scene before the read; or a branch taken only when the
        // grid read is of the caller's own scene (if (ReferenceEquals(v.Entity.Data.Scene, ...)) ...)
        static bool SceneKept(SyntaxNode n) => n.Ancestors().OfType<ForEachStatementSyntax>().Any(fe =>
        {
            string coll = Regex.Replace(fe.Expression.ToString(), @"\s+", "");
            if (coll != "GridMembers.All()" && coll != "GridMembers.AllCopy()") return false;
            var sm = Regex.Match(fe.Statement.ToString(), $@"ReferenceEquals\(\s*{Regex.Escape(fe.Identifier.Text)}\.Entity\??\.Data\.Scene\s*,");
            return sm.Success && fe.Statement.SpanStart + sm.Index < n.SpanStart;
        }) || n.Ancestors().OfType<IfStatementSyntax>().Any(ifs => ifs.Statement.Span.Contains(n.Span)
            && Regex.IsMatch(ifs.Condition.ToString(), $@"^ReferenceEquals\(\s*{Regex.Escape(Regex.Match(n.ToString(), @"^\w+").Value)}\.Entity\??\.Data\.Scene\s*,"));

        // the reads
        var hits = new List<(string file, SyntaxNode node, string what)>();
        foreach (var (f, root) in trees)
        {
            string src = root.ToFullString();
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (inv.Expression is MemberAccessExpressionSyntax ma && ma.Expression.ToString() == "GridMembers" && PhysicsReads.Contains(ma.Name.Identifier.Text))
                    hits.Add((f, inv, ma.ToString()));
            foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                string t = Regex.Replace(ma.ToString(), @"\s+", "");
                if (!ServerHelpers.Contains(t)) continue;
                var call = ma.Parent as InvocationExpressionSyntax;
                if (t == "WarpBill.Describe" && call?.ArgumentList.Arguments.FirstOrDefault()?.ToString() == "session") continue;   // (the client session's: no server)
                // (contacts forget / sensor / on / off touch no grid: the ternary sends the rest to the server thread)
                if (t == "Contacts.Command" && ma.Ancestors().OfType<ConditionalExpressionSyntax>().Any(c => c.WhenTrue.Span.Contains(ma.Span) && c.Condition.ToString().Contains("\"forget\""))) continue;
                hits.Add((f, ma, t));
            }
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())   // (a helper called inside its own class)
                if (inv.Expression is IdentifierNameSyntax hn && ServerHelpers.Contains(ClassOf(inv) + "." + hn.Identifier.Text)) hits.Add((f, inv, ClassOf(inv) + "." + hn.Identifier.Text));
            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var server = new HashSet<string>(); var sensor = new HashSet<string>();
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (var v in m.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    {
                        string init = v.Initializer?.Value.ToString() ?? "";
                        var am = Regex.Match(init, @"^(\w+)\??\.Entity$");
                        if (ServerSource.IsMatch(init) || am.Success && server.Contains(am.Groups[1].Value)) server.Add(v.Identifier.Text);
                        if (SensorSource.IsMatch(init)) sensor.Add(v.Identifier.Text);
                    }
                    foreach (var fe in m.DescendantNodes().OfType<ForEachStatementSyntax>())
                    {
                        if (ServerSource.IsMatch(fe.Expression.ToString())) server.Add(fe.Identifier.Text);
                        if (SensorSource.IsMatch(fe.Expression.ToString())) sensor.Add(fe.Identifier.Text);
                    }
                    foreach (var fv in m.DescendantNodes().OfType<ForEachVariableStatementSyntax>())
                        if (ServerSource.IsMatch(fv.Expression.ToString())) foreach (var d in fv.Variable.DescendantNodes().OfType<SingleVariableDesignationSyntax>()) server.Add(d.Identifier.Text);
                    foreach (var ip in m.DescendantNodes().OfType<IsPatternExpressionSyntax>())
                        if (ServerSource.IsMatch(ip.Expression.ToString())) foreach (var d in ip.Pattern.DescendantNodes().OfType<SingleVariableDesignationSyntax>()) server.Add(d.Identifier.Text);
                }
                foreach (var id in m.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    string v = id.Identifier.Text;
                    if (!server.Contains(v) && !sensor.Contains(v)) continue;
                    if (id.Parent is MemberAccessExpressionSyntax pm && pm.Name == id) continue;   // (x.v: someone else's v)
                    string tail = src.Substring(id.SpanStart, Math.Min(80, src.Length - id.SpanStart));
                    var r = sensor.Contains(v) ? Regex.Match(tail, $@"^{v}\??\.\w+")
                          : Regex.Match(tail, $@"^{v}\??\.(Entity\??\.)?(Data\??\.(?!Scene\b)|TryGet\b|Get\b|AsInterface\b|GetTopLevelParent\b|GetSession\b|GetEntitiesOfType\b)");
                    if (r.Success) hits.Add((f, id, r.Value));
                }
            }
            foreach (Match sm in Regex.Matches(src, @"ServerPlanetBeacon\.ServerSession\??\.GetEntitiesOfType"))
                hits.Add((f, root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(sm.Index, 1)), sm.Value));
        }

        // the methods only the server thread runs: the entries, then every method whose callers all are such places
        var ok = new HashSet<string>(ServerEntries.Concat(SpawnPath.Keys));
        var refs = new List<(string key, SyntaxNode at)>();
        foreach (var root in trees.Values)
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                string key;
                if (id.Parent is MemberAccessExpressionSyntax pm && pm.Name == id) key = Regex.Match(pm.Expression.ToString(), @"(\w+)$").Value + "." + id.Identifier.Text;
                else key = ClassOf(id) + "." + id.Identifier.Text;
                if (methods.Contains(key)) refs.Add((key, id));
            }
        bool Ok(SyntaxNode n) => InServerArg(n) || ok.Contains(MethodKey(n)) || InRoutedGen(n) || SceneKept(n);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var mk in methods)
            {
                if (ok.Contains(mk)) continue;
                var callers = refs.Where(r => r.key == mk && MethodKey(r.at) != mk).ToList();
                if (callers.Count > 0 && callers.All(c => Ok(c.at))) { ok.Add(mk); grew = true; }
            }
        }

        int onServer = 0;
        foreach (var (f, node, what) in hits)
        {
            if (InServerArg(node)) onServer++;
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            Check(Ok(node), $"{rel(f)}:{line}: {MethodKey(node)} reads '{what}' (a server entity) off the server thread");
        }
        Check(hits.Count >= 40, $"harness: server-entity reads found ({hits.Count})");
        Check(onServer >= 25, $"harness: reads inside OnServerThread / RunOnServer found ({onServer})");
    }

    static string ClassOf(SyntaxNode n) => n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";

    static int Main()
    {
        string sim = Path.Combine(Root(), "Scripts", "Simulation");
        var files = Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        var trees = files.ToDictionary(f => f, f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot());
        string Rel(string f) => Path.GetRelativePath(sim, f).Replace('\\', '/');

        // ── 1. the registries ──
        var seen = new HashSet<string>();
        foreach (var (f, root) in trees)
            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "OnAddedToScene"))
                foreach (var inv in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                    string verb = ma.Name.Identifier.Text;
                    if (verb != "Add" && verb != "Register") continue;
                    if (inv.ArgumentList.Arguments.Count != 1 || inv.ArgumentList.Arguments[0].Expression is not ThisExpressionSyntax) continue;
                    string target = Regex.Replace(ma.Expression.ToString(), @"\s+", "");
                    if (!target.Contains('.') && verb == "Add") target = ClassOf(m) + "." + target;
                    string reg = verb == "Register" ? target + ".Register" : target;
                    reg = Regex.Replace(reg, @"^.*\.(\w+\.\w+)$", "$1");
                    seen.Add(reg);
                    Check(Registries.ContainsKey(reg), $"{Rel(f)}: {reg} (filled in OnAddedToScene) is classed as both scenes' or one scene's");
                }
        foreach (var r in Registries.Keys) Check(seen.Contains(r), $"registry {r} still filled in an OnAddedToScene");

        // ── 2. the loops over GridMembers' grids ──
        int client = 0, server = 0;
        foreach (var (f, root) in trees)
        {
            string rel = Rel(f), dir = rel.Split('/')[0];
            bool isClient = ClientDirs.Contains(dir), isServer = ServerDirs.Contains(dir);
            if (!isClient && !isServer) continue;
            foreach (var fe in root.DescendantNodes().OfType<ForEachStatementSyntax>())
            {
                string coll = Regex.Replace(fe.Expression.ToString(), @"\s+", "");
                if (coll != "GridMembers.All()" && coll != "GridMembers.AllCopy()") continue;
                string v = Regex.Escape(fe.Identifier.Text), body = fe.Statement.ToString();
                var read = Regex.Match(body, $@"GridMembers\.\w+\(\s*{v}\b|\b{v}\.Entity\??\.(Data\.(?!Scene\b)\w+|TryGet|Get\b|AsInterface|GetTopLevelParent|GetSession)|Alive\(\s*{v}\.Entity\b|EntityAlive\(\s*{v}\.Entity\b");
                if (!read.Success) continue;
                int line = fe.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var scene = Regex.Match(body, $@"ReferenceEquals\(\s*{v}\.Entity\??\.Data\.Scene\s*,");
                if (isClient)
                {
                    client++;
                    Check(scene.Success && scene.Index < read.Index, $"{rel}:{line}: client-side foreach over {coll} reads '{read.Value}' - keeps to its own scene first");
                }
                else
                {
                    server++;
                    var srv = Regex.Match(body, $@"\b{v}\.IsServer\b");
                    var first = scene.Success && (!srv.Success || scene.Index < srv.Index) ? scene : srv;
                    Check(first.Success && first.Index < read.Index, $"{rel}:{line}: server-side foreach over {coll} reads '{read.Value}' - keeps to the server copies first");
                }
            }
        }
        Check(server >= 10, $"server-side loops over GridMembers found ({server})");
        Check(client >= 1, $"client-side loops over GridMembers found ({client})");

        // ── 3. the sites ──
        MethodDeclarationSyntax Method(string relPath, string name) => trees[Path.Combine(sim, relPath.Replace('/', Path.DirectorySeparatorChar))]
            .DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == name);
        var rb = Method("Server/StationKeepCharge.cs", "RebuildByGrid");
        Check(rb != null && Regex.IsMatch(rb.ToString(), @"ReferenceEquals\(\s*c\.Entity\.Data\.Scene\s*,\s*scene\s*\)"), "StationKeepCharge.RebuildByGrid: the server scene's thrusters only");
        var gm = Method("Api/OrbitalApi.cs", "GetMemberId");
        Check(gm != null && gm.ToString().Contains("g.IsServer && g.Id >= GridMembers.IdBase"), "OrbitalApi.GetMemberId: a component's own id answers for the server copy only");
        var owner = Method("Render/ParticleCensus.cs", "Owner");
        Check(owner != null && !owner.ToString().Contains("GridMembers.Position("), "ParticleCensus.Owner: the server grids' places from ServerFrames.GridPositions");

        // ── 4. the harness ──
        DevScan(trees.Where(kv => Rel(kv.Key).StartsWith("Dev/") || Rel(kv.Key) == "Render/SpecCam.cs").ToDictionary(kv => kv.Key, kv => kv.Value), Rel);

        Console.WriteLine(_fails == 0 ? $"SceneTests: {_checks}/{_checks} passed" : $"SceneTests: {_fails} of {_checks} FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
