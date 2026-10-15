#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE ORBITAL MOD'S SETTINGS: one list of named values over the mod's static tunables (OrbitalConfig and the
/// systems' switches; no reflection: each setting is a getter and a setter on its field), in two scopes:
///  - WORLD (server): the same for every player - planet spin, legacy-space capture, reentry, asteroids and
///    encounters, time warp. Saved WITH THE WORLD on the server planets' beacon (ServerPlanetBeacon's builder, key
///    "settings:", next to the orbital state). Optional host overrides in
///    %APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.world.cfg (uncommented lines win over the save).
///  - CLIENT (this PC): proxy planets, the orbit HUD, the map, the autopilot. Saved in ...\ModSettings\OrbitalMod.client.cfg.
/// Both files are polled (~2 s): edit them while playing. Most settings take effect at once (the fields are read each
/// tick or frame); those marked "reload" are read when the solar system is built and apply after a world reload.
/// Reached by other mods through OrbitalApi; documented in docs/API.md. The same design as the Aerodynamics Mod's
/// AeroSettings (the two mods compile separately, so the code is not shared).
///
/// Each setting also carries what an in-game settings menu needs (Ui / Pick / Visible below): a player-facing label, a
/// group, a tier (Basic: on the menu; Advanced: under the group's "Advanced"; Hidden: the files and the API only), a
/// unit, a slider step, labels for an int that is a choice, and an order. Doc is the tooltip (and the files' comment).
/// Never registered: a Dev*, Test*, Debug*, Probe*, *Log or Trace* field (Tests/SettingsTests scans for it).
/// The registry is here; the store (the API, the text, the world save, the files) is OrbitalSettings.Store.cs.
///
/// Multiplayer: SE2 2.4 runs its server in the same process as the client (its networking is a mock), so the world
/// values ARE the server's. A process that only ever saw a client tick (a future remote client) refuses world sets.
/// </summary>
public static partial class OrbitalSettings
{
    /// <summary>Where a setting shows on a settings menu: Basic on it, Advanced under its group's "Advanced", Hidden in
    /// the files and the API only.</summary>
    internal enum Tier { Basic, Advanced, Hidden }

    internal sealed class Setting
    {
        public string Name, Type, Scope, Doc;
        public double Default, Min, Max;
        public bool Reload;
        public Func<double> Get;
        public Action<double> Set;
        public Func<double, string> Check;   // (a cross-setting rule: why not, or null)

        // ── for a settings menu ──
        public string Label, Group, Unit;     // (Unit: "" for none)
        public Tier Tier = Tier.Hidden;       // (until Ui says otherwise)
        public double Step;                   // a slider's step (0: the menu picks; a bool has none)
        public string[] Choices;              // an int that is a choice: the label of each value, Min first (null: a number)
        public int Order;                     // its place in its group (the registration order unless Ui gives one)
        public bool ClientVisible;            // a world value a client also draws from (the plasma, the warp bar). Nothing
                                              // replicates yet: a remote client would draw from its own copy.

        /// <summary>The menu's label, group and tier (and unit, slider step, order).</summary>
        public Setting Ui(string label, string group, Tier tier, string unit = "", double step = 0, int order = 0)
        {
            Label = label; Group = group; Tier = tier; Unit = unit ?? ""; Step = step;
            if (order != 0) Order = order;
            return this;
        }
        /// <summary>An int that is a choice: one label per value, Min first.</summary>
        public Setting Pick(params string[] choices) { Choices = choices; return this; }
        /// <summary>A world value clients also draw from.</summary>
        public Setting Visible() { ClientVisible = true; return this; }
    }

    // The groups (a menu's sections; the world's first).
    const string GWorld = "Orbits and world", GWarp = "Time warp",
                 GPlanets = "Planets", GHud = "Flight HUD", GMap = "Map", GPilot = "Autopilot";

