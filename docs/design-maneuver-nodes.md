# Maneuver nodes

KSP-style planning on the unified map: place burns on your orbit, see the resulting trajectory
(through SOI changes), see how close it brings you to a target, then fly the burn with a HUD
marker. Fast travel stays as it is.

## What already exists

- `Core/Rendezvous/InterceptPlanner.Plan(myState, targetOrbit, mu, now, caps, options)`: direct
  Lambert sweep, phasing, and Hohmann-then-phase; returns a `ManeuverPlan` of `Maneuver`s
  (time from now, world delta-v, and its prograde / radial / normal components) plus an arrival
  prediction (time, miss distance, relative speed). Same-SOI only (one mu).
- `Core/Rendezvous/Lambert`, `ClosestApproach`, `ArrivalErrorBand`.
- `Core/Orbital/PatchedConic.Propagate(body, state, t0, horizon)`: conic arcs through the SOI
  tree (escape, encounter), and `SamplePath` for drawing.
- The player's orbit: the frame's elements on the rails, `FrameHost.TryGetLocalOrbit` in a planet
  cell. Warp already stops at arrivals (`AdvanceClock`), so it can stop at nodes the same way.
- The map already draws orbits, picks by what it draws, and knows the selected sector's site.

## Input (researched)

- Key bindings: not reachable (they live in `Keen.VRage.Input`, not referenceable by mods).
- Raw polling is: `Keen.VRage.Core.Input` (`IInputDevice.GetDigitalState`, `KeyboardInputs.*`,
  `MouseInputs.Left/Right/VerticalWheel/Position`), devices from
  `Keen.VRage.Core.Platform.IPlatformInput` (to verify: `[Service]` or `VRageCore.Instance.Engine`).
  Edges (new press) are ours to track. Mouse position: `IPlatformWindows.Window.ClientMousePosition`
  (already used for picking).
- Map clicks go through Avalonia to `OnSelectSector`; clicking our own handles resolves to no
  sector, so the two do not fight.
- Notifications: `IUserMessages.DisplayMessage` or `InGameUI.ShowNotification`.

## Design

**Node**: absolute time on the current trajectory + delta-v (prograde, normal, radial). A list per
player, saved in the orbital state.

**Preview**: from the trajectory state at the node time plus the delta-v, `PatchedConic.Propagate`
over a horizon; drawn dashed on the map, one colour per arc, with SOI patch points marked. With a
target (the selected sector's site, or an encounter frame): closest approach on the previewed
trajectory, both positions marked, "3.2 km in 2 h 14 m, 41 m/s".

**Creating nodes**:
1. *Auto-plan* (first): select a sector, press the plan key (polled) or click a "Plan" row we
   draw: `InterceptPlanner` to its site's orbit gives the burns as nodes; the preview shows the
   predicted arrival. Same SOI first (all of Kemik's sectors from Kemik orbit).
2. *Manual editor* (second): click your orbit line to add a node (picked like the sectors);
   six drag handles around it (prograde / retrograde, normal / anti-normal, radial in / out),
   drag distance sets the delta-v rate; the wheel over the node slides it along the orbit;
   right-click deletes.

**Flying a node**: a HUD marker (as the frame-transferred GPS markers) in the burn direction, in
the window's axes (chart-aware in a planet cell): "Burn 42.1 m/s, T-3:12". Remaining delta-v
counts down from the thrust actually folded into the rails (or the velocity change in a planet
cell); the node completes below 0.1 m/s. Warp stops a lead time before the node.

**Interplanetary (later)**: `InterceptPlanner` is single-mu. Kemik to Verdure needs a patched
planner: heliocentric Lambert between the planets over a departure/arrival sweep (a light
porkchop), then the escape hyperbola's v-infinity turned into a periapsis burn at the departure
planet. `PatchedConic` already previews the result.

## Milestones

1. Nodes + preview + auto-plan within an SOI + HUD burn marker + execution + warp stop.
2. Manual editor (click to add, drag handles, wheel to slide, right-click delete).
3. Interplanetary planner.

## Built: the manual editor (milestone 2)

`Frames/Maneuvers.cs`, `Frames/MapInput.cs`.

- **Input**: the mouse device from `VRageCore.Instance.Engine.Single<IPlatformInput>()`, polled
  each frame (`GetDigitalState(MouseInputs.Left/Right)`), edges tracked; the position is the
  window's `ClientMousePosition` (as the map's own picking).
- **Map** (system and solar views): the trajectory through every node is patched-conic
  (`PatchedConic.Propagate`), the planned part dashed. Hover the trajectory: "add node (in T)";
  click adds a node there. A node: ring; the selected one gets six handles (P / R, N / AN on
  the diagonals since normal points out of the map, RO / RI). Pull a handle: delta-v grows at
  0.5 x (pull / 20 px)^2 m/s per second (20 px 0.5, 60 px 4.5, 120 px 18; sizes scale from
  1080p). Drag a node along the trajectory to slide it; right-click deletes. Readout: delta-v,
  time to go, P / N / R, the post-burn Pe / Ap (or escape). With a sector selected, the closest
  approach to its site is marked on both paths: distance and time.
- **Target**: a node's P / N / R fix a target post-burn orbit when set (editing re-targets);
  burning does not move it, so what is left is the target's velocity minus the current orbit's
  at the node point (as KSP). Tested: 50 m/s node, +30 m/s burned -> 20.3 left, +20 more ->
  3.7 left (the burn was 45 s early, so some is left, as it should be).
- **Flight**: the next node is a HUD marker in the burn direction (chart-aware in a planet
  cell), "Burn 25.0 m/s  T-2:51", edge-clamped like the game's markers; it completes below
  0.1 m/s. Warp stops 30 s before a node.
- While the editor has the mouse (over the trajectory, a node or a handle, or dragging), the
  map does not select a sector.
- Saved: `node T pro nor rad` lines (re-targeted from the loaded orbit).
- Harness: `node add/list/select/clear/pull/clickat/rclick`, `framedv`, `devbtn`.
