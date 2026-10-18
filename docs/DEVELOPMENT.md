# Orbital Mod: development

The developer notes that used to sit in the README. Players: see the [wiki](https://github.com/xianson/se2-orbital-mod/wiki).


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
