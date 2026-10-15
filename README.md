# Orbital Mod (SE2)

Orbital mechanics for Space Engineers 2 (SE2 2.4), the SE2 counterpart of the SE1 project at `D:\SE-Aerospace`: a
real, compact solar system, orbits on rails, time warp, KSP-style maneuver planning on the game's map, reentry, and
encounters you reach by conjunction. Separate from the Aerodynamics Mod on purpose: the two share almost nothing,
find each other by reflection, and each runs alone. Single player and listen host only (see Limits).

Docs: [how it works](docs/ARCHITECTURE.md) · [the API for other mods](docs/API.md) ·
[reflection and the whitelist](docs/REFLECTION_AND_WHITELIST.md) · design notes
([encounters](docs/design-encounter-frames.md), [maneuver nodes](docs/design-maneuver-nodes.md),
[colonization on orbits](docs/design-colonization-orbits.md)) · [the 2026-10-05 audit log](docs/AUDIT_2026-10-05.md) ·
a README in each client folder (`Scripts/Simulation/Flight`, `Hud`, `Map`, `Render`).

## Playing it

**The system.** Verdure and Kemik orbit Delfos, a red dwarf (SE-Aerospace's scale rule, then orbits a further 10x
smaller: Verdure's year is about 3 days, Kemik's about 6, a transfer about 2.3). Palatine orbits Verdure at 1,140 km;
Caligo orbits Kemik at ~1,170 km (6x its charted distance). Planets spin with the world's day. The colonization
sectors are places in this system: orbits about their planet, L1-L5 points, the belts, the ring. Worlds without
sectors get the same kind of zones built from the system (each planet's space, the belts, rings, Lagrange points).

**Getting somewhere.** A planet's own space ends at the top of its atmosphere (9.5 km up at Verdure; airless bodies a
little above their hills). Below that is ordinary flight; climb past it and your orbit goes on the rails. Every
sector is a site on its orbit; when your orbit meets it (a conjunction) you arrive there, at your true relative
position and speed, and the game spawns that sector's encounters around you. Brown-dwarf killing fields are kept
clear. Fast travel is untouched.

**Keys.**
- `.` faster time warp, `,` slower, `/` back to x1 (on rails only; x1 to x10,000). The warp bar shows the level
  (clickable on the map).
- `N` targets what is under the crosshair (in flight) or the cursor (on the map).
- `;` turns the attitude hold on and off; `Ctrl+;` picks the mode (prograde, retrograde, normal, anti-normal, radial
  out / in, level). The cockpit HUD's align button shows it, lit while holding.

**Warp stops by itself** at arrivals, 30 s before a maneuver's burn (5 s for an armed one), ahead of a rendezvous,
before the entry interface, at a sphere-of-influence change, before Delfos's heat and at a point you asked for
('Warp here' on the map). A ship moving in your frame limits how fast you can warp. Warp is not free: batteries and
tanks on railed grids run at the warp rate, and warp drops to x1 when one on your ship runs dry.

**Map (terminal Map tab).**
- Left click a sector's orbit, marker or list row: select it (the game's side panel).
- The list gives each sector a quick guide from your orbit: delta-v and trip time.
- Your trajectory is drawn patched-conic: each SOI in its own colour, encounters around a ghost of
  the body where you meet it, periapsis / apoapsis tagged.
