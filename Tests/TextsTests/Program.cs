using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

// THE MOD'S TEXTS (2026-10-20): every text key the game looks up for this mod is in its en-US texts file - else the game shows
// the raw key and logs "Could not find key ... in the culture set en-US". The keys: every [Label] / [Group] / [Button("...")] in
// a file that defines a panel or a toolbar action (BlockDetailModel), the blocks' names and descriptions (UIData, BlockKind,
// BlockGroup definitions), and the probe Ui/ModTexts.cs reads the texts in by (OrbitalClientHost.ModTextsProbe).
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Assets", "Texts", "en-US", "OrbitalMod.loc-texts"))) d = d.Parent;
        if (d == null) { Console.WriteLine("FAIL the mod's texts file not found from the test's folder"); return 1; }
        string root = d.FullName;
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "Assets", "Texts", "en-US", "OrbitalMod.loc-texts")));
        var need = new SortedDictionary<string, string>(StringComparer.Ordinal);
        need["OrbitalModTexts_Probe"] = "OrbitalClientHost.ModTextsProbe";
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories))
        {
            string s = File.ReadAllText(f);
            if (!s.Contains("BlockDetailModel")) continue;
            foreach (Match m in Regex.Matches(s, @"\[(?:Label|Group|Button)\(""((?:[^""\\]|\\.)*)""\)\]")) need.TryAdd(Regex.Unescape(m.Groups[1].Value), Path.GetFileName(f));
        }
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*def", SearchOption.AllDirectories))
        {
            if (!f.EndsWith(".def") && !f.EndsWith(".partialdef")) continue;
            string s = File.ReadAllText(f);
            foreach (Match u in Regex.Matches(s, @"""UIData""\s*:\s*\{(.*?)\}", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(u.Groups[1].Value, @"""(?:Name|Description)""\s*:\s*""((?:[^""\\]|\\.)*)""")) need.TryAdd(Regex.Unescape(m.Groups[1].Value), Path.GetFileName(f));
            if (s.Contains("BlockKindDefinition") || s.Contains("BlockGroupDefinition"))
                foreach (Match m in Regex.Matches(s, @"""(?:Name|Description)""\s*:\s*""((?:[^""\\]|\\.)*)""")) need.TryAdd(Regex.Unescape(m.Groups[1].Value), Path.GetFileName(f));
        }
        var missing = need.Where(kv => kv.Key.Length > 0 && !texts.ContainsKey(kv.Key)).ToList();
        foreach (var kv in missing) Console.WriteLine($"     missing '{kv.Key}' ({kv.Value})");
        Check(need.Count >= 8, $"the keys found ({need.Count})");
        Check(missing.Count == 0, $"every key in the texts file ({missing.Count} of {need.Count} missing)");
        Check(texts.All(kv => !string.IsNullOrEmpty(kv.Value)), "no empty text (the game's label lookup falls back to the key)");
        // (a toast's title and content are localization KEYS: a sentence passed as one logged two "Could not find key" warnings a
        //  toast. Every toast goes through Ui/ModToast.Literal - the game's "{Title}" / "{Content}" keys, the text the parameter:
        //  no UserMessage built anywhere else, no TitleId / ContentId made from a string but ModToast's own two keys)
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(f);
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("//")) continue;
                bool built = Regex.IsMatch(t, @"new\s+UserMessage\b") && name != "ModToast.cs";
                bool asKey = Regex.IsMatch(t, @"\b(TitleId|ContentId)\s*=\s*(LocKey\.FromString|new\s+LocKey)\b") && !Regex.IsMatch(t, @"FromString\((TitleKey|ContentKey)\)");
                if (built || asKey) bad.Add($"{name}:{i + 1}");
            }
        }
        string mt = Path.Combine(root, "Scripts", "Simulation", "Ui", "ModToast.cs");
        Check(File.Exists(mt) && File.ReadAllText(mt).Contains("\"UserMessageFanfareContent\""), "toasts: Ui/ModToast.cs shows the text through the game's {Title} / {Content} keys");
        Check(bad.Count == 0, $"toasts: no UserMessage outside ModToast, no title / content key made from a text ({string.Join(", ", bad)})");
        Console.WriteLine($"== TextsTests: {(_fails == 0 ? $"{_checks} passed, 0 failed" : $"{_fails} of {_checks} FAILED")}");
        return _fails == 0 ? 0 : 1;
    }
}
