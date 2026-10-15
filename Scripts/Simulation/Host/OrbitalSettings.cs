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
public static partial class OrbitalSettings
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
}
