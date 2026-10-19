[i]"Earth is the cradle of humanity, but one cannot live in a cradle forever."[/i] - Konstantin Tsiolkovsky

Real orbits and rendezvous. Some gamifying involved; but it should scratch the KSP itch. Your planets become a solar system you travel by orbit.

[b]Source, wiki and bug reports:[/b] [url=https://github.com/xianson/se2-orbital-mod]GitHub[/url] - [url=https://github.com/xianson/se2-orbital-mod/wiki]Wiki[/url] - [url=https://github.com/xianson/se2-orbital-mod/issues]Issues[/url]

[h2]Requirements[/h2]
[list]
[*][b]Space Engineers 2, version 4.0.x[/b] (made on 4.0.0.3151).
[*][b]The launch option -loadScripts.[/b] Steam > Library > right-click Space Engineers 2 > Properties > General > Launch Options, type [b]-loadScripts[/b]. Without it the game loads the mod's blocks but never runs its scripts: planets stay put, nothing goes on orbits and there is no Orbital section under Settings > Mods.
[*][b]Single player or hosting a game.[/b] Dedicated servers are not supported yet.
[*][b]Works on its own.[/b] Pairs with the [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3822388536][b]Aerodynamics Mod[/b][/url]: re-entry plasma, heat and scorching, structural-failure alerts for the ship you fly, and build and flight hints on the HUD.
[/list]

[h2]Features[/h2]
[list]
[*][b]Planets[/b] - Orbit their star, with moons and spin.
[*][b]Space flight[/b] - Orbits on rails above each planet, simulated even while you're away.
[*][b]Travel[/b] - Arrive where your orbit meets the target; time warp up to x10,000.
[*][b]Map[/b] - Plan burns, targets and rendezvous; burns can fly themselves.
[*][b]Re-entry[/b] - Fast arrivals brake in the upper air and heat your hull.
[*][b]Targeting[/b] - Only what your Telescope or Radar has seen.
[*][b]Asteroids[/b] - Belts on orbits; stray untouched generated asteroids are removed (can be turned off).
[*][b]Speed cap[/b] - 1,000 m/s near planets, none on rails.
[/list]

[h2]Not RSS[/h2]
This mod functions differently from the popular orbit mod combo, realistic orbit / RSS. It is an orbital mechanics-first mod, so you will always be experiencing gravity anywhere you go with the exception of Lagrange deadzones (gamified Lagrange points). You should expect the experience to be closer to KSP, minus the planet speed-limit.

[h2]Keys[/h2]
[table]
[tr][th]Key[/th][th]Does[/th][/tr]
[tr][td]. and ,[/td][td]time warp faster / slower (on rails)[/td][/tr]
[tr][td]/[/td][td]warp back to x1[/td][/tr]
[tr][td]N[/td][td]target what is under the crosshair, or the cursor on the map[/td][/tr]
[tr][td];[/td][td]attitude hold on / off (seated)[/td][/tr]
[tr][td]Ctrl + ;[/td][td]hold mode: prograde, retrograde, normal, anti-normal, radial, level[/td][/tr]
[/table]

On the map, right-click your path to add a maneuver; drag its handles to add delta-v.

[h2]Old worlds and other mods[/h2]
[list]
[*]Back up a world before adding the mod. Removing it from a world that used it is not supported.
[*]Mods that move planets, change the speed cap or replace the map may conflict.
[/list]

[h2]Settings[/h2]
In a world: [b]pause menu > Settings > Mods[/b], section [b]Orbital[/b]. World settings (planet day, re-entry, asteroid belts, encounters, ring rocks, time warp limit and cost, [i]Remove stray generated asteroids[/i]) are saved with the world and changed by the host. Your own settings (distant planet globes, rings, HUD, map) are per PC. Both are also in [i]%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.*.cfg[/i].

[i]Remove stray generated asteroids[/i] (on by default) deletes the game's own procedural asteroids that slipped past the mod - never one a player edited, one a grid or player is near, or anything it cannot prove the game generated. Each deletion is written to the game log. Turn it off to keep every asteroid.

[h2]Known limits[/h2]
[list]
[*]No dedicated servers yet.
[*]In an existing world, things already parked in open space outside every planet's space stay as the game has them (they would fall into the planet on an orbit).
[*]The 1,000 m/s speed cap applies to every world while the mod is loaded.
[*]No automatic route planning: you place the burns.
[*]Removing the mod from a world that used it is not supported.
[/list]

[h2]Troubleshooting[/h2]
Nothing happens (no orbit card, no warp, no Orbital section under Settings > Mods)? The game was started without [i]-loadScripts[/i]. Anything else: please post your game log (%APPDATA%\SpaceEngineers2\Temp\Logs\, the newest SpaceEngineers2_*.log); the mod's log lines are tagged ORBIT.

[h2]Licence[/h2]
Free to use in any world, server or video, and free to depend on. Re-uploads only unlisted, private or friends-only. Code may be reused except in a competing aerodynamics or orbital mod; credit appreciated, not required. Commercial use (including by game developers) needs a paid licence. Full text: [url=https://github.com/xianson/se2-orbital-mod/blob/master/LICENSE]LICENSE on GitHub[/url].
