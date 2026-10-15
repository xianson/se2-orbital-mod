# Orbital Mod API

The Orbital Mod exposes its data to other mods through one class, `OrbitalMod.OrbitalApi`: a `partial` class whose version 1 members are in `Scripts/Simulation/Api/OrbitalApi.cs` and whose version 2 members are in `Scripts/Simulation/Api/OrbitalApi.<Area>.cs` (`Peek`, `World`, `Grids`, `Orbits`, `Time`, `Player`, `Sites`), with their engine-free parts in `Scripts/Simulation/Api/Core/` (tested offline: `Tests/ApiTests`). Its settings go through the same class (`Scripts/Simulation/Host/OrbitalSettings.cs`).

**At a glance (version 2):** the solar system (bodies, their states, orbits, spin and where their planets sit in the world), the frames (lock-free `Peek*` reads), any grid's own orbit and status, the conversion between world positions and body-relative states, orbit math on any element set (propagate, convert, apsides and times, sampled paths into your buffer), time warp (levels, ask for a factor, a stop time), the local player's plan (orbit, maneuver nodes, the next burn, attitude hold, target, closest approach), sites and contacts, reentry, and the settings.

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `OrbitalMod.OrbitalApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Find each member by its name **and its parameter types** (`GetMethod(name, Type[])`; an `out` parameter is `typeof(T).MakeByRefType()`), and bind it once as a **typed delegate** (`Delegate.CreateDelegate(typeof(YourDelegate), method, false)`: `null` when the signature does not match). A call is then an ordinary call: no argument array, no boxing, no `MethodInfo.Invoke` per call. Look again when the number of loaded assemblies changes, if the orbital mod may load after yours.

Every signature uses only .NET and engine types (`bool`, `int`, `long`, `float`, `double`, `string`, `Vector3`, `Vector3D`, `Entity`, `List<string>`, `List<long>`, `double[]`, `long[]`), never an orbital type. An orbit travels as **eight doubles** (see *Orbits* below). `Tests/ApiTests` checks this from the source, with no overloads and every member documented here.

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

- **Frames.** The mod keeps everything on orbit in a *frame*: a group of grids and players sharing one Keplerian orbit about a body, simulated around a *berth* in the game world (a sphere of world space, `TryPeekFrameBerth`). Frame ids are runtime numbers, stable for the session; a save keeps the frames, but their ids may change.
- **Places in the game world.** A world position is in one of: a planet's **cell** (the real voxel planet where the world put it, `TryGetBodyCell`: planet space, the game's own physics), a frame's **berth** (on rails), or neither (the spawn space). `TryWorldToBody` / `TryBodyToWorld` convert between the world and the frame below.
- **States** (positions, velocities, orbits) are **relative to a body, in the solar system's inertial axes**: m, m/s. Which body is always said: a frame's or a grid's is the one it orbits; a body's own (`TryGetBodyState`) is about its parent; `TryGetBodySystemState` is about the star.
  - A **berth's** axes are the inertial ones: a point p in it is at *frame position + (p − berth centre)* about the frame's body.
  - A **planet's cell turns with the planet** (its rotating chart, `TryGetBodyRotation`): a cell offset maps to inertial axes through the planet's rotation, and a velocity measured in the cell gains ω × r. The world vectors a grid feels in a cell (its physics velocity, a direction on the HUD) are in the cell's axes.
  - **World** vectors (marked *world*) are in world axes: the entry state's travel and braking, a burn's direction, `TryGetGridEdits`' sum.
- **Orbits** travel as eight doubles, in this order: semi-major axis *a* (m; negative when hyperbolic), eccentricity *e*, inclination *i*, longitude of the ascending node *Ω*, argument of periapsis *ω*, true anomaly *ν* (rad) **at the epoch**, the epoch (universe time, s), and the body's gravitational parameter μ (m³/s²). Any member that gives an orbit gives these; the orbit math members take them.
- **Time** is the universe clock (`GetTime`, s), which runs faster in time warp; the game's physics never does (warp runs only the rails). Pass `NaN` for "now" where a member takes a time.
- **Where it answers:** where the frame registry lives, the machine that runs the server (single player, a host). The player calls (`GetPlayerFrame`, `GetPlayerBody`, `TryGetEntryPrediction`, and version 2's *Player* members) answer where that player's client runs. SE2 2.4 runs both in one process. A remote multiplayer client gets nothing yet (as with the Aerodynamics Mod).
- **Grids:** pass the grid's server copy or, in the same process, its client copy (matched to the nearest server grid within 20 m). `TryGetGridEdits` is the exception: the server copy only (it is about the server's physics). Pass a live grid.
- **Threads:** any. Each member's table row says what it takes:
  - **frame lock**: copied under the frame lock, one consistent tick. **That lock is held for a whole server frame step** (milliseconds; longer in a hitch), so these members **can block the caller that long**: never call them from a per-frame render path. Version 1's `GetFrames`, `TryGetFrame`, `GetFrameMembers`, `TryGetFrameOrbit`, `TryGetFrameOrbitSummary`, `TryGetFrameState`, `GetFrameOf`; version 2's `TryGetPlayerOrbit`, `SetTarget`, `TryGetTargetState`, `TryGetClosestApproach`.
  - **snapshot**: version 2's `Peek*` members and everything built on them (the world conversion, the grid members): read **without** the frame lock from a copy of the frames made on the last server tick (at most a tick or two old), never blocking, never allocating. Use these on a per-frame or per-tick path.
  - **none / small**: no lock, or only a small lock of its own (`GetMemberId`, `TryGetGridEdits`, the entry state and glow, the node list).
- **Allocation:** no member allocates on its own, except the ones marked (a planning call such as `TryGetClosestApproach`); lists and arrays you pass are filled, never kept. Arrays you pass are filled as far as they go and the member returns the full count: grow and ask again.
- **Locks (the lock rule):** never call this API while holding a lock of your own, and orbital never calls another mod while holding one of its own: its calls into the Aerodynamics Mod (the entry-source hook, the ballistic coefficients, the border's air, `NoteMoved`) are collected under the frame lock and made after it. A call that waits on the other mod's lock while the other waits on yours is a deadlock neither mod can see.
- **Stability:** members are only **added**; an existing member never changes its signature or meaning. `Version` goes up with each set of additions (each version's section below lists what it added): check `Version >= n` for the members of version n.
- **Never overloaded:** no two public members share a name. A name-only lookup (`GetMethod(name)`) throws `AmbiguousMatchException` the day an overload appears, so a new shape gets a **new name** (`TryGetEntryState2` beside `TryGetEntryState`), and the old member stays. Look members up with their parameter types anyway. `tools/check_contract.sh` fails the build on an overload in either mod's API, and `Tests/ApiTests` checks every partial file of this one.

## Version 1

### The solar system

Threads: any; no lock (the bodies are fixed once the system is built).

| Member | What it gives |
|---|---|
| `const int Version` | The API contract: 1, then 2 since version 2 (check `>= 1` for these members). |
| `bool IsReady()` | The solar system is built. `false` early in a session. |
| `double GetTime()` / `double GetTimescale()` | The universe clock (s) and the time warp factor (1: none). |
| `int GetBodies(List<string> names)` | The bodies: the star first, then planets and moons. |
| `bool TryGetBody(string name, out string parent, out double mu, out double radius, out double soiRadius, out bool hasAtmosphere, out double atmosphereHeight, out double rotationPeriod)` | A body: parent (`""` for the star), GM (m³/s²), radius (m), sphere of influence (m; infinity for the star), atmosphere and its height (m), sidereal day (s; 0: none). |
| `bool TryGetBodyState(string name, double time, out Vector3D position, out Vector3D velocity)` | A body's state relative to its parent. `false` for the star. |

### Frames

Threads: any; every member here takes the **frame lock** (see *Threads*). Version 2's `Peek*` members give the same without it.

| Member | What it gives |
|---|---|
| `int GetFrames(List<long> ids)` | Every frame's id. |
| `bool TryGetFrame(long frameId, out string body, out int memberCount, out bool isEncounter)` | The body it orbits, its member count, and whether it is an encounter site. |
| `int GetFrameMembers(long frameId, List<long> memberIds)` | Its members: grid member ids (from 1 000 000 000) and player ids. |
| `bool TryGetFrameOrbit(long frameId, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | The orbit's Keplerian elements: a (m; negative when hyperbolic), e, i, Ω, ω, ν (rad) at the epoch (s), and the body's GM. |
| `bool TryGetFrameOrbitSummary(long frameId, out double periapsisAltitude, out double apoapsisAltitude, out double period, out double inclination)` | Periapsis and apoapsis altitude over the surface (m), period (s), inclination (rad). Apoapsis and period are infinity when hyperbolic. |
| `bool TryGetFrameState(long frameId, double time, out Vector3D position, out Vector3D velocity)` | Where the frame is on its orbit, propagated on rails. |

### Grids and the player

Threads: any. `GetFrameOf` takes the frame lock (version 2: `PeekFrameOf` does not); the others take small locks of their own or none.

| Member | What it gives |
|---|---|
| `long GetMemberId(Entity grid)` | The grid's member id (what frames list), or −1. |
| `bool TryGetGridEdits(Entity grid, out Vector3D velocityWrittenSum, out long moveStamp)` | *(added 2026-10-14)* What orbital has **written into the grid's physics**, for mods that balance the physics' own velocity change (the Aerodynamics Mod's contact loads: m·Δv less the known forces). `velocityWrittenSum`: the running sum (world axes, m/s, double) of every velocity change orbital wrote into its rigid body, new − old at each write: a frame anchor's motion drained into the rails every tick, warp's rescale and steering, the N² differential gravity in a frame, a spinning planet's fictitious forces, the HighSpeed exit, arrivals and moves. Take its **change** between two of your reads and subtract it (× mass) from the velocity's change: none of it is a force. `moveStamp`: changes on every teleport or turn in place orbital makes (it is never reused, so a record made anew also reads as changed): a changed stamp means that step is no load; re-baseline. When: the writes land after the physics step (a deferred move's before it), so between two reads made before the step. Server copy only; `false`: orbital never wrote into it. |
| `long GetFrameOf(Entity grid)` | The frame the grid is in, or −1: not on an orbit (inside a planet's cell, or not framed). |
| `long GetPlayerFrame()` | The local player's frame, or −1. |
| `string GetPlayerBody()` | The planet whose cell the local player is in, or `""`. |

### Reentry

Threads: any. The entry members take only small locks of their own (`TryGetEntryPrediction`: the client's last prediction, no lock).

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

Threads: any; the settings' own lock.

| Member | What it gives |
|---|---|
| `int GetSettings(List<string> names)` | Every setting's name. |
| `bool TryGetSettingInfo(string name, out string type, out double defaultValue, out double min, out double max, out string scope, out bool needsReload, out string description)` | Type (`bool`, `int`, `float`), default, range (a bool is 0..1), scope (`world` or `client`), whether it applies only after a world reload, and what it does. |
| `bool TryGetSetting(string name, out double value)` | The value now (a bool as 0 / 1). |
| `bool SetSetting(string name, double value, out string why)` | Change it, effective at once (or after a world reload, when so marked) and saved. `false` with a reason: no such setting, out of range, a rule between settings, or a world setting where the server does not run. |
| `int GetSettingsVersion()` | Bumped on every change (the API, a settings file, a world load). |

## Version 2

*(2026-10-15)* Everything a mod or a script would reasonably want from orbital, added beside version 1 (nothing of version 1 changed). Check `Version >= 2`. The members are in `Scripts/Simulation/Api/OrbitalApi.<Area>.cs`, each marked `(v2)` in its summary.

### Peek: the frames without the frame lock

`OrbitalApi.Peek.cs`, over `Api/Core/FrameTable.cs`. A copy of every frame (id, body, elements, berth, anchor, flags, members), refreshed by the first read after the universe clock moved (or a quarter second passed) **if the frame lock is free at that moment** (no wait); while a server step holds it, readers get the last copy. So an answer is at most a tick or two old, and a read never blocks and never allocates. States are still propagated to the time you ask from the copied elements, which is exact on rails between burns.

Threads: any. Lock: none (snapshot). Allocation: none.

| Member | What it gives |
|---|---|
| `int PeekFrames(long[] ids)` | Every frame's id, as many as fit. Returns the number of frames (more than `ids.Length`: grow it and ask again). |
| `int PeekFrameSetVersion()` | Changes whenever a frame is made or dissolved, or any frame's members change. Cache what you built from the frames until it moves. |
| `bool TryPeekFrame(long frameId, out string body, out int memberCount, out long anchorId, out bool isEncounter)` | The body it orbits, its member count, its anchor (the member its orbit is measured on; 0: none yet), whether it is an encounter's. |
| `int PeekFrameMembers(long frameId, long[] memberIds)` | Its members (grid member ids from 1 000 000 000, and player ids), as many as fit. Returns the member count, or −1: no such frame. |
| `bool TryPeekFrameOrbit(long frameId, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | The frame's orbit (the eight numbers; *Orbits*), about its body. |
| `bool TryPeekFrameState(long frameId, double time, out Vector3D position, out Vector3D velocity)` | The frame's state about its body (m, m/s, inertial) at a universe time (`NaN`: now). |
| `bool TryPeekFrameBerth(long frameId, out Vector3D worldCenter, out double radius)` | Where its berth is in the world: centre (world, m) and radius (m, 10 km). The berth's axes are the inertial ones. |
| `long PeekFrameOfMember(long memberId)` | The frame a member (a grid member id from `GetMemberId`, or a player id) is in, or −1. |
| `long PeekFrameOf(Entity grid)` | `GetFrameOf` without the frame lock. |

### The bodies and the game world

`OrbitalApi.World.cs`. Threads: any. Lock: none (the berths from the snapshot). Allocation: none.

| Member | What it gives |
|---|---|
| `bool TryGetBodySystemState(string name, double time, out Vector3D position, out Vector3D velocity)` | A body's state about the **star** (m, m/s, inertial) at a universe time (`NaN`: now). The star itself: zero. |
| `bool TryGetBodyOrbit(string name, double time, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | A body's orbit about its parent, osculating at that time (it is the epoch); μ is the **parent's**. `false` for the star. |
| `bool TryGetBodyRotation(string name, double time, out Vector3D axis, out double angle, out double angularVelocity)` | A body's spin: its axis (unit, inertial: the north pole), its angle (rad, [0, 2π)) and rate (rad/s). Planets spin on the **game's** clock (shared by every player, not sped up by warp): the rate is per game second. `false`: it does not spin. |
| `bool TryGetBodyCell(string name, out Vector3D worldCenter, out double radius)` | Where a planet or moon sits in the game world: its cell's centre (world, m: the voxel planet's centre) and the cell's radius (m: planet space, the game's physics; beyond it, the rails). `false`: no cell (the star). |
| `bool TryWorldToBody(Vector3D worldPosition, Vector3D worldVelocity, out string body, out long frameId, out Vector3D position, out Vector3D velocity)` | A world point (and a velocity measured there) as the solar system has it **now**: the body it is about and its state about that body (m, m/s, inertial). In a cell: that planet, through its rotating chart (`frameId` −1). In a berth: the frame's body (`frameId`: that frame). `false`: the spawn space, or not built. In warp a velocity measured in a berth is the physics' (N × the true relative one): pass the true one if you have it (`TryGetGridState` does). |
| `bool TryBodyToWorld(string body, long frameId, Vector3D position, Vector3D velocity, out Vector3D worldPosition, out Vector3D worldVelocity)` | The inverse, **now**. `frameId` −1: into that body's own cell (through its chart; the velocity is the one the game's physics would have there). `frameId` ≥ 0: into that frame's berth (a state about any body is carried through the star). A point far outside is placed all the same, but only means something inside. |

### A grid's own orbit and status

`OrbitalApi.Grids.cs`. A grid's state is read from the grid you pass (its world transform and its rigid body's velocity) and placed as `TryWorldToBody` places it: on rails, its frame's orbit plus its place **and motion** in the berth (in warp: the true relative velocity the mod's steering keeps); in a planet's cell, through the chart. A grid drifting in its berth has an orbit a little off its frame's, as it truly does.

