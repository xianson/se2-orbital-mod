# Host/ - the client half and the shared runtime

- `FrameHost.cs` - the local player's frame (SE2 simulates the character client-side). `Tick` is called once per client
  frame from `PlanetFrameComponent` (client thread) and drives the client's guarded systems. Partials: `.Stow` (onto the
  rails, the harness orbit commands), `.Rider` (the treadmill and the rider in a frame), `.Arrival` (into a planet cell;
  the server half's requests), `.Teleport`, `.Seat` (the seated player's orbit), `.Crossing` (camera / shake / cockpit
  target across a chart crossing), `.Guard` (faults logged and skipped, per-system cost, the DEV allocation watch).
- `SystemHost.cs` - builds and owns the system from the world: registry, universe clock (`AdvanceClock`, warp), frames.
- `OrbitalSettings.cs` - the mod's settings (world scope rides `SavedState`, client scope a file); `docs/API.md`.
- `ModCost.cs` - `ModCost` / `TickRate`: what the mod's code costs per frame and the tick rates (the `modcost` status
  line, `[HITCH]`).
- `Num.cs` - `Wall()` and `IsFinite`, shared by server and client/HUD code (`using static OrbitalMod.Num;`).
- `FrameMath.cs` (game-free: the offline FrameMathTests compile it), `PlayerCache.cs`, `Prewarm.cs`, `WarpControl.cs`
  (the warp keys).
