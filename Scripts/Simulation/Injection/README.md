# Injection/ - components and limits added at definition load

- `Injections.cs` - the base for adding components to prefab compositions (the ScriptingTemplate pattern).
- `PlanetInjector.cs` - the orbital components into the prefabs: server planets get `ServerPlanetBeacon`, client planets
  `PlanetFrameComponent` (separate compositions: the game's validator rejects client types on server ones), server grids
  `OrbitalGridComponent`, thrusters `StationKeepCharge`, the telescope / radar blocks their components.
- `SpeedLimitInjector.cs` - the world's speed caps raised to 1000 m/s (grids and characters).
