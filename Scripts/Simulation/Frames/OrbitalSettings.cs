using System.Globalization;
using System.IO;
using System.Text;
using Keen.Game2.Simulation.GameSystems;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE ORBITAL MOD'S SETTINGS: one list of named values over the mod's static tunables (OrbitalConfig and the
/// systems' switches; no reflection: each setting is a getter and a setter on its field), in two scopes:
///  - WORLD (server): the same for every player - planet spin, legacy-space capture, reentry, asteroids and
///    encounters. Saved WITH THE WORLD on the server planets' beacon (ServerPlanetBeacon's builder, key "settings:",
///    next to the orbital state). Optional host overrides in %APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.world.cfg
///    (uncommented lines win over the save).
///  - CLIENT (this PC): proxy planets, the orbit HUD, the map. Saved in ...\ModSettings\OrbitalMod.client.cfg.
/// Both files are polled (~2 s): edit them while playing. Most settings take effect at once (the fields are read each
/// tick or frame); those marked "reload" are read when the solar system is built and apply after a world reload.
/// Reached by other mods through OrbitalApi; documented in docs/API.md. The same design as the Aerodynamics Mod's
/// AeroSettings (the two mods compile separately, so the code is not shared).
///
/// Multiplayer: SE2 2.4 runs its server in the same process as the client (its networking is a mock), so the world
/// values ARE the server's. A process that only ever saw a client tick (a future remote client) refuses world sets.
/// </summary>
public static class OrbitalSettings
{
    internal sealed class Setting
    {
        public string Name, Type, Scope, Doc;
        public double Default, Min, Max;
        public bool Reload;
        public Func<double> Get;
        public Action<double> Set;
        public Func<double, string> Check;   // (a cross-setting rule: why not, or null)
    }

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

    static OrbitalSettings()
    {
        // ── WORLD ──
        F("Orbit.PlanetDaySeconds", "world", -1, 1e7, true, () => OrbitalConfig.PlanetDaySeconds, v => OrbitalConfig.PlanetDaySeconds = v,
            "Planet day (sidereal, s): -1 the world's own sun period, 0 planets do not spin, more: that period.");
        B("Orbit.CaptureLegacySpace", "world", false, () => OrbitalConfig.CaptureLegacySpace, v => OrbitalConfig.CaptureLegacySpace = v,
            "Put the space outside every planet cell (e.g. the spawn area) on orbits too. Off: it stays as the game has it (things there would fall into the planet).");
        F("Orbit.FictitiousMinSpeed", "world", 0, 1000, false, () => OrbitalConfig.FictitiousMinSpeed, v => OrbitalConfig.FictitiousMinSpeed = v,
            "In a spinning planet's cell, the spin's fictitious forces apply above this speed (m/s) or the altitude below.");
        F("Orbit.FictitiousMinAltitude", "world", 0, 1e6, false, () => OrbitalConfig.FictitiousMinAltitude, v => OrbitalConfig.FictitiousMinAltitude = v,
            "... or above this altitude (m).");
        B("Entry.Enabled", "world", false, () => EntryHost.Enabled, v => EntryHost.Enabled = v,
            "Reentry: frames on orbit are braked through a planet's air (heat, wear, the plasma) before they arrive. Off: they arrive at orbital speed.");
        F("Entry.DragDensity", "world", 0, 0.1, false, () => SEAerospace.Entry.Reentry.DragDensity, v => SEAerospace.Entry.Reentry.DragDensity = v,
            "Aerobraking: the outer atmosphere's density at the planet frame's border (kg/m3; falls off above it). Orbits dipping into the band lose speed by drag (each ship's own ballistic coefficient). 0: off - only speed over the world cap is braked.");
        B("World.Asteroids", "world", true, () => AsteroidFrames.Enabled, v => AsteroidFrames.Enabled = v,
            "Asteroid belts and their rocks on orbits.");
        B("World.Encounters", "world", true, () => EncounterFrames.Enabled, v => EncounterFrames.Enabled = v,
            "Encounter sites (the game's procedural encounters) on orbits.");
        B("World.RingRocks", "world", true, () => RingRocks.Enabled, v => RingRocks.Enabled = v,
            "Rocks in planetary rings to rendezvous with.");
        I("World.RingRocksPerRing", "world", 0, 1000, false, () => RingRocks.PerRing, v => RingRocks.PerRing = (int)v,
            "Rocks per ring.");

        // ── CLIENT ──
        I("Proxy.Mode", "client", 0, 3, false, () => (int)OrbitalConfig.Mode, v => OrbitalConfig.Mode = (ProxyMode)(int)v,
            "Planets far away: 0 = a proxy globe outside the planet's frame (normal), 1 = always the proxy, 2 = never (the real planet), 3 = alternate (comparison).");
        B("Proxy.HideRealPlanets", "client", false, () => OrbitalConfig.HideRealPlanets, v => OrbitalConfig.HideRealPlanets = v,
            "Hide the real planet where the proxy stands in. Off: real planets are never touched.");
        B("Proxy.HideAtmosphere", "client", false, () => OrbitalConfig.HideAtmosphere, v => OrbitalConfig.HideAtmosphere = v,
            "Hide the real planet's atmosphere and clouds with its terrain.");
        F("Proxy.ClampDistance", "client", 0, 1e9, false, () => OrbitalConfig.ProxyClampDistance, v => OrbitalConfig.ProxyClampDistance = v,
            "Draw proxies no further than this (m), keeping their angular size. 0: at their true distance.");
        F("Proxy.FrameEnterFactor", "client", 0.1, 10, false, () => OrbitalConfig.FrameEnterFactor, v => OrbitalConfig.FrameEnterFactor = v,
            "Outside the frames model: the real planet shows inside gravity reach x this.").Check = v => v >= OrbitalConfig.FrameExitFactor ? "must stay under Proxy.FrameExitFactor" : null;
        F("Proxy.FrameExitFactor", "client", 0.1, 10, false, () => OrbitalConfig.FrameExitFactor, v => OrbitalConfig.FrameExitFactor = v,
            "... and hides again past gravity reach x this (hysteresis).").Check = v => v <= OrbitalConfig.FrameEnterFactor ? "must stay over Proxy.FrameEnterFactor" : null;
        F("Proxy.MinFrameRadii", "client", 1, 20, false, () => OrbitalConfig.MinFrameRadii, v => OrbitalConfig.MinFrameRadii = v,
            "The smallest frame sphere, in planet radii (planets with tiny gravity reach).");
        F("Proxy.AlternateSeconds", "client", 1, 600, false, () => OrbitalConfig.AlternateSeconds, v => OrbitalConfig.AlternateSeconds = v,
            "Proxy.Mode 3: seconds between switching real and proxy.");
        B("Hud.ShowOrbit", "client", false, () => OrbitalConfig.ShowOrbit, v => OrbitalConfig.ShowOrbit = v,
            "The predicted orbit line and the orbit card.");
        F("Hud.OrbitLineThickness", "client", 0.0005, 0.05, false, () => OrbitDisplay.LineThickness, v => OrbitDisplay.LineThickness = (float)v,
            "The orbit line's width, per metre of distance.");
        B("Map.BodyMarkers", "client", false, () => BodyMarkers.Enabled, v => BodyMarkers.Enabled = v, "Planet and moon markers in flight.");
        B("Map.FrameMarkers", "client", false, () => FrameMarkers.Enabled, v => FrameMarkers.Enabled = v, "Markers on other orbiting frames.");
        F("Map.LineWidth", "client", 0.0005, 0.05, false, () => MapView.LineWidth, v => MapView.LineWidth = v, "The map's orbit lines' width.");
        F("Map.ZoomOutFactor", "client", 1, 100, false, () => GameMap.ZoomOutFactor, v => GameMap.ZoomOutFactor = v, "How far the map zooms out, x the game's own limit.");
        B("Map.ManeuverEditor", "client", false, () => CleanMap.ManeuverEditor, v => CleanMap.ManeuverEditor = v, "Maneuver nodes on the map (click an orbit to add one).");
        I("Map.OrbitPatches", "client", 0, 10, false, () => Maneuvers.MaxPatches, v => Maneuvers.MaxPatches = (int)v, "Conic patches drawn ahead (each later one fainter).");
    }

    static Setting Add(string name, string type, string scope, double min, double max, bool reload, Func<double> get, Action<double> set, string doc)
    {
        var s = new Setting { Name = name, Type = type, Scope = scope, Min = min, Max = max, Reload = reload, Get = get, Set = set, Doc = doc };
        s.Default = get();   // (the field's own initial value)
        _all.Add(s); _by[name] = s;
        return s;
    }
    static Setting B(string n, string scope, bool reload, Func<bool> get, Action<bool> set, string doc) => Add(n, "bool", scope, 0, 1, reload, () => get() ? 1 : 0, v => set(v != 0), doc);
    static Setting I(string n, string scope, double min, double max, bool reload, Func<int> get, Action<double> set, string doc) => Add(n, "int", scope, min, max, reload, () => get(), set, doc);
    static Setting F(string n, string scope, double min, double max, bool reload, Func<double> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, reload, get, set, doc);
    static Setting F(string n, string scope, double min, double max, bool reload, Func<float> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, reload, () => get(), set, doc);

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
        if (value < s.Min || value > s.Max) { why = "out of range " + s.Min.ToString(Inv) + ".." + s.Max.ToString(Inv); return false; }
        if (s.Scope == "world" && !fromStorage && !WorldWritable) { why = "world settings are set by the host / server"; return false; }
        lock (_lock)
        {
            if (s.Get() == value) return true;
            if (s.Check != null && (why = s.Check(value)) != null) return false;
            s.Set(value);
            System.Threading.Interlocked.Increment(ref _version);
            if (s.Scope == "client" && !fromStorage) _clientDirtyAt = System.Diagnostics.Stopwatch.GetTimestamp();
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
            double v = s.Get();
            if (!all && v == s.Default) continue;
            if (all) sb.Append("# ").Append(s.Doc).Append(" (").Append(s.Type).Append(", default ").Append(Fmt(s, s.Default))
                       .Append(", ").Append(Fmt(s, s.Min)).Append("..").Append(Fmt(s, s.Max)).Append(s.Reload ? ", applies after a world reload" : "").Append(")\n");
            sb.Append(s.Name).Append(" = ").Append(Fmt(s, v)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Apply "name = value" lines of a scope (others ignored). Returns the count applied.</summary>
    internal static int Apply(string text, string scope)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (!_by.TryGetValue(name, out var s) || s.Scope != scope) continue;
            if (!Parse(val, out double v)) { Log.Default?.Info($"[ORBIT] setting {name}: '{val}' is not a value"); continue; }
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
        foreach (var s in _all) if (s.Scope == scope && s.Get() != s.Default) Set(s.Name, s.Default, out _, true);
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
            Log.Default?.Info($"[ORBIT] world settings loaded: {n}");
            _worldFileAt = DateTime.MinValue;   // (the host's overrides again, over the save)
            return;
        }
    }

    /// <summary>Save: the world settings off their default, as one key (the planet itself as the value: one must exist).</summary>
    internal static void Save(EntityNameSessionComponentObjectBuilder ob, Entity self)
    {
        string text = Text("world", false);
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
                if (at != _worldFileAt && WorldWritable) { _worldFileAt = at; Apply(File.ReadAllText(WorldPath), "world"); }
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
