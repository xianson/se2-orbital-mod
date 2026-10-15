# Core/ - the engine-free orbital core (namespace SEAerospace)

Ported from SE-Aerospace. No engine types beyond SE2's math library (VRage.Library): the offline suites in `Tests/`
compile these files literally (each `.csproj` includes folders of it by path - update them if a file moves).

- `Orbital/` - Kepler elements, propagation, gravity bodies, patched conics, frames of reference.
- `Frames/` - proximity frames, the frame registry, rails (drain/fold), berths, membership rules, the player's orbit,
  the rendezvous plot and tracker.
- `Berths/` - the world layout of the system (planet cells, shells, the rotating surface chart), voxel berths.
- `SystemDef/` - the star system's definition and builder, sample systems, asteroids and encounters.
- `Rendezvous/`, `Time/`, `Entry/`, `Sensing/`, `Persistence/` - intercepts and Lambert, the universe clock and warp,
  reentry's rules, sensor models, the save format.

Threading: none of its own; callers hold `ServerFrames.FramesLock` where they share a registry.
