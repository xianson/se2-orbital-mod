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
//    IsServer or the scene - before the first read. Dev/ is the harness (dev only: its client-thread reads are listed in
//    the 2026-10-15 scene audit, not checked).
// 3. THE SITES: StationKeepCharge's rebuild keeps to the server scene; OrbitalApi.GetMemberId answers a server copy's id only.
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

        Console.WriteLine(_fails == 0 ? $"SceneTests: {_checks}/{_checks} passed" : $"SceneTests: {_fails} of {_checks} FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
