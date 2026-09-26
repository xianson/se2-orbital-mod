# Orbital Mod (SE2)

Orbital mechanics for Space Engineers 2, the SE2 counterpart of the SE1 project at
`D:\SE-Aerospace`. Status as of 2026-09-25, SE2 **2.4.0.77**. Separate from the Aerodynamics Mod on purpose:
the two share almost nothing, and players can run either alone.

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
- **Spectator camera (harness):** `cam planet|player|map ...` overrides the render camera
  (CameraComponent.SetTransformOverride) and rebuilds the proxies for that viewpoint. Screenshot 12.
- **Total partition incl. legacy space (opt-in):** with `OrbitalConfig.CaptureLegacySpace` (harness
  `legacy on`) the player and every dynamic grid outside all planet cells are captured into frames,
  treating that space as a window around the nearest planet. Off by default: objects there sit at
  rest relative to the planet, so they fall in (verified: 11 grid groups captured, fell, merged,
  arrived at Verdure at ~1340 m/s in HighSpeed).
- **Radial states:** zero-angular-momentum states (at rest, or straight in/out) are captured as a
  needle ellipse (tiny perpendicular nudge) instead of NaN elements. Verified: a=370.7 km e=1.000.
- **Lone grids** in a planet cell stow into their own frame when they leave the keep (or cross above
  the shell on an escaping arc), taking grids within 5 km along.
- **Warp never skips an arrival:** the rails clock stops exactly at the earliest inbound shell
  crossing and drops to x1, so arrival works at any tick rate.

Remaining limits: local player only (client-driven; SP / listen host), HighSpeed thrust
verified only as no false folds plus a harness kick (no real key press; grid thrust untested), legacy-space capture is opt-in, planets do not spin (no rotating surface chart), sun direction not driven, relative motion of members is integrated at x1 while the rails warp.

## Open items, highest value first

1. **Proxy fidelity.** The map globe is low-poly and pale, and has no atmosphere rim (compare screenshots 02 and 03).
   It is also sized from the base radius, about 5% small next to the real limb. Options: size from the surface
   radius, a denser runtime mesh, or reusing the planet's own low-res clipmap far away.
2. **Real orbital gravity.** The runtime switch exists (dev command). Still needed: prove bodies follow it
   (a free-floating grid, or dampeners off), decide whether it becomes the default, and handle saves.
   GravityGeneratorComponent serializes the patched law, so a world saved while patched keeps it.
3. **Orbit display with real motion.** Verified with harness `fakevel` (synthetic velocity, screenshots 07 and 08).
   With real motion it still needs a moving body: the jetpack dampeners hold the player still.
4. **Dedicated-server pairing** of client planets to their server data.
5. **SE-Aerospace's frames and rails** (berths, time warp, rails teleport). The core is here. The runtime
   still needs its SE2 design.
