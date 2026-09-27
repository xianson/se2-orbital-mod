# Orbital Mod (SE2)

Orbital mechanics for Space Engineers 2, the SE2 counterpart of the SE1 project at
`D:\SE-Aerospace`. Status as of 2026-09-27, SE2 **2.4.0.77**. Separate from the Aerodynamics Mod on purpose:
the two share almost nothing, and players can run either alone.

## Playing it (2026-09-27)

**The system.** Verdure and Kemik orbit the sun (SE-Aerospace's scale rule, then orbits a further
10x smaller: Verdure's year ~3.6 days, Kemik's ~6.8, a transfer ~2.5 days). Palatine orbits Verdure
at ~380 km; Caligo orbits Kemik at ~585 km (3x its charted distance). The colonization sectors are
places in this system: orbits about their planet, L1/L2 loops, the belt, the Byblos ring.

**Getting somewhere.** Leave a planet's space and your orbit goes on the rails. Every sector is a
site on its orbit; when your orbit meets it (a conjunction) you arrive there, at your true relative
position and speed, and the game spawns that sector's encounters around you. Brown-dwarf killing
fields are kept clear. Fast travel is untouched.

**Keys.** `.` faster time warp, `,` slower, `/` back to x1 (rails only; warp stops by itself at
arrivals and before a burn).

**Map (terminal Map tab).**
- Left click a sector's orbit, marker or list row: select it (the game's side panel).
- The list gives each sector a quick guide from your orbit: delta-v and trip time.
- Your trajectory is drawn patched-conic: each SOI in its own colour, encounters around a ghost of
  the body where you meet it, periapsis / apoapsis tagged.
- Right click the trajectory: **Add maneuver**. Right click a maneuver: **Remove maneuver**, or type
  prograde / normal / radial exactly (the game's number dialog). Right click anywhere: remove all.
- A selected maneuver has six navball handles: drag one to add delta-v along it (the further you
  pull, the faster); drag the maneuver along the trajectory to move it; click a handle's number to
  type it.

**In flight.** The game's notification card shows your orbit (altitude, speed, periapsis /
apoapsis, period, warp) and the next burn (delta-v, burn time, time to start). A navball marker
points along the burn; the countdown runs to half the burn before the node; the node completes
when what is left is under 0.1 m/s. GPS markers in another place (another planet, a sector) are
drawn in their true direction with the true distance.

**Encounters.** Encounters live on orbits too: close spawns stay with you, far ones get an orbit of
their own; NPC ships fly plain Newtonian inside their frame.

Design notes: docs/design-colonization-orbits.md, docs/design-encounter-frames.md,
docs/design-maneuver-nodes.md.

## Frames and rails (the SE-Aerospace design) - verified in game 2026-09-25

The same design as SE-Aerospace (planet cells + conjunction berths, rails, the treadmill, the
interaction shell, HighSpeed), ported. Verified in game with the harness:

- **System from the world:** Verdure and Kemik become bodies around a synthetic star, mu matching the
  patched 1/r² physics (incl. the world gravity multiplier 2); planet cells pinned where the planets
  sit; the conjunction lattice 2000 km beyond the farthest planet.
- **Stow:** leaving Verdure's keep (113 km) on an escaping arc captures the player into a
  conjunction frame and teleports them into a berth (0 g there).
- **Treadmill:** in the berth the player is pinned, velocity drained and folded into the rails; the
  planets are proxies at the frame-relative celestial offset; the frame orbit is drawn from the rails.
- **Warp:** rails-only; locks to x1 whenever the player is materialized.
- **Arrival:** the rails orbit crossing the shell (75.6 km) hands the player back to the real planet at
  the crossing, with the exact crossing state.
