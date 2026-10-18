# tools/harness - driving the game from a shell

Bash (Git Bash) and PowerShell scripts that launch Space Engineers 2, drive the Orbital Mod's DevHarness and judge
what happens from the game's log and the harness's status file. The game-free checks are `tools/test_fast.sh`
(ModCheck + every offline suite + `tools/check_contract.sh`); these are for engine-facing work and releases.

**Every script that launches kills the running game first. Never run one while someone is playing.** Most open
with one launch (the Steam launch, `launch.sh`) or `relaunch.sh` (the scheduled task); many take `NOLAUNCH=1` to use
the game already running. None saves the user's world (`save` only ever writes the harness's own
"Orbital Test World").

The names these scripts depend on (DevHarness verbs, `aeroget`/`aeroset` members, dev keys, log and status text)
are listed in [COMMANDS.md](COMMANDS.md) - keep them when changing either mod.

## How it works

- The mod's DevHarness runs only when `%TEMP%\OrbitalMod\dev.flag` exists (`relaunch.sh` creates it). It polls
  `%TEMP%\OrbitalMod\cmd.txt`, runs each line and writes `status.txt` (state lines, then `> verb ... => reply`).
- The flag also turns on the mod's narrated log lines (`OrbitalLog`: `[ORBIT-FRAME]` events, `[ORBIT-ENC]`, `[ORBIT-ROIDS]`,
  `[ORBIT-CONTACT]`, `[HITCH]`, the per-planet / prefab `[ORBIT]` lines). A player's log has only the startup summary,
  warnings and failures: a script that greps a narrated line needs the flag (every launcher here sets it).
- `orb.sh "cmd" ["cmd" ...]` writes the commands, waits until they are consumed (60 s at most) and prints the tail
  of the status; `--eshot [settle]` adds an engine screenshot (works with the display asleep), `--shot` a desktop
  capture through the `OrbitalShotSE2` task.
