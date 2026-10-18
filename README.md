# Orbital Mod for Space Engineers 2

Real orbits for Space Engineers 2. Your world's planets become a small solar system: planets circle their star, moons
circle their planets, and a ship that climbs out of a planet's space goes into orbit, on rails, exactly where it is.
Plan burns on the game's map, warp time, meet ships, sectors and asteroids by really arriving in the same place at the
same time, and come home through a re-entry that brakes and heats your ship.

**Version 1.0.0** · SE2 2.4.x · single player and listen hosts ·
**[Wiki](https://github.com/xianson/se2-orbital-mod/wiki)** · Steam Workshop: **[WORKSHOP LINK - to be added]**

## Features

- **A solar system:** planets on orbits, moons around them, planet spin, gravity that falls off with distance.
  [Orbits and Planets](https://github.com/xianson/se2-orbital-mod/wiki/Orbits-and-Planets)
- **Orbits on rails** above each planet's air, simulated exactly even while nobody is there.
- **Time warp** x1 to x10,000 that stops itself before anything important and costs energy.
  [Time Warp](https://github.com/xianson/se2-orbital-mod/wiki/Time-Warp)
- **Maneuver planning on the map:** your path ahead, burns you place and drag, targets and closest approach, a
  Rendezvous tab with a porkchop plot, auto-burns, attitude hold.
  [Map and Maneuvers](https://github.com/xianson/se2-orbital-mod/wiki/Map-and-Maneuvers)
- **Arrivals by conjunction** at sectors, asteroid belts, ring rocks and other ships.
  [Encounters and Asteroids](https://github.com/xianson/se2-orbital-mod/wiki/Encounters-and-Asteroids)
- **Re-entry** braking and heating, predicted ahead of time.
  [Re-entry](https://github.com/xianson/se2-orbital-mod/wiki/Re-entry)
- **Telescope and Radar blocks** and a Contacts list: you can only target what you have seen.
  [Sensors and Contacts](https://github.com/xianson/se2-orbital-mod/wiki/Sensors-and-Contacts)
- **Flight HUD:** orbit card, orbit line, markers across planets, warp bar.
  [Flight HUD](https://github.com/xianson/se2-orbital-mod/wiki/Flight-HUD)
- **Speed cap** raised to 1,000 m/s inside a planet's space; none on rails.
- **In-game settings:** pause menu > Settings > Mods > Orbital.
  [Settings](https://github.com/xianson/se2-orbital-mod/wiki/Settings)
- **Works with the [Aerodynamics Mod](https://github.com/xianson/se2-aerodynamics-mod)** (optional): re-entry plasma,
  structural-failure alerts, build and flight hints.
  [Using with the Aerodynamics Mod](https://github.com/xianson/se2-orbital-mod/wiki/Using-with-the-Aerodynamics-Mod)

## Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks but never runs its scripts.
  The option turns on scripts for every mod you load.
- **Single player or a listen host.** Dedicated servers are not supported yet.
- Optional: the Aerodynamics Mod.

## Install

1. Subscribe on the Steam Workshop (link above).
2. Add **Orbital Mod** to the world's mod list.
3. Start the game with `-loadScripts`, load the world, and check for the **Orbital** section under pause menu >
   Settings > Mods.

Step by step: [Getting Started](https://github.com/xianson/se2-orbital-mod/wiki/Getting-Started) ·
Keys: [Keys and Controls](https://github.com/xianson/se2-orbital-mod/wiki/Keys-and-Controls) ·
Problems: [Troubleshooting and FAQ](https://github.com/xianson/se2-orbital-mod/wiki/Troubleshooting-and-FAQ) ·
Bugs: [issues](https://github.com/xianson/se2-orbital-mod/issues) (attach your game log:
`%APPDATA%\SpaceEngineers2\Temp\Logs\`, the newest `SpaceEngineers2_*.log`).

## Known limits

- No dedicated servers yet.
- Removing the mod from a world that has used it is not supported.
- The 1,000 m/s speed cap applies to every world while the mod is loaded.
- No automatic route planning: you place the burns.

## For developers

- **Docs:** [how it works](docs/ARCHITECTURE.md) · [the API for other mods](docs/API.md) (`OrbitalMod.OrbitalApi`,
  version 3, by reflection) · [reflection and the whitelist](docs/REFLECTION_AND_WHITELIST.md) (keep it current with
  every change) · design notes ([encounters](docs/design-encounter-frames.md),
  [maneuver nodes](docs/design-maneuver-nodes.md), [colonization on orbits](docs/design-colonization-orbits.md)) ·
  [audit log](docs/AUDIT_2026-10-05.md) · [changelog](CHANGELOG.md) · a README in each folder of `Scripts/Simulation`.
- **Player texts:** [player guide](README_PLAYERS.md) · [release notes](RELEASE_NOTES.md) ·
  [Steam description](WORKSHOP_DESCRIPTION.md) (BBCode).
- **Modder overview:** [For Modders](https://github.com/xianson/se2-orbital-mod/wiki/For-Modders).

### Testing: three tiers

| Tier | Command | Time | What | When |
|---|---|---|---|---|
| fast gate | `tools/test_fast.sh` | ~1 min, no game | the scripts compiled as the game compiles them (ModCheck; `MODCHECK=` overrides its path), the cross-mod contract (`tools/check_contract.sh`; `AERO=` points at another aero checkout), every offline suite in `Tests/`: ~4,480 checks | every change |
| smoke | `tools/harness/test_smoke.sh` | ~3 min, one launch | the game side: the mod loads, a jointed grid goes on rails, an orbit stows, warp, contacts, the map / Rendezvous tab / porkchop / HUD, a predicted and braked reentry; no fault, no Havok assertion | engine-facing changes, before a commit |
| integrate | `tools/harness/test_integrate.sh` | ≤ 5 min, one launch | an integration batch with both mods (API versions, structure failure and alert, debris drag, warp billing, the settings tab); see `tools/harness/RUNBOOK.md` | before merging a batch |
| long | `tools/harness/test_all.sh` | ~1 h | jointed-grid moves, regressions, perf budgets, save / load, a random soak | releases, bug hunts |

Logic belongs in the game-free core (`Scripts/Simulation/Core`) where the fast gate can test it. The in-game harness
(`DevHarness`, commands through `tools/harness/orb.sh`) is live only when `%TEMP%\OrbitalMod\dev.flag` exists
(`tools/harness/relaunch.sh` creates it). **Every harness script that launches the game kills the running one first:
never run one while someone is playing.** See [`tools/harness/README.md`](tools/harness/README.md).

### Performance and profiling

- Budgets: `tools/harness/test_perf.sh` (11 scenes against per-frame budgets).
- Per-section timings: the harness status file's `modcost` / `mapcost` lines; `statusfast on` writes it every poll.
- Garbage per system: the harness command `allocs on|off|show`.

### Mod-script rules

- Never use `var` for an array of a mod-defined type: the game rejects it (VRS1001) and the mod fails to load
  (ModCheck's rule MODVAR1 catches it).
- One cross-mod link each way: this mod finds the Aerodynamics Mod only through `Scripts/Simulation/Api/AeroLink.cs`;
  aero finds this one only through its `Api/OrbitalLink.cs`. `tools/check_contract.sh` checks both.
- Sensor block definitions are generated: `tools/blocks/gen_blocks.py --build`.

### Open items

1. Multiplayer: a dedicated server needs the player's frame logic server-side and a server-to-client channel for
   character moves.
2. A piloted ship through the whole loop is verified piecewise by the harness, not yet flown end to end by hand.
3. The speed cap as a setting (the physics cap is set at definition load, before a world is known).
4. The ~230 ms stall when a grid lands in a fresh berth is softened by warm berths; a berth layout inside one region
   would remove it.
5. The frame lock is still held across the map draw and single-grid moves.

## Credits and licence

By xianson. The orbital core is ported from the author's Space Engineers 1 project, SE-Aerospace.

**[LICENCE - none in the repository yet; add one]**