Threads: any (best the one that updates the grid: another may read a transform mid-step). Lock: none (snapshot; `GetMemberId`'s small lock for a client copy). Allocation: none.

| Member | What it gives |
|---|---|
| `bool TryGetGridState(Entity grid, out string body, out long frameId, out Vector3D position, out Vector3D velocity)` | The grid's state now about the body it orbits (m, m/s, inertial), the body, and its frame (−1: in a planet's cell). `false`: in neither a cell nor a berth. |
| `bool TryGetGridOrbit(Entity grid, out string body, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | Its own osculating orbit (epoch: now). On the ground it is still the conic its state gives (periapsis inside the planet). |
| `bool TryGetGridOrbitSummary(Entity grid, out string body, out double periapsisAltitude, out double apoapsisAltitude, out double period, out double timeToPeriapsis, out double timeToApoapsis)` | In plain numbers: periapsis and apoapsis **altitude** over the surface (m; apoapsis +∞ when hyperbolic), period (s; +∞ hyperbolic), time to the next periapsis and apoapsis (s from now; hyperbolic: periapsis negative once past, apoapsis NaN). |
| `bool TryGetGridStatus(Entity grid, out long memberId, out long frameId, out bool isFrameAnchor, out bool holdingStation, out bool joinedToAnchor, out bool isEncounter)` | What orbital knows of it: member id, frame (−1), whether it anchors its frame, holds station (dampeners and thrust cancelling the pull: no relative force), is joined to the anchor (docked, a sub-grid: moves as one with it), is an encounter's (NPC-owned or spawned by one). From the views (every 5 server ticks). `false`: not a grid orbital tracks. |

### Orbits: the conic math on any element set

`OrbitalApi.Orbits.cs`, over `Api/Core/OrbitQueries.cs` (the code the rails fly on). An orbit is passed as its eight numbers (*Conventions*); positions are about its body, inertial. `false` (or 0): a set the math cannot use (non-finite, μ ≤ 0, e < 0, exactly e = 1, or *a*'s sign not the regime's).

Threads: any. Lock: none. Allocation: none (your buffer is filled).

| Member | What it gives |
|---|---|
| `bool PropagateOrbit(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis, double trueAnomaly, double epoch, double mu, double time, out Vector3D position, out Vector3D velocity)` | Where the orbit is at a universe time (two-body: no sphere-of-influence change, no burn). |
| `bool TryStateToOrbit(Vector3D position, Vector3D velocity, double mu, double time, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly)` | The osculating orbit of a state (the epoch is `time`). A radial state (straight up or down, at rest) gets a needle-thin ellipse, never NaN. |
| `bool TryGetOrbitEvents(double semiMajorAxis, double eccentricity, double trueAnomaly, double mu, out double periapsisRadius, out double apoapsisRadius, out double period, out double timeToPeriapsis, out double timeToApoapsis)` | Apsides as **radii** (m from the centre; less the body's radius for altitude; apoapsis +∞ hyperbolic), period (s; +∞), and the time **from the epoch** to the next periapsis and apoapsis (elliptic: in [0, period); hyperbolic: periapsis negative once past, apoapsis NaN). |
| `int SampleOrbit(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis, double trueAnomaly, double epoch, double mu, double t0, double t1, double[] xyz)` | The path evenly in **time** over [t0, t1] into `xyz` (x, y, z per point: `xyz.Length / 3` points, the first at t0, the last at t1). Returns the points written (0: unusable, or room for fewer than 2). One conic. |
| `int SampleOrbitShape(double semiMajorAxis, double eccentricity, double inclination, double raan, double argPeriapsis, double mu, double maxRadius, double[] xyz)` | The orbit's **shape**, as the map draws it: an ellipse evenly in eccentric anomaly, one loop from periapsis (close it yourself); a hyperbola out to `maxRadius` (m; 0 or +∞: a fixed wide arc) on both legs. |

### Time warp

`OrbitalApi.Time.cs`. Warp runs only the celestial layer (the rails, the bodies, `GetTime`), never the game's physics. It needs the local player on rails, and drops to ×1 by itself at an arrival's shell crossing, ahead of a maneuver burn, a rendezvous, an entry, a sphere-of-influence change, the star's heat, or the stop time asked for. One clock for the session.

Threads: any (the clock reads the factor once per tick). Lock: none (`RequestTimescale`: the warp limit's small lock). Allocation: none.

| Member | What it gives |
|---|---|
| `int GetWarpLevels(double[] levels)` | The factors warp steps through (1 first), as many as fit. Returns how many. |
| `bool CanWarp()` | Warp can go above ×1 now (built, and the player on rails). |
| `double RequestTimescale(double timescale, out string why)` | Ask for a factor: the highest level at or under it is set, held lower when a ship moving in the player's frame could not be carried (as the keys are), ×1 off rails. Returns the factor set; `why`: what held it (`""` none). The mod may drop it again at any time. |
| `double GetWarpStopTime()` | The universe time warp is asked to stop at (`NaN`: none): the map's "Warp here". |
| `bool SetWarpStopTime(double time)` | Ask warp to drop to ×1 at that time (`NaN`: clear). **Only while warping**: the clock forgets the stop on any tick at ×1, so raise the warp first. `false`: not in the future. |

### The local player's plan

`OrbitalApi.Player.cs`. What the map and the HUD show, for a mod that shows, checks or flies it. Client-side (SE2 2.4: the same process as the server).

Threads: any. Lock: as marked. Allocation: none, except `TryGetClosestApproach`.

| Member | Lock | What it gives |
|---|---|---|
| `bool TryGetPlayerOrbit(out string body, out double semiMajorAxis, out double eccentricity, out double inclination, out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)` | frame lock | The player's orbit as the planner flies from it: their frame's (their own in it while riding a frame something else anchors), or the local one in a planet's cell. `false`: none (on the ground, a hop). |
| `int GetManeuverNodeCount()` | node list | How many nodes are planned. |
| `bool TryGetManeuverNode(int index, out double time, out double prograde, out double normal, out double radial, out bool auto)` | node list | The index-th node **in time order**: its universe time (s), its delta-v as set (prograde, normal, radial out; m/s, the orbit's axes at the node), whether it flies itself. |
| `bool AddManeuverNode(double time, double prograde, double normal, double radial, bool auto)` | node list | Plan a burn (time in the future): it chains after earlier nodes as one placed on the map, the HUD counts it down, warp stops ahead of it, and with `auto` it flies itself. |
| `int ClearManeuverNodes()` | node list | Delete every node; returns how many there were. |
| `bool TryGetNextBurn(out double startTime, out double deltaVLeft, out Vector3D directionWorld, out bool auto, out bool autoFlying)` | node list | The next burn as the HUD flies it (its last tick): start (universe time, s: half the burn before the node), delta-v left (m/s), direction (**world**, unit), whether it flies itself and whether the auto-burn is flying it now. |
| `int GetAttitudeHold()` | none | 0 off, 1 prograde, 2 retrograde, 3 normal, 4 anti-normal, 5 radial out, 6 radial in, 7 level. |
| `bool SetAttitudeHold(int mode)` | none | Hold a mode (0 lets go), applied on the next client tick as the cockpit's keys are. The pilot's own steering, leaving the seat or losing the orbit lets go. `false`: not a mode. |
| `string GetTarget()` | none | The player's target as a **target key** (`""` none): a body's name (`"Verdure"`), `"grid:"` + a member id in invariant digits (`"grid:1000000042"`), or a site's sector name (`TryGetSite`; a ring rock's too after its site is gone). |
| `bool SetTarget(string key)` | frame lock | Set it (`null` / `""`: none). `false`: no such target now. |
| `bool TryGetTargetState(string key, double time, out string body, out Vector3D position, out Vector3D velocity)` | frame lock | A target's state at a universe time (`NaN`: now): the body it is about, its state (m, m/s, inertial). A ship on rails: its frame's orbit plus its place in the berth; in planet space, the arc its motion gives. |
| `bool TryGetClosestApproach(string keyA, string keyB, double t0, double t1, out double time, out double distance, out double relativeSpeed)` | frame lock | The deepest pass of two targets over [t0, t1] (universe times), each on its present orbit: when, how close (m), relative speed then (m/s). `false`: unknown, **not about the same body now**, or no pass. Allocates: a planning call, not every frame. |

### Sites and contacts

`OrbitalApi.Sites.cs`. A **site** is a frame the mod placed on its own: a sector's encounter (its anchor kept even when empty), a ring rock, a Lagrange point's cluster. A **contact** is a grid that is not the local player's; the player **knows** of one once their sensors saw it, and **tracks** it after watching it a while.

Threads: any. Lock: none for the sites (a published snapshot), the contacts' own small locks. Allocation: none.

| Member | What it gives |
|---|---|
| `int GetSiteFrames(long[] frameIds)` | Every site's frame id, as many as fit. Returns how many sites. |
| `bool TryGetSite(long frameId, out string sector, out string host, out string label, out bool isAnchor, out bool isLagrange)` | Its sector's name (its target key), the body its home point is about, its label (a rock's or station's name; `""`), whether it is its sector's anchor, whether it sits at a Lagrange point (a co-orbital cluster, not a plain conic). |
| `bool TryGetContact(long memberId, out bool isContact, out bool known, out bool tracked)` | A grid as the player's sensors have it: a contact (not theirs)?, known?, tracked? Their own grids are known and tracked; with contacts off, everything is known. |

### Example: a per-frame orbit readout, no lock

```csharp
delegate bool GridSummaryFn(Entity grid, out string body, out double peAlt, out double apAlt, out double period, out double tPe, out double tAp);
static GridSummaryFn _summary;   // bound once, as in "How to call it"

