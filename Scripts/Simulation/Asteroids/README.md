# Asteroids/ - belts and rings

- `AsteroidFrames.cs` - every belt holds a fixed set of asteroid frames reached by rendezvous. `ServerTick` (under
  FramesLock: which rocks are wanted) then `ServerAfterLock` (the generator's slow work, off the lock). Partials:
  `.Rocks` (rocks made real near you and put away), `.Volumes` (the compositions reserved for them).
- `RingRocks.cs` - the rings' rocks as numbers (seeded orbits, nothing stored, nothing spawned): your planned path's passes
  near them are rendezvous on the map and the flight disc.
- `AsteroidBridge.cs` - the game's own asteroids switched off (its volume definitions get a negative density for the
  session; ones already spawned deleted through the generator), our asteroid frames' reserved compositions exempt;
  distances to asteroids for frames. (Known, pending a decision: `IsManual`, docs/REFLECTION_AND_WHITELIST.md.)
