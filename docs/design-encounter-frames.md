# Encounters as conjunction frames

An encounter is a conjunction frame. Everything the game spawns or authors outside the planet
cells lives in frame space, with an orbit, and you reach it by conjunction (your orbit meets its
orbit), never by flying through the meaningless world space between windows.

## Sites (authored encounters, sector anchors)

- A **site** is a latent frame (no lattice slot): its berth is the encounter's authored world spot,
  so the encounter never moves and everything the game pins there (mission areas, contract
  triggers, procedural sectors) keeps working.
- Its orbit is **prescribed**: its sector's home (ellipse about its planet, Lyapunov loop about
  L1/L2, belt or ring orbit about the sun, Trojan) plus the site's offset inside the sector, held
  in the home's co-moving radial/along/normal axes and scaled like every orbit (`OrbitScale`).
  The frame's elements are re-osculated from that ephemeris every tick.
- **Sector anchors**: every deep-space sector gets a site at its charted centre, even when empty.
  Reaching a sector's orbit drops you into its region, where the game spawns the sector's
  procedural encounters around you as usual; they are adopted into the site.
- Anything unframed in a site's bubble (20 km) belongs to the site: grids, the player.
- Sites are rebuilt from the world on load (not saved); their members are re-adopted.

## Conjunction

- The merge screen (closest approach over 30 min, sticky rendezvous tracker, 10 km / 290 m/s
  gates) runs between a player-carrying frame and any other frame. NPC-only frames never screen
  against each other; two sites never merge.
- **Host priority**: a site (never moves) > a frame with players > an NPC-only frame. The incomer
  is moved into the host's berth at its true relative position and velocity: arriving at a site
  puts you at your real offset from it.
- Leaving: past the slot radius (20 km) a player grid or the player splits into a frame of its
  own on the rails (a lattice berth).

## Procedural spawns

A grid that appears outside the planet cells near a frame's berth is framed by distance:

- **Close** (within 5 km of a berth, or anywhere in a site's bubble): joins that frame, i.e. a
  similar orbit; it floats alongside.
- **Far**: it (and everything spawned with it, 2 km cluster) gets a frame of its own on a
  slightly different orbit: the spawning frame's state plus its offset plus a deterministic
  2-15 m/s kick, mostly in the orbit plane. The relative orbit is eccentric; the encounter drifts
  off and may come round again as a conjunction.
- The game's procedural lifetime still applies: when the player leaves the originating procedural
  sector the game despawns the encounter wherever it is; an emptied encounter frame dissolves.

## Dynamics

- Frames follow orbital dynamics (the rails).
- Player grids and characters inside a frame feel the relative-motion terms: differential gravity
  minus the frame acceleration (CW), and in a spinning planet cell Coriolis + centrifugal.
- **NPC grids do not.** They are plain Newtonian inside their frame (their frame of course has
  orbital dynamics). This keeps NPC behaviour (autopilot routes, AI) simple at the local scale.
  NPC grids are never frame anchors, never drained, never stowed on their own, never attached to a
  stowing player, never split off.
- Encounter frames have no anchor: their origin is the berth, their orbit their own.

## GPS markers across frames

A GPS marker is a world position, and a world position belongs to a window: a planet cell, a
frame's berth / site bubble, or plain world space.

- Same window as the camera: the world position is the truth; the game draws it.
- Another window: the marker is **frame-transferred**. Its model position (the window's orbit
  position plus its offset there) is taken relative to the camera's model position and drawn at
  that relative location from the camera, with the true distance (edge-clamped when off screen).
  The game's copy is hidden meanwhile and shown again when the windows agree.
- Markers the mod hid are recorded in the save and shown again first on load.
- Covered: the player's GPS list, its groups, and contract HUD markers.

## Harness

`encounters`, `gotosite <i> [behindKm]`, `devsite <gridId|0> <sector>`, `gps`,
`gpsat <marker> <encounter> [offsetKm]`.
