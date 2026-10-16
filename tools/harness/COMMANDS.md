# Names the harness scripts depend on

The scripts reach both mods only by text: DevHarness command verbs, `Full.Type.Member` strings (read and set by
reflection), dev key names, and the log and status lines they grep. None of it is checked by the compiler, so a
rename in either mod compiles and fails silently in game. **Keep every name below** (or change the scripts in the
same commit). Taken from the scripts on 2026-10-15; every command and member was checked to exist then, in the
live mods and in every reorg worktree.

How the names are reached:
- **Commands**: `orb.sh "verb args" ...` writes `%TEMP%\OrbitalMod\cmd.txt`; `DevHarness.Execute`
  (Orbital `Scripts/Simulation/Dev/DevHarness*.cs`) switches on the verb. Replies come back in `status.txt` as
  `> verb ... => reply` lines (scripts take the text after ` => `).
- **aeroget / aeroset `Type.Member [value]`**: `PlanetRenderBridge.ForeignValue` - `Assembly.GetType("AeroMod.Type")`
  (the `AeroMod.` prefix is added when the type has no dot; `OrbitalMod.X.Y` reads this mod's own statics), then a
  static field or property of that name, public or not. So the type must stay a top-level type of that namespace
  (nested: `Outer+Inner`), and the member a static field/property of type bool, int, float, double or string.
  The reply is `AeroMod.Type.Member=value`; scripts cut at `=` (and grep `=2 tips` etc. right after it).
- **Commands that reach the aero mod themselves** (DevHarness code, by name): `aeroflames` ->
  `AeroMod.ThrustTorque.FlamesEnabled`; `aeroexport` -> `AeroMod.AeroExport.Enabled`; `aeroflight` ->
  `AeroMod.AeroGridComponent+AeroFlightTest.Enabled`/`.Only`; `aeroaware` -> `AeroMod.ThrustTorque.AeroAware`;
  `pitchlog` -> `AeroMod.AeroGridComponent.PitchLog`; `aerocost` -> `AeroMod.AeroCost.Log`; `aero` ->
  `AeroMod.AeroSwitch.Enabled`; `aerospike` -> `AeroMod.AeroSpeedSpike.Enabled`; `fxcheck` -> `AeroApi.GetFxConsistency`;
  `flightdump` -> `AeroMod.FlightFixture.Build`; `gridup` -> `AeroMod.PhysicsHack.GetGravityDirection`; `gen ... air`
  -> `AeroMod.AeroEntryFx.TestWorld`. The AeroApi/OrbitalApi members themselves are guarded by
  `tools/check_contract.sh`.

## DevHarness commands the scripts send

`experiments/` = tools/harness/experiments/. A verb used by more than eight scripts shows the count and the first few.

| verb | scripts |
|---|---|
| `aero` | aeroab.sh, experiments/dampshot.sh |
| `aeroaware` | experiments/fbwab.sh, experiments/membisect.sh |
| `aeroexport` | experiments/dampshot.sh |
| `aeroflight` | aeroab.sh |
| `aeroget` | 15 scripts (bakedcheck.sh, checkall.sh, combo.sh, experiments/fxlook.sh, ...) |
| `aeroset` | 20 scripts (bakedcheck.sh, checkall.sh, ctrlcheck.sh, experiments/bakeprof.sh, ...) |
| `aerospike` | relaunch.sh |
| `alignbtn` | experiments/hudgeom.sh, experiments/lowresui.sh, experiments/membisect.sh |
| `approach` | test_smoke.sh |
| `atmosphz` | experiments/atmoab.sh |
| `bodymarkers` | holdcharge.sh |
| `cam` | 15 scripts (bakedcheck.sh, contactshot.sh, experiments/atmoab.sh, experiments/hintshot2.sh, ...) |
| `camview` | play_capture.sh, play_grid.sh, play_transfer.sh, rv_views.sh, sweep.sh |
| `celcheck` | scenarios.sh |
| `celmark` | scenarios.sh |
| `contacts` | test_perf.sh, test_regress.sh, test_save.sh, test_smoke.sh, test_soak.sh |
| `contactstab` | contactsave.sh, contactshot.sh, experiments/showshot.sh |
| `dblclick` | sweep.sh |
| `devrider` | test_regress.sh, test_soak.sh |
| `devsite` | rv_cases.sh |
| `devtarget` | rv_views.sh |
| `encon` | 50 scripts (aeroab.sh, bakedcheck.sh, burns.sh, checkall.sh, ...) |
| `entry` | reentry_e2e.sh |
| `entrycap` | reentry_e2e.sh |
| `exitcheck` | 24 scripts (bakedcheck.sh, checkall.sh, combo.sh, ctrlcheck.sh, ...) |
| `findgrids` | 39 scripts (bakedcheck.sh, checkall.sh, combo.sh, contactshot.sh, ...) |
| `flight` | burns.sh, play.sh, play_capture.sh, play_grid.sh, play_transfer.sh, randshots.sh |
| `flyby` | airless.sh |
| `focus` | map_compare.sh, play_capture.sh, sweep.sh |
| `frameof` | 9 scripts (airless.sh, combo.sh, exits.sh, exits2.sh, ...) |
| `fxcheck` | 13 scripts (bakedcheck.sh, checkall.sh, combo.sh, experiments/bakeprof.sh, ...) |
| `fxcull` | experiments/cullab.sh, experiments/membisect.sh |
| `gridatt` | experiments/dampshot.sh |
| `gridclone` | 9 scripts (exits.sh, exits2.sh, fuzz.sh, holdcharge.sh, ...) |
| `griddamp` | 21 scripts (checkall.sh, combo.sh, ctrlcheck.sh, exits.sh, ...) |
| `griddel` | scenarios.sh |
| `gridhold` | 17 scripts (bakedcheck.sh, checkall.sh, experiments/ascentshot.sh, experiments/bakeprof.sh, ...) |
| `gridinfo` | 13 scripts (burns.sh, exits.sh, exits2.sh, fuzz.sh, ...) |
| `gridlaunch` | 33 scripts (bakedcheck.sh, checkall.sh, combo.sh, contactsave.sh, ...) |
| `gridlevel` | 9 scripts (bakedcheck.sh, checkall.sh, ctrlcheck.sh, experiments/recsuper.sh, ...) |
| `gridmove` | test_joints.sh |
| `gridnudge` | exits.sh, fuzz.sh, hercules.sh, quick.sh, scenarios.sh, warm.sh |
| `gridorbit` | 18 scripts (burns.sh, combo.sh, exits.sh, experiments/lowresui.sh, ...) |
| `gridspin` | 11 scripts (bakedcheck.sh, experiments/fbwab.sh, experiments/recsuper.sh, experiments/spinshot.sh, ...) |
| `gridsstop` | relaunch.sh |
| `hints` | experiments/hintshot.sh, experiments/hintshot2.sh, experiments/hintshot3.sh, experiments/showshot.sh |
| `hiresglobe` | globeshot.sh |
| `hitchms` | warm.sh |
| `hold` | checkall.sh, experiments/flameprobe.sh, experiments/lowresui.sh |
| `hudcheck` | 10 scripts (exits.sh, exits2.sh, experiments/ascentshot.sh, hercules.sh, ...) |
| `hudtree` | experiments/hudgeom.sh |
| `inv` | fuzz.sh, hercules.sh, quick.sh |
| `joins` | combo.sh |
| `key` | 11 scripts (contactsave.sh, contactshot.sh, experiments/showshot.sh, experiments/structprof.sh, ...) |
| `legacy` | play.sh, play_grid.sh |
| `map` | 17 scripts (contactsave.sh, contactshot.sh, experiments/lowresui.sh, experiments/showshot.sh, ...) |
| `mapcam` | infoshot.sh, sweep.sh, targetshot.sh, test_perf.sh, test_regress.sh, test_smoke.sh, test_soak.sh |
| `mapring` | test_regress.sh, test_smoke.sh |
| `mem` | experiments/membisect.sh, experiments/memdump.sh |
| `menu` | sweep.sh |
| `mode` | experiments/atmoab.sh, globeshot.sh, globesun.sh |
| `node` | 9 scripts (burns.sh, play_capture.sh, play_grid.sh, play_transfer.sh, ...) |
| `noderound` | burns.sh |
| `orbit` | 14 scripts (contactsave.sh, contactshot.sh, infoshot.sh, play.sh, ...) |
| `orbitnow` | burns.sh, experiments/orbitjitter.sh |
| `particles` | experiments/reparentab.sh, wreckcam.sh |
| `pickat` | infoshot.sh, sweep.sh, targetshot.sh |
| `pitchlog` | experiments/fbwab.sh |
| `player` | airless.sh, exits2.sh, fuzz.sh, quick.sh, scenarios.sh, test_regress.sh, test_soak.sh |
| `radarpower` | test_save.sh, test_smoke.sh |
| `rclickat` | sweep.sh |
| `reentrytest` | reentry_e2e.sh |
| `ringrocks` | test_regress.sh, test_soak.sh |
| `roids` | rv_cases.sh |
| `rvtab` | map_compare.sh, map_dim.sh, rv_views.sh, targetshot.sh, test_perf.sh, test_regress.sh, test_smoke.sh, test_soak.sh |
| `save` | contactsave.sh, test_blocksave.sh (`save <the loaded 'Orbital Test...' world>`), test_save.sh |
| `seat` | fuzz.sh, play.sh, play_grid.sh, quick.sh, reentry_e2e.sh, test_integrate.sh |
| `seatid` | 37 scripts (bakedcheck.sh, burns.sh, checkall.sh, combo.sh, ...) |
| `sensorblocks` | test_smoke.sh |
| `settingsui` | test_integrate.sh (not a verb yet - the settings UI branch adds it; until then SKIP. The script wants a reply naming the tab with "found" (or true/yes/shown) and "N rows") |
| `sensornopower` | test_save.sh, test_smoke.sh |
| `setting` | settingsui.sh |
| `settingsui` | settingsui.sh (`open`, `select`, `flip <setting>`, `close`, `report`); by hand for screenshots: `expand all\|none\|section\|advanced`, `scroll top\|bottom\|<px>\|<setting>` (every mod's section on the tab) |
| `shot` | experiments/fxlook.sh, experiments/herctrail.sh, experiments/tipab.sh, experiments/tipsweep.sh, experiments/wings.sh, holdcharge.sh, orb.sh, trails.sh |
| `shotui` | 22 scripts (contactsave.sh, contactshot.sh, experiments/ascentshot.sh, experiments/cullab.sh, ...) |
| `sites` | map_compare.sh, rv_cases.sh, rv_views.sh |
| `skcharge` | holdcharge.sh |
| `statusfast` | perf_phase.sh |
| `stores` | holdcharge.sh |
| `sunrel` | globesun.sh, sun.sh |
| `target` | 14 scripts (contactsave.sh, contactshot.sh, infoshot.sh, map_compare.sh, ...) |
| `targetpanel` | infoshot.sh |
| `targetrow` | targetshot.sh |
| `targets` | targetshot.sh |
| `testship` | test_save.sh, test_smoke.sh |
| `thrust` | ctrlcheck.sh, exits.sh, experiments/flameprobe.sh, fuzz.sh, quick.sh |
| `tpgrid` | 40 scripts (bakedcheck.sh, burns.sh, checkall.sh, combo.sh, ...) |
| `unseat` | 46 scripts (aeroab.sh, airless.sh, bakedcheck.sh, burns.sh, ...) |
| `warm` | warm.sh |
| `warp` | 20 scripts (airless.sh, burns.sh, combo.sh, exits.sh, ...) |

DevHarness verbs no script sends (kept for hand use and the debug panel; not a reason to remove them): adopt aerocost
aeroflames allocs atmohills bigs blockhealth breakblocks buildmenu burn camdiag clean clock crossfade curvecull cutgrid
debugdraw debugpanel devbtn devfar encounters fakevel flightdump floaters fps framedv freegrids front gc gen gotosite gps
gpsat gravity gridup gridvel hide hidesectors hinge hsfold kick lagstop lagtest look lookat mapcamon mapclick mapdiag
mapview meshrebuild nearcenter npcrel numdialog perf planets proxyring seats sectors sectorvis selrock starproxy
starscale stow sun targets tp turntest ui unified view warpbill warphere. (`flightdump` is used by the aero mod's
tools/flightdump.sh.)

## Dev keys (`key <name>`: `MapInput.DevKeys`, consumed where the real key is read)

| key | read in (Orbital) | scripts |
|---|---|---|
| `period`, `comma`, `slash` | WarpControl.cs | play_capture.sh, play_grid.sh, play_transfer.sh, sweep.sh |
| `n` | CleanMap.Ui.cs | targetshot.sh |
| `escape` | MapMenu.cs | sweep.sh |
| `contactdel` | ContactsTab.cs | contactsave.sh, contactshot.sh |
| `contactrename` | ContactsTab.cs | experiments/showshot.sh |
| `flytest` | FlyTest.cs | loacdiag.sh, structtest.sh, experiments/structprof.sh |

## Statics read and set by name (aeroget / aeroset)

| `Full.Type.Member` | aeroget / aeroset | scripts | declared in (2026-10-15) |
|---|---|---|---|
| `AeroMod.AeroEntryFx.FlightStatus` | get | bakedcheck, experiments/fxlook, experiments/recsuper, experiments/spotshot | Simulation/Integration/AeroEntryFx.cs |
| `AeroMod.AeroEntryFx.TestAxis` | set | bakedcheck, checkall, fxramp, spinfx, experiments/recsuper, experiments/spinshot, experiments/spotshot, experiments/vapourab | Simulation/Integration/AeroEntryFx.cs |
| `AeroMod.AeroEntryFx.TestSpeed` | set | bakedcheck, checkall, fxramp, spinfx, experiments/bakeprof, experiments/recsuper, experiments/spinshot, experiments/spotshot, experiments/vapourab | Simulation/Integration/AeroEntryFx.cs |
| `AeroMod.AeroEntryFx.TestWorld` | set | bakedcheck, spinfx, experiments/spinshot (and the `gen` command, GridGen.cs) | Simulation/Integration/AeroEntryFx.cs |
| `AeroMod.AeroFlameComponent.Probe` | set | experiments/flameprobe | Simulation/Integration/AeroFlameComponent.cs |
| `AeroMod.AeroWind.Enabled` | set | experiments/membisect | Simulation/Integration/AeroWind.cs |
| `AeroMod.FlightField.FixTimeEvery` | set | experiments/tipab | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.NearTipWhy` | get | experiments/herctrail, experiments/tipsweep, experiments/wings | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.TipWhy` | get | combo, trails, experiments/tipab | Simulation/Fx/FlightField.cs |
| `AeroMod.FlightField.Spot` | set | experiments/spotshot | Simulation/Fx/FlightField.cs |
| `AeroMod.FlightField.SpotOn` | set | experiments/spotshot | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.TipScale` | set | experiments/tipsweep | Simulation/Fx/FlightField.cs |
| `AeroMod.FlightField.TipSpread` | set | experiments/tipsweep | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.TipsOn` | set | experiments/tipsweep | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.TrailDebug` | set | trails, experiments/fxlook, experiments/herctrail, experiments/tipab, experiments/tipsweep, experiments/wings | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.VapourGain` | set | experiments/vapourab | Simulation/Fx/FlightFieldBaked.cs |
| `AeroMod.FlightField.VapourOn` | set | experiments/vapourab | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.FlightField.VapourSpeed` | set | experiments/vapourab | Simulation/Fx/FlightField.cs |
| `AeroMod.FlightField.SparkAspect` | set | experiments/fxlook (only as the PRESETS example) | Simulation/Fx/FlightField.cs |
| `AeroMod.FlightField.TipLead` | set | experiments/recsuper (only as the PRE example) | Simulation/Fx/FlightFieldHost.cs |
| `AeroMod.GameStallWarning.Status` | get | structtest | Simulation/Integration/GameStallWarning.cs |
| `AeroMod.LoacWarning.Last` | get | loacdiag, structtest | Simulation/Integration/Loac.cs |
| `AeroMod.StallWarning.Last` | get | loacdiag, structtest | Simulation/Integration/StallWarning.cs |
| `AeroMod.StructureLoads.Damage` | set | structtest | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.Enabled` | set | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.Trace` | set | structtest | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.Breaks` | get | structtest, experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.Wears` | get | structtest, experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.Builds` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.SimUsPerSec` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.SimMaxUs` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.BgMsPerSec` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.EvalsPerSec` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.GridsEvaluated` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.GridsTicked` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.StructureLoads.LastEvalMs` | get | experiments/structprof | Simulation/Integration/StructureServer.cs |
| `AeroMod.ThrustTorque.FlamesEnabled` | set | ctrlcheck (and the `aeroflames` command) | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.ThrustTorque.Probe` | set | ctrlcheck | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.ThrustTorque.ProbeHz` | set | ctrlcheck | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.ThrustTorque.FlameLookups` | get | checkall | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.ThrustTorque.FlameHits` | get | checkall | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.ThrustTorque.FlameLit` | get | checkall | Simulation/Integration/ThrustTorque.cs |
| `AeroMod.AeroApi.Version` | get | test_integrate (a const: must read 8) | Simulation/Api/AeroApi.cs |
| `AeroMod.AeroGridComponent.CracksStarted`, `.CracksDone`, `.CracksRunning`, `.CrackLast`, `.CutsForced` | get | test_integrate | Simulation/Structure/Server/StructureServer.Failure.cs (feat/a-break) |
| `AeroMod.StructureTear.AnchorsMade`, `.AnchorsPeeled`, `.Faults` | get | test_integrate | Simulation/Structure/Server/StructureTear.cs |
| `AeroMod.AeroDebrisDrag.Steps` | get | test_integrate | Simulation/Debris/AeroDebrisDrag.cs (feat/a-debris) |
| `AeroMod.StructureLoads.Breaks` | get | test_integrate (also above) | Simulation/Structure/Server/StructureLoads.cs |
| `AeroMod.ShipStructure.Strength`, `AeroMod.StructureLoads.Damage` / `.Trace`, `AeroMod.AeroEntryFx.TestSpeed`, `AeroMod.FlightField.TrailDebug` | set | test_integrate (its releases) | |
| `AeroMod.AeroBlockSave.Restored`, `.Written`, `.LegacyClaimed`, `.Counts`, `.Last` | get | test_blocksave | Simulation/Dev/AeroBlockSaveDev.cs |
| `AeroMod.AeroBlockSave.DevDump` (`grid` -> `dump 'grid': N block(s) on G grid(s): kind@x,y,z=name:v;...;rb:0/1 ...`), `.DevSet` (`grid\|kind\|index-or-x,y,z\|value` -> `set <token> -> <values>`) | set, then get the answer (`asked...` until the server step gave it) | test_blocksave | Simulation/Dev/AeroBlockSaveDev.cs (text: AeroBlockSaveDevForm.cs) |
| `OrbitalMod.OrbitalApi.Version` | get | test_integrate (a const: must read 3) | Simulation/Api/OrbitalApi.cs |
| `OrbitalMod.WarpBill.Bills`, `.BilledSeconds` | get | test_integrate | Simulation/Server/WarpBill.cs |
| `OrbitalMod.WorldReset.Count` | get | test_integrate | Simulation/Host/WorldReset.cs |
| `OrbitalMod.StructureAlerts.Sections`, `.Status` | get | test_integrate | Simulation/Hud/StructureAlerts.cs |
| `OrbitalMod.StationKeepCharge.Status` | get | test_integrate | Simulation/Server/StationKeepCharge.cs |
| `OrbitalMod.FrameHost.SeatGridServerId` | get | experiments/fxlook, experiments/herctrail, experiments/tipsweep | Simulation/Host/FrameHost*.cs |
| `OrbitalMod.FrameHost.Seated` | get | reentry_e2e | Simulation/Host/FrameHost*.cs |

## Text the scripts grep (keep the wording)

**settingsui.sh** (2026-10-15) greps the `settingsui report` reply: `Mods tab: found`, `<ModId> rows N/M` per section, `OrbitalMod: N rows built, registry shows M`, `selected True` (Ui/SettingsTab.cs `MakeReport`); and `setting <name>`'s `<name> = <value>`.

**status.txt** (`%TEMP%\OrbitalMod\status.txt`, DevHarness.WriteStatus): the ` => ` reply marker and `> verb` echo
lines; line prefixes `server=` (relaunch.sh: the game is up), `shot ` (orb.sh --eshot), `frame server` (perf_phase.sh),
`modcost` (test_perf.sh: `part avg/max`), `orbit frame #N ... Pe X km  Ap Y km` (test_regress, test_save, test_smoke,
targetshot), `  frame #N ... anchor=` (test_joints, test_save: `a=..km e=..`), `relnow` (test_regress), `time ... rails
t=` (test_smoke), `mapview` (targetshot, test_smoke: not `map closed`/`no map`), `bodymarkers` (contactshot), `planet N`
(globeshot), `gravityMultiplier` (combo).

**Command replies** matched by text (the reply's wording is the contract): `findgrids` -> `id=Name` pairs (names with
spaces as `_`: `Blue_Fighter`, `SZ1_-_Hercules`, `F-7_Interceptor`, `Tupolev_Tu-134_Big`, `MC-82_FireFly`);
`gridinfo` -> `=> grid N 'Name' server`, `dyn False`; `exitcheck` -> `seated True`, `server d X m dv Y`, `you #F`, `ship #F`,
`hud #F`, `graph hidden True`, `FAIL ...`; `seatid` -> `seating in`; `hudcheck` -> `hudcheck PASS`; `inv` -> `inv: none`,
`predictions judged N`; `hold` -> `holding Prograde: X deg`; `skcharge` -> `holding`/`free, ... 0 charged`, `suit billed
... taken|refused: creative`, `holdingStation(you) True card shown`; `noderound` -> `noderound SAME`, `auto/target`
(the node signature); `sunrel` -> `light local (x,y,z)`, `frame #F`/`frame planet`, `game sun az .. el .. -> az .. el ..`,
`Verdure's chart`; `fxcheck` -> `bake ms ... baked-ms avg X max Y` and its counters (bakedcheck, fx, fxramp);
`hints status` -> `overlay N (M faces)`; `rvtab on` -> `porkchop best`; `sensorblocks` -> `Telescope: kind yes ...
unlocked True ... Radar: kind yes ... unlocked True`, `N in the world (N working)`; `radarpower` -> `radar power 25 % draw
115`; `contactstab` -> `Contacts:`/`Hidden:` groups, `[key ...]` rows, `pinned=True`, `hidden=True|False`, `contacts: N`;
`targets` -> `[grid:N 'Name' yours`, `| list [..`, `| ships ..`, `| under ..`, `target .*[key]`; `targetpanel` -> `panel
shown`, `Orbit: Apoapsis=...Periapsis=...Inclination=`, `From you: Closest approach=`; `hiresglobe status` ->
`materials=ok`, `mesh=ok`, `pinned 12`, `error=`; `warm` -> `warm berths:`, `(N free`; `frameof` -> `in frame #N`,
`anchor <id>`; `save` -> `save -> 'Orbital Test World':` (test_blocksave: `save -> '<world>': Success`).

**Game log** (Orbital `Event`/`Log` lines and aero log lines): tags `[ORBIT-FAULT]`, `[AERO-FAULT]` (every suite's
"no mod fault"), `[ORBIT-INV] VIOLATION prediction`, `[ORBIT-CONTACT] ... rocks spotted`, `[ORBIT-PARTICLES]`
(`particles` census), `[ORBIT-REENTRY]`, `[HITCH] server tick gap`, `[AERO-STRUCT]` (`built`, `broke`, `damage`),
`[AERO-STRUCT-T]`, `[AERO-PITCH]`, `[AERO-THRUST]`, `[AERO-FLAME]`, `[FLIGHT] t=` / `subject bound` / `SETUP`
(AeroFlightTest); frame events `ARRIVE frame`, `ARRIVE grid frame ... capped`, `ATTACH N grid(s)`, `MERGE frame #A -> #B:
N grid(s) moved`, `SPLIT grid N`, `SPLIT player`, `STOW grid N`, `RESTORE from save: N frame`, `saved state captured: N
frame`, `saved state found`, `ROCKS out: Oblivara #N (frame F)`, `ROCKS away: ... composition is the game's again`, `ring
rock Oblivara #N near`, `site frame #N kept as a plain frame`, `SOI Verdure -> Palatine`, `grid N 'Name' and 1 joined
entit`, `not moved: joined to something static`, `gridlaunch: grid N .*refused`, `moved N grid(s) in the teleport
phase`, `has no air`, `arrival of frame`, `crossing fade`, `spawn guard: frame`, `+StationKeepCharge`, `auto: burn
complete`, `Warp stopped`, `warp x10 -> x1`, `warp xA -> xB: a member`, `server planet prefab`, `sensor block prefab.*
Telescope|Radar`, `radar setting restored: transmit power 25`, `known: N grid(s), N rock(s)`, `backdrop`, `sectorless
map scene`, `map scene toggle`. test_integrate.sh: `[ORBIT] world reset:` (and `another world's planets`), `[ORBIT]
beacon Root Entity`, `[AERO] Injected`, `[ORBIT-STRUCT] seated ship:`, `[AERO-STRUCT] ... failed whole - N of M members cracked
across it`, `did not separate`, `[AERO-TEAR] ... failed`, `[AERO-COST] blocks|patch|sim|draw X ms/s`, `Warp stopped`; status
`warpbill`, `time ... server=N ... rails t=T xW`, `modcost ... client A/M/P server A/M/P ... alloc NMB`, `orbit frame #`;
game text `GameCoreScene UnloadAllScenes GameServer` (the world-reset audit), stack frames `at AeroMod.` / `at OrbitalMod.`. Game text: `beacon Root Entity` / `Loading Failed` (world loaded), `not migrated` (Havok),
`Crash Handler]: Fatal`, `Exception occurred`.

## Suspect references (found 2026-10-15)

- test_regress.sh `never "border stow: no dirty-slot move" "moved to clear slot"`: that log line was removed with the
  dirty-slot moves (Orbital 599b70b); the check can no longer fail. Candidate for removal or a new event to watch.
- airless.sh waits for `ARRIVE.* -> Palatine\|ARRIVE player`: no `ARRIVE player` event exists any more (the first
  alternative still matches `ARRIVE frame`).
- combo.sh and globeshot.sh send `status` - not a verb (`unknown command`); it only pokes the harness so status.txt is
  rewritten. Harmless.