- Right click the trajectory: **Add maneuver**. Right click a maneuver: **Remove maneuver**, or type
  prograde / normal / radial exactly (the game's number dialog). Right click anywhere: remove all.
- A selected maneuver has six navball handles: drag one to add delta-v along it (the further you
  pull, the faster); drag the maneuver along the trajectory to move it; click a handle's number to
  type it.
- **Targets** (2026-10-11): a planet or moon, a sector or rock, or a ship or station (yours, and the
  contacts you have seen - drawn as pins, with their orbits when on rails).
  - **N** over something on the map targets it (again: the next thing there; on the target alone: clears it).
    Right-click works too; when several things lie under the cursor, the menu lists each one.
  - The **target list** under the sector list: your ships and known contacts (nearest first), then the
    planets and moons, with distances; click a row to target it.
  - The target is bracketed on the map; the path's closest approach is marked at ANY distance
    ("closest 1,240 km in 2:10:00 · 380 m/s"; a planet's as the altitude you pass at), a rendezvous
    when within the merge range. The orbit card, the Rendezvous tab and the HUD's relative plot follow it.
  - In flight, **N** targets the body or contact marker nearest the crosshair; the target is bracketed with its
    name and distance. The target is saved with the world.
- **Contacts** (2026-10-11): the map's GPS tab lists everything you can target instead of GPS points - the game's own
  screen (search, sort by name or distance, folding groups, the eye) over the mod's list: Your ships · Contacts ·
  Planets and moons · Sectors · Places (your GPS points, where they truly are). Distances are true across frames.
  Selecting a row targets it; its eye pins it to the HUD. The HUD marks your target, what you pinned and contacts
  within 50 km; everything else lives in the list. Renames and pins are saved with the world; your real GPS data is
  never changed (a GPS point added from the tab is a real one and shows under Places).
  Del on a selected row puts a ship, a body or a sector away under Hidden (Del there brings it back); on a place it
  deletes that GPS point. Renaming a place renames its GPS point. The game's words for the screen are the tab's
  ("Contacts", "Search contacts", "Rename contact", "Remove contact"; its string table, relabelled at load).
- **Orbital elements** (2026-10-11): point at anything on the map - a ship, a planet or moon, an orbit line, a sector,
  your own path - and a card by the cursor gives its orbit (Ap / Pe, about which body, inclination, period). The
  target has its own card at the foot of the map: Ap / Pe / e, inclination, node, argument of periapsis, period and
  semi-major axis, where it is now (height, speed, the next apsis), against your orbit (Ap / Pe differences, the
  planes' angle, how far ahead or behind) and the closest approach on your path.

**Rendezvous tab** (the map's third tab): your planned path relative to the target, in the target's own frame, with
its merge zone; the nodes are edited there as on the map. With a planet or moon targeted it shows a porkchop plot
(departure time against flight time, coloured by total delta-v; your plan is the cross).

**In flight.** The game's notification card shows your orbit (altitude, speed, periapsis / apoapsis, period, warp) and
the next burn (delta-v, burn time, time to start). A navball marker points along the burn; the countdown runs to half
the burn before the node; the node completes when what is left is under 0.1 m/s. Maneuvers with **Auto-burn: on**
(their right-click menu) fly themselves: the ship turns to the burn and its thrusters fly it (the jetpack on foot). GPS markers in
another place (another planet, a sector) are drawn in their true direction with the true distance.

**Encounters.** Encounters live on orbits too: close spawns stay with you, far ones get an orbit of their own; NPC
ships fly plain Newtonian inside their frame.

**Holding station.** Ships near you in your frame feel the relative pull of your orbit. With dampeners on a ship
holds station instead, and its thrusters pay for it in power (or the suit, on foot). A "Frame origin" marker shows
where holding is free.

**Reentry and the speed cap.** Inside a planet's space the world's speed cap applies (raised from 300 to 1000 m/s);
on rails nothing caps you.
- From the entry interface (about 10 km above the border at Verdure) down to the border, speed over the cap is braked
  away, so you always arrive at or under the cap. Climbs and ordinary orbits are untouched; the band is also a thin
  atmosphere, so aerobraking passes lower the apoapsis.
- **Heat:** the energy shed, against a tolerance of 1.15 MJ/kg (x2 with heavy armour in front). Every normal transfer
  arrival comes in within it. Past it a grid's forward layer is worn in 5 % chunks, at most half a block's health per
  entry: the rule alone never destroys a ship.
- **Airless bodies** (Palatine) have no band: an arrival over the cap is clamped at the border, a jolt worth a quarter
  of the same heat.
- **Seen ahead:** the orbit card ('Entry in 2:46 · 1,669 -> 990 m/s · heat 82%', then 'braking 5.3 g'), map marks
  (Atmosphere entry, Max q, No air) and warp stopping 30 s before a pass.
- **Plasma:** with the Aerodynamics Mod installed, ships braked on rails glow with its reentry plasma.

**Sensors and contacts.** What you can target beyond your own things is what you have seen: by eye, with a
**Telescope** (passive: sunlit or warm targets) or a **Radar** (active: everything in range, and it gives you away).
The blocks come from the companion "Orbital Mod Blocks" content (`tools/blocks/gen_blocks.py`).

**Settings.** World settings (planet spin, legacy-space capture, reentry, asteroids, encounters) are saved with the
world, with host overrides in `%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.world.cfg`; client settings (proxy
planets, the orbit HUD, the map) in `OrbitalMod.client.cfg` next to it. Both files are re-read every ~2 s.

**Saved with the world:** your orbit and every frame, the clock, maneuver nodes (armed or not), your target, contacts,
the Contacts tab's pins, names and hidden rows.

## For modders

- **`OrbitalMod.OrbitalApi`**: the system, frames and their orbits, which frame a grid is in, reentry, settings - by
  reflection, .NET and engine types only, versioned. Reference and example: [docs/API.md](docs/API.md).
- **With the Aerodynamics Mod:** Aero reads each railed grid's true air state and Orbital's velocity edits through
  `OrbitalApi`; Orbital registers as Aero's reentry-plasma source through `AeroMod.AeroApi`. `tools/check_contract.sh`
  (in the fast gate) checks both directions from source. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#12-the-aero-link).
- Reflection into the engine (render, the colonization map, UI): every use is listed in
  [docs/REFLECTION_AND_WHITELIST.md](docs/REFLECTION_AND_WHITELIST.md) - keep it current with every change.
- Mod scripts: never use `var` for an array of a mod-defined type. The game bans it (VRS1001) and the mod fails to
  load; the files' `#pragma warning disable` hides that from offline analyzers, ModCheck's rule MODVAR1 catches it.

## Testing: three tiers

| tier | command | time | what | when |
|---|---|---|---|---|
| fast gate | `tools/test_fast.sh` | ~30 s, no game | the scripts compiled as the game compiles them (ModCheck), the cross-mod contract (`tools/check_contract.sh`), every offline suite in `Tests/` (orbits, frames, entry, persistence, player orbit and membership, rendezvous, time, sensing, system definitions): ~1,440 checks | every change |
| smoke | `tools/harness/test_smoke.sh` | ~3 min, one launch | the game side only: the mod loads (components, sensor blocks), a jointed grid goes on rails, an orbit stows, warp runs, contacts are spotted, the map / ring / Rendezvous tab / porkchop / HUD plot draw, a reentry is predicted, braked and handed over at the cap; no fault, no Havok assertion | engine-facing changes, before a commit |
| long | `tools/harness/test_all.sh` | ~1 h | jointed-grid moves, regression scenarios, perf budgets, save / load, a random soak | releases, bug hunts |

Logic belongs in the game-free core (`Scripts/Simulation/Core`) where the fast gate can test it; the in-game tiers
only check what needs the engine. The in-game harness (`DevHarness`, commands through `tools/harness/orb.sh`) is live
only when `%TEMP%\OrbitalMod\dev.flag` exists (`tools/harness/relaunch.sh` creates it); see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#13-dev-tooling).

## Performance and profiling

- **Budgets:** `tools/harness/test_perf.sh`: 11 scenes against per-frame budgets (2026-10-02: flight 0.15 ms client /
  0.04 ms server, map 2.1-2.4 ms).
- **Per-section timings:** the status file's `modcost` / `mapcost` lines (`ModCost`): every `FrameHost.Guard`ed client
  system and the map's `ui.*`, `trajectory.*` and `gm.*` sections; avg / max / peak in ms. `statusfast on` writes the
  status file every poll.
- **Garbage per system:** the harness command `allocs on|off|show`. By type and call site: `dotnet-trace` plus
  `D:/aero/tools/AllocReport` (see its header).
- **The rings lock:** `PlanetRings.Known` must never wait for `PlanetRenderBridge.Lock`. The server calls it holding the
  frames lock, while the client takes the frames lock inside the render lock: waiting deadlocked both threads (fixed
  2026-10-02 with a try-lock).

## Limits and open items, highest value first

1. **Multiplayer.** The client half drives only the local player and shares static state with the server half, so it
   works in single player and on a listen host. A dedicated server needs the player's frame logic server-side and a
   server-to-client command channel for character moves (characters are client-authoritative).
2. **A piloted ship through the whole loop** (take off, rails, plan, burn, arrive by conjunction) is verified piecewise
   (seated frames, armed burns in `tools/harness/burns.sh`, the multi-grid `hercules.sh`), not yet flown end to end by
   hand; the map editor with a real mouse is verified through the harness only.
3. **The speed cap as a setting** (vanilla 300 to 1000): the reentry rules follow any cap (tested with 300), but the
   physics cap is set at definition load, before a world is known.
4. **Gravity patch as the default.** The 1/r^2 patch is applied at runtime and saved with the world; decide whether a
   world without the mod's first load should be patched automatically.
5. **Legacy space** (world space outside every planet cell and berth) is captured into frames only with the
   `CaptureLegacySpace` setting: objects there sit at rest relative to the planet, so they fall in.
6. **No auto-planning, ever.** The route planner was removed (2026-09-29): no auto target, no auto rendezvous. You place
   the burns; an armed burn flies the burn you placed.
7. **The ~230 ms stall** when a grid lands in a fresh berth (the engine builds a new physics world) is softened by warm
   berths kept alive with placeholder grids (`WarmBerths`); a berth layout inside one region would remove it.
8. The FramesLock is still held across the map draw and single-grid moves (see docs/AUDIT_2026-10-05.md, 2026-10-09).
