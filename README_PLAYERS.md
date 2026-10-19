# Orbital Mechanics Mod for Space Engineers 2 - player guide

Real orbits for Space Engineers 2. Your world's planets become a small solar system: they circle their star (and
their moons circle them), and a ship that climbs out of a planet's space goes into orbit, on rails, where it really
is. You plan burns on the game's map, warp time to get there, meet other ships, sectors and asteroids by actually
arriving in the same place at the same time, and come back down through a re-entry that brakes and heats your ship.

## Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **The `-loadScripts` launch option.** SE2 2.4 runs mod scripts only when the game is started with it; without it
  the mod's blocks load but nothing else happens, silently. In Steam: right-click Space Engineers 2 > Properties >
  General > Launch Options, and add `-loadScripts`.
- **Single player or a listen host.** A dedicated server is not supported yet (see Known limits).
- **Optional: the Aerodynamics Mod.** The Orbital Mod does not need it. With it installed, you also get:
  - re-entry plasma on ships braking on rails, with the heat, load and hull scorching of a real entry,
  - an alert when the ship you fly breaks up ("Structural failure: 2 sections broke away, 41 blocks"),
  - the build and flight hints on the HUD.
  Without it, all of that is simply absent; everything else works the same.

## What it does

- **A solar system.** The planets orbit their star; moons orbit their planets. Planets spin with the world's day.
  Gravity falls off with distance the way real gravity does.
- **Orbits on rails.** A planet's own space ends near the top of its atmosphere (about 9.5 km up at Verdure; a little
  above the hills on airless moons). Below that, flying is the game as you know it. Climb past it and your ship's
  orbit is simulated exactly, around the planet, the star or a moon, even while nobody is there.
- **Getting somewhere.** Sectors, asteroid belts and other ships have orbits too. When your orbit meets one (a
  conjunction) you arrive there, at your true relative position and speed, and the game spawns that sector's
  encounters around you.
- **Time warp.** x1 up to x10,000, only on rails. Warp stops by itself before arrivals, before a planned burn, before
  re-entry and at a sphere-of-influence change. Warp is not free: batteries and tanks run at the warp rate.
- **Maneuver planning on the map** (terminal, Map tab): your path drawn ahead, burns you place and drag, a target with
  its closest approach, a Rendezvous tab with a porkchop plot for planet transfers. A burn can fly itself
  ("Auto-burn: on" in its right-click menu): the ship turns to the burn and fires when it starts, until it is done. If
  it can't, it stops and says why ("no thrust along the burn", "can't turn to the burn"); out of the seat it pauses.
  The orbit card shows the burn's length and when it starts; warp stops ahead of it and says so ("burn in 30 s").
  Ships landed on a planet show as landed (no orbit, no Ap/Pe); their closest approach is to where they stand.
- **Re-entry.** Coming in from orbit, speed over the world's cap is braked away in the upper air; the energy shed heats
  the front of your ship. Normal transfers stay within tolerance; a much faster arrival wears the forward armour.
  The orbit card warns you ahead ("ENTRY 2:46 · 1,669 → 990 m/s · heat 82%": heat is how much of what your hull takes
  the pass puts in; over 100% the forward blocks wear). A planned deorbit shows its entry on the map and where it comes
  down ("Lands on Verdure near ..."); a periapsis just above the air says how low it must go.
- **Sensors.** Two new blocks, unlocked from the start: the **Telescope** (passive; spots sunlit or warm things far
  away, gives nothing away) and the **Radar** (active; ranges everything in reach, but others see you). What you can target beyond your own
  things is what you have seen. The map's GPS tab becomes a Contacts list.
- **Speed cap.** Inside a planet's space the speed cap is raised from 300 to 1,000 m/s; on rails nothing caps you.

## Keys

| key | does |
|---|---|
| `.` / `,` | time warp faster / slower (on rails only) |
| `/` | warp back to x1 |
| `N` | target what is under the crosshair (in flight) or the cursor (on the map); again: the next thing there |
| `;` | attitude hold on / off (seated) |
| `Ctrl` + `;` | pick the hold mode: prograde, retrograde, normal, anti-normal, radial out / in, level |
| `Del`, `F2` | on the Contacts tab: hide a row / rename it |

On the map: right-click your path to **Add maneuver**, right-click a maneuver to remove it or **Edit exact values...**
(prograde, normal, radial in m/s, e.g. `120, 0, -5`), drag its six handles to add delta-v. Notices (a target set, a
warp stop, a burn done or stopped, contacts) come as a toast in flight, titled by what they are about, and as a line on
the map.

