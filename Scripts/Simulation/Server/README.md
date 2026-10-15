# Server/ - the server half of the frames

Everything that touches the server's grids. Runs on the server scene's thread, ticked by `ServerPlanetBeacon`'s jobs
(every planet's beacon runs them: `ServerFrames.Tick` runs one step at a time and once per game frame, claimed by game
time - `Core/Time/FrameClaim`, as `WarpBill` does; the beacon's own once-a-frame work is claimed by `ClaimFrame`). Frame state is shared with the
client half (`Host/FrameHost`) under `ServerFrames.FramesLock`; nothing calls into the aero mod under that lock.

- `ServerPlanetBeacon.cs` - the server planet component: its two jobs are the entry points. `TeleportJob` (before the
  physics step: `ServerFrames.RunDeferredMoves`, `WarpBill.Run`) and `BeaconJob` (after: `ServerFrames.Tick`, DEV
  `GridGen`, the player's queued `PlayerRequest`, gravity changes). Also `PlanetBeacons`, the server/client twin registry.
- `ServerFrames.cs` - `Tick` / `TickBody` (the order of the whole server step), constants, warp limits, requests from the
  client half. Partials by concern:
  `.GridFrame` (one frame's step: anchor, members, warp steering, the exact differential gravity, splits), `.Merge` (the merge screen, its
  slow part off the lock), `.Stow` (grids onto the rails), `.Arrival` (grid frames into a planet cell, grid HighSpeed),
  `.Moves` (every long grid move, deferred to the teleport phase; `AeroApi.NoteMoved`), `.Forces` (spin's fictitious
  forces, station keeping), `.Views` (what the client may read: `GridView`, sensor views, positions, the harness
  snapshot), `.Invariants`, `.DevCommands` (harness requests).
- Per-frame and per-grid state: a frame leaving the registry (every `Dissolve`, a merge's incomer) runs
  `ServerFrames.FrameRetiring` (wired as `FrameRegistry.Retiring` in `SystemHost.Build`) - anchor acceleration, entry
  state, a dissolved frame's members' entry wear; a grid gone for good runs `ServerFrames.Forget`. Per-WORLD state goes
  through `Host/WorldReset`, never a reset of its own.
- `GridMembers.cs` - `OrbitalGridComponent` (injected into server grids) and the grid registry: positions, velocity
  writes (recorded for `Api/GridEdits`), joined sets.
- `SavedState.cs` - the orbital state in the world save (carried by the planets' name-map builder).
- `WarmBerths.cs`, `StationKeepCharge.cs`, `WarpBill.cs`, `SensorBlocks.cs` - berths kept warm (no stall), holding
  station billed to thrusters, warp billed to batteries/tanks (once per game frame, for its game seconds; `Bills` /
  `BilledSeconds` for the harness), the telescope and radar blocks.
- `Invariants.cs` - the rules checked all the time (`[ORBIT-INV]`); the server's check is `ServerFrames.CheckInvariants`.
