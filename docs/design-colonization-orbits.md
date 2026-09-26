# Fusing colonization with orbits: design

Status: proposal, 2026-09-26. Nothing here is built yet.

## 1. The constraint that decides everything

Every colonization system is pinned to fixed world coordinates:

| System | What is fixed |
|---|---|
| Sectors | Centre + weight; the polygon is a 2D power diagram on world X/Z. Infinite prisms in Y. |
| Fast travel | Field transforms, computed once at init; destinations in world coordinates. |
| Procedural encounters | Seeded 4096 m world cells; they materialise within 4 km of a player. |
| Contracts | Targets placed once, in absolute coordinates, around the contract board. |
| Discovery | Trigger volumes subscribed once (planet: its gravity reach; fields: their range). |

So the world must stay a **fixed chart**. Planets cannot move in world space, and neither can
sectors, fields or encounters. Orbits must live in a model that maps onto that chart, the same
way the planet cells are already charts.

## 2. The campaign in numbers (measured)

- Map about 15,000 km across; 15 sectors; only 2 contain a planet.
- Verdure and Kemik are 3,975 km apart; each has a moon about 200 km out (Palatine, Caligo).
- The other 13 sectors are 3,000 to 11,000 km from any body.
- With the world's gravity multiplier, each planet has mu = 7.8e10 m^3/s^2:

| Distance from the pair | Gravity | Circular speed | Period |
|---|---|---|---|
| 5,000 km | 0.006 m/s^2 | about 175 m/s | about 2 days |
| 10,000 km | 0.0016 m/s^2 | about 125 m/s | about 6 days |

- Escape from Verdure's surface: about 1.57 km/s. Speed caps do not matter: ships in frames
  carry any velocity on the rails.

## 3. The chart: which frame is "the world"?

Real physics says Verdure and Kemik would orbit each other every 35 h. The world cannot move
them. Two honest options:

**A. Fixed-centres chart (recommended default).** The world is an inertial frame in which both
planets are fixed attractors (Euler's two-fixed-centres problem). Not strictly physical (the
pair should circle each other), but:
- Authored content "at rest" feels only the true weak pull: about 10 km of drift per hour at
  10,000 km, easily hidden by keeping unpiloted, far-away content on rails (KSP packing).
- Energy is conserved: E = v^2/2 - mu/r1 - mu/r2. Reachability is a clean number.

**B. Rotating chart (realism option).** The world co-rotates with the binary (35 h), which is
exactly the frame in which both planets really are at rest: the circular restricted three-body
problem, with Lagrange points and Jacobi zero-velocity curves. The catch: anything at rest in
the chart feels centrifugal force, 0.025 m/s^2 at 10,000 km, about 150 km of drift per hour. A
parked ship is really moving at about 500 m/s inertially and must station-keep. Correct, but
hostile to the authored campaign.

Both keep the planet cells as local windows (Kepler inside about 240 km, where the other
planet's tidal pull is about 1/2000 of local gravity) and put the star far away (sun direction).

## 4. Deep space: in-place frames

The world is big enough, so deep space needs no berths. A ship leaving a planet cell into deep
space becomes an **in-place frame**: its world position follows its trajectory each tick (like
HighSpeed today), members keep their offsets, and physics velocity stays near zero. Because the
chart is 1:1:
- when your trajectory passes an encounter, you are physically there (it materialises within
  4 km as usual);
- sector membership, discovery triggers and fast-travel fields just work;
- warp advances the trajectory; a proximity lock (as SE-Aerospace's crunch window) drops to x1
  before you pass within a few km of any grid, field or encounter.

Trajectories in deep space are numerically propagated (two attractors, not a conic), with
analytic Kepler inside the cells as now. The core already has the rendezvous kit (closest
approach, Lambert, intercept planner) to build on.

## 5. Gameplay fusion

1. **Sectors as orbital regions.** Each sector gets an energy (and, in the rotating option, a
   Jacobi) threshold: the energy needed to reach its centre from a colonized sector. The map
   shades sectors you can reach with your ship's current delta-v. Colonization progress and
   delta-v progression become the same curve: the far sectors need better engines, not only
   contracts.
2. **Fast travel becomes transfers.** A fast-travel lane becomes a precomputed transfer
   trajectory between two fields. "Travel" puts your ship on that trajectory with warp to
   arrival: time passes, and fuel is spent for the departure and arrival burns (or free when
   both ends are colonized, as a lane service). Vanilla instant travel stays an option.
3. **Contracts in orbit.** Targets can be placed on real trajectories (a frame on the rails)
   instead of a static point: salvage a derelict on a slow drift, intercept a cargo ship,
   deliver into a capture orbit. The existing target placement gives the start point; the
   mod gives it a velocity.
4. **Encounters.** Procedural encounters stay put in the chart (on rails while no player is
   near), so the authored layout survives. Cargo ships, which spawn around the player, get real
   trajectories and become interceptable.
5. **Colonized sectors as infrastructure.** A colonized sector's capital or fast-travel field
   can act as a station: a place where warp is allowed to x1000, where transfers are planned,
   and where a lane service departs.

## 6. Display

The colonization map becomes the system map (one scale, no second layer):
- the planets and moons as today, the moons' orbits drawn around their planets;
- your trajectory propagated ahead (hours to days), with where it crosses each sector and its
  closest approaches to fields and encounters ("Oblivara in 6 h 20 m, 3.1 km from field");
- reachable-sector shading from your energy; transfer lanes with their delta-v and duration;
- zoomed in on a planet: the orbit, apsides and keep ring as now;
- option B only: Lagrange points and zero-velocity curves.

## 7. Phases

1. **Campaign chart.** When the world has sectors, build the fixed-centres model from the real
   layout (pair, moons, far star) instead of the invented heliocentric one. The sandbox world
   keeps the invented system.
2. **Deep-space in-place frames** with numerical trajectories, warp with proximity lock, and
   the arrival back into planet cells.
3. **Display:** trajectory prediction on the colonization map, sector crossings, closest
   approaches.
4. **Gameplay hooks:** reachability shading, fast travel as transfers, orbital contract
   targets, interceptable cargo ships.
5. **Option B** (rotating chart) behind a setting.

## 8. Open decisions

- Fixed-centres (gentle, authored content stays put) or rotating (true physics, station-keeping)?
- Should fast travel cost time and fuel, or stay instant with the transfer only drawn?
- Should far sectors require delta-v (a real progression gate) or only show it?