    static OrbitalSettings()
    {
        // ── WORLD ──
        F("Orbit.PlanetDaySeconds", "world", -1, 1e7, true, () => OrbitalConfig.PlanetDaySeconds, v => OrbitalConfig.PlanetDaySeconds = v,
            "Planet day (sidereal, s): -1 the world's own sun period, 0 planets do not spin, more: that period.")
            .Ui("Planet day length", GWorld, Tier.Basic, "s", 60).Visible();   // (clients draw the spin)
        B("Orbit.CaptureLegacySpace", "world", false, () => OrbitalConfig.CaptureLegacySpace, v => OrbitalConfig.CaptureLegacySpace = v,
            "Put the space outside every planet cell (e.g. the spawn area) on orbits too. Off: it stays as the game has it (things there would fall into the planet).")
            .Ui("Orbits in the spawn area", GWorld, Tier.Advanced);
        F("Orbit.FictitiousMinSpeed", "world", 0, 1000, false, () => OrbitalConfig.FictitiousMinSpeed, v => OrbitalConfig.FictitiousMinSpeed = v,
            "In a spinning planet's cell, the spin's fictitious forces apply above this speed (m/s) or the altitude below.")
            .Ui("Spin forces above speed", GWorld, Tier.Hidden, "m/s", 1);
        F("Orbit.FictitiousMinAltitude", "world", 0, 1e6, false, () => OrbitalConfig.FictitiousMinAltitude, v => OrbitalConfig.FictitiousMinAltitude = v,
            "... or above this altitude (m).")
            .Ui("Spin forces above altitude", GWorld, Tier.Hidden, "m", 100);
        B("Entry.Enabled", "world", false, () => EntryHost.Enabled, v => EntryHost.Enabled = v,
            "Reentry: frames on orbit are braked through a planet's air (heat, wear, the plasma) before they arrive. Off: they arrive at orbital speed.")
            .Ui("Re-entry", GWorld, Tier.Basic).Visible();   // (the client's entry prediction and plasma)
        F("Entry.DragDensity", "world", 0, 0.1, false, () => SEAerospace.Entry.Reentry.DragDensity, v => SEAerospace.Entry.Reentry.DragDensity = v,
            "Aerobraking: the outer atmosphere's density at the planet frame's border (kg/m3; falls off above it). Orbits dipping into the band lose speed by drag (each ship's own ballistic coefficient). 0: off - only speed over the world cap is braked.")
            .Ui("Aerobraking strength", GWorld, Tier.Advanced, "kg/m³", 1e-4).Visible();   // (the client's entry prediction)
        B("World.Asteroids", "world", true, () => AsteroidFrames.Enabled, v => AsteroidFrames.Enabled = v,
            "Asteroid belts and their rocks on orbits.")
            .Ui("Orbiting asteroid belts", GWorld, Tier.Basic);
        B("World.Encounters", "world", true, () => EncounterFrames.Enabled, v => EncounterFrames.Enabled = v,
            "Encounter sites (the game's procedural encounters) on orbits.")
            .Ui("Orbiting encounters", GWorld, Tier.Basic);
        B("World.SectorAnchors", "world", true, () => EncounterFrames.SectorAnchors, v => EncounterFrames.SectorAnchors = v,
            "Every deep-space sector gets an encounter site on orbit at its charted centre: reaching it drops you into the sector, where the game spawns its encounters as usual.")
            .Ui("Encounters near sectors", GWorld, Tier.Advanced);
        B("World.RingRocks", "world", true, () => RingRocks.Enabled, v => RingRocks.Enabled = v,
            "Rocks in planetary rings to rendezvous with.")
            .Ui("Ring rocks", GWorld, Tier.Basic);
        I("World.RingRocksPerRing", "world", 0, 1000, false, () => RingRocks.PerRing, v => RingRocks.PerRing = (int)v,
            "Rocks per ring.")
            .Ui("Rocks per ring", GWorld, Tier.Advanced, "", 10);

        I("Warp.MaxLevel", "world", 0, WarpControl.Levels.Length - 1, false, () => WarpControl.MaxLevel, v => WarpControl.MaxLevel = (int)v,
            "The highest time warp the keys, the warp bar and other mods can ask for (a level of the warp bar; warp above it drops to it).")
            .Ui("Maximum time warp", GWarp, Tier.Basic).Pick(WarpChoices()).Visible();   // (the client's keys and warp bar)
        B("Warp.CostsEnergy", "world", false, () => WarpBill.Enabled, v => WarpBill.Enabled = v,
            "Warping drains your ship's batteries and tanks: what its systems would have used in the time skipped.")
            .Ui("Warp uses energy", GWarp, Tier.Basic);
        F("Warp.StopLead", "world", 5, 120, false, () => Maneuvers.WarpLead, v => { Maneuvers.WarpLead = v; EntryHost.WarpLead = v; },
            "Warp drops to x1 this long (s) before a maneuver burn or a planet's air.")
            .Ui("Warp stops before events", GWarp, Tier.Advanced, "s", 1).Visible();   // (the client's map menu)
        F("Warp.SafetySpeed", "world", 100, 5000, false, () => ServerFrames.WarpMemberSpeed, v => ServerFrames.WarpMemberSpeed = v,
            "Warp is held so that no ship moving in your frame is carried faster than 0.75 x this (m/s; the physics' speed cap).")
            .Ui("Warp limit speed", GWarp, Tier.Hidden, "m/s", 50);

        // ── CLIENT ──
        I("Proxy.Mode", "client", 0, 3, false, () => (int)OrbitalConfig.Mode, v => OrbitalConfig.Mode = (ProxyMode)(int)v,
            "Planets far away: 0 = a proxy globe outside the planet's frame (normal), 1 = always the proxy, 2 = never (the real planet), 3 = alternate (comparison).")
            .Ui("Distant planets", GPlanets, Tier.Hidden).Pick("A globe outside its space", "Always the globe", "Never (the real planet)", "Alternate");
        B("Proxy.HideRealPlanets", "client", false, () => OrbitalConfig.HideRealPlanets, v => OrbitalConfig.HideRealPlanets = v,
            "Hide the real planet where the proxy stands in. Off: real planets are never touched.")
            .Ui("Distant planets as globes", GPlanets, Tier.Basic);
        B("Proxy.HideAtmosphere", "client", false, () => OrbitalConfig.HideAtmosphere, v => OrbitalConfig.HideAtmosphere = v,
            "Hide the real planet's atmosphere and clouds with its terrain.")
            .Ui("Hide far atmospheres with terrain", GPlanets, Tier.Advanced);
        B("Proxy.Rings", "client", false, () => PlanetRings.ProxyRings, v => PlanetRings.ProxyRings = v,
            "Planetary rings around the distant globes.")
            .Ui("Planet rings", GPlanets, Tier.Basic);
        // (SetNormals with the current values: the mesh's public rebuild - the globes take the new model on their next update)
        I("Proxy.GlobeDetail", "client", 0, 3, false, GlobeDetail, v => { PlanetMesh.Quads = 16 << (int)v; PlanetMesh.SetNormals(PlanetMesh.NormalMaps, PlanetMesh.TangentSign); },
            "The distant globes' mesh: quads per cube-face edge, 16 x 2^this (more: rounder, slower to build).")
            .Ui("Distant globe detail", GPlanets, Tier.Advanced).Pick("Low (16)", "Medium (32)", "High (64)", "Very high (128)");
        B("Proxy.NormalMaps", "client", false, () => PlanetMesh.NormalMaps, v => PlanetMesh.SetNormals(v, PlanetMesh.TangentSign),
            "The globes' baked surface relief (normal maps). Off: flat shading.")
            .Ui("Globe surface relief", GPlanets, Tier.Advanced);
        B("Proxy.WarmBeforeShow", "client", false, () => PlanetFrameComponent.WarmBeforeShow, v => PlanetFrameComponent.WarmBeforeShow = v,
            "Keep the globe up until the real planet's terrain has loaded, then swap (no pop-in).")
            .Ui("Wait for terrain before swapping", GPlanets, Tier.Advanced);
        F("Proxy.ClampDistance", "client", 0, 1e9, false, () => OrbitalConfig.ProxyClampDistance, v => OrbitalConfig.ProxyClampDistance = v,
            "Draw proxies no further than this (m), keeping their angular size. 0: at their true distance.")
            .Ui("Globe draw distance limit", GPlanets, Tier.Hidden, "m", 1000);
        F("Proxy.FrameEnterFactor", "client", 0.1, 10, false, () => OrbitalConfig.FrameEnterFactor, v => OrbitalConfig.FrameEnterFactor = v,
            "Outside the frames model: the real planet shows inside gravity reach x this.")
            .Ui("Real planet shows within", GPlanets, Tier.Hidden, "×", 0.05)
            .Check = v => v >= OrbitalConfig.FrameExitFactor ? "must stay under Proxy.FrameExitFactor" : null;
        F("Proxy.FrameExitFactor", "client", 0.1, 10, false, () => OrbitalConfig.FrameExitFactor, v => OrbitalConfig.FrameExitFactor = v,
            "... and hides again past gravity reach x this (hysteresis).")
            .Ui("Real planet hides past", GPlanets, Tier.Hidden, "×", 0.05)
            .Check = v => v <= OrbitalConfig.FrameEnterFactor ? "must stay over Proxy.FrameEnterFactor" : null;
        F("Proxy.MinFrameRadii", "client", 1, 20, false, () => OrbitalConfig.MinFrameRadii, v => OrbitalConfig.MinFrameRadii = v,
            "The smallest frame sphere, in planet radii (planets with tiny gravity reach).")
            .Ui("Smallest planet space", GPlanets, Tier.Hidden, "radii", 0.1);
        F("Proxy.AlternateSeconds", "client", 1, 600, false, () => OrbitalConfig.AlternateSeconds, v => OrbitalConfig.AlternateSeconds = v,
            "Proxy.Mode 3: seconds between switching real and proxy.")
            .Ui("Alternate every", GPlanets, Tier.Hidden, "s", 1);
        B("Fx.ArrivalFade", "client", false, () => CrossingFade.Enabled, v => CrossingFade.Enabled = v,
            "A short fade to black as you arrive in or leave a planet's space (hides the swap).")
            .Ui("Fade on arrival", GPlanets, Tier.Advanced);

        B("Hud.ShowOrbit", "client", false, () => OrbitalConfig.ShowOrbit, v => OrbitalConfig.ShowOrbit = v,
            "The predicted orbit line and the orbit card.")
            .Ui("Orbit line and card", GHud, Tier.Basic);
        F("Hud.OrbitLineThickness", "client", 0.0005, 0.05, false, () => OrbitDisplay.LineThickness, v => OrbitDisplay.LineThickness = (float)v,
            "The orbit line's width, per metre of distance.")
            .Ui("Orbit line width", GHud, Tier.Advanced, "", 0.0005);
        B("Map.BodyMarkers", "client", false, () => BodyMarkers.Enabled, v => BodyMarkers.Enabled = v, "Planet and moon markers in flight.")
            .Ui("Planet and moon markers", GHud, Tier.Basic);
        B("Map.FrameMarkers", "client", false, () => FrameMarkers.Enabled, v => FrameMarkers.Enabled = v, "Markers on other orbiting frames.")
            .Ui("Orbiting ship markers", GHud, Tier.Basic);
        B("Hud.FrameOrigin", "client", false, () => BodyMarkers.ShowOrigin, v => BodyMarkers.ShowOrigin = v,
            "A marker at the centre of the orbiting frame you ride in.")
            .Ui("Frame origin marker", GHud, Tier.Advanced);
        B("Hud.AlignButton", "client", false, () => AlignButton.On, v => AlignButton.On = v,
            "The align button on the cockpit HUD.")
            .Ui("Align button on the cockpit HUD", GHud, Tier.Basic);
        B("Hud.SpeedIcon", "client", false, () => SpdIcon.On, v => SpdIcon.On = v,
            "The orbital speed icon on the HUD.")
            .Ui("Orbital speed icon", GHud, Tier.Advanced);
        B("Hud.BuildHints", "client", false, () => BuildHints.On, v => BuildHints.On = v,
            "Build and aero hints while building (the O key cycles the overlay).")
            .Ui("Build and aero hints", GHud, Tier.Basic);
        // (Version++: the contact lists redraw - as the harness's 'contacts on / off')
        B("Hud.ContactsNeedDetection", "client", false, () => Contacts.Enabled, v => { Contacts.Enabled = v; Contacts.Version++; },
            "Other ships and rocks show only once your sensors have seen them. Off: everything is known.")
            .Ui("Contacts must be detected", GHud, Tier.Advanced);

        B("Map.Orbital", "client", false, () => CleanMap.Enabled, v => CleanMap.Enabled = v,
            "The orbital map in place of the game's map (orbits, planets to scale, maneuver nodes).")
            .Ui("Orbital map", GMap, Tier.Basic);
        B("Map.ManeuverEditor", "client", false, () => CleanMap.ManeuverEditor, v => CleanMap.ManeuverEditor = v, "Maneuver nodes on the map (click an orbit to add one).")
            .Ui("Maneuver nodes", GMap, Tier.Basic);
        I("Map.OrbitPatches", "client", 0, 10, false, () => Maneuvers.MaxPatches, v => Maneuvers.MaxPatches = (int)v, "Conic patches drawn ahead (each later one fainter).")
            .Ui("Predicted orbit segments", GMap, Tier.Basic, "", 1);
        F("Map.TextScale", "client", 0.5, 3, false, () => MapPipeline.TextScale, v => MapPipeline.TextScale = (float)v,
            "The map's text size.")
            .Ui("Map text size", GMap, Tier.Basic, "×", 0.1);
        F("Map.ZoomOutFactor", "client", 1, 100, false, () => GameMap.ZoomOutFactor, v => GameMap.ZoomOutFactor = v, "How far the map zooms out, x the game's own limit.")
            .Ui("Map zoom-out limit", GMap, Tier.Advanced, "×", 1);
        B("Map.FreeCamera", "client", false, () => MapCamera.Enabled, v => MapCamera.Enabled = v,
            "The map's free camera: right-drag to orbit, drag to pan.")
            .Ui("Free map camera", GMap, Tier.Advanced);
        B("Map.HideGameSectors", "client", false, () => GameMap.HideGameSectors, v => GameMap.HideGameSectors = v,
            "Hide the game's own sector grid on the map.")
            .Ui("Hide the game's sector grid", GMap, Tier.Advanced);
        B("Map.Rings", "client", false, () => PlanetRings.MapRings, v => PlanetRings.MapRings = v,
            "Planetary rings on the map.")
            .Ui("Planet rings on the map", GMap, Tier.Advanced);
        // (reload: once the tab is relabelled it stays so for the session)
        B("Map.ContactsTab", "client", true, () => ContactsTab.Enabled, v => ContactsTab.Enabled = v,
            "The map's contacts tab (what your sensors have seen) in place of the GPS tab.")
            .Ui("Contacts tab", GMap, Tier.Basic);
        // (no longer drawn: kept so that old files still parse)
        F("Map.LineWidth", "client", 0.0005, 0.05, false, () => MapView.LineWidth, v => MapView.LineWidth = v, "The map's orbit lines' width (no longer used).")
            .Ui("Map line width (unused)", GMap, Tier.Hidden, "", 0.0005);

        F("Autopilot.HoldTurnRate", "client", 5, 180, false, () => AttitudeHold.TurnDegPerSec, v => AttitudeHold.TurnDegPerSec = v,
            "How fast attitude hold turns your ship to a new direction (deg/s).")
            .Ui("Attitude hold turn rate", GPilot, Tier.Basic, "°/s", 5);
        F("Autopilot.BurnAlignLead", "client", 5, 120, false, () => AutoBurn.AlignLead, v => AutoBurn.AlignLead = v,
            "An auto-burn starts turning to its burn this long (s) before it.")
            .Ui("Align before auto-burn", GPilot, Tier.Advanced, "s", 1);
    }

