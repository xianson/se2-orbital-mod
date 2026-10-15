# Orbital Mod API

The Orbital Mod exposes its data to other mods through one class, `OrbitalMod.OrbitalApi` (`Scripts/Simulation/Frames/OrbitalApi.cs`), and its settings through the same class (`Scripts/Simulation/Frames/OrbitalSettings.cs`).

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `OrbitalMod.OrbitalApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Find each member by its name **and its parameter types** (`GetMethod(name, Type[])`; an `out` parameter is `typeof(T).MakeByRefType()`), and bind it once as a **typed delegate** (`Delegate.CreateDelegate(typeof(YourDelegate), method, false)`: `null` when the signature does not match). A call is then an ordinary call: no argument array, no boxing, no `MethodInfo.Invoke` per call. Look again when the number of loaded assemblies changes, if the orbital mod may load after yours.

Every signature uses only .NET and engine types (`Entity`, `Vector3`, `Vector3D`, `List<T>`), never an orbital type.

```csharp
delegate long FrameOfFn(Entity grid);
delegate bool OrbitSummaryFn(long frameId, out double periapsisAltitude, out double apoapsisAltitude, out double period, out double inclination);
static FrameOfFn _frameOf;
static OrbitSummaryFn _orbit;
static int _asmCount = -1;