- Two ways to start the game: `relaunch.sh` (the `OrbitalTestSE2` scheduled task -> `launch-orbital.ps1`, the
  args from `D:\aero\se2-launch-args.txt` with the world in `world.txt`) and `launch()` from `launch.sh` (the Steam
  launch, the game's last world, never two instances).

## Core scripts (paths are fixed: other scripts and notes call them by path)

| script | what it does |
|---|---|
| `orb.sh` | send harness commands, print the status / reply (see above) |
| `testlib.sh` | sourced by the suites: `mark`/`since` (the log since a point), `check`/`never`/`expect` -> PASS/FAIL, `send`/`reply`/`stat`, `alive`/`hold`, `summary`; `LOGF`, `waitloaded` (world loaded, then grids); sources `launch.sh` |
| `launch.sh` | `launch()`: close every game process, the Steam launch, then close any second instance for 90 s |
| `relaunch.sh` | kill the game (no save), start it through `OrbitalTestSE2`, wait for the server, `gridsstop` |
| `launch-orbital.ps1` | what `OrbitalTestSE2` runs: the launch args with this mod (`orbital-only.flag`: only this mod; `no-orbital.flag`: without it; `world.txt`: the save, or `content:<folder>` for a new game); writes `last-launch-args.txt` |
| `register-tasks.ps1` | registers `OrbitalTestSE2` and `OrbitalShotSE2` (interactive tasks: the agent shell cannot open windows) - once |
| `shot.ps1` | what `OrbitalShotSE2` runs: a desktop capture to `shot_HHmmss.png`, result in `shot-result.txt` |
| `key.ps1`, `mouse.ps1` | a key by scan code / a mouse move or click on the game window (tests without the harness) |
| `quiet.ps1` | the game behind the user's windows at low priority (`back`), or `restore` |
| `montage.ps1`, `shrink.ps1` | screenshots tiled into a labelled contact sheet; an 8 MB engine PNG to a JPEG |
| `eshot.sh` | commands, an engine shot, a JPEG in `docs/shots-work/<name>.jpg` |
| `perf_phase.sh` | the server's frame times (status `frame` line) over a phase, for A/B load tests |
| `randshots.sh`, `sweep.sh` | engine shots at random moments while a scenario runs; a randomized map/flight interaction sweep |
| `play.sh`, `play_grid.sh`, `play_transfer.sh`, `play_capture.sh` | a session set up for the user to play, or played by script for random-time screenshots |
| `fix_ctrl.py` | repairs sed back-references (`\1`) that string escaping turned into control bytes |
| `orbital-only.flag`, `world.txt`, `last-launch-args.txt` | local launch state (not in git) |

## Suites (PASS/FAIL, exit 0 only if all pass)

| run | what |
|---|---|
| `test_integrate.sh` | an integration batch checked once before a merge (~3.5 min, one launch of the SSTO's world, both mods): load and API versions, the structure failure (crack, tear anchors, split, the Orbital alert), debris drag, warp billed once a frame, the settings UI, our exceptions; perf numbers; `--release`, `--audit-log`. How to run, kill and read it: [RUNBOOK.md](RUNBOOK.md) |
| `test_blocksave.sh` | the aero blocks' own saved data through save -> kill -> reload (~4 min, two launches of an `Orbital Test...` copy of the SSTO's world - refuses any other): one surface, gear, airbrake, thruster and Airshock set, saved into the same container, compared after the reload from the aero mod's dumps and counters (`AeroBlockSave.*`); `--release`; `DRY=1` with a mock harness. [RUNBOOK.md](RUNBOOK.md) |
| `test_smoke.sh` | the in-game smoke run (~3 min, one launch): the mod loads, an orbit stows, a jointed grid on rails, warp, the map, contacts, no fault |
| `quick.sh [seed]` | the 5-minute in-game check: multi-grid ship in a frame, exits, warp, random actions, invariants |
| `checkall.sh` | the 2026-10-09 round (attitude hold, flames, the effects ramp) |
| `test_all.sh` | the long suites in a row: `test_joints`, `test_regress`, `test_perf`, `test_save`, `test_soak` |
| `test_joints.sh`, `test_regress.sh`, `test_perf.sh`, `test_save.sh`, `test_soak.sh` | jointed grids through frames; the regression suite; the mod's cost per frame against a budget; save/load round trip; a random soak |

Feature checks, one launch each, 5 minutes or less: `airless` (arrival at an airless body over the cap), `bakedcheck`
(the baked flight field), `burns` (armed maneuvers), `combo`, `contactsave`/`contactshot` (the Contacts tab),
`ctrlcheck` (the control audit), `exits`/`exits2` (getting out of vehicles), `fuzz [seed] [steps]` (random play against
the invariants), `fx`/`fxramp`/`spinfx` (the effects judged per frame), `globeshot`/`globesun`/`sun` (globe and sun
across frames), `hercules` (the multi-grid player ship), `holdcharge` (holding station is charged), `hud_cases`,
`rv_cases`, `rv_views` (the orbit card and rendezvous from every seat), `infoshot`/`targetshot` (map cards, targeting),
`map_compare`/`map_dim` (the map with and without sectors), `reentry_e2e` (DevReentry top to bottom), `scenarios`
(frame events), `structtest`/`loacdiag` (the aero mod's structure and warnings), `havok_repro` (the 2026-10-17 Havok
cluster crash on purpose: a breakup at speed with and without a FlyTest teleport, watched by the `[ORBIT-HAVOK]` audit),
`trails` (wing tip trails), `warm`
(warm berths), `wreckcam`, `aeroab` (the aero flight A/B, see the aero mod's README).

Run them in the background and read the summary: `bash tools/harness/test_smoke.sh > %TEMP%/smoke.out 2>&1`.

## experiments/

One-off A/B runs, screenshot sets and probes kept for re-running; same rules (they relaunch). Summaries (`*_sum.py`)
sit beside their script.

| script | what |
|---|---|
| `ascentshot.sh` | the orbit plot on climbs: the F-7 at ascent altitudes and speeds, shot and HUD-checked |
| `atmoab.sh` | the proxy atmosphere against the real planet, same view |
| `bakeprof.sh` | where the baked field's bake time goes (stage totals, on-demand bakes, worst frame) |
| `cullab.sh` | far grids' block effects with EffectCull off, then on |
| `dampshot.sh` | the F-7's pitch wobble, measured (blocks exported for AeroBench, then flown) |
| `fbwab.sh` (+`fbw_sum.py`) | the F-7's pitch at speed, aero-aware attitude law off then on |
| `flameprobe.sh` | why thruster flames stick: the aero mod's `[AERO-FLAME]` probe through a scripted flight |
| `fxlook.sh` | the Hercules' vapour and plasma by speed, photographed (`PRESETS` of aeroset values) |
| `herctrail.sh` | the Hercules' wing tip trails, low and fast, photographed |
| `hintshot.sh`, `hintshot2.sh`, `hintshot3.sh` | the build hints: the jetliner; a Blue Fighter by the spectator camera; the F-7 from four sides |
| `hudgeom.sh` | what HudGeom reads off the game's HUD |
| `lowresui.sh` | our HUD against the game's at the running resolution |
| `membisect.sh`, `memdump.sh` | the managed-memory runaway on joining a frame: bisected by feature; stacks and a heap dump |
| `orbitjitter.sh` (+`orbitjitter_sum.py`) | how much the HUD's orbit readout jumps frame to frame through air |
| `recsuper.sh` | a real-time recording of the Hercules going supersonic (puppeted airflow) |
| `reparentab.sh` | stranded block effects before and after EffectReparent |
| `showshot.sh` | screenshots of the new UI for the user |
| `spinshot.sh`, `spotshot.sh` | the entry effects while spinning; the entry spot light at x1 / x10 / off |
| `structprof.sh` | what the aero structure and hull heat cost (sim and background time, evaluations) |
| `tipab.sh`, `tipsweep.sh`, `wings.sh` | which ships get tip trails and the clumping A/B; a size/spread sweep; find a ship with real tips |
| `vapourab.sh` | the transonic vapour at a few gain/speed settings, side by side |

Outputs: engine shots in the game's temp folder (`%TEMP%\OrbitalMod` for most summaries), JPEGs and contact sheets in
`docs/shots-work/` (ignored by git).
