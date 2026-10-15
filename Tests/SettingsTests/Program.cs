using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OrbitalMod;

namespace OrbitalTests.Settings
{
    /// <summary>
    /// Offline checks of the settings registry and store (OrbitalSettings*.cs, the real files) over stand-ins whose starting
    /// values come from the mod's source (Mirrors.cs). Every setting has its menu metadata and a sane range; text and the world
    /// save round-trip; ResetGroup restores defaults; the host file forces and refuses; a process that cannot edit the world
    /// refuses world sets; no dev / test / debug / probe / log / trace field is a setting. No game, no framework; files only in
    /// a temp folder (never %APPDATA%); exit 0 = all pass.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool ok, string detail = "")
        {
            if (ok) _passed++;
            else { _failed++; Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }

        static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        static readonly string Dir = Path.Combine(Path.GetTempPath(), "OrbitalSettingsTests-" + Environment.ProcessId);

        static int Main()
        {
            Console.WriteLine("=== OrbitalSettings tests ===\n");
            Log.Default = new Log();
            FillMirrors();             // (first: the registry's static constructor reads the fields)
            Directory.CreateDirectory(Dir);
            typeof(OrbitalSettings).GetField("_dir", Any).SetValue(null, Dir);   // (the files: never %APPDATA%)
            try
            {
                Registry();
                Metadata();
                TextRoundTrip();
                ResetGroups();
                WorldEditRefused();
                HostFileForces();
                WorldSave();
                Denylist();
            }
            finally { try { Directory.Delete(Dir, true); } catch { } }
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ── the stand-ins' starting values, from the mod's source ──

        static readonly Dictionary<string, string> _src = new Dictionary<string, string>();
        static string[] SourcesOf(string type)
        {
            if (_src.Count == 0)
                foreach (var f in Directory.GetFiles(Path.Combine(Root(), "Scripts", "Simulation"), "*.cs", SearchOption.AllDirectories))
                    if (!Path.GetFileName(f).StartsWith("OrbitalSettings")) _src[f] = Regex.Replace(File.ReadAllText(f), @"//.*", "");
            var decl = new Regex(@"\b(class|struct)\s+" + type + @"\b");
            return _src.Where(kv => decl.IsMatch(kv.Value)).Select(kv => kv.Value).ToArray();
        }

        static readonly Dictionary<string, Type> Kw = new Dictionary<string, Type>
        { ["bool"] = typeof(bool), ["int"] = typeof(int), ["float"] = typeof(float), ["double"] = typeof(double), ["ProxyMode"] = typeof(ProxyMode) };

        static void FillMirrors()
        {
            Console.WriteLine("-- the stand-ins take the mod's starting values");
            int n = 0;
            foreach (var t in typeof(Program).Assembly.GetTypes().Where(t => t.GetCustomAttribute<MirrorAttribute>() != null))
            {
                var srcs = SourcesOf(t.Name);
                Ok($"{t.Name}: declared in the mod's source", srcs.Length > 0);
                foreach (var f in t.GetFields(Any))
                {
                    if (f.Name == "Rebuilds") continue;   // (the stand-in's own counter)
                    var line = new Regex(@"^[^\n=;(]*\bstatic\b[^\n;(]*?\b" + f.Name + @"\b\s*[=;,][^\n]*", RegexOptions.Multiline);
                    Match m = null;
                    foreach (var s in srcs) { var mm = line.Match(s); if (mm.Success) { m = mm; break; } }
                    string where = $"{t.Name}.{f.Name}";
                    if (!Ok2($"{where}: a static field in the source", m != null)) continue;
                    string l = m.Value;
                    Ok($"{where}: not a const", !Regex.IsMatch(l, @"\bconst\b"), l.Trim());
                    var tm = Regex.Match(l, @"\bstatic\s+(?:readonly\s+|volatile\s+)*([\w.\[\]]+)\s+");
                    string kw = tm.Groups[1].Value;
                    if (f.IsInitOnly)
                    {
                        // WarpControl.Levels: the list itself
                        var arr = Regex.Match(l, f.Name + @"\s*=\s*\{([^}]*)\}");
                        var want = arr.Success ? arr.Groups[1].Value.Split(',').Select(x => double.Parse(x.Trim(), Inv)).ToArray() : null;
                        Ok($"{where}: the same list as the source", want != null && want.SequenceEqual((double[])f.GetValue(null)), l.Trim());
                        continue;
                    }
                    if (!Ok2($"{where}: the same type as the source ({kw})", Kw.TryGetValue(kw, out var kt) && kt == f.FieldType)) continue;
                    var vm = Regex.Match(l, @"\b" + f.Name + @"\s*=\s*([^,;]+?)\s*[,;]");
                    object v;
                    if (!vm.Success) v = Activator.CreateInstance(f.FieldType);   // (no initialiser: the type's default)
                    else
                    {
                        string lit = vm.Groups[1].Value.Trim();
                        try
                        {
                            v = f.FieldType == typeof(bool) ? bool.Parse(lit)
                              : f.FieldType == typeof(int) ? int.Parse(lit, Inv)
                              : f.FieldType == typeof(float) ? float.Parse(lit.TrimEnd('f', 'F'), NumberStyles.Float, Inv)
                              : f.FieldType == typeof(double) ? double.Parse(lit.TrimEnd('d', 'D'), NumberStyles.Float, Inv)
                              : (object)Enum.Parse(f.FieldType, lit.Substring(lit.LastIndexOf('.') + 1));
                        }
                        catch (Exception e) { Ok($"{where}: a literal starting value", false, $"'{lit}': {e.Message}"); continue; }
                    }
                    f.SetValue(null, v);
                    n++;
                }
            }
            // ProxyMode's names, in order (Proxy.Mode's values are their ordinals)
            var en = SourcesOf("OrbitalConfig").Select(s => Regex.Match(s, @"enum\s+ProxyMode\s*\{([^}]*)\}")).FirstOrDefault(x => x.Success);
            var names = en == null ? new string[0] : Regex.Replace(en.Groups[1].Value, @"///.*|<[^>]*>", "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            Ok("ProxyMode: the stand-in's names in the source's order", names.SequenceEqual(Enum.GetNames(typeof(ProxyMode))), string.Join(",", names));
            Ok($"{n} starting values read", n > 40);
        }
        static bool Ok2(string name, bool ok) { Ok(name, ok); return ok; }

        // ── the registry ──

        static readonly List<string> Names = new List<string>();
        static object All => typeof(OrbitalSettings).GetField("_all", Any).GetValue(null);
        static IEnumerable<OrbitalSettings.Setting> Settings => (List<OrbitalSettings.Setting>)All;

        static void Registry()
        {
            Console.WriteLine("-- the registry");
            int n = OrbitalSettings.Names(Names);
            Ok($"{n} settings", n >= 45, n.ToString());
            Ok("names unique (any case)", Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == n);
            bool worldFirst = true; bool seenClient = false;
            foreach (var s in Settings) { if (s.Scope == "client") seenClient = true; else if (seenClient) worldFirst = false; }
            Ok("world settings first", worldFirst);
            foreach (var s in Settings)
            {
                Ok($"{s.Name}: scope world / client", s.Scope == "world" || s.Scope == "client", s.Scope);
                Ok($"{s.Name}: type bool / int / float", s.Type == "bool" || s.Type == "int" || s.Type == "float", s.Type);
                Ok($"{s.Name}: Min <= Default <= Max", s.Min <= s.Default && s.Default <= s.Max, $"{s.Min} <= {s.Default} <= {s.Max}");
                Ok($"{s.Name}: a description", !string.IsNullOrWhiteSpace(s.Doc));
                Ok($"{s.Name}: its default passes its rule", s.Check == null || s.Check(s.Default) == null);
                Ok($"{s.Name}: Get gives the default", s.Get() == s.Default);
                if (s.Type == "int") Ok($"{s.Name}: an int default", s.Default == Math.Round(s.Default));
                if (s.Type == "bool") Ok($"{s.Name}: a bool is 0..1", s.Min == 0 && s.Max == 1);
            }
            Ok("one Default per field: Warp.MaxLevel is the top level", Val("Warp.MaxLevel") == WarpControl.Levels.Length - 1);
        }

        static void Metadata()
        {
            Console.WriteLine("-- the menu metadata");
            var tiers = new Dictionary<string, int>(); var groups = new Dictionary<string, int>();
            int lastOrder = -1;
            foreach (var name in Names)
            {
                Ok($"{name}: UiInfo", OrbitalSettings.UiInfo(name, out string label, out string group, out string tier, out string unit,
                    out double step, out string[] choices));
                OrbitalSettings.Info(name, out string type, out _, out double min, out double max, out string scope, out _, out _);
                var st = Settings.First(x => x.Name == name);
                int order = st.Order; bool cv = st.ClientVisible;
                Ok($"{name}: a label", !string.IsNullOrWhiteSpace(label));
                Ok($"{name}: a group", !string.IsNullOrWhiteSpace(group));
                Ok($"{name}: a tier", tier == "basic" || tier == "advanced" || tier == "hidden", tier);
                Ok($"{name}: a unit string (\"\" none)", unit != null);
                Ok($"{name}: a step (a bool's is 1, an int's at least 1)", type == "bool" ? step == 1 : type == "int" ? step >= 1 && step == Math.Round(step) : step >= 0, step.ToString(Inv));
                Ok($"{name}: the order is the listing's", order == lastOrder + 1, $"{order} after {lastOrder}");
                lastOrder = order;
                Ok($"{name}: client-visible only on a world setting", !cv || scope == "world");
                if (choices != null)
                {
                    Ok($"{name}: choices on an int only", type == "int", type);
                    Ok($"{name}: one choice per value (Max - Min + 1)", choices.Length == (int)(max - min) + 1, $"{choices.Length} for {min}..{max}");
                    choices[0] = "changed";
                    OrbitalSettings.UiInfo(name, out _, out _, out _, out _, out _, out string[] again);
                    Ok($"{name}: choices are a copy", again[0] != "changed");
                    Ok($"{name}: every choice labelled", choices.All(c => !string.IsNullOrWhiteSpace(c)));
                }
                tiers[tier] = tiers.TryGetValue(tier, out int t) ? t + 1 : 1;
                string gk = scope + "/" + group;
                groups[gk] = groups.TryGetValue(gk, out int g) ? g + 1 : 1;
            }
            Ok("UiInfo: unknown name false", !OrbitalSettings.UiInfo("No.Such", out _, out _, out _, out _, out _, out _));
            OrbitalSettings.UiInfo("Warp.MaxLevel", out _, out _, out _, out _, out _, out string[] warp);
            Ok("Warp.MaxLevel: a choice per warp level", warp != null && warp.Length == WarpControl.Levels.Length && warp[0] == "×1" && warp[^1] == "×10,000", string.Join(" ", warp ?? new string[0]));
            OrbitalSettings.UiInfo("Hud.ShowOrbit", out _, out _, out _, out _, out _, out string[] none);
            Ok("a plain setting: no choices", none == null);
            foreach (var g in groups) Ok($"group {g.Key}: in one scope", !groups.Keys.Any(k => k != g.Key && k.Substring(k.IndexOf('/')) == g.Key.Substring(g.Key.IndexOf('/'))));
            Console.WriteLine("   tiers:  " + string.Join(", ", tiers.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
            Console.WriteLine("   groups: " + string.Join(", ", groups.Select(k => $"{k.Key} {k.Value}")));
            Ok("some basic, some advanced, some hidden", tiers.ContainsKey("basic") && tiers.ContainsKey("advanced") && tiers.ContainsKey("hidden"));
        }

        static double Val(string name) { OrbitalSettings.TryGet(name, out double v); return v; }

        /// <summary>A valid value off the setting's default (its rule kept): the far end of its range, or a step in.</summary>
        static double Other(OrbitalSettings.Setting s)
        {
            foreach (double c in new[] { s.Max, s.Min, (s.Min + s.Max) / 2, s.Default + (s.Max - s.Default) / 3 })
            {
                double v = s.Type == "float" ? c : Math.Round(c);
                if (v != s.Default && v >= s.Min && v <= s.Max && (s.Check == null || s.Check(v) == null)) return v;
            }
            return s.Default;
        }

        // ── the text ──

        static void TextRoundTrip()
        {
            Console.WriteLine("-- text: Text -> Apply is the identity");
            foreach (var scope in new[] { "world", "client" })
            {
                string atDefault = OrbitalSettings.Text(scope, false);
                Ok($"{scope}: nothing off its default at start", atDefault.Length == 0, atDefault);
                var want = new Dictionary<string, double>();
                foreach (var s in Settings.Where(s => s.Scope == scope))
                {
                    double v = Other(s);
                    Ok($"{s.Name}: set off its default", OrbitalSettings.Set(s.Name, v, out string why) || v == s.Default, why ?? "");
                    want[s.Name] = Val(s.Name);
                }
                string all = OrbitalSettings.Text(scope, true), off = OrbitalSettings.Text(scope, false);
                OrbitalSettings.ResetScope(scope);
                Ok($"{scope}: ResetScope puts every default back", Settings.Where(s => s.Scope == scope).All(s => s.Get() == s.Default));
                int n = OrbitalSettings.Apply(all, scope);
                Ok($"{scope}: Apply(Text(all)) applied each line", n == want.Count, $"{n} of {want.Count}");
                foreach (var kv in want) Ok($"{kv.Key}: round-trips exactly", Val(kv.Key) == kv.Value, $"{Val(kv.Key):R} vs {kv.Value:R}");
                OrbitalSettings.ResetScope(scope);
                OrbitalSettings.Apply(off, scope);
                foreach (var kv in want) Ok($"{kv.Key}: round-trips through the save's text (off-default only)", Val(kv.Key) == kv.Value);
                OrbitalSettings.ResetScope(scope);
            }
            Ok("other scope's lines ignored", OrbitalSettings.Apply("Hud.ShowOrbit = false\n", "world") == 0 && Val("Hud.ShowOrbit") == 1);
            Ok("comments and junk ignored", OrbitalSettings.Apply("# Map.TextScale = 2\nnonsense\n= 3\n", "client") == 0);
        }

        // ── ResetGroup ──

        static void ResetGroups()
        {
            Console.WriteLine("-- ResetGroup");
            OrbitalSettings.Set("Map.TextScale", 2, out _); OrbitalSettings.Set("Map.Orbital", 0, out _); OrbitalSettings.Set("Map.LineWidth", 0.01, out _);
            OrbitalSettings.Set("Hud.ShowOrbit", 0, out _); OrbitalSettings.Set("Warp.MaxLevel", 2, out _);
            int v0 = OrbitalSettings.Version;
            int n = OrbitalSettings.ResetGroup("client", "map", out string why0);   // (any case)
            Ok("Map: three changed back (hidden ones too)", n == 3 && why0 == null, n + " " + why0);
            Ok("Map: every one at its default", Settings.Where(s => s.Group == "Map").All(s => s.Get() == s.Default));
            Ok("another group untouched", Val("Hud.ShowOrbit") == 0);
            Ok("another scope untouched", Val("Warp.MaxLevel") == 2);
            Ok("the version moved", OrbitalSettings.Version != v0);
            Ok("a group in the wrong scope: none, and why", OrbitalSettings.ResetGroup("world", "Map", out string w1) == 0 && w1 != null && w1.StartsWith("no group"), w1 ?? "");
            Ok("nothing to reset: 0, no why", OrbitalSettings.ResetGroup("client", "Map", out string w2) == 0 && w2 == null);
            OrbitalSettings.Set("Map.TextScale", 2, out _);
            Ok("scope null: either", OrbitalSettings.ResetGroup(null, "Map", out _) == 1 && Val("Map.TextScale") == Settings.First(x => x.Name == "Map.TextScale").Default);
            Ok("null group: 0, and why", OrbitalSettings.ResetGroup(null, null, out string w3) == 0 && w3 != null);
            // a pair with a rule: Enter 3 / Exit 4, back to 1 / 1.1 in either order
            OrbitalSettings.Set("Proxy.FrameExitFactor", 4, out _); OrbitalSettings.Set("Proxy.FrameEnterFactor", 3, out _);
            OrbitalSettings.ResetGroup("client", "Planets", out _);
            Ok("a ruled pair resets (the second pass)", Val("Proxy.FrameEnterFactor") == 1.0 && Val("Proxy.FrameExitFactor") == 1.1);
            int rb = PlanetMesh.Rebuilds;
            OrbitalSettings.Set("Proxy.GlobeDetail", 3, out _);
            Ok("Proxy.GlobeDetail 3: 128 quads, the mesh rebuilt", PlanetMesh.Quads == 128 && PlanetMesh.Rebuilds == rb + 1);
            OrbitalSettings.ResetGroup("client", "Planets", out _);
            Ok("Proxy.GlobeDetail back: 64 quads", PlanetMesh.Quads == 64 && Val("Proxy.GlobeDetail") == 2);
            OrbitalSettings.ResetGroup("client", "Flight HUD", out _); OrbitalSettings.ResetGroup("world", "Time warp", out _);
            Ok("all back at defaults", Settings.All(s => s.Get() == s.Default));
        }

        // ── authority ──

        static void WorldEditRefused()
        {
            Console.WriteLine("-- CanEditWorld false: world sets refused");
            Ok("nothing seen yet: may edit the world", OrbitalSettings.CanEditWorld());
            OrbitalSettings.SawClient = true; OrbitalSettings.SawServer = false;   // (a remote client)
            Ok("a client only: may not", !OrbitalSettings.CanEditWorld());
            Ok("a world set refused", !OrbitalSettings.Set("Warp.MaxLevel", 3, out string why) && why.Contains("host") && Val("Warp.MaxLevel") == 6, why);
            Ok("a client set still fine", OrbitalSettings.Set("Map.TextScale", 2, out _) && Val("Map.TextScale") == 2);
            OrbitalSettings.Set("Map.TextScale", 1.3, out _);
            OrbitalSettings.Apply("Warp.MaxLevel = 2", "world");
            Ok("ResetGroup of world: refused, 0, why", OrbitalSettings.ResetGroup("world", "Time warp", out string w4) == 0 && w4 != null && w4.Contains("host") && Val("Warp.MaxLevel") == 2, w4 ?? "");
            OrbitalSettings.ResetScope("world");
            int v = OrbitalSettings.Version;
            Ok("the world save still applies (not an edit)", OrbitalSettings.Apply("Warp.MaxLevel = 4", "world") == 1 && Val("Warp.MaxLevel") == 4);
            OrbitalSettings.ResetScope("world");
            OrbitalSettings.SawServer = true;
            Ok("the server seen: may edit again", OrbitalSettings.CanEditWorld());
        }

        static void HostFileForces()
        {
            Console.WriteLine("-- the host file forces (IsForced)");
            string dir = Dir;
            {
                Ok("the files go to the temp folder", OrbitalSettings.Folder == dir);
                string world = Path.Combine(dir, "OrbitalMod.world.cfg");
                File.WriteAllText(world, "# host overrides\nWarp.MaxLevel = 3\n# World.Asteroids = false\nwarp.costsenergy = off\nHud.ShowOrbit = false\nNo.Such = 1\n");
                Ok("nothing forced before", !OrbitalSettings.IsForced("Warp.MaxLevel"));
                OrbitalSettings.Poll(true);
                Ok("no file problem", OrbitalSettings.FileWhy == "", OrbitalSettings.FileWhy);
                Ok("the file's values applied", Val("Warp.MaxLevel") == 3 && Val("Warp.CostsEnergy") == 0);
                Ok("forced: an uncommented line", OrbitalSettings.IsForced("Warp.MaxLevel") && OrbitalSettings.IsForced("Warp.CostsEnergy"));
                Ok("forced: any case", OrbitalSettings.IsForced("warp.maxlevel"));
                Ok("not forced: a commented line", !OrbitalSettings.IsForced("World.Asteroids"));
                Ok("not forced: a client setting in the world file", !OrbitalSettings.IsForced("Hud.ShowOrbit") && Val("Hud.ShowOrbit") == 1);
                Ok("not forced: unknown / null", !OrbitalSettings.IsForced("No.Such") && !OrbitalSettings.IsForced(null));
                bool refused = !OrbitalSettings.Set("Warp.MaxLevel", 5, out string why);
                Ok("a change refused, naming the file", refused && why.Contains("forced by the host file") && why.Contains("OrbitalMod.world.cfg") && Val("Warp.MaxLevel") == 3, why);
                Ok("even the same value: refused (as AeroSettings)", !OrbitalSettings.Set("Warp.MaxLevel", 3, out _));
                Ok("an unforced world setting: fine", OrbitalSettings.Set("World.Asteroids", 0, out _));
                Ok("ResetGroup skips the forced, and says so", OrbitalSettings.ResetGroup("world", "Time warp", out string w5) == 0 && Val("Warp.MaxLevel") == 3 && w5 != null && w5.Contains("forced"), w5 ?? "");
                Ok("WorldUnsaved after a change (not after the file: what it forces wins at load anyway)", OrbitalSettings.WorldUnsaved);
                Ok("the client file written", File.Exists(Path.Combine(dir, "OrbitalMod.client.cfg")));
                // the line commented out: no longer forced on the next apply
                File.WriteAllText(world, "# Warp.MaxLevel = 3\nWarp.CostsEnergy = off\n");
                File.SetLastWriteTimeUtc(world, DateTime.UtcNow.AddSeconds(5));
                OrbitalSettings.Poll(true);
                Ok("commented out: not forced", !OrbitalSettings.IsForced("Warp.MaxLevel") && OrbitalSettings.IsForced("Warp.CostsEnergy"));
                Ok("then settable", OrbitalSettings.Set("Warp.MaxLevel", 5, out _) && Val("Warp.MaxLevel") == 5);
                OrbitalSettings.BeaconAdded();   // (balances the removal: one beacon)
                OrbitalSettings.BeaconRemoved();
                Ok("the world gone: nothing forced, defaults, saved", !OrbitalSettings.IsForced("Warp.CostsEnergy") && Val("Warp.MaxLevel") == 6
                    && Val("World.Asteroids") == 1 && !OrbitalSettings.WorldUnsaved);
                File.WriteAllText(world, "# nothing forced\n");   // (the beacons below poll it again)
                File.SetLastWriteTimeUtc(world, DateTime.UtcNow.AddSeconds(10));
            }
        }

        static void WorldSave()
        {
            Console.WriteLine("-- the world save (beacon builder)");
            OrbitalSettings.Set("Warp.MaxLevel", 2, out _); OrbitalSettings.Set("Orbit.PlanetDaySeconds", 3600, out _); OrbitalSettings.Set("Map.TextScale", 2, out _);
            Ok("unsaved after a change", OrbitalSettings.WorldUnsaved);
            var ob = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder();
            OrbitalSettings.Save(ob, new Entity());
            Ok("saved: one key", ob.NamedEntities != null && ob.NamedEntities.Count == 1 && ob.NamedEntities.Keys.First().StartsWith(OrbitalSettings.SaveKey));
            Ok("saved: not unsaved", !OrbitalSettings.WorldUnsaved);
            Ok("saved: world only", !ob.NamedEntities.Keys.First().Contains("Map.TextScale"));
            OrbitalSettings.BeaconAdded(); OrbitalSettings.BeaconRemoved();
            Ok("unloaded: defaults", Val("Warp.MaxLevel") == 6 && Val("Orbit.PlanetDaySeconds") == -1);
            OrbitalSettings.Load(ob);
            Ok("loaded: the saved values", Val("Warp.MaxLevel") == 2 && Val("Orbit.PlanetDaySeconds") == 3600);
            Ok("loaded: not unsaved", !OrbitalSettings.WorldUnsaved);
            var empty = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder();
            OrbitalSettings.BeaconAdded(); OrbitalSettings.BeaconRemoved();
            OrbitalSettings.Save(empty, new Entity());
            Ok("all defaults: nothing saved", empty.NamedEntities == null || empty.NamedEntities.Count == 0);
            OrbitalSettings.ResetScope("client");
        }

        // ── never a dev knob ──

        static readonly Regex Denied = new Regex(@"^(Dev|Test|Debug|Probe|Trace)|Log$");

        static void Denylist()
        {
            Console.WriteLine("-- no Dev* / Test* / Debug* / Probe* / *Log / Trace* setting");
            foreach (var name in Names)
                foreach (var part in name.Split('.'))
                    Ok($"{name}: '{part}' not a dev name", !Denied.IsMatch(part));
            string reg = File.ReadAllText(Path.Combine(Root(), "Scripts", "Simulation", "Host", "OrbitalSettings.cs"));
            int fields = 0;
            foreach (Match m in Regex.Matches(reg, @"\b([A-Z]\w*)\.([A-Z]\w*)\b(?=\s*(=[^=>]|[,;)]))"))
            {
                fields++;
                Ok($"{m.Value}: not a dev field", !Denied.IsMatch(m.Groups[2].Value) && !Denied.IsMatch(m.Groups[1].Value));
            }
            Ok($"{fields} field uses scanned", fields > 40);
        }
    }
}
