# Orbital Mod - release notes

For players. The developer-facing history (commits, API changes, internals) is in [CHANGELOG.md](CHANGELOG.md).

## 1.0.0 - first public release

Needs Space Engineers 2 2.4.x started with the `-loadScripts` launch option (see [the player guide](README_PLAYERS.md)).

**What is in it**
- A solar system made from your world's planets: planets on orbits around their star, moons around their planets,
  planet spin with the world's day, gravity that falls off with distance.
- Orbits on rails above each planet's atmosphere; arrivals at sectors, asteroids and other ships by conjunction, at
  your true relative position and speed.
- Time warp x1 to x10,000 that stops by itself before arrivals, burns, re-entry and sphere-of-influence changes, and
  costs your ship's energy.
- Maneuver planning on the game's map: burns you place and drag, targets and closest approach, a Rendezvous tab with a
  porkchop plot, burns that fly themselves, an attitude hold (`;`).
- Re-entry: braking and heating in the upper air, with warnings ahead of time; heat wear on the forward armour only
  for arrivals far faster than a normal transfer.
- Telescope and Radar blocks, a Contacts list in place of the map's GPS tab, contact toasts.
- Asteroid belts, ring rocks and encounter sites on orbits.
- An in-game settings menu: pause menu > Settings > Mods > Orbital (shared with the Aerodynamics Mod).
- With the Aerodynamics Mod (optional): re-entry plasma, structural-failure alerts, build and flight hints.

**Good to know**
- Single player and listen hosts only; no dedicated servers yet.
- The game's own procedural asteroids that slip past the mod are removed when nobody is near them
  (`Remove stray generated asteroids`, on by default; each one is written to the game log). Asteroids a player edited
  or is near are never touched. Turn the setting off to keep every asteroid.
- The speed cap is 1,000 m/s inside a planet's space while the mod is loaded.
- A quiet game log: the mod writes its startup summary, warnings and real failures only (about twenty lines in an
  hour of play).
