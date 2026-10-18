# Runbook - in-game runs

The rules for every in-game run: **nobody playing** (ask if unsure), **one run at a time**, **5 minutes end to end**, **in
the background**, every dev override **released** after. The game-free gate (`tools/test_fast.sh`) comes first.

## test_integrate.sh - the integration batch, once, before a merge

One launch of the SSTO's world (Concordia Research Facility 7, both mods, deployed from the integration branches), then
PASS / FAIL / SKIP lines and a summary; the timeline is in the script's header (typically ~200 s, cut at 285 s).

**Before**: deploy both integration branches into the live mod folders, run `tools/test_fast.sh` in each, make sure the
Steam launch's last world is the SSTO's (the script uses the Steam launch: the last world, the launch options' mods).

**Run it** (the orchestrator, from its own shell):

```
Bash(run_in_background: true):
  KILL_GAME=1 bash "C:/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness/test_integrate.sh" \
      > C:/Users/slob/AppData/Local/Temp/OrbitalMod/integrate.out 2>&1
Monitor: until grep -q "== summary:" C:/Users/slob/AppData/Local/Temp/OrbitalMod/integrate.out; do sleep 5; done   (cap 6 min)
```

Without `KILL_GAME=1` it refuses (exit 2) while any game is running - it may be the user's. `NOLAUNCH=1` drives a game
already running instead (its log from now). Exit codes: 0 all ran and passed (SKIPs listed), 1 a FAIL, 2 refused,
3 nothing failed but the deadline cut phases (no verdict on those - rerun).

**Kill it** (TaskStop stops only the wrapper; the script and its `orb.sh` calls go on sending commands). From Git Bash - single
quotes, so bash leaves `$_` alone; in the PowerShell tool run the quoted part alone:

```
powershell -NoProfile -Command 'Get-CimInstance Win32_Process | ? { $_.CommandLine -match "test_integrate|orb\.sh" -and $_.CommandLine -notmatch "Get-CimInstance" } | % { Stop-Process -Id $_.ProcessId -Force }'
bash ".../tools/harness/test_integrate.sh" --release     # a killed script runs no trap: release by hand
```

**After**: the game is left running with every override released (cam off, hold off, thrust 0, warp x1, aerocost off,
statusfast off, TestSpeed 0, TrailDebug off, Strength 7, Damage on, Trace off, the SSTO's gridhold 0 and dampeners on).
Close it if the user is not about to play. The SSTO is broken in that session only: nothing is saved.

**Later, no game needed**: `test_integrate.sh --audit-log [LOG]` judges a game log's world changes (one `[ORBIT] world
reset:` per world left) - run it on the user's own log after they quit to the menu and load a world.

### What each line means

| line | signal | a FAIL means |
|---|---|---|
| `launch: the world loaded` / `the harness answers` | a new log with `beacon Root Entity`; `findgrids` answered | the game did not start, the world failed (`Loading Failed`), or the Orbital DevHarness is off (`%TEMP%\OrbitalMod\dev.flag`) or not compiled in |
| `load: Orbital Mod running` / `Aerodynamics Mod running` | log tags `[ORBIT] beacon Root Entity`, `[AERO] Injected` | that mod did not load (compile error in the log: search `vrgproj`), or the launch options lack it |
| `load: AeroApi.Version = 8` / `OrbitalApi.Version = 3` | `aeroget` of the consts | an older copy deployed, or a contract bump not matched on the other side (run `tools/check_contract.sh`) |
| `world reset: none at ... first load` | `WorldReset.Count`, `[ORBIT] world reset:` lines | a reset fired with no world change - a session/planet event misread (Host/WorldReset.cs) |
| `world reset: one ... per quit-and-load` | always SKIP in a run (no verb loads a world) | - use `--audit-log` on a log with world changes; there a FAIL names the load with 0 or 2+ resets |
| `setup: a grid named SSTO` / `seated in the SSTO` | `findgrids`, `hudcheck` | the wrong world (the last one played), or seating failed (`seat SSTO` reply) - every structure check is then SKIP |
| `structure: a failed cut cracked across` | `[AERO-STRUCT] ... failed whole - N of M members cracked across it` | no cut failed at Strength 1.5 under the pull (loads too low, damage off, the eval off) or it failed the old way (`failed whole - N of M members (worst ...` without the crack) |
| `structure: every crack finished` | `AeroGridComponent.CracksStarted/CracksDone/CracksRunning` | a crack stalled (running forever, or done < started): StructureServer.Failure.cs, StepCrack |
| `structure: tear anchors made / peeled` | `StructureTear.AnchorsMade` (+3), `AnchorsPeeled` (+1) | the tear's springs not made (no body, too light, MaxActive) or never let go in order (StructureTear.cs) |
| `structure: no cut needed forcing apart` | `AeroGridComponent.CutsForced` +0, `did not separate` lines | a cut's members survived and bridged it; the verify had to destroy them - the split path is wrong |
| `structure: no tear fault` | `StructureTear.Faults` +0, `[AERO-TEAR] ... failed` | the tear threw (its status line names the exception) |
| `structure: the ship came apart` | `findgrids` count before / after | the game never split the grid - the failed members did not separate it |
| `alerts: ... reported` | `[ORBIT-STRUCT] seated ship:` line, `StructureAlerts.Sections` | the Orbital mod did not see aero's events: `StructureAlerts.Status` in the line says why (not bound, older API, threw) |
| `debris: drag acts on the fracture pieces` | `AeroDebrisDrag.Steps` increases | no loose pieces in air (a break with no fracture chips) or the job is off (`Debris.Drag` setting, `AeroSwitch`) |
| `warp: billed seconds = the rails clock's surplus` | `WarpBill.BilledSeconds` vs the status `rails t=` advance x (N-1)/N | ~2: billed twice a frame (the 2026-10-15 bug back); <1: frames unbilled (a fixed dt again, or the claim dropping frames) |
| `warp: one bill a server tick` | `WarpBill.Bills` per second vs the status `server=` ticks/s | as above, counted per frame; a FAIL "warp did not hold xN" means warp was cut (`Warp stopped`) - rerun higher |
| `settings UI: the tab found, with rows` | `settingsui` reply: a "tab ... found" phrase and "N rows" | the tab not found in the game's settings screen, or no rows built (SKIP while the verb does not exist) |
| `whole run: no exception in our code, no mod fault` | the log since launch: `at AeroMod.`/`at OrbitalMod.`/`at SEAerospace.` frames, `[AERO-FAULT]`/`[ORBIT-FAULT]`, our tags with `Exception` | the first hits are printed with their log line numbers |
| `whole run: no Havok migration assertion` / `no fatal crash` / `game still running` | `not migrated`, `still active bodies in native world`, `Crash Handler]: Fatal`, the process | a grid moved outside the pre-physics phase; a static body left in a physics world whose box no longer reaches it (an `[ORBIT-HAVOK] cluster` line before it names the cluster and the last moves - see havok_repro.sh); a crash (see Temp/CrashReports) |

The `-- numbers` block (no verdict): the aero mod's `[AERO-COST]` top sections (ms/s), the Orbital `modcost` timers x
ticks, the process's GC allocation (MB/s, the whole game), against the 2026-10 baseline (mods 140 ms/s, 1.74 MB/s - that
one a dotnet-trace profile of the mods' frames, so compare trends, not digits); the failure counters before / after; the
warp window.

## test_blocksave.sh - the aero blocks' own saved data survives save -> reload

Two launches of an **'Orbital Test ...' copy** of the SSTO's world: dump the SSTO's aero blocks, set one control surface
(trim, max), one gear (up) and its Airshock (off), one airbrake (max), one thruster (attitude fraction 0.73) through
`aeroset AeroBlockSave.DevSet` (applied on the server step), `save` into the SAME container, kill, relaunch into it, dump
again and compare. Every value read from the aero mod (`AeroBlockSave.DevDump`, `.Restored`, `.Written`, `.LegacyClaimed`).
Typically ~200-260 s, cut at 290 s.

**Before** (the orchestrator - the script edits neither the saves nor the args file):
1. Copy `%APPDATA%\SpaceEngineers2\AppData\SaveGames\Concordia Research Facility 7` to a NEW folder next to it whose name
   starts with `Orbital Test` (e.g. `Orbital Test SSTO Blocksave`) - the script saves into it, so never a player's world.
2. Point `-start:` in `D:\aero\se2-launch-args.txt` at it: `"-start:Orbital Test SSTO Blocksave"` (keep the other args).
   Put the old value back afterwards.
3. Deploy both mods with the dev additions (aero: `Dev/AeroBlockSaveDev.cs`), `tools/test_fast.sh` green; nobody playing.

**Run it**:

```
Bash(run_in_background: true):
  KILL_GAME=1 bash "C:/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod/tools/harness/test_blocksave.sh" \
      > C:/Users/slob/AppData/Local/Temp/OrbitalMod/blocksave.out 2>&1
Monitor: until grep -q "== summary:" C:/Users/slob/AppData/Local/Temp/OrbitalMod/blocksave.out; do sleep 5; done   (cap 6 min)
```

Refuses (exit 2) unless the args file's `-start:` is an `Orbital Test...` world, and while a game runs without
`KILL_GAME=1`. Exit 0 all passed (SKIPs listed: a block kind the grid has none of), 1 a FAIL, 3 cut by the deadline.
`GRID=<name as findgrids prints it>` tests another grid. Kill it as test_integrate (match `test_blocksave|orb\.sh`), then
`test_blocksave.sh --release`.

**PASS looks like**: `launch 1/2: '<world>' loaded`; `dump: SSTO's aero blocks listed (N)`; `set: every change took`;
`save: written to the test world ('save -> '<world>': Success')`, `Written grew`; one `reload: kind@x,y,z name = value (from
its own data, rb:1)` per changed value (6 with every kind present); `every aero block ... came back`, `every block's state as
saved`; `Restored N > 0`; `LegacyClaimed 0 = 0`; both logs clean; `game still running`.

| line | a FAIL means |
|---|---|
| `aero: the per-block save's dev statics` | the deployed aero build predates `Dev/AeroBlockSaveDev.cs` |
| `launch N: the game loaded the test world` | the game ran another world than the args file's (`Running application with arguments` in the log) |
| `set: ...` | `DevSet` answered `no <kind> at ...` / `bad request` - the dump and the set disagree (AeroBlockSaveDev.cs) |
| `save: written ...` / `Written grew` | the save failed (the reply's result), or the blocks' `[Serializer]` methods never ran (no keys) |
| `reload: kind@cell name = value` | that value did not come back from the block's own data: `rb:0` = the block's `[Init]` got no key (the injection's fixed id / builder: EntityInjector, AeroBlockSave.Info); right `rb` but wrong value = the key's text (AeroBlockSaveForm) |
| `every block's state as saved` | an unchanged block changed over the reload (an old line or a client copy claimed it) |
| `LegacyClaimed = 0` | an old position-matched line was still in the world after the first save and claimed a block (AeroSurfaceSaved) |
| `log ...: no exception` | an exception in our code in that run's log (the first hits printed) |

**After**: the game is left running in the test world, the overrides released and the changed blocks set back (that session
only - the test world keeps the saved values; it is a copy). Close it if the user is not about to play, and restore
`-start:`.