- **HighSpeed (analytic):** above the 300 m/s cap the player rides the conic, placed each tick.
- **A full skimming revolution:** Ap 200 / Pe 8 km -> rails -> ARRIVE at 1262 m/s -> HighSpeed through
  periapsis at exactly 71.0 km -> STOW again at the 79.4 km floor. Orbit after two handoffs:
  a 166.6 km e 0.574 (was 167.0 / 0.575).

- **Ships as frame members (server side):** grids within 5 km of a stowing player join its frame;
  the heaviest dynamic grid is the anchor (pinned, drained, folded into the rails on the server);
  the other members and the player get the berth's differential gravity minus the frame
  acceleration (Clohessy-Wiltshire); a member beyond 20 km splits into its own frame. Verified:
  the Nova35 wreck (5 grids, 90 t anchor) plus the player stow together, warp, arrive together in
  HighSpeed (offsets held at 0.32-0.34 km through periapsis) and re-stow together.
- **Merge (a rendezvous, as in SE1):** same-SOI frame pairs are screened for closest approach over
  30 min; miss < 10 km and rel speed < 1000 m/s feed a sticky tracker (2 s dwell), and a latched
  pair merges when it is also within 10 km and below the cap now. The lighter frame moves into the
  heavier one's berth at the celestial relative state. Verified: the player's frame merged into
  the wreck's (sep 1.18 km, 3.7 m/s).
- **Thrust in HighSpeed:** the character's per-frame thrust impulse (ActiveThrustData) is folded
  into the conic by re-osculating; unexplained jumps above 2 m/s (collisions) are folded from the
  velocity. With no input the orbit is unchanged (0 folds over a periapsis pass); a harness 50 m/s
  prograde kick at periapsis gave a 167.0 -> 229.7 km (vis-viva: 229.8). The physics velocity
  alone cannot be used: the thrust job zeroes components below the movement minimum speed, which
  swallows a frame of gravity.
- **Clock on game time:** the rails and the server's tidal integration run on IGameTime (synced
  client/server, follows game speed, pauses with the game), so the rails and the physics agree.
- **Persistence:** frames, the rails clock and HighSpeed conics are saved with the world, on every
  planet's beacon component (planets always exist). The builder is the engine's own
  EntityNameSessionComponentObjectBuilder (string -> Entity): mod-defined builders cannot compile,
  because the serializer generator always emits code needing System.Linq.Expressions. Member ids
  are remapped through saved entity references. Verified: a frame with 5 grids and the player was
  saved and reloaded intact (orbit, clock t=150, all members). The harness `save` only ever writes
  the "Orbital Test World" copy.
