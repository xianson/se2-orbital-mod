# Dev/ - the harness and test tools (DEV only)

Runs only when `%TEMP%\OrbitalMod\dev.flag` exists (`OrbitalConfig.DevHarness`). Players never run it.

- `DevHarness.cs` - the command file protocol (`%TEMP%\OrbitalMod\cmd.txt` -> `status.txt`), polled from the client frame.
  `Execute` parses a line and runs the player / view / orbit / warp commands; the rest are grouped in partials, tried in
  order through `default:` - `.Grids`, `.Map`, `.Systems`. Command names are what `tools/harness/*.sh` send: never rename.
- `DevFlight` (fly a ship without a human), `DevReentry` (reentry checked every tick), `DevHudCheck`, `DevTurn`,
  `DevStress`, `DevTestShip`, `FlyTest` (harness key `flytest`; its Ctrl+T is now the aero mod's creative quick launch - `Flight/QuickLaunch.cs` forwards the harness key `quicklaunch` to it), `GridGen` / `HingeRig` (generated grids, hinges), `DebugPanel` (F8 map menu).
- Threads: commands arrive on the client thread; anything touching server grids is queued to the server step
  (`ServerFrames.DevCommands`, `GridGen.ServerTick`), and every read of a server entity - a server grid's place, velocity,
  mass, motion, components, the server session's entities, the sensor blocks - runs there too (`GridGen.OnServerThread`:
  "not run: ..." while the server is not ticking), NEVER waited for in the frame: the in-process server's next tick starts
  only after the client's frame, so a wait there is a deadlock until its timeout. The command answers `ServerAsks.Pending`,
  its client side after the answer goes in `GridGen.Then`, and the poll finishes it (and holds the file's later lines) once
  the server ran it - `ServerAsks`, `Tests/HarnessTests`. Polled reads (findgrids, nearcenter, seat, thrust) answer from what
  the server step publishes: `ServerFrames.ViewValues` (id, name, place, dynamic, mass), `ServerFrames.GridPositions`.
  Spawning too (`gen spawn|measure|add`, `testship`, the hinge heads and their blocks): on the server thread, staged and
  async (IEntitySpawner.SpawnBundleAsync; GridGen finds the new grid by its name a tick later) - a synchronous spawn from
  here raced the server's tick and crashed the game, one inside the server's job failed ("Data not found"), 2026-10-19/20.
  `Tests/SceneTests` (section 4) checks it from source: no synchronous spawn in Dev/, every SpawnBundleAsync on the server thread.
