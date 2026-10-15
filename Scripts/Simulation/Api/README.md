# Api/ - what other mods may call

- `OrbitalApi.cs` - the public API (`docs/API.md`): the system, frames and their orbits, a grid's frame, reentry, the
  settings. Other mods reach it by reflection; member names and signatures are a contract (`tools/check_contract.sh`
  checks both directions against the aero mod). Never rename; add a new name for a new shape.
- `GridEdits.cs` - what this mod wrote into each grid's physics velocity (`OrbitalApi.TryGetGridEdits`), recorded at every
  write.

## Version 2

`OrbitalMod.OrbitalApi` is one `partial` class: version 1's members are in `OrbitalApi.cs`, version 2's in `OrbitalApi.<Area>.cs`, one file per area. Other mods reach it by reflection; the contract (no overloads, allowed types, every member in `docs/API.md`) is checked by `Tests/ApiTests` and `tools/check_contract.sh`.

- `OrbitalApi.Peek.cs` - the frames without the frame lock (the snapshot, refreshed by readers with `Monitor.TryEnter`).
- `OrbitalApi.World.cs` - bodies (system state, orbit, spin, cell) and world <-> body-relative conversion.
- `OrbitalApi.Grids.cs` - a grid's own state, orbit, summary, status.
- `OrbitalApi.Orbits.cs` - conic math on any element set (eight doubles).
- `OrbitalApi.Time.cs` - warp levels, request, stop time.
- `OrbitalApi.Player.cs` - the local player's orbit, maneuver nodes, next burn, attitude hold, target, closest approach.
- `OrbitalApi.Sites.cs` - sites and contacts.
- `Core/` - engine-free parts, compiled offline by `Tests/ApiTests`: `FrameTable` (the Peek snapshot, a sequence lock) and `OrbitQueries` (the pure orbit math).

Threads: every member may be called from any thread; which lock each takes is in its summary and in `docs/API.md`. Nothing here is called by the mod itself.
