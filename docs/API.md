# Orbital Mod API

The Orbital Mod exposes its data to other mods through one class, `OrbitalMod.OrbitalApi` (`Scripts/Simulation/Frames/OrbitalApi.cs`), and its settings through the same class (`Scripts/Simulation/Frames/OrbitalSettings.cs`).

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `OrbitalMod.OrbitalApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Cache the `MethodInfo`s you use. Look again when the number of loaded assemblies changes, if the orbital mod may load after yours.

Every signature uses only .NET and engine types (`Entity`, `Vector3`, `Vector3D`, `List<T>`), never an orbital type.

```csharp
static MethodInfo _frameOf, _orbit;
static int _asmCount = -1;

static bool OrbitOf(Entity grid, out double periapsisAlt, out double apoapsisAlt)
{
    periapsisAlt = apoapsisAlt = 0;
    var asms = AppDomain.CurrentDomain.GetAssemblies();
    if (asms.Length != _asmCount)
    {
        _asmCount = asms.Length; _frameOf = _orbit = null;
        foreach (var a in asms)
        {
            var t = a.GetType("OrbitalMod.OrbitalApi");
            if (t == null) continue;
            if ((int)t.GetField("Version").GetRawConstantValue() < 1) break;
            _frameOf = t.GetMethod("GetFrameOf");
            _orbit = t.GetMethod("TryGetFrameOrbitSummary");
            break;
        }
    }
    if (_frameOf == null) return false;   // (the orbital mod is not installed)
    long frame = (long)_frameOf.Invoke(null, new object[] { grid });
    if (frame < 0) return false;          // (not on an orbit)
    var args = new object[] { frame, null, null, null, null };
    if (!(bool)_orbit.Invoke(null, args)) return false;
    periapsisAlt = (double)args[1]; apoapsisAlt = (double)args[2];
    return true;
}
```

## Conventions

- **Frames.** The mod keeps everything on orbit in a *frame*: a group of grids and players sharing one Keplerian orbit about a body, simulated around a berth in the game world. Frame ids are runtime numbers, stable for the session; a save keeps the frames, but their ids may change.
- **States** are relative to the frame's (or body's) parent body, in the solar system's inertial axes: m, m/s.
- **Time** is the universe clock (`GetTime`, s), which runs faster in time warp. Pass `NaN` for "now".
- **Where it answers:** where the frame registry lives, the machine that runs the server (single player, a host). The player calls (`GetPlayerFrame`, `GetPlayerBody`, `TryGetEntryPrediction`) answer where that player's client runs. SE2 2.4 runs both in one process.
- **Grids:** pass the grid's server copy or, in the same process, its client copy (matched to the nearest server grid within 20 m).
- **Threads:** any. Frame answers are copied under the frame lock.
- **Stability:** members are only **added** within a version. Changing an existing member bumps `Version`.

## Version 1

### The solar system

| Member | What it gives |
|---|---|
| `const int Version` | The API contract (1). |
| `bool IsReady()` | The solar system is built. `false` early in a session. |
| `double GetTime()` / `double GetTimescale()` | The universe clock (s) and the time warp factor (1: none). |
| `int GetBodies(List<string> names)` | The bodies: the star first, then planets and moons. |
| `bool TryGetBody(string name, out string parent, out double mu, out double radius, out double soiRadius, out bool hasAtmosphere, out double atmosphereHeight, out double rotationPeriod)` | A body: parent (`""` for the star), GM (m³/s²), radius (m), sphere of influence (m; infinity for the star), atmosphere and its height (m), sidereal day (s; 0: none). |
| `bool TryGetBodyState(string name, double time, out Vector3D position, out Vector3D velocity)` | A body's state relative to its parent. `false` for the star. |

### Frames

| Member | What it gives |
|---|---|
| `int GetFrames(List<long> ids)` | Every frame's id. |
| `bool TryGetFrame(long frameId, out string body, out int memberCount, out bool isEncounter)` | The body it orbits, its member count, and whether it is an encounter site. |
| `int GetFrameMembers(long frameId, List<long> memberIds)` | Its members: grid member ids (from 1 000 000 000) and player ids. |
| `bool TryGetFrameOrbit(long frameId, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | The orbit's Keplerian elements: a (m; negative when hyperbolic), e, i, Ω, ω, ν (rad) at the epoch (s), and the body's GM. |
| `bool TryGetFrameOrbitSummary(long frameId, out double periapsisAltitude, out double apoapsisAltitude, out double period, out double inclination)` | Periapsis and apoapsis altitude over the surface (m), period (s), inclination (rad). Apoapsis and period are infinity when hyperbolic. |
| `bool TryGetFrameState(long frameId, double time, out Vector3D position, out Vector3D velocity)` | Where the frame is on its orbit, propagated on rails. |

### Grids and the player

| Member | What it gives |
|---|---|
| `long GetMemberId(Entity grid)` | The grid's member id (what frames list), or −1. |
| `long GetFrameOf(Entity grid)` | The frame the grid is in, or −1: not on an orbit (inside a planet's cell, or not framed). |
| `long GetPlayerFrame()` | The local player's frame, or −1. |
| `string GetPlayerBody()` | The planet whose cell the local player is in, or `""`. |

### Reentry

A frame on orbit that comes down into a planet's cell is first braked through the planet's thin outer envelope (the *band*), so that it arrives at or under the world's speed cap. It heats up on the way, and a grid past its tolerance loses some of its forward layer.

| Member | What it gives |
|---|---|
| `double GetSpeedCap()` | The world's speed cap (m/s), what reentry slows a frame to. |
| `bool TryGetEntryBand(string body, out double bottomRadius, out double topRadius)` | A body's band: from the planet cell's border (bottom) to the entry interface (top), radii in m. `false` without an atmosphere. |
| `bool TryGetReentry(long frameId, out double heat, out bool braking, out double deceleration)` | A frame's heat (J/kg; the unshielded tolerance is 1.15×10⁶), whether it is braked now and how hard (m/s²). `false` when neither. |
| `bool TryGetEntryWear(Entity grid, out double tolerance, out double wornUpTo, out double worn)` | A grid's wear this entry: its tolerance (J/kg; doubled by heavy armour in front), the heat it is worn up to, and the fraction of its forward layer worn. |
| `bool TryGetEntryGlow(Entity grid, out Vector3 airWorld, out float strength)` | The plasma orbital reports to the Aerodynamics Mod for a grid: the air past it (world, m/s) and how hard it burns (0..1). |
| `bool TryGetEntryPrediction(out string body, out double enterTime, out double peakDeceleration, out double peakHeat, out double bottomTime, out double speedAtBottom)` | The local player's next pass through a band, predicted. |

### Settings

| Member | What it gives |
|---|---|
| `int GetSettings(List<string> names)` | Every setting's name. |
| `bool TryGetSettingInfo(string name, out string type, out double defaultValue, out double min, out double max, out string scope, out bool needsReload, out string description)` | Type (`bool`, `int`, `float`), default, range (a bool is 0..1), scope (`world` or `client`), whether it applies only after a world reload, and what it does. |
| `bool TryGetSetting(string name, out double value)` | The value now (a bool as 0 / 1). |
| `bool SetSetting(string name, double value, out string why)` | Change it, effective at once (or after a world reload, when so marked) and saved. `false` with a reason: no such setting, out of range, a rule between settings, or a world setting where the server does not run. |
| `int GetSettingsVersion()` | Bumped on every change (the API, a settings file, a world load). |

## Settings

Two scopes, the same design as the Aerodynamics Mod's settings:

- **World** (the same for every player): saved **with the world**, next to the orbital state on the server planets (`ServerPlanetBeacon`'s `EntityNameSessionComponentObjectBuilder`, key `settings:`). Only values off their default are saved. A host or dedicated server can force values in `%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.world.cfg`: uncommented lines win over the save. The mod writes that file, all commented out, the first time.
- **Client** (this PC): `%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.client.cfg`, written with every setting and its description, and rewritten after a change through the API.

Both files are read again within ~2 s of an edit, so they can be changed while playing. The format is `name = value`, `#` for comments, `true` / `false` for switches. Settings marked **reload** are read when the solar system is built: change them, then reload the world.

| Setting | Scope | Type | Default | Range | What it does |
|---|---|---|---|---|---|
| `Orbit.PlanetDaySeconds` | world | float | −1 | −1..10⁷ | Planet day (sidereal, s): −1 the world's own sun period, 0 no spin. **Reload.** |
| `Orbit.CaptureLegacySpace` | world | bool | false | | Put the space outside every planet cell (the spawn area) on orbits too. |
| `Orbit.FictitiousMinSpeed` | world | float | 20 | 0..1000 | In a spinning planet's cell, the spin's fictitious forces apply above this speed (m/s)... |
| `Orbit.FictitiousMinAltitude` | world | float | 5000 | 0..10⁶ | ... or above this altitude (m). |
| `Entry.Enabled` | world | bool | true | | Reentry braking, heat and wear. Off: frames arrive at orbital speed. |
| `World.Asteroids` | world | bool | true | | Asteroid belts on orbits. **Reload.** |
| `World.Encounters` | world | bool | true | | Encounter sites on orbits. **Reload.** |
| `World.RingRocks` | world | bool | true | | Rocks in planetary rings. **Reload.** |
| `World.RingRocksPerRing` | world | int | 120 | 0..1000 | Rocks per ring. |
| `Proxy.Mode` | client | int | 0 | 0..3 | 0 a proxy globe outside the planet's frame, 1 always the proxy, 2 never, 3 alternate. |
| `Proxy.HideRealPlanets` | client | bool | true | | Hide the real planet where the proxy stands in. |
| `Proxy.HideAtmosphere` | client | bool | true | | Hide its atmosphere and clouds with it. |
| `Proxy.ClampDistance` | client | float | 0 | 0..10⁹ | Draw proxies no further than this (m), keeping their angular size. 0: true distance. |
| `Proxy.FrameEnterFactor` / `Proxy.FrameExitFactor` | client | float | 1 / 1.1 | 0.1..10 | Outside the frames model: the real planet shows inside gravity reach × enter, hides past × exit. Enter must stay under exit. |
| `Proxy.MinFrameRadii` | client | float | 1.5 | 1..20 | The smallest frame sphere, in planet radii. |
| `Proxy.AlternateSeconds` | client | float | 10 | 1..600 | Mode 3's switching period. |
| `Hud.ShowOrbit` | client | bool | true | | The predicted orbit line and the orbit card. |
| `Hud.OrbitLineThickness` | client | float | 0.0025 | 0.0005..0.05 | The orbit line's width per metre of distance. |
| `Map.BodyMarkers` / `Map.FrameMarkers` | client | bool | true | | Planet and moon markers; markers on other frames. |
| `Map.LineWidth` | client | float | 0.003 | 0.0005..0.05 | The map's orbit lines. |
| `Map.ZoomOutFactor` | client | float | 6 | 1..100 | How far the map zooms out, × the game's own limit. |
| `Map.ManeuverEditor` | client | bool | true | | Maneuver nodes on the map. |
| `Map.OrbitPatches` | client | int | 3 | 0..10 | Conic patches drawn ahead. |

**Multiplayer:** SE2 2.4 runs its server in the same process as the client (its networking is a mock), so world values are the server's. A process that has only seen a client tick refuses world sets.

## Reaching orbital from a script

SE2 2.4 has the engine side of programmable blocks (`InGameScriptingComponent`, compiled against the same whitelist as mods), but no vanilla block uses it, so there is no player script yet. A script, once there is one, could reach this API the same way a mod does: by reflection, if its whitelist keeps `System.Reflection`. SE2 has no mod-to-mod message channel and no mod settings screen; this API and the settings files are the way in.

## The Aerodynamics Mod

Orbital registers an **entry source** with the Aerodynamics Mod (`AeroMod.AeroApi.RegisterEntrySource`, see that mod's `docs/API.md`) so that its plasma shows during reentry on rails. `tools/check_contract.sh`, run by the fast gate, checks that contract against the aero mod's source.
