# Encounters/ - sectors, sites and spawns

- `EncounterFrames.cs` - encounters are conjunction frames (docs/design-encounter-frames.md): authored sites built from the
  world's sectors, NPC / encounter classification of grids, site ephemerides, pruning. `ServerTick` runs inside the
  server step (`ServerFrames.TickBody`, under FramesLock); the client reads published copies (`Sites`, `SiteOf`, `RideOf`).
  Partials: `.Lagrange` (Lagrange sectors as rides), `.Spawns` (the game's procedural spawns put into frames), `.Dev`.
- `SectorHomes.cs` - where each colonization sector lives (a body, a ring, a Lagrange point).
- `SpawnGuard.cs` - no encounter spawns on a planet's border (the generator's spawn spots kept off berths).
