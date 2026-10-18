[h1]Orbital Mod[/h1]

Real orbits for Space Engineers 2. Your world's planets become a small solar system: they circle their star, their moons circle them, and a ship that climbs out of a planet's space goes into orbit, on rails, exactly where it is. Plan burns on the map, warp time, meet ships, sectors and asteroids by really arriving in the same place at the same time, and come home through a re-entry that brakes and heats your ship.

[h2]Before you start[/h2]
[list]
[*][b]Add [i]-loadScripts[/i] to the game's launch options.[/b] Space Engineers 2 (2.4) runs mod scripts only with it. In Steam: right-click Space Engineers 2 > Properties > General > Launch Options, type [i]-loadScripts[/i]. Without it the Telescope and Radar blocks load but the mod itself never runs.
[*]Space Engineers 2 version 2.4.x (made on 2.4.0.95).
[*]Single player or a listen host. Dedicated servers are not supported yet.
[*]Optional: the [b]Aerodynamics Mod[/b]. Not needed; with it you also get re-entry plasma (with heat, load and scorching), structural-failure alerts for the ship you fly, and its build and flight hints on the HUD.
[/list]

[h2]What you get[/h2]
[list]
[*][b]A solar system.[/b] Planets orbit their star, moons orbit their planets, planets spin with the world's day, and gravity falls off with distance as real gravity does.
[*][b]Orbits on rails.[/b] A planet's own space ends near the top of its atmosphere. Below it, flying is the game you know; above it, your orbit is simulated exactly - around the planet, a moon or the star - even while nobody is there.
[*][b]Arrivals by conjunction.[/b] Sectors, asteroid belts and other ships have orbits too. When your orbit meets one, you arrive there at your true relative position and speed, and the game spawns that sector's encounters around you.
[*][b]Time warp[/b] from x1 to x10,000, on rails only. It stops by itself before arrivals, planned burns, re-entry and sphere-of-influence changes. It is not free: batteries and tanks run at the warp rate.
[*][b]Maneuver planning on the game's map:[/b] your path drawn ahead, burns you place and drag, targets with their closest approach, a Rendezvous tab with a porkchop plot. Burns can fly themselves.
[*][b]Re-entry.[/b] Speed over the world's cap is braked away in the upper air, and the energy shed heats your ship's front. Normal transfers stay within tolerance; much faster arrivals wear the forward armour. The orbit card warns you ahead of time.
[*][b]Sensors:[/b] a passive [b]Telescope[/b] (sunlit or warm targets, far off, silent) and an active [b]Radar[/b] (everything in range - and others see you). What you can target is what you have seen. The map's GPS tab becomes a Contacts list.
[*][b]Speed cap[/b] raised from 300 to 1,000 m/s inside a planet's space; on rails, nothing caps you.
[/list]

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
Creative quick launch (Ctrl + T seated) is in the [b]Aerodynamics Mod[/b]; with both mods, this one moves the ship for it and keeps it under the planet's space.

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
Nothing happens (no orbit card, no warp, no Orbital section under Settings > Mods)? The game was started without [i]-loadScripts[/i]. Anything else: please post your game log (%APPDATA%\SpaceEngineers2\Temp\Logs\, the newest SpaceEngineers2_*.log); the mod's lines start with [noparse][ORBIT[/noparse].
