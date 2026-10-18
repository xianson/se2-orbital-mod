# Orbital Mechanics Mod for Space Engineers 2

> *"Earth is the cradle of humanity, but one cannot live in a cradle forever."* - Konstantin Tsiolkovsky

Real orbits for Space Engineers 2. Your world's planets become a small solar system: planets circle their star, moons
circle their planets, and a ship that climbs out of a planet's space goes into orbit, on rails, exactly where it is.
Plan burns on the game's map, warp time, meet ships, sectors and asteroids by really arriving in the same place at the
same time, and come home through a re-entry that brakes and heats your ship.

**Version 1.0.0** · SE2 2.4.x · single player and listen hosts ·
**[Wiki](https://github.com/xianson/se2-orbital-mod/wiki)** · Steam Workshop: [subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3822389424)

## Features

- **A solar system:** planets on orbits, moons around them, planet spin, gravity that falls off with distance.
  [Orbits and Planets](https://github.com/xianson/se2-orbital-mod/wiki/Orbits-and-Planets)
- **Orbits on rails** above each planet's air, simulated exactly even while nobody is there.
- **Time warp** x1 to x10,000 that stops itself before anything important and costs energy.
  [Time Warp](https://github.com/xianson/se2-orbital-mod/wiki/Time-Warp)
- **Maneuver planning on the map:** your path ahead, burns you place and drag, targets and closest approach, a
  Rendezvous tab with a porkchop plot, auto-burns, attitude hold.
  [Map and Maneuvers](https://github.com/xianson/se2-orbital-mod/wiki/Map-and-Maneuvers)
- **Arrivals by conjunction** at sectors, asteroid belts, ring rocks and other ships.
  [Encounters and Asteroids](https://github.com/xianson/se2-orbital-mod/wiki/Encounters-and-Asteroids)
- **Re-entry** braking and heating, predicted ahead of time.
  [Re-entry](https://github.com/xianson/se2-orbital-mod/wiki/Re-entry)
- **Telescope and Radar blocks** and a Contacts list: you can only target what you have seen.
  [Sensors and Contacts](https://github.com/xianson/se2-orbital-mod/wiki/Sensors-and-Contacts)
- **Flight HUD:** orbit card, orbit line, markers across planets, warp bar.
  [Flight HUD](https://github.com/xianson/se2-orbital-mod/wiki/Flight-HUD)
- **Speed cap** raised to 1,000 m/s inside a planet's space; none on rails.
- **Quick launch (creative)** is the Aerodynamics Mod's (Ctrl+T seated); with both mods, this one moves the ship for it
  and keeps it under the planet's space.
- **In-game settings:** pause menu > Settings > Mods > Orbital.
  [Settings](https://github.com/xianson/se2-orbital-mod/wiki/Settings)
- **Works with the [Aerodynamics Mod](https://github.com/xianson/se2-aerodynamics-mod)** (optional): re-entry plasma,
  structural-failure alerts, build and flight hints.
  [Using with the Aerodynamics Mod](https://github.com/xianson/se2-orbital-mod/wiki/Using-with-the-Aerodynamics-Mod)

## Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks but never runs its scripts.
  The option turns on scripts for every mod you load.
- **Single player or a listen host.** Dedicated servers are not supported yet.
- Optional: the Aerodynamics Mod.

## Install

1. Subscribe on the Steam Workshop (link above).
2. Add **Orbital Mod** to the world's mod list.
3. Start the game with `-loadScripts`, load the world, and check for the **Orbital** section under pause menu >
   Settings > Mods.

Step by step: [Getting Started](https://github.com/xianson/se2-orbital-mod/wiki/Getting-Started) ·
Keys: [Keys and Controls](https://github.com/xianson/se2-orbital-mod/wiki/Keys-and-Controls) ·
Problems: [Troubleshooting and FAQ](https://github.com/xianson/se2-orbital-mod/wiki/Troubleshooting-and-FAQ) ·
Bugs: [issues](https://github.com/xianson/se2-orbital-mod/issues) (attach your game log:
`%APPDATA%\SpaceEngineers2\Temp\Logs\`, the newest `SpaceEngineers2_*.log`).

## Known limits

- No dedicated servers yet.
- Removing the mod from a world that has used it is not supported.
- The 1,000 m/s speed cap applies to every world while the mod is loaded.
- No automatic route planning: you place the burns.

## Credits

By xianson. The orbital core is ported from the author's Space Engineers 1 project, SE-Aerospace.


## Licence

Free to use in any world, server or video, and free to depend on. Re-uploads only unlisted, private or
friends-only. Code may be reused except in a competing aerodynamics or orbital mod; credit appreciated, not required.
Commercial use (including by game developers) needs a paid licence. Full text: [LICENSE](LICENSE).

---
Developing or building the mod yourself? See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
