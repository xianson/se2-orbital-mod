# Dev/ - the harness and test tools (DEV only)

Runs only when `%TEMP%\OrbitalMod\dev.flag` exists (`OrbitalConfig.DevHarness`). Players never run it.

- `DevHarness.cs` - the command file protocol (`%TEMP%\OrbitalMod\cmd.txt` -> `status.txt`), polled from the client frame.
  `Execute` parses a line and runs the player / view / orbit / warp commands; the rest are grouped in partials, tried in
  order through `default:` - `.Grids`, `.Map`, `.Systems`. Command names are what `tools/harness/*.sh` send: never rename.
- `DevFlight` (fly a ship without a human), `DevReentry` (reentry checked every tick), `DevHudCheck`, `DevTurn`,
  `DevStress`, `DevTestShip`, `FlyTest` (Ctrl+T), `GridGen` / `HingeRig` (generated grids, hinges), `DebugPanel` (F8 map menu).
- Threads: commands arrive on the client thread; anything touching server grids is queued to the server step
  (`ServerFrames.DevCommands`, `GridGen.ServerTick`).