// (binding: Version >= 2)
var d = typeof(double).MakeByRefType();
var mi = t.GetMethod("TryGetGridOrbitSummary", new[] { typeof(Entity), typeof(string).MakeByRefType(), d, d, d, d, d });
_summary = (GridSummaryFn)Delegate.CreateDelegate(typeof(GridSummaryFn), mi, false);

// every frame (never blocks, never allocates):
if (_summary != null && _summary(myGrid, out var body, out var pe, out var ap, out _, out var tAp, out _))
    hud = $"{body}: Pe {pe / 1000:F0} km  Ap {ap / 1000:F0} km  Ap in {tAp:F0} s";
```

### Example: the path ahead into your own buffer

```csharp
static readonly double[] _path = new double[3 * 128];   // kept: no garbage per call
// (TryPeekFrameOrbit and SampleOrbit bound as typed delegates)
if (peekOrbit(frame, out var a, out var e, out var i, out var raan, out var w, out var nu, out var ep, out var mu))
{
    double now = getTime();
    int n = sample(a, e, i, raan, w, nu, ep, mu, now, now + 3600, _path);   // the next hour, about the frame's body
    // to draw it in the world: each point through TryBodyToWorld(body, frame, point, velocity, ...)
}
```

## Roadmap / gaps

What the API does **not** give yet, and why. Additions only; each would come as a new version.

| Gap | Why not yet |
|---|---|
| The player's **patched-conic** trajectory (legs across spheres of influence, each node as applied) | It lives in the map's own memoised lists on the client thread; giving it safely needs a copy into caller buffers per leg (body, elements, start, end). `SampleOrbit` gives one conic. |
| Edit or delete **one** maneuver node | Only add and clear today; an edit needs a stable node id (nodes are list entries). |
| **Why** warp stopped, and the next scheduled stop | The clock decides the stops inside its step and keeps only a log line. |
| **Events** (frame made / merged / split, sphere-of-influence change, arrival, entry) | No push channel: poll `PeekFrameSetVersion`, `PeekFrameOf`, `TryGetReentry`. A registration like aero's entry sources could come. |
| A server-side **publish** of the Peek snapshot | The snapshot is refreshed by readers (`Monitor.TryEnter`, no wait). A publish at the end of the server's locked step would make it exactly one tick old always; it needs a hook in `ServerFrames`. |
| Asteroid belts and ring rocks as **bodies** (`AsteroidFrames`, `RingRocks`) | Reachable only as sites (frames) once materialised; the belts' own rock orbits are not listed. |
| Sensor blocks and the full **contact list** | `TryGetContact` answers per grid; the list itself is built with allocations on the client. |
| Station keeping's **thruster bill** (`StationKeepCharge`) and the warp power bill (`WarpBill`) | Text-only today (the harness status). |
| Body **definitions** beyond `TryGetBody` (surface gravity law, atmosphere profile, albedo, rings) | Few consumers yet; the Aerodynamics Mod owns the atmosphere. |
| Encounter **expiry** timers, NPC classification beyond `isEncounter` | Internal and short-lived. |
| **Writes** that move things (set an orbit, teleport, merge / split frames) | Deliberately not exposed: they are dev tools (the harness) and bypass the physics (no cheats). |
| A **remote** multiplayer client | Nothing is sent to clients yet (as with the Aerodynamics Mod); SE2 2.4 runs both in one process. |

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

The two mods compile apart and meet only by reflection. Every link, both ways (`tools/check_contract.sh`, run by the fast gate, checks each name and parameter list against the other mod's source, that every lookup passes its parameter types, and that neither API overloads a name - over every `AeroApi*.cs` / `OrbitalApi*.cs` partial - and that every `AeroMod` flag the dev harness sets by name exists):

**Orbital → Aero** (`AeroMod.AeroApi`, see that mod's `docs/API.md`; all made outside the frame lock):

| Member | Who | What for |
|---|---|---|
| `RegisterEntrySource(string, Delegate)`, `HasEntrySource(string, Delegate)` | `EntryHost` (before the locked step, ~every 10 s) | The **entry source**: the plasma of a railed re-entry (`TryGetEntryGlow`'s values). Contract number checked: aero's `EntrySourceContract` must equal `EntryHost.AeroEntryContract`. |
| `PredictHullForces(Entity, Vector3, float, float, out Vector3, out Vector3, out float)`, `RequestForceTable(Entity)` | `EntryHost.MeasureBeta` (after the locked step) | Aerobraking: each railed frame's ballistic coefficient from aero's force tables. |
| `TryGetAtmosphereAt(Vector3D, out float ×4)` | `EntryHost.SampleBorder` (after the locked step) | The air at a planet's border: the band's density (the rails' drag meets the physics' at the handover) and temperature (`TryGetEntryState2`). |
| `NoteMoved(Entity)` | `ServerFrames.AeroNoteMoved` (moves made under the lock: told right after it) | The push: a grid was teleported or turned; its next physics contacts are no loads. The pull side is `TryGetGridEdits`' stamp. |
| `SetFocusGrid`, `TryGetFlight`, `TryGetForces`, `TryGetStructure`, `TryGetStall`, `TryGetLoac`, `TryDesign`, `GetTopSpeed`, `GetTopSpeedAt`, `GetThrustAlong`, `TryGetCenterOfMass`, `GetMass`, `GetDesignPressures`, `GetControlRatio` | `BuildHints` (client) | The build hints and the seated flight gauges (STRUCT, HEAT, STALL, LOSS OF CONTROL). |
| `GetStructureEventSequence`, `ReadStructureEvents` (version 7) | `StructureAlerts` (client, every 0.5 s; typed delegates through `AeroLink`) | The seated ship's structural failures and burn-throughs as a toast. |
| `TryGetGroundHeight`, `GetTopSpeedAt` | `ServerFrames.DoFlyTest` (dev) | The fly test's altitude and cruise speed. |
| `TryGetFlight`, `TryGetForces`, `TryGetLift`, `TryGetGroundHeight`, `TryGetEntry`, `TryGetAtmosphere`, `GetFxConsistency`; `FlightFixture.Build`, `PhysicsHack.GetGravityDirection`; static fields by name (`aeroset`) | `GridGen`, `DevReentry`, `DevHarness` (dev) | Harness readouts and switches. |

**Aero → Orbital** (`OrbitalMod.OrbitalApi`, this API):

| Member | Who (aero) | What for |
|---|---|---|
| `TryGetEntryState2` (else `TryGetEntryState`) | `OrbitalLink` (`AeroEntryFx.EntryStateAt` through it) | A railed re-entry's true travel, density and temperature (the hull heats, weakens and scorches as in a real one) and its braking (the structure is loaded with it). |
| `TryGetGridEdits` | `OrbitalLink` → `StructureContacts` | The contact loads' momentum balance: orbital's velocity writes and moves taken out, so a drained frame anchor, a warp switch or the fictitious forces never read as loads on the plates and wheels. |

Aero binds every member above in `Api/OrbitalLink.cs`, on one rule (`Api/CrossModLink.cs`): the last loaded copy of `OrbitalApi`, bound again when this mod's assembly changes, as this mod's `AeroLink` does the other way.

**What the Aerodynamics Mod could use next** (version 2; none of it is bound yet - looked at 2026-10-15 and left, each for its reason):

| Member | What for |
|---|---|
| `PeekFrameOf` (≥ 0: on rails) | A grid in a frame's berth is in no air: skip its air model there without asking the atmosphere, without the frame lock. *Not adopted:* a railed grid is still simulated (its physics in the berth; aero's attitude law flies its gyros and thrusters, its railed entry heats and loads it), and aero's model already returns at once under its minimum density - what is left is the work a railed grid needs, so a per-grid call into orbital each tick would only add cost. |
| `TryGetBodyCell`, `TryGetBodyRotation` | The planet's centre and spin straight from orbital (the turning air is ω × r: the same law the rails' entry state uses). *Not adopted:* aero computes neither - its air comes from the atmosphere generators (their centres, `AtmosphereProbe`) and the game's cells do not turn (orbital's chart and fictitious forces do); a railed entry's turning air already arrives in `TryGetEntryState2`'s travel. |
| `TryGetGridOrbitSummary` | Orbit figures on the build hints and flight gauges (periapsis in the air: an aerobraking pass ahead). *Not adopted:* aero draws no hints or gauges of its own (this mod's `BuildHints` does, with `OrbitHud`'s orbit card beside them). |
| `GetTimescale`, `CanWarp` | Know that warp is running (the physics are not sped up, but the entry heat and the rails' loads arrive faster). *Not adopted:* warp drops to ×1 ahead of an entry, so a railed entry never runs warped. |
| `TryWorldToBody` | A world velocity as a planet-relative one, for a grid that has just left or not yet reached the air. *Not adopted:* out of the air aero has nothing to compute. |