    /// <summary>Warp.MaxLevel's labels: the warp levels ("×1" .. "×10,000").</summary>
    static string[] WarpChoices()
    {
        var c = new string[WarpControl.Levels.Length];
        for (int k = 0; k < c.Length; k++) c[k] = "×" + WarpControl.Levels[k].ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return c;
    }

    /// <summary>Proxy.GlobeDetail from PlanetMesh.Quads (16 x 2^choice; a value in between rounds down).</summary>
    static int GlobeDetail() { int q = PlanetMesh.Quads, c = 0; while (c < 3 && (16 << (c + 1)) <= q) c++; return c; }

    static Setting Add(string name, string type, string scope, double min, double max, bool reload, Func<double> get, Action<double> set, string doc)
    {
        var s = new Setting { Name = name, Type = type, Scope = scope, Min = min, Max = max, Reload = reload, Get = get, Set = set, Doc = doc };
        s.Default = get();   // (the field's own initial value)
        s.Order = (_all.Count + 1) * 10;
        _all.Add(s); _by[name] = s;
        return s;
    }
    // (B/I/F: the bool after the range is "applies after a world reload", not the default - the default is the field's)
    static Setting B(string n, string scope, bool reload, Func<bool> get, Action<bool> set, string doc) => Add(n, "bool", scope, 0, 1, reload, () => get() ? 1 : 0, v => set(v != 0), doc);
    static Setting I(string n, string scope, double min, double max, bool reload, Func<int> get, Action<double> set, string doc) => Add(n, "int", scope, min, max, reload, () => get(), set, doc);
    static Setting F(string n, string scope, double min, double max, bool reload, Func<double> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, reload, get, set, doc);
    static Setting F(string n, string scope, double min, double max, bool reload, Func<float> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, reload, () => get(), set, doc);
}
