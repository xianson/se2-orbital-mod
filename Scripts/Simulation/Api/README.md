# Api/ - what other mods may call

- `OrbitalApi.cs` - the public API (`docs/API.md`): the system, frames and their orbits, a grid's frame, reentry, the
  settings. Other mods reach it by reflection; member names and signatures are a contract (`tools/check_contract.sh`
  checks both directions against the aero mod). Never rename; add a new name for a new shape.
- `GridEdits.cs` - what this mod wrote into each grid's physics velocity (`OrbitalApi.TryGetGridEdits`), recorded at every
  write.
