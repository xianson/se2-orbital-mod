# Fusing colonization with orbits: design (revision 2)

Status: proposal, 2026-09-26. Decisions from the user: **a real solar system, no compromises**
(planets orbit the sun; nothing in the model is pinned for the game's convenience) and **no fast
travel at all** (every trip is flown).

## 1. The consequence: the world is storage, not space

Every colonization system is pinned to fixed world coordinates (sector areas, fast-travel
fields, procedural encounter cells, contract targets, discovery triggers). Planets that really
orbit the sun cannot stay in a fixed world layout: Verdure and Kemik have different periods, so
their separation changes all the time. Therefore the SE2 world cannot be the solar system.

It becomes what the frames design already makes it: **a set of windows.**
- Each **planet cell** is a window on the space around a planet (built).
- Each **conjunction berth** is a window on a ship in transit (built).
- New: each **region cell** is a window on a piece of authored deep space: a colonization
  sector's content (its fast-travel fields, contract boards, encounters), pinned where it is
  authored, exactly as the planet cells are pinned where the planets are authored.

The empty world space between windows means nothing any more. Nothing flies through it: you
leave a window by stowing onto the rails and enter another by arrival. Because every window
keeps its authored coordinates, everything the game pins in the world keeps working inside its
window: sector membership, contract placement, encounter seeds, discovery, fields.

## 2. The solar system

- The star at the root; planets on real heliocentric orbits; moons on real orbits around their
  planets (built: Palatine around Verdure, Caligo around Kemik).
- **Region cells get real orbits too.** A deep-space sector is a real place in the system. The
  physically honest homes, all stable:
  - **Trojan clusters** at a planet's L4/L5 (60 degrees ahead of or behind it on its orbit);
  - **belt regions** on their own heliocentric orbits between or beyond the planets;
  - **high planetary orbits** for sectors that belong to a planet (inside its SOI).
- The authored campaign layout says which sectors are near which planet; that decides each
  region's home (see section 6).

## 3. Travel is orbital, always

- Leaving any window (planet, region, berth) is a stow onto the rails; arriving is a crossing
  of the target window's shell, with the exact state. Built for planets; region cells reuse it
  with a region radius instead of a planet shell.
- Deep space is conjunction space: ships coast on conics about the deepest SOI (patched
  conics, SOI reparenting: built), with warp.
- Transfers between planets are real Hohmann/Lambert transfers with **launch windows**: the
  planets move, so when you leave decides how much it costs and how long it takes.
- Speed caps are irrelevant: ships in frames carry any velocity on the rails.

## 4. Colonization gameplay on real orbits

1. **No fast travel.** Every trip is flown: stow, coast (with warp), arrive. The mod disables
   the game's fast travel (its fields stay as landmarks and discovery points, but never teleport).
   The campaign's lanes stay only as drawn routes between sectors, if at all.
2. **Planning is the player's job, with tools.** A transfer planner on the orbital map (Lambert
   and intercept planning are in the core): pick a destination window, see the next launch
   window, its delta-v and duration, and a maneuver marker to burn at. Launch windows open and
   close as the planets and regions move.
3. **Reachability as progression.** Each route has a delta-v at the current window. Far regions
   need better engines as well as contracts. The map shades what your ship can reach now.
4. **Orbital contracts.** Targets can be given real orbits within a window (salvage a derelict
   on a drift, intercept a cargo ship, deliver into a capture orbit).
5. **Encounters stay authored.** Procedural and static encounters live in their region window
   at their authored coordinates. Cargo ships get real trajectories.

## 5. Display

Two maps, each honest about what it is:
- **The orbital map is the truth:** the sun, planets and moons at their real positions, every
  region at its real position, sector boundaries drawn around each region, your orbit and
  planned transfers, launch-window countdowns. This is the KSP map, with the colonization
  sectors on it.
- **The colonization map becomes the network:** the authored layout stays as a schematic
  (like a subway map), with the orbital data overlaid between sectors: next window, delta-v,
  duration. Its geometry can no longer be physical, because real positions change every hour.
  (Moving the sectors on it to real positions is possible through public setters, but sector
  membership uses the same coordinates, so the content would fall out of its own sectors.)

## 6. Mapping the campaign onto a real system

The campaign layout (15 sectors over about 15,000 km, two planets 3,975 km apart) is a
snapshot, not a physical system. We choose:
- **The system's scale:** where Verdure and Kemik orbit (today's model: 160,000 km and
  100,000 km from a small star). Larger orbits mean longer transfers and more warp.
- **Each sector's home:** for example, sectors nearest Verdure become Verdure Trojans or high
  Verdure orbits, those nearest Kemik likewise, the outermost ones a belt beyond Kemik.
- **Region radius:** the window around each sector's content (sectors are 1,000 to 2,500 km
  across; the window can cover the authored content, not the whole polygon).

## 7. Phases

1. **Region cells:** a pinned window per deep-space sector, with a model orbit; stow and
   arrival for region windows; legacy space outside every window is no longer flyable.
2. **Transfers:** a player transfer planner (Lambert, launch windows, maneuver markers) and
   warp to arrival; the game's fast travel disabled.
3. **Orbital map with sectors:** regions and sector outlines at true positions, planned
   transfers, window countdowns. Colonization map overlay: next window, cost, duration per route.
4. **Gameplay:** reachability shading, orbital contract targets, interceptable cargo ships.

## 8. Open decisions

- System scale (transfer times against warp).
- Home of each deep-space sector (Trojans, belts, high planetary orbits).