static bool OrbitOf(Entity grid, out double periapsisAlt, out double apoapsisAlt)
{
    periapsisAlt = apoapsisAlt = 0;
    var asms = AppDomain.CurrentDomain.GetAssemblies();
    if (asms.Length != _asmCount)
    {
        _asmCount = asms.Length; _frameOf = null; _orbit = null;
        foreach (var a in asms)
        {
            var t = a.GetType("OrbitalMod.OrbitalApi");
            if (t == null) continue;
            if ((int)t.GetField("Version").GetRawConstantValue() < 1) break;
            var d = typeof(double).MakeByRefType();
            var frameOf = t.GetMethod("GetFrameOf", new[] { typeof(Entity) });
            var orbit = t.GetMethod("TryGetFrameOrbitSummary", new[] { typeof(long), d, d, d, d });
            if (frameOf == null || orbit == null) break;
            _frameOf = (FrameOfFn)Delegate.CreateDelegate(typeof(FrameOfFn), frameOf, false);
            _orbit = (OrbitSummaryFn)Delegate.CreateDelegate(typeof(OrbitSummaryFn), orbit, false);
            break;
        }
    }
    if (_frameOf == null || _orbit == null) return false;   // (the orbital mod is not installed, or too old)
    long frame = _frameOf(grid);
    if (frame < 0) return false;                             // (not on an orbit)
    return _orbit(frame, out periapsisAlt, out apoapsisAlt, out _, out _);
}
```

## Conventions

- **Frames.** The mod keeps everything on orbit in a *frame*: a group of grids and players sharing one Keplerian orbit about a body, simulated around a berth in the game world. Frame ids are runtime numbers, stable for the session; a save keeps the frames, but their ids may change.
- **States** are relative to the frame's (or body's) parent body, in the solar system's inertial axes: m, m/s.
- **Time** is the universe clock (`GetTime`, s), which runs faster in time warp. Pass `NaN` for "now".
- **Where it answers:** where the frame registry lives, the machine that runs the server (single player, a host). The player calls (`GetPlayerFrame`, `GetPlayerBody`, `TryGetEntryPrediction`) answer where that player's client runs. SE2 2.4 runs both in one process.
- **Grids:** pass the grid's server copy or, in the same process, its client copy (matched to the nearest server grid within 20 m). `TryGetGridEdits` is the exception: the server copy only (it is about the server's physics).
- **Threads:** any. Frame answers are copied under the frame lock. **That lock is held for a whole server frame step** (milliseconds; longer in a hitch), so the members that take it (`GetFrames`, `TryGetFrame`, `GetFrameMembers`, `TryGetFrameOrbit`, `TryGetFrameOrbitSummary`, `TryGetFrameState`, `GetFrameOf`) can **block the caller that long**: never call them from a per-frame render path. The per-grid members (`GetMemberId`, `TryGetGridEdits`, the entry state and glow) take only small locks of their own.
- **Locks (the lock rule):** never call this API while holding a lock of your own, and orbital never calls another mod while holding one of its own: its calls into the Aerodynamics Mod (the entry-source hook, the ballistic coefficients, the border's air, `NoteMoved`) are collected under the frame lock and made after it. A call that waits on the other mod's lock while the other waits on yours is a deadlock neither mod can see.
- **Stability:** members are only **added** within a version. Changing an existing member bumps `Version`.
- **Never overloaded:** no two public members share a name. A name-only lookup (`GetMethod(name)`) throws `AmbiguousMatchException` the day an overload appears, so a new shape gets a **new name** (`TryGetEntryState2` beside `TryGetEntryState`), and the old member stays. Look members up with their parameter types anyway. `tools/check_contract.sh` fails the build on an overload in either mod's API.

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
| `bool TryGetGridEdits(Entity grid, out Vector3D velocityWrittenSum, out long moveStamp)` | *(added 2026-10-14)* What orbital has **written into the grid's physics**, for mods that balance the physics' own velocity change (the Aerodynamics Mod's contact loads: m·Δv less the known forces). `velocityWrittenSum`: the running sum (world axes, m/s, double) of every velocity change orbital wrote into its rigid body, new − old at each write: a frame anchor's motion drained into the rails every tick, warp's rescale and steering, the N² differential gravity in a frame, a spinning planet's fictitious forces, the HighSpeed exit, arrivals and moves. Take its **change** between two of your reads and subtract it (× mass) from the velocity's change: none of it is a force. `moveStamp`: changes on every teleport or turn in place orbital makes (it is never reused, so a record made anew also reads as changed): a changed stamp means that step is no load; re-baseline. When: the writes land after the physics step (a deferred move's before it), so between two reads made before the step. Server copy only; `false`: orbital never wrote into it. |
| `long GetFrameOf(Entity grid)` | The frame the grid is in, or −1: not on an orbit (inside a planet's cell, or not framed). |
| `long GetPlayerFrame()` | The local player's frame, or −1. |
| `string GetPlayerBody()` | The planet whose cell the local player is in, or `""`. |

### Reentry

A frame on orbit that comes down into a planet's cell is first braked through the planet's thin outer envelope (the *band*), so that it arrives at or under the world's speed cap. It heats up on the way, and a grid past its tolerance loses some of its forward layer.

| Member | What it gives |
|---|---|
| `double GetSpeedCap()` | The **arrival** cap (m/s; 990, a hair under the physics' cap): what reentry slows a frame to and an arrival is capped to. Not the physics' own limit. |
| `double GetPhysicsSpeedCap()` | *(added 2026-10-14)* The **physics'** speed cap (m/s): the engine's `VelocityLimitProvider.LinearVelocityLimit` (the world definition's `Physics.MaximumSpeedLinear`, which the physics world clamps every body to; 1000 with this mod's injector), from the server session. |
| `bool TryGetEntryBand(string body, out double bottomRadius, out double topRadius)` | A body's band: from the planet cell's border (bottom) to the entry interface (top), radii in m. `false` without an atmosphere. |
| `bool TryGetReentry(long frameId, out double heat, out bool braking, out double deceleration)` | A frame's heat (J/kg; the unshielded tolerance is 1.15×10⁶), whether it is braked now and how hard (m/s²). `false` when neither. |
| `bool TryGetEntryWear(Entity grid, out double tolerance, out double wornUpTo, out double worn)` | A grid's wear this entry: its tolerance (J/kg; doubled by heavy armour in front), the heat it is worn up to, and the fraction of its forward layer worn. |
| `bool TryGetEntryState(Entity grid, out Vector3 trueAirWorld, out float density)` | *(added 2026-10-14)* A grid's re-entry as the air sees it: its **true** air velocity (world, m/s; its real speed through the band, not the physics' capped one) and the air density there (kg/m³). For heating models; the Aerodynamics Mod heats its hull from these. "Air velocity" is the **travel**: the frame's velocity less the planet's turning air at that point, v − ω×r, the direction the grid moves **into** the air (the wind it feels is the opposite), as the Aerodynamics Mod's "travel". Published on **every tick the frame is braked** (not only while the plasma shows), then faded as the glow: held 0.25 s, the density down to 0 over 1.5 s. False: not in a re-entry now. |
| `bool TryGetEntryState2(Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingAccelWorld)` | *(added 2026-10-14)* The fuller state: the travel (as above), the density, the air's **temperature** there (K: the band's, which is its border's, sampled from the Aerodynamics Mod's atmosphere; the band's envelope is exponential in height, an isothermal one; 0: unknown, use your own model at the density) and the frame's **braking acceleration** (world, m/s²: against the travel, this tick's peak deceleration from drag and the cap rule). The rails apply no physical force, so the braking is what the ship's structure would feel: the Aerodynamics Mod loads its structure with it (`Structure.EntryLoads`). Density and braking fade as the glow does. False: not in a re-entry now. |
| `bool TryGetEntryGlow(Entity grid, out Vector3 airWorld, out float strength)` | The plasma orbital reports to the Aerodynamics Mod for a grid: the air past it (world, m/s: the travel, as above) and how hard it burns (0..1). Only while the plasma shows (braking over 1 m/s², or the cap rule). |
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

The two mods compile apart and meet only by reflection. Every link, both ways (`tools/check_contract.sh`, run by the fast gate, checks each name and parameter list against the other mod's source, that every lookup passes its parameter types, and that neither API overloads a name):

**Orbital → Aero** (`AeroMod.AeroApi`, see that mod's `docs/API.md`; all made outside the frame lock):

| Member | Who | What for |
|---|---|---|
| `RegisterEntrySource(string, Delegate)`, `HasEntrySource(string, Delegate)` | `EntryHost` (before the locked step, ~every 10 s) | The **entry source**: the plasma of a railed re-entry (`TryGetEntryGlow`'s values). Contract number checked: aero's `EntrySourceContract` must equal `EntryHost.AeroEntryContract`. |
| `PredictHullForces(Entity, Vector3, float, float, out Vector3, out Vector3, out float)`, `RequestForceTable(Entity)` | `EntryHost.MeasureBeta` (after the locked step) | Aerobraking: each railed frame's ballistic coefficient from aero's force tables. |
| `TryGetAtmosphereAt(Vector3D, out float ×4)` | `EntryHost.SampleBorder` (after the locked step) | The air at a planet's border: the band's density (the rails' drag meets the physics' at the handover) and temperature (`TryGetEntryState2`). |
| `NoteMoved(Entity)` | `ServerFrames.AeroNoteMoved` (moves made under the lock: told right after it) | The push: a grid was teleported or turned; its next physics contacts are no loads. The pull side is `TryGetGridEdits`' stamp. |
| `SetFocusGrid`, `TryGetFlight`, `TryGetForces`, `TryGetStructure`, `TryGetStall`, `TryGetLoac`, `TryDesign`, `GetTopSpeed`, `GetTopSpeedAt`, `GetThrustAlong`, `TryGetCenterOfMass`, `GetMass`, `GetDesignPressures`, `GetControlRatio` | `BuildHints` (client) | The build hints and the seated flight gauges (STRUCT, HEAT, STALL, LOSS OF CONTROL). |
| `TryGetGroundHeight`, `GetTopSpeedAt` | `ServerFrames.DoFlyTest` (dev) | The fly test's altitude and cruise speed. |
| `TryGetFlight`, `TryGetForces`, `TryGetLift`, `TryGetGroundHeight`, `TryGetEntry`, `TryGetAtmosphere`, `GetFxConsistency`; `FlightFixture.Build`, `PhysicsHack.GetGravityDirection`; static fields by name (`aeroset`) | `GridGen`, `DevReentry`, `DevHarness` (dev) | Harness readouts and switches. |

**Aero → Orbital** (`OrbitalMod.OrbitalApi`, this API):

| Member | Who (aero) | What for |
|---|---|---|
| `TryGetEntryState2` (else `TryGetEntryState`) | `OrbitalLink`, `AeroEntryFx.EntryStateAt` | A railed re-entry's true travel, density and temperature (the hull heats, weakens and scorches as in a real one) and its braking (the structure is loaded with it). |
| `TryGetGridEdits` | `OrbitalLink` → `StructureContacts` | The contact loads' momentum balance: orbital's velocity writes and moves taken out, so a drained frame anchor, a warp switch or the fictitious forces never read as loads on the plates and wheels. |
