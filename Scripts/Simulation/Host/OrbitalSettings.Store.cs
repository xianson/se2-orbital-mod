using System.Globalization;
using System.IO;
using System.Text;
using Keen.Game2.Simulation.GameSystems;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The settings' STORE: the registry's lists and lock, the API (names, info, get, set), who may change what (CanEditWorld,
/// IsForced), the "name = value" text, the world save (ServerPlanetBeacon's builder) and the two files. The settings
/// themselves are registered in OrbitalSettings.cs.
/// </summary>
public static partial class OrbitalSettings
{
    static readonly List<Setting> _all = new List<Setting>();
    static readonly Dictionary<string, Setting> _by = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new object();
    static int _version;
    /// <summary>Bumped on every change (API, file, world load).</summary>
    public static int Version => _version;

    /// <summary>Seen in this process: the server frame tick / the client frame tick.</summary>
    internal static volatile bool SawServer, SawClient;
    /// <summary>World settings can be set here: the server runs in this process (or nothing is known yet).</summary>
    public static bool WorldWritable => SawServer || !SawClient;

    /// <summary>
    /// World settings may be changed from here (a menu, the API, the harness): the world is hosted in this process. THE one
    /// place for an admin check once SE2 has real multiplayer; a dedicated server keeps world.cfg. (The world save and the
    /// host's file are not edits: they always apply.)
    /// </summary>
    public static bool CanEditWorld() => WorldWritable && IsLocalHostOrSinglePlayer();
    // (SE2 2.4: the server runs in the client's process - WorldWritable already says the server is here; no admins exist)
    static bool IsLocalHostOrSinglePlayer() => true;

    /// <summary>The world settings named by the host file's uncommented lines, as last applied (null: none). Replaced whole,
    /// never changed in place: read without the lock.</summary>
    static volatile HashSet<string> _forced;
    /// <summary>A world setting the host file (OrbitalMod.world.cfg) forces: a menu or API change is refused (the file would
    /// win again on the next world load).</summary>
    public static bool IsForced(string name)
    {
        var f = _forced;
        return name != null && f != null && f.Contains(name);
    }

    static volatile bool _worldUnsaved;
    /// <summary>A world setting changed (the API, a menu) since the world was loaded or last saved: it goes into the save on the
    /// world's next save (nothing is written now). A menu's "saved with this world" hint.</summary>
    public static bool WorldUnsaved => _worldUnsaved;

    // ── the API ──

    public static int Names(List<string> names)
    {
        if (names == null) return 0;
        names.Clear();
        foreach (var s in _all) names.Add(s.Name);
        return names.Count;
    }

    public static bool Info(string name, out string type, out double def, out double min, out double max, out string scope, out bool reload, out string doc)
    {
        type = scope = doc = null; def = min = max = 0; reload = false;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        type = s.Type; def = s.Default; min = s.Min; max = s.Max; scope = s.Scope; reload = s.Reload; doc = s.Doc;
        return true;
    }

    /// <summary>A setting's UI metadata (see Setting): tier "basic", "advanced" or "hidden"; unit "" for none; choices a
    /// copy of an int choice's labels, Min first (null: a number).</summary>
    public static bool UiInfo(string name, out string label, out string group, out string tier, out string unit, out double step, out string[] choices)
    {
        label = group = tier = unit = null; step = 0; choices = null;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        label = s.Label; group = s.Group; unit = s.Unit; step = s.Step;
        tier = s.Tier == Tier.Basic ? "basic" : s.Tier == Tier.Advanced ? "advanced" : "hidden";
        choices = s.Choices == null ? null : (string[])s.Choices.Clone();
        return true;
    }

    public static bool TryGet(string name, out double value)
    {
        value = 0;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        value = s.Get();
        return true;
    }

    /// <summary>Change a setting (from the API, a script or the harness). False, with why, when it cannot be.</summary>
    public static bool Set(string name, double value, out string why) => Set(name, value, out why, false);

    static bool Set(string name, double value, out string why, bool fromStorage)
    {
        why = null;
        if (name == null || !_by.TryGetValue(name, out var s)) { why = "no setting " + name; return false; }
        if (double.IsNaN(value) || double.IsInfinity(value)) { why = "not a number"; return false; }
        if (s.Type != "float") value = Math.Round(value);
        if (s.Type == "bool" && value != 0 && value != 1) { why = "a bool is 0 or 1"; return false; }
        // (a float field at a bound reads back a hair past it - (double)0.05f is 0.0500000007 - and its own client file then
        //  refused it: within float precision of a bound is the bound)
        if (s.Type == "float")
        {
            if (value > s.Max && value - s.Max <= Math.Abs(s.Max) * 1e-6) value = s.Max;
            else if (value < s.Min && s.Min - value <= Math.Abs(s.Min) * 1e-6) value = s.Min;
        }
        if (value < s.Min || value > s.Max) { why = "out of range " + s.Min.ToString(Inv) + ".." + s.Max.ToString(Inv); return false; }
        if (s.Scope == "world" && !fromStorage && !CanEditWorld()) { why = "world settings are set by the host / server"; return false; }
        if (s.Scope == "world" && !fromStorage && IsForced(s.Name)) { why = "forced by the host file " + WorldPath; return false; }
        lock (_lock)
        {
            // (the live field may hold a direct write - a harness's - that this value equals: it still becomes the player's)
            bool same = s.Get() == value;
            if (same && s.Stored == value) return true;
            if (!same && s.Check != null && (why = s.Check(value)) != null) return false;
            if (!same)
            {
                s.Set(value);
                System.Threading.Interlocked.Increment(ref _version);
            }
            s.Stored = s.Get();   // (what the field took: the files' text as it always was)
            if (s.Scope == "client" && !fromStorage) _clientDirtyAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s.Scope == "world" && !fromStorage) _worldUnsaved = true;
        }
        Log.Default?.Info($"[ORBIT] setting {s.Name} = {Fmt(s, value)}{(fromStorage ? " (loaded)" : "")}{(s.Reload ? " (applies after a world reload)" : "")}");
        return true;
    }

    // ── text: "name = value" lines (# comments), invariant culture ──

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Fmt(Setting s, double v) => s.Type == "bool" ? (v != 0 ? "true" : "false") : v.ToString(s.Type == "int" ? "0" : "R", Inv);

    /// <summary>The scope's settings as text: every one (all = true, with its description), or only those off their
    /// default (for the world save).</summary>
    public static string Text(string scope, bool all)
    {
        var sb = new StringBuilder();
        foreach (var s in _all)
        {
            if (s.Scope != scope) continue;
            double v = s.Stored;   // (the player's value, never a harness's direct write to the field)
            if (!all && v == s.Default) continue;
            if (all) sb.Append("# ").Append(s.Doc).Append(" (").Append(s.Type).Append(", default ").Append(Fmt(s, s.Default))
                       .Append(", ").Append(Fmt(s, s.Min)).Append("..").Append(Fmt(s, s.Max)).Append(s.Reload ? ", applies after a world reload" : "").Append(")\n");
            sb.Append(s.Name).Append(" = ").Append(Fmt(s, v)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Apply "name = value" lines of a scope (others ignored). Returns the count applied. named (optional): gets the
    /// name of every line of the scope with a value (applied or refused).</summary>
    internal static int Apply(string text, string scope, HashSet<string> named = null)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        // (a value refused against its partner's CURRENT value - Proxy.FrameEnterFactor under FrameExitFactor - is tried again
        //  others: an edit of 1.0/1.2 to 3/4 refused 3 for good, against the old 1.2)
        List<(string name, double v, string val)> retry = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (!_by.TryGetValue(name, out var s) || s.Scope != scope) continue;
            if (!Parse(val, out double v)) { Log.Default?.Info($"[ORBIT] setting {name}: '{val}' is not a value"); continue; }
            named?.Add(s.Name);
            if (Set(name, v, out string why, true)) n++;
            else (retry ??= new List<(string, double, string)>()).Add((name, v, val));
        }
        if (retry != null)
            foreach (var (name, v, val) in retry)
            {
                if (Set(name, v, out string why, true)) n++;
                else Log.Default?.Info($"[ORBIT] setting {name} = {val} refused: {why}");
            }
        return n;
    }

    static bool Parse(string s, out double v)
    {
        switch (s.ToLowerInvariant())
        {
            case "true": case "on": case "yes": v = 1; return true;
            case "false": case "off": case "no": v = 0; return true;
        }
        return double.TryParse(s, NumberStyles.Float, Inv, out v);
    }

    /// <summary>Every setting of a scope back to its default (a new world).</summary>
    internal static void ResetScope(string scope)
    {
        // (twice: a default refused against a partner not yet reset - Enter 1.0 over a world's Exit 0.9 - goes in on the second)
        for (int pass = 0; pass < 2; pass++)
            foreach (var s in _all) if (s.Scope == scope && (s.Get() != s.Default || s.Stored != s.Default)) Set(s.Name, s.Default, out _, true);
    }

    /// <summary>Every setting of a group (any tier; and scope - null: either) back to its default, through Set (so a world
    /// group needs CanEditWorld, and a forced name stays). Returns how many changed; why: the first refusal, or no such group.</summary>
    public static int ResetGroup(string scope, string group, out string why)
    {
        why = null;
        int n = 0; bool any = false;
        // (twice, as ResetScope: a default refused against a partner not yet reset goes in on the second)
        for (int pass = 0; pass < 2; pass++)
            foreach (var s in _all)
            {
                if (!string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase) || (scope != null && s.Scope != scope)) continue;
                any = true;
                if (s.Get() == s.Default && s.Stored == s.Default) continue;
                if (Set(s.Name, s.Default, out string w)) n++;
                else if (pass == 1) why ??= s.Name + ": " + w;
            }
        if (!any) why = "no group " + group + (scope != null ? " in " + scope : "");
        return n;
    }

    // ── the world save (ServerPlanetBeacon's builder) ──

    internal const string SaveKey = "settings:";
    static int _beacons;
    static bool _worldApplied;

    /// <summary>A server planet loaded with its saved builder: the world's settings (the first non-empty one wins).</summary>
    internal static void Load(EntityNameSessionComponentObjectBuilder ob)
    {
        if (_worldApplied || ob?.NamedEntities == null) return;
        foreach (var kv in ob.NamedEntities)
        {
            if (!kv.Key.StartsWith(SaveKey)) continue;
            _worldApplied = true;
            ResetScope("world");
            int n = Apply(kv.Key.Substring(SaveKey.Length), "world");
            _worldUnsaved = false;   // (what the save holds)
            Log.Default?.Info($"[ORBIT] world settings loaded: {n}");
            _worldFileAt = DateTime.MinValue;   // (the host's overrides again, over the save)
            return;
        }
    }

    /// <summary>Save: the world settings off their default, as one key (the planet itself as the value: one must exist).</summary>
    internal static void Save(EntityNameSessionComponentObjectBuilder ob, Entity self)
    {
        string text = Text("world", false);
        if (self != null) _worldUnsaved = false;   // (written now, or all at their defaults: nothing to write)
        if (text.Length == 0 || self == null) return;
        ob.NamedEntities ??= new Dictionary<string, Entity>();
        ob.NamedEntities[SaveKey + text] = self;
    }

    internal static void BeaconAdded() { SawServer = true; System.Threading.Interlocked.Increment(ref _beacons); Poll(true); }

    /// <summary>The last beacon gone (the world unloaded): world settings back to defaults for the next world.</summary>
    internal static void BeaconRemoved()
    {
        if (System.Threading.Interlocked.Decrement(ref _beacons) > 0) return;
        _worldApplied = false;
        ResetScope("world");
        _worldFileAt = DateTime.MinValue;
        _forced = null; _worldUnsaved = false;   // (the next world applies the host file again)
    }

    // ── the files (~2 s poll from the frame ticks; never a throw per tick: a failing file is left alone) ──

    static string _dir;
    static DateTime _clientFileAt = DateTime.MinValue, _worldFileAt = DateTime.MinValue;
    static long _pollAt, _clientDirtyAt;
    static int _fileFails;
    /// <summary>Diagnostics: the settings folder, the last file problem.</summary>
    public static string Folder => _dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "ModSettings");
    public static string FileWhy = "";
    static string ClientPath => Path.Combine(Folder, "OrbitalMod.client.cfg");
    static string WorldPath => Path.Combine(Folder, "OrbitalMod.world.cfg");

    /// <summary>The frame ticks: cheap (a timestamp compare); the files every ~2 s.</summary>
    internal static void Poll(bool force = false)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), due = System.Threading.Interlocked.Read(ref _pollAt);
        if (!force && now < due) return;
        if (System.Threading.Interlocked.CompareExchange(ref _pollAt, now + 2 * System.Diagnostics.Stopwatch.Frequency, due) != due && !force) return;
        if (_fileFails >= 3) return;
        try
        {
            Directory.CreateDirectory(Folder);
            long dirty = _clientDirtyAt;
            if ((dirty != 0 && now - dirty > System.Diagnostics.Stopwatch.Frequency) || !File.Exists(ClientPath))
            {
                _clientDirtyAt = 0;
                File.WriteAllText(ClientPath, ClientHeader + Text("client", true));
                _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
            }
            else
            {
                var at = File.GetLastWriteTimeUtc(ClientPath);
                if (at != _clientFileAt) { _clientFileAt = at; Apply(File.ReadAllText(ClientPath), "client"); }
            }
            if (SawServer || force)
            {
                if (!File.Exists(WorldPath)) File.WriteAllText(WorldPath, WorldHeader + Commented(Text("world", true)));
                var at = File.GetLastWriteTimeUtc(WorldPath);
                if (at != _worldFileAt && WorldWritable)
                {
                    _worldFileAt = at;
                    var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Apply(File.ReadAllText(WorldPath), "world", named);
                    _forced = named.Count > 0 ? named : null;
                }
            }
            FileWhy = "";
        }
        catch (Exception e) { _fileFails++; FileWhy = e.GetType().Name + ": " + e.Message + (_fileFails >= 3 ? " (files given up for this session)" : ""); }
    }

    static string Commented(string text)
    {
        var sb = new StringBuilder();
        foreach (var l in text.Split('\n')) if (l.Length > 0) sb.Append(l[0] == '#' ? l : "# " + l).Append('\n');
        return sb.ToString();
    }

    const string ClientHeader = "# Orbital Mod - this player's settings (proxy planets, orbit HUD, map). Saved by the mod; edit while playing:\n# changes apply within ~2 s. name = value; true / false for switches.\n\n";
    const string WorldHeader = "# Orbital Mod - host / server overrides for WORLD settings. Uncomment a line to force that value on every\n# world this machine hosts (it wins over the value saved with the world; applied within ~2 s).\n# The world saves its own values; without overrides here, those count.\n\n";
}