**Quick launch** (`Ctrl` + `T` seated, creative worlds) is part of the **Aerodynamics Mod** now (its settings: Settings >
Mods > Aerodynamics, Quick launch and Controls). With both mods loaded, this mod moves the ship for it and keeps it under
the planet's space; ships on rails are not moved.

## Settings

In a world: **pause menu > Settings > Mods**, section **Orbital** (one Mods tab is shared with the Aerodynamics Mod).
Basic settings are shown; each group's advanced ones are under "Advanced". Every setting has its description in the
screen's side panel and "Reset to defaults" per group.

**World settings** are saved with the world; only the host (or you, in single player) can change them:

| setting | default | what it does |
|---|---|---|
| Planet day length | the world's own | how long a planet takes to spin once; 0 = planets do not spin (applies after a reload) |
| Re-entry | on | ships coming in from orbit are braked and heated in the upper air; off: they arrive at orbital speed |
| Orbiting asteroid belts | on | belts and their rocks on orbits (applies after a reload) |
| Orbiting encounters | on | the game's encounter sites on orbits (applies after a reload) |
| Ring rocks | on | rocks in planetary rings you can rendezvous with (applies after a reload) |
| Remove stray generated asteroids (`World.DeleteGeneratedAsteroids`) | on | deletes the game's own procedural asteroids that slipped through (spawned before the mod blocked them, or left behind by an encounter moved off a planet's border). Only untouched ones nobody is near: never one a player edited, one a grid is on or within 100 m of, one a player is within 300 m of, an asteroid the mod placed, or anything it cannot prove the game generated. Each deletion is written to the game log. Turn it off to keep every asteroid. |
| Maximum time warp | x10,000 | the highest warp the keys, the warp bar and other mods can ask for |
| Warp uses energy | on | warping drains batteries and tanks for the time skipped |
| Warp stops before events | 30 s | how long before a burn or a planet's air warp drops to x1 (5 s before an armed burn the ship already points at) |
| Advanced: Orbits in the spawn area, Aerobraking strength, Encounters near sectors, Rocks per ring | | see each one's description |

**Your own settings** (this PC only): distant planets drawn as globes, planet rings, the orbit line and card, HUD
markers, the align button, the orbital map, maneuver nodes, map text size, the Contacts tab, the attitude hold's turn
rate and more.

The same settings are also in two text files, re-read every couple of seconds:
`%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.client.cfg` (yours) and `OrbitalMod.world.cfg` next to it (a host's
overrides for its worlds; a world setting forced there shows locked in the menu).

**Saved with the world:** your orbit and every ship's, the clock, maneuver nodes, your target, contacts, and the
Contacts tab's pins, names and hidden rows.

## Known limits

- **No dedicated servers yet.** Single player and listen hosts only.
- **Old worlds:** the mod works in an existing world, but things already sitting in open space outside every planet's
  space (for example near the spawn area) stay as the game has them, not on orbits: they are at rest relative to the
  planet and would fall in. ("Orbits in the spawn area" puts them on orbits anyway.)
- **The speed cap** is raised to 1,000 m/s for every world while the mod is loaded (it is set before a world loads).
- **No automatic route planning.** You place the burns; an armed burn flies only the burn you placed.
- **A short stall** (a fraction of a second) can happen when a ship lands in a fresh piece of space far from the
  others; the mod keeps a few such places warmed up to soften it.
- **Removing the mod** from a world that has used it is not supported: ships on orbits are kept far out in space
  between visits, and without the mod they stay wherever it last put them.
- Tested by its author in single player; a whole flight (take off, orbit, transfer, arrive, land) is checked in
  pieces by automated tests. Please report anything odd, with your game log
  (`%APPDATA%\SpaceEngineers2\Temp\Logs\`, the newest `SpaceEngineers2_*.log`): the mod's lines start with `[ORBIT`.

## Troubleshooting

- **Nothing happens** (no orbit card, no warp, the map is the game's own, no Orbital section under Settings > Mods),
  but the Telescope and Radar are in the build menu: the game was started without `-loadScripts` (see
  Requirements). Your game log then has no line starting with `[ORBIT]`.
- **The Mods tab is missing** from Settings: the settings files above still work; the log says why (a line with
  `settings tab: not added`).
- **Something went wrong in flight:** the game log's `[ORBIT-FAULT]` and `[ORBIT-FRAME]` lines say what the mod saw.