- **Orbital map (KSP style):** SE2's strategic map only exists in worlds with colonization
  sectors, so the mod has its own map mode. It opens automatically with the terminal's Map tab
  when there is no colonization map (or `omap on`), switches the renderer to map-only drawing
  (the world hidden, as the strategic map does) and builds a diorama at the player: focus on the
  planet you orbit (its textured map globe, keep ring, every frame's orbit with Ap/Pe, "you + N
  grids") or the whole system (sun, planet globes on heliocentric orbits, SOI rings). Where the
  colonization map exists, orbits are also drawn over it (MapView). Screenshots 09-11.
- **Campaign (colonization) map overlay:** in worlds with sectors (a new SurvivalNOFTUE game, 15
  sectors) the orbits draw on SE2's own map: zoomed in, the orbit around the planet's globe (screenshot
  13); zoomed out, the star system over the sectors (14). Vertices are offset to the map entity
  (MeshBuilder.SetPrimitiveOffset + UpdateEntityTransform): the map is ~1 m across hundreds of km from
  the origin, where float positions only resolve centimetres.
- **Moons:** a body within 1000 km of one 10x heavier orbits it (campaign: Palatine around Verdure,
  Caligo around Kemik); overlapping planet cells go to the nearest centre; each body's gravity reach
  stops short of its neighbours' SOI.
- **Constrained grids are never moved:** teleporting one body of a constrained pair (landing gear,
  connectors) broke Havok's constraint migration and hung the server; attach and lone-grid stow skip them.
- **Spectator camera (harness):** `cam planet|player|map ...` overrides the render camera
  (CameraComponent.SetTransformOverride) and rebuilds the proxies for that viewpoint. Screenshot 12.
- **Total partition incl. legacy space (opt-in):** with `OrbitalConfig.CaptureLegacySpace` (harness
  `legacy on`) the player and every dynamic grid outside all planet cells are captured into frames,
  treating that space as a window around the nearest planet. Off by default: objects there sit at
  rest relative to the planet, so they fall in (verified: 11 grid groups captured, fell, merged,
  arrived at Verdure at ~1340 m/s in HighSpeed).
- **Radial states:** zero-angular-momentum states (at rest, or straight in/out) are captured as a
  needle ellipse (tiny perpendicular nudge) instead of NaN elements. Verified: a=370.7 km e=1.000.
- **Planet spin (rotating surface chart):** planets spin with the world's own sun period (9600 s
  in the test world; `OrbitalConfig.PlanetDaySeconds` overrides). SE2 voxels cannot rotate, so the
  whole planet cell is the rotating chart (terrain at rest); inertial states convert where objects
  leave or enter the voxel world (stow, arrival, HighSpeed placement, thrust folds), free flight in
  the cell gets Coriolis + centrifugal (above 20 m/s or 5 km), the observer frame carries the spin
  so proxies and the sun turn with the day. Verified: the grid-frame cycle (arrival, HighSpeed,
  re-stow of the player + 5 grids) preserves the orbit exactly as without spin (a 168.6 km).
- **Sun from the model:** see above; in a spinning cell the sun rises and sets with the chart.
- **Lone grids** in a planet cell stow into their own frame when they leave the keep (or cross above
  the shell on an escaping arc), taking grids within 5 km along.
- **Warp never skips an arrival:** the rails clock stops exactly at the earliest inbound shell
  crossing and drops to x1, so arrival works at any tick rate.

Remaining limits: local player only (client-driven; SP / listen host), HighSpeed thrust
verified only as no false folds plus a harness kick (no real key press; grid thrust untested), legacy-space capture is opt-in, relative motion of members is integrated at x1 while the rails warp.

## Open items, highest value first

1. **Multiplayer.** The client host drives only the local player and shares static state with the
   server half, so it works in single player and on a listen host. A dedicated server needs the
   player's frame logic server-side and a server-to-client command channel for character moves
   (characters are client-authoritative). No dedicated server is installed here to test with.
2. **A piloted ship through the whole loop** (take off, rails, plan, burn with thrusters, arrive by
   conjunction). Everything above is verified with the character; seated players now get their
   grid's frame (observer, warp, planning), but no one has flown it yet. Also the map editor with a
   real mouse (verified through the harness only).
3. **Real key-press test of HighSpeed thrust** (jetpack and piloted grids). Folding is verified with
   no false folds and a harness kick only.
4. **Proxy fidelity.** The map globe is low-poly and pale, with no atmosphere rim.
5. **Gravity patch as the default.** The 1/r^2 patch is applied at runtime and saved with the world;
   decide whether a world without the mod's first load should be patched automatically.
6. **Warp and member motion.** While the rails warp, members' relative motion runs at x1 (as KSP
   keeps vessels on rails); fine for coasting, not for long warps of loose formations.
7. **Ship shipping checklist.** `PlanetFrameComponent.DevHarness` must be false in a release build.
8. **Old map code.** `Frames/OrbitalMap.cs`, `Frames/MapView.cs` and the band path in
   `Frames/UnifiedMap.cs` are superseded by the clean map (gated off); removing them waits on a go-ahead.
9. **Route planner** (`RoutePlanner.cs`: same-SOI intercepts, interplanetary porkchop + Newton
   shooting + capture) is kept but not in the UI (harness `route <sector>`).
