# How the Orbital mod works

The end-to-end picture, for someone about to change the code. Every claim names the code that does it (`Type.Member`);
server-side types are named without paths because their folders are being reorganised (2026-10-15). Client folders:
`Scripts/Simulation/Flight`, `Hud`, `Map`, `Render` (each has a README). The engine-free core is `Scripts/Simulation/Core`.

Contents: 1 the model · 2 frames · 3 moving between frames · 4 the server tick · 5 the client tick · 6 time warp ·
7 entry · 8 encounters and sectors · 9 threading · 10 persistence · 11 Core and its tests · 12 the Aero link ·
13 dev tooling · 14 known mismatches between comments and code.

---

## 1. The model: the world is a set of windows

SE2 has one flat world with fixed planets, no orbits and no rotation of voxels. The mod keeps a real solar system
in a model (Core) and uses the SE2 world only as storage: a set of **windows** onto that model.

- **The system** is built from the world by `SystemHost.Build`: every SE2 planet (found through its server beacon,
  `PlanetBeacons`) becomes a body orbiting a synthetic star (`SystemHost.StarMu` = 1.41e13, Delfos). A body's mu
  matches what the engine applies after the mod's inverse-square gravity patch (mu = multiplier x g0 x r0^2). A body
  within 1,000 km of one at least 10x heavier becomes its moon (Palatine round Verdure, Caligo round Kemik). Scale:
  `SystemHost.K` (Verdure plays Earth) x `SystemHost.OrbitScale` (0.1) - a compact, KSP-like system; `SystemHost.AU`
  = 30,000 km. `SystemHost.EnsureBuilt` waits until the beacon count has been stable for `SettleSeconds` (2 s);
  a world whose planets have all gone (another world loaded) is rebuilt.
- **Planet cells** (`VoxelBerthRegistry`, Core/Berths) are pinned where the SE2 planets sit. Inside one, play is the
  game's own physics, in the planet's **rotating chart** (`Chart`): SE2 voxels cannot rotate, so the whole cell turns
  with the planet's day and objects moving in it get Coriolis + centrifugal (`Chart.Fictitious` = -2w x v - w x (w x r)),
  applied by `ServerFrames.ApplyFictitious` (grids) and `FrameHost.ApplyFictitious` (the character), only above
  `OrbitalConfig.FictitiousMinSpeed` (20 m/s) or `FictitiousMinAltitude` (5 km). Positions and velocities are turned
  between the chart and inertial axes wherever something crosses the cell's border (`Chart.ToInertial`,
  `VelToInertial`, `FromInertialRotation`).
- **Conjunction berths** are lattice slots far from every planet (`VoxelBerthRegistry.SharedAllocator`,
  `BerthAllocator`; slot spacing from `PlanetBerths.CellIsolationRadius`, at least 150 km, plus 100 km clearance).
  Each berth hosts one **proximity frame** (`ProximityFrame`, Core/Frames): a group of grids and players sharing one
  Keplerian orbit, simulated around the berth.
- Everything else in world space ("legacy space") means nothing. It is captured into frames only when
  `OrbitalConfig.CaptureLegacySpace` is on (off by default).

There is no floating origin: SE2 positions are doubles. What plays that role is the **treadmill**: a frame's anchor is
held at its berth and its motion is folded into the orbit (section 2), so a ship crossing the system never moves far
in the world.

### Planet borders (Core: `PlanetBerths`)

One radius per body decides every hand-over, `PlanetBerths.ShellRadius` (R_c): the radius plus the atmosphere's height
(`ShellAtmosphereMult` = 1.0: the border **is** the atmosphere's top, ~9.5 km up at Verdure since 2026-10-03), or for
an airless body 0.12 R (`ShellAirlessClearanceFraction`), never under 4 km (`ShellMinClearanceMeters`). All the bands
are multiples of it:

| band | radius | constant |
|---|---|---|
| arrival (rails -> physics) | R_c | - |
| stow floor (physics -> rails) | R_c x 1.05 | `StowShellMargin` |
| keep (the real planet stays loaded / rendered) | R_c x 1.5 | `ShellKeepHysteresis` (`KeepRadius`) |
| keep drop edge | keep x 1.02 | `KeepDropPad` (`KeepDropRadius`) |
| rotating-chart latch | R_c .. R_c x 1.01 | `SurfaceZoneDropPad` |

Inside R_c everything is physical flight under the world speed cap; every orbit above it is on rails.

## 2. Frames and the rails

A `ProximityFrame` carries its orbit (`Elements`, about `ParentBodyName`), its berth (`BerthCenter`), its members
(`Members`) and an anchor (`AnchorEntityId`). The **rails** (`FrameRails`, Core) are that orbit, propagated on the
universe clock (`SystemHost.Now`). The registry is `FrameRegistry` (`SystemHost.Frames` = `FrameRegistry.Active`).

- **The anchor** is the frame's fixed point. `MembershipRules.ElectAnchor` (Core, tested offline) picks it; the kinds
  (`ServerFrames.StaticAnchor`, `UpdateGridFrame`):
  - a static grid in the frame (in at `ServerFrames.CaptureEnterRadius` 8 km, out at `CaptureRadius` 10 km);
  - a rock near the berth (`ServerFrames.AsteroidAnchorId` = -2, found by `AsteroidBridge.NearestAsteroid`) - only on
    a stable orbit (`MembershipRules.StaticAnchorKind`, `ServerFrames.OrbitStable`: periapsis 5 km over the border);
    "a frame is always on a static grid or a rock when one is in it" (2026-10-07; `ServerFrames.RockReach` is now unused);
  - else the heaviest dynamic player grid (the heaviest grid of a joined ship); NPC grids never anchor;
  - else the player (`FrameHost.PlayerId`). Encounter frames have no anchor: their origin is the berth.
- **Pin, drain, fold.** Each tick the anchor is held at the berth (re-pinned by shifting the whole frame past
  `ServerFrames.PinTolerance` 200 m, or `FrameHost.PinTolerance` 50 m for the player), its velocity is read
  (mass-weighted over a joined ship), zeroed, and folded into the orbit (`FrameRails.FoldDrain`, threshold
  `FrameHost.FoldThreshold` 0.03 m/s). Thrust therefore changes the orbit, not the world position. A new anchor does not
  move the orbit (`FrameRails.TryRebaseShift`). Rails are never re-osculated before their epoch (`FrameRails.CanMutateAt`,
  the SOI-commit quarantine; SOI changes in `SoiReparent`).
- **The frame's acceleration** A = the folded slice / (dt x N) is published per frame in `ServerFrames.AnchorAccel`
  (by the server for a grid anchor, by the client when the player anchors).
- **Fictitious forces on members.** Every other member gets the berth's differential gravity minus A as a velocity
  change: `(Grav(rA + rRel) - gA) - A` in `ServerFrames.UpdateGridFrame` (exact tidal difference; the comments call it
  "CW", but `ClohessyWiltshire` is not called at runtime), and the same for an on-foot rider in `FrameHost.UpdateRider`.
  Inside a Lagrange region a `Ride` supplies the acceleration instead (`EncounterFrames`). A member holding station
  (dampeners on and able to cancel the pull, `ServerFrames.StationKeeping`) gets nothing and pays for it in power
  (`StationKeepCharge`). NPC grids feel no relative terms: plain Newtonian inside their frame.
- **Split.** A member past `ServerFrames.LeaveRadius()` (= `SlotRadius`, 10 km) gets a frame of its own at its true
  state (`ServerFrames.SplitGrid`). (`SlotRadius` is the frame's size; the berth lattice's slot radius is the 150 km+
  one above - two different things.)
- **Merge (rendezvous).** Frame pairs in one SOI are screened for closest approach (section 4); a pair that stays
  within `RendezvousParams.EnterRangeMeters` (7.5 km) below `EnterRelSpeedMps` (1000 m/s) for `EnterDwellSeconds` (2 s)
  merges (`ServerFrames.Rendezvous`; two sites never merge, nor two NPC-only frames): the lower-priority frame moves into the host's berth at its true relative state
  (`ServerFrames.ExecuteMerge`; host by `EncounterFrames.HostPriority` - site > static > player > NPC - then mass).

## 3. Moving between frames

All world moves of grids go through `ServerFrames.MoveGrid`:

- a grid with no joints moves at once (`GridMembers.SetPosition` / `SetVelocity`);
- a grid with joints (rotors, wheels, connectors) moves with its whole joined set (`GridMembers.MoveSet`), deferred
  to the teleport phase before physics (`ServerFrames.RunDeferredMoves`, queued in `_deferred` / `_deferredRot` and
  `GridMembers.Pending`), because Havok cannot migrate constraints mid-frame. A set joined to anything static is never
  moved (a constrained pair moved apart hung the server). A second move in one tick is added to the queued one
  (`ServerFrames.AddToQueuedMove`); `GridMembers.Position` reads a queued move.
- Every move and velocity write is reported to the Aero mod (`GridEdits.Wrote` / `Moved`, `ServerFrames.AeroNoteMoved`;
  section 12).

The transitions:

- **Stow (planet -> rails).** The player: `FrameHost.TryStow` - out past the stow floor (R_c x 1.05), or faster than
  the cap well above the ground, creates a frame (`FrameRegistry.CreateFrame`) at the captured orbit
  (`CaptureMath.CaptureElements` -> `RendezvousPlot.Capture`; radial states become a needle ellipse), teleports the
  character into the berth, and queues an `ServerFrames.AttachRequest` so unconstrained grids within `AttachRadius`
  (5 km) moving with you (`AttachRelSpeed` 100 m/s) come along. Grids with no player: `ServerFrames.StowLoneGrids`
  (every 10 ticks).
- **Arrival (rails -> planet).** When the rails cross R_c inbound: the player `FrameHost.TryMaterialize` (via a server
  `FrameHost.RequestArrival` when the frame is grid-anchored), grids `ServerFrames.TryMaterializeGrids`. Each member is
  placed in the planet cell at the crossing state, turned into the chart; a dynamic grid's speed is scaled to the cap
  (`FrameHost.SpeedCap` 990 m/s), a static grid goes on an analytic conic (`ServerFrames._gridHighSpeed`). The fade over
  a seated arrival is `CrossingFade`.
- **Join.** A new grid near a frame is adopted (`ServerFrames.AdoptLooseGrids`, every 30 ticks;
  `EncounterFrames.ProcessNewGrids` for spawns); the on-foot player joins a framed ship it walks into
  (`PlayerOrbit.FrameToJoin`); seated, you are in your seat grid's frame (`FrameHost.SeatedOrbit`,
  `PlayerOrbit.KeepMembershipSeated`). A ship moved far out of its berth (fast travel) leaves quietly
  (`PlayerOrbit.MovedOut`).
- **Warm berths.** A fresh berth far from any physics makes the engine build a new Havok world (~230 ms). `WarmBerths`
  keeps 3-6 berths alive with a one-block placeholder grid ("Orbital warm berth"), and `BerthAllocator.Allocate` hands
  warm slots out first.
- **Grid identity.** `OrbitalGridComponent` (injected into every grid) registers with `GridMembers` on entering the
  scene; ids start at `GridMembers.IdBase` (1e9). A server grid that leaves and comes back within 60 s, same name,
  within 100 m, keeps its id (tombstones, `GridMembers.SweepTombs`).

## 4. The server tick

Driven by `ServerPlanetBeacon` (injected into every server planet; it also carries the save):

- `ServerPlanetBeacon.TeleportJob` (`OnTeleportPhase`, before the simulation): `ServerFrames.RunDeferredMoves`, then
  `WarpBill.Run`.
- `ServerPlanetBeacon.BeaconJob` (`OnBeaconTick`, after render submission begins): `ServerFrames.Tick`, then the
  player's server-side requests (`ApplyPlayerRequest`: `PendingPlayer`, rider velocity changes, the suit bill,
  `DelfosHeat.Apply`) and a pending gravity patch (`PlanetBeacon.PendingGravity`). Every planet's beacon runs the job;
  `ServerFrames.Tick` lets one per frame through (an Interlocked compare-exchange on its tick stamp).

`ServerFrames.TickBody`, in order:

1. Housekeeping: `OrbitalSettings.Poll`, `SpawnGuard.Tick`, `StationKeepCharge.Sweep`, `GridMembers.SweepTombs`,
   `WarmBerths.Tick`, `AsteroidBridge.Tick`; dt from the game clock (`IGameTime`: paused = no step); `SystemHost.SpinNow`.
2. `EntryHost.ServerBeforeLock`.
3. Under `ServerFrames.FramesLock`:
   - drain the client's request queues (`Attach`, `GridOrbit`, `GridDamp`, `GridLaunch`, `FlyTest`, `GridLevel`,
     `GridDelete`, `GridMove`; `PendingBerthShift`);
   - `EncounterFrames.Prune` (every 60 ticks) and `EncounterFrames.ServerTick` (sites, rides, new spawns);
   - `EntryHost.ServerTick` (braking, heat; before arrivals);
   - `UpdateGridFrame` for each frame (anchor, pin/drain/fold, members, split, warp steering, `TryMaterializeGrids`);
   - `StepGridHighSpeed`, `StowLoneGrids`, `ApplyFictitious`;
   - the merge screen's apply (last round's results) and collect; `CheckInvariants` every 60 ticks.
4. After the lock (slow game calls kept out of it): `AsteroidFrames.ServerAfterLock` (rock volumes),
   `FlushAeroNoteMoved`, `EntryHost.ServerAfterLock` (damage, Aero calls), `ScreenMergesCompute`.
5. Snapshots for the client: every 5 ticks `BuildViews` (immutable `GridView` / `SensorView` arrays published with
   `Volatile.Write`), else `RefreshGridPositions`; every 30 ticks `BuildSnapshot` + `AdoptLooseGrids`.

**The merge screen** (`ServerFrames.ScreenMergesCollect` / `Compute` / `Apply`, `ScreenJob`): pairs are sharded over
`MergeScreenInterval` (30) ticks, screened off the lock with `ClosestApproach.CertainlyApart` / `ClosestApproach.Find`
over 30 minutes, fed to a sticky `RendezvousTracker` (Core), and a latched pair merges. The nearest coming merge is
published as `ServerFrames.MergeAhead` for the warp stop.

## 5. The client tick

`PlanetFrameComponent` (injected into every client planet) runs `PlanetFrameComponent.PlanetFrameJob` (`OnPlanetFrame`,
after render submission begins) for each planet. Under `PlanetRenderBridge.Lock` it polls the harness
(`DevHarness.Poll`), runs `FrameHost.Tick`, then decides its own planet: real terrain or a proxy globe.

`FrameHost.Tick` (the first planet job of a frame does the work: `SystemHost.AdvanceClock` returns dt <= 0 for the rest):

1. Session change (`NewSession`), `OrbitalSettings.Poll`, `SystemHost.EnsureBuilt`, `SystemHost.AdvanceClock`.
2. A fresh `PlayerState` (`_cur`, Core/Frames) built every tick and published whole (`_pub`, volatile) at the end.
3. On foot, under `FramesLock`: settle a teleport, apply a loaded save (`SavedState.TryApply`), the server's requests
   (`ApplyServerRequests`: arrival, merge, shift), leave / join checks (`PlayerOrbit`), then `UpdatePlayerFrame`
   (anchor: pin, drain, fold, `TryReparent`, `TryMaterialize`; rider: `UpdateRider`) or, in no frame,
   `ApplyFictitious`, `EncounterFrames.TryAdoptPlayer`, `TryStow`.
4. Seated: `SeatedOrbit` - your frame is your seat grid's, from the server's `GridPositions` / `PlayerSeatGrid`.
5. `PublishObserver`, `DelfosHeat.Apply`; warp forced to x1 in no frame; `OrbitDisplay.DrawFrameOrbit`.
6. Each client system inside `FrameHost.Guard` (try/catch -> `Fault`, `ModCost` timing, optional allocation watch):
   `MapInput.Poll`, `CrossingFade`, `Invariants`, `AttitudeHold.ClientTick`, `WarpControl.Tick`, `MapView.Tick`,
   `FrameMarkers.Tick`, `Maneuvers.HudTick`, `AutoBurn.Tick`, `OrbitHud.Draw`, `BuildHints.Draw`, `SpdIcon.Draw`,
   `AlignButton.Draw`, `EffectCull.Tick`, `WarpBar`, the HUD speed, `SunDriver.Tick`, `StarProxy.Tick`,
   `EntryHost.ClientTick` (plus dev systems). `HoldActions.Hook` / `ClientTick` run unguarded.

**What you see.** `FrameHost.PublishObserver` sets `FrameHost.Observer`: in a frame, the berth plus the frame's
celestial state; in a planet cell, that cell with its spin (`ObserverPlanet`); else none. Each planet then shows its
real terrain only when it is the observer's planet and the camera is inside the keep (`PlanetFrameComponent.TickFramesMode`,
latched at `KeepRadius` / `KeepDropRadius`, terrain warmed first); otherwise a proxy globe placed by
`PlanetBerths.ProjectProxy` (true direction, true angular size, distance clamped to `SkyClampDistance` 2,000 km) and
turned by `ProxyTurn`. Proxies: `PlanetRenderBridge.CreateProxy` / `UpdateProxy` (the map-visual model, or `PlanetMesh`'s
hi-res cube-sphere with the planet's own baked overlay maps), `PlanetRings`, `StarProxy` (Delfos), `MapGlobes` (map-only).
Proxy mode is `OrbitalConfig.Mode` (`ProxyMode.Frame` by default).

**Flying.** `Maneuvers` (Flight/): the node list, the patched-conic trajectory (`PatchedConic.Propagate`), the map
editor, targets and closest approach, flying a node from the HUD; `AutoBurn` flies an armed node (thrusters seated,
jetpack on foot); `AttitudeHold` holds prograde / normal / radial / target through the cockpit's target orientation.
**HUD** (Hud/): `OrbitDisplay` / `OrbitHud` (orbit card, disc, relative plot), `FrameMarkers` (GPS markers across
windows), `Contacts`, `Targets`. **Map** (Map/): `MapView` -> `GameMap` -> `CleanMap`, the game's colonization map taken
over and drawn through `MapPipeline` (Render/). See each folder's README.

## 6. Time warp

- **The clock.** `SystemHost.Now` (universe time) advances in `SystemHost.AdvanceClock` by the game's dt (paused: no
  step; capped at 0.25 s) x `SystemHost.Timescale`. Planet spin runs on the game's own clock (`SystemHost.SpinNow`,
  `SpinAt`): warp does not turn the planets' day. Warp only advances the rails, never Havok.
- **Levels and keys.** `WarpControl.Levels` = 1, 5, 10, 50, 100, 1000, 10000; keys `.` `,` `/` (`WarpControl.Tick`),
  the warp bar (`WarpBar`, `WarpControl.SetLevel`). Warp above x1 needs your orbit on rails (a frame).
- **Stops.** Each step `AdvanceClock` finds the earliest of: an inbound shell crossing (`FrameHost.EarliestArrival` -
  the clock stops exactly there, so arrival is never skipped), a maneuver's lead (`Maneuvers.NextBurnStart`,
  `WarpLeadAt`), a rendezvous (`ServerFrames.MergeAhead` - `MergeWarpLead`), the entry interface
  (`EntryHost.NextWarpStop`), 'Warp here' (`SystemHost.WarpStopAt`), an SOI change (`Maneuvers.NextSoiChange`), the
  star's heat (`DelfosHeat.WarnAbove`) - and drops to x1 there.
- **Members in warp.** The anchor folds as usual; other members are steered along their own orbits, their relative
  velocity carried at N x (`ServerFrames._gridN`, `_trueRel`; the pull at N^2). A member too fast to carry
  (N x speed over 0.75 x `WarpMemberSpeed` 800 m/s - the physics cap) lowers warp (`ServerFrames.LimitWarp`); a raise
  is clamped beforehand (`ServerFrames.ClampWarpRaise`, called by `WarpControl.Raise`). Ships at a station are held there
  (`StationHoldMargin`). Server notices reach the player through `WarpControl.PendingSay`.
- **The bill.** `WarpBill` ages batteries and tanks on railed grids by the warped time; a store on your frame running
  dry drops warp to x1.
- Core/Time (`UniverseTime`, `WarpPolicy`) is a tested library that the game side does not use: the live clock and
  warp are `SystemHost.Now` / `Timescale`.

## 7. Entry (reentry, the speed cap)

- **The cap.** `SpeedLimitInjector` / `CharacterSpeedLimitInjector` raise the engine's speed limit to 1000 m/s
  (`SpeedLimitInjector.SpeedCap`) at definition load; the mod's own rules use `FrameHost.SpeedCap` (990 m/s) =
  `EntryHost.Cap`.
- **The braking band** (`Reentry`, Core/Entry, `Tests/EntryTests`): from the entry interface (`Reentry.BandMult` x the
  atmosphere's height above the border: ~19.5 km up at Verdure) down to the border. Only speed over the cap is braked
  (the excess decays over `VerticalEFolds`), so the handover to physics is always at or under the cap; plus a thin
  drag atmosphere (`Reentry.DragDensity`, ballistic coefficient beta, `DefaultBeta`) for aerobraking.
- **Heat** is the energy shed (J/kg) against `Reentry.ToleranceJPerKg` (1.15 MJ/kg; x `ShieldFactor` 2 with heavy
  armour in front), cooling over `CoolSeconds`. Past it `EntryDamage.Apply` wears the forward layer, at most half a
  block's health per entry.
- **Airless bodies** have no band: an arrival over the cap is clamped at the border, a jolt worth `AirlessJoltShare`
  of the heat (`EntryHost.AirlessBorder`).
- **Where.** `EntryHost.ServerTick` (under `FramesLock`) advances each frame's pass; anything that calls the Aero mod or
  damages blocks runs in `EntryHost.ServerAfterLock`. Predictions for the HUD, map and warp stop come from the same
  `Reentry` code. Plasma: Aero draws it from Orbital's registered entry source (section 12).
- **The star.** `DelfosHeat` writes the game's own killing-field data onto the player near Delfos.

## 8. Encounters, sectors, rocks

- **Sectors** get homes in the system (`SectorHomes`: about a planet, a ring, or a Lagrange point; worlds with no
  colonization sectors get virtual zones, `SectorHomes.Virtual`). `ZoneLevel` (Core) decides which map level draws a
  zone.
- **Sites** (`EncounterFrames.Site`): latent frames at an encounter's authored world spot, with a prescribed orbit (the
  sector's home plus the site's offset), re-osculated each tick (`EncounterFrames.RefreshSites`); built 8 s after the
  system (`BuildSites`), not saved. You reach one by conjunction: the merge screen merges your frame into it (sites are
  always host).
- **Procedural spawns** (`EncounterFrames.ProcessNewGrids`): close ones join the frame; far ones get a frame of their
  own 25-60 km out with a small kick (`FarFrame`). NPC grids never anchor, are never drained, stowed or split.
- **Lagrange regions:** a frame inside one rides it (`EncounterFrames.Ride`, `RefreshRides`, `LagrangeDynamics`).
- **Rocks:** belt rocks are site frames on reserved slots with voxel volumes made on demand (`AsteroidFrames`); ring
  rocks are analytic, nothing spawned (`RingRocks`). The game's spawning near berths is controlled by `SpawnGuard`.
- **Contacts and sensors:** `Contacts` (Hud/) knows what you have seen (sight, `TelescopeComponent`, `RadarComponent`;
  Core/Sensing `SensorModel`, `Tracking`), from the server's `SensorView` snapshot.

## 9. Threading

| thread | runs | notes |
|---|---|---|
| server job (one per planet beacon, in parallel) | `ServerFrames.Tick` (one per frame wins), `ApplyPlayerRequest`, `TeleportJob` | per-thread scratch is `[ThreadStatic]` |
| client job (one per client planet) | `PlanetFrameJob` -> `FrameHost.Tick`, proxies, map, HUD | serialized by `PlanetRenderBridge.Lock` |
| Avalonia UI thread | `UiThread.Post` jobs (posted by `AlignButton`: `HudGeom`, `NativeHints`) | results in volatile fields |
| thread pool | `Prewarm` (JIT warm-up of map/HUD methods) | the only background work |

- `ServerFrames.FramesLock` guards the frame registry and frame state on both sides. The server holds it for its whole
  locked step; the map draws under it (`MapView`: `lock (FramesLock) GameMap.Draw`). Lock order: `FramesLock` may be
  held while taking `GridMembers`' lock, never the reverse; `PlanetRings.Known` must never wait for
  `PlanetRenderBridge.Lock` (the deadlock of 2026-10-02).
- Client -> server: `ConcurrentQueue`s drained under the lock (`ServerFrames.Attach`, `GridOrbit`, ...),
  `ServerPlanetBeacon.PendingPlayer` (Interlocked exchange). Server -> client: `FrameHost.RequestArrival` /
  `RequestMerge` / `RequestShift` under the lock, `ServerFrames.AnchorAccel`, `GridPositions` (own lock),
  `PlayerSeatGrid` (Interlocked), the `GridView` / `SensorView` snapshots (volatile, immutable), `MergeAhead`,
  `WarpControl.PendingSay`.
- `SystemHost.Timescale` is written by both sides (warp keys, `LimitWarp`, `WarpBill`) and read once per use.
- Server and client share statics: the mod runs in single player and on a listen host only (one process).

## 10. Persistence

- **Where.** `ServerPlanetBeacon` holds the engine's `EntityNameSessionComponentObjectBuilder` (string -> entity; a
  mod-defined builder cannot compile). `ServerPlanetBeacon.SerializeState` -> `SavedState.Capture` +
  `OrbitalSettings.Save`; `ServerPlanetBeacon.InitState` -> `OrbitalSettings.Load` + `SavedState.OnLoaded`. Every planet
  writes the same state; on load the first wins.
- **What.** A text block under `state:` (`SavedState.Magic` "ORBITAL-STATE 1"): the clock, frames (elements, pending
  drain, anchor, slot, berth, members), HighSpeed conics, maneuver nodes (`Maneuvers.SaveLines`), the target, contacts,
  the Contacts tab's pins / names / hidden rows, hidden GPS markers, radar power. Grid and player references are saved as
  entity references (`member:<id>`, `player:<id>`) and remapped to live ids on load (`SavedState.TryApply`, run by the
  client under `FramesLock`, waiting up to 20 s for grids to appear); frames come back through
  `FrameRegistry.RestoreFrame`.
- **Settings.** `OrbitalSettings`: world-scope values (saved with the world under `settings:`, host overrides in
  `%APPDATA%\SpaceEngineers2\ModSettings\OrbitalMod.world.cfg`) and client-scope values (`OrbitalMod.client.cfg`),
  polled about every 2 s; other mods use `OrbitalApi.GetSettings` / `SetSetting`.
- **Core/Persistence** (`FramePersistence`, `FrameRegistrySnapshot`, `FrameSnapshot`): the game uses
  `FramePersistence.Capture` and `IsValidElements`; the XML round trip is exercised only by `Tests/PersistenceTests`.

## 11. Core and its offline tests

`Scripts/Simulation/Core` is game-free (namespaces `SEAerospace.*`; no `Keen.*` game types, only VRage.Library maths):

| folder | what |
|---|---|
| Berths | planet cells, the border bands, proxy projection, the sun plane |
| Entry | the reentry band |
| Frames | `ProximityFrame`, `FrameRegistry`, `FrameRails`, `BerthAllocator`, `MembershipRules`, `PlayerOrbit`, `PlayerState`, `RendezvousPlot`, `RendezvousTracker`, `SoiReparent`, `TerminalLayout`, `ZoneLevel` |
| Orbital | Kepler elements, propagation, gravity bodies, patched conics, encounter scan |
| Persistence | snapshots |
| Rendezvous | Lambert, closest approach, intercept planner |
| Sensing | sensor model, tracking |
| SystemDef | system / body / asteroid / encounter definitions, belt generator |
| Time | universe clock, warp policy (not used by the game side) |

`Tests/*` are console projects (`Tests/Directory.Build.props`: net9.0, VRage.Library reference, `$(Core)`) that compile
Core's sources by folder (FrameMathTests also compiles `FrameMath.cs` from the game side). Suites: EntryTests,
FrameMathTests, OrbitalTests, PersistenceTests, PlayerOrbitTests (incl. `MembershipCases`), RendezvousTests,
SensingTests, SystemDefTests, TimeTests - about 1,440 checks (2026-10-15). `tools/test_fast.sh` runs ModCheck (the
game's compiler and whitelist on the scripts), `tools/check_contract.sh`, then every suite. Rule: logic goes into Core
where the fast gate can test it; the in-game tiers check only what needs the engine.

## 12. The Aero link

The mods compile apart and find each other by reflection, by type name; neither needs the other.
`tools/check_contract.sh` checks the contract both ways from source (names, exact signatures, explicit parameter
types, no overloads).

- **Aero reads Orbital** (`OrbitalApi`, `Version` 1; reference in `docs/API.md`):
  - `OrbitalApi.TryGetEntryState` / `TryGetEntryState2` - a railed grid sits still in its berth, so Aero cannot see its
    true air velocity; Orbital gives it (travel direction, density, temperature, braking acceleration);
  - `OrbitalApi.TryGetGridEdits` - the running sum of velocity Orbital wrote into a grid and a move stamp
    (`GridEdits`, own lock), so Aero's structure model does not read a teleport or a drain as an impact.
  - `OrbitalApi.GetSpeedCap` / `GetPhysicsSpeedCap` exist for other mods; Aero does not read them today.
- **Orbital calls Aero** (`AeroMod.AeroApi`): `EntryHost.HookAero` registers Orbital as an entry source
  (`RegisterEntrySource("OrbitalMod", ...)`, contract `EntryHost.AeroEntryContract` = 2): Aero asks it per grid for the
  plasma's air direction and glow. Also `PredictHullForces`, `TryGetAtmosphereAt`, `RequestForceTable` (entry drag), and
  `NoteMoved` after every move (`ServerFrames.CallNoteMoved`, deferred out of `FramesLock` by `FlushAeroNoteMoved`).
  The old `AeroEntryFx.External` field is retired.
- Dev only: `aeroget` / `aeroset` and other harness commands read and write Aero statics by name
  (`PlanetRenderBridge.ForeignValue`, `SetForeignFlag`).

## 13. Dev tooling

- **The harness** (`DevHarness`) is on only when `%TEMP%\OrbitalMod\dev.flag` exists (`OrbitalConfig.DevHarness`;
  `tools/harness/relaunch.sh` creates it). `DevHarness.Poll` (from each client planet job) reads
  `%TEMP%\OrbitalMod\cmd.txt` every 0.5 s, runs each line through `DevHarness.Execute`, deletes the file, and rewrites
  `%TEMP%\OrbitalMod\status.txt` (`DevHarness.WriteStatus`: every poll for 30 s after a command, else every 5 s;
  `statusfast on` -> every poll). The status file has the clock and tick rates (`time ... server=`), `modcost` / `mapcost`
  (`ModCost`), frames, entry, warp bill, contacts, the orbit readout and the last 20 command results.
- **Commands** (by group): teleport and view (`view`, `tp`, `tpgrid`, `approach`, `flyby`), spectator camera (`cam`),
  orbits and time (`orbit`, `warp`, `clock`, `gravity`, `stow`), save (`save` - only into "Orbital Test ..." worlds),
  `legacy`, map driving and synthetic input (`map`, `mapclick`, `pickat`, `key`, ...), maneuvers (`node ...`, `burn`,
  `hold`, `seat`), grids (`grid*`, `gen` - `GridGen`, `hinge` - `HingeRig`, `testship`), encounters (`encounters`,
  `gotosite`, `devsite`, `devfar`, `gps`), checks (`inv` - `Invariants`, `hudcheck` - `DevHudCheck`, `reentrytest` -
  `DevReentry`), perf (`fps`, `perf`, `allocs`, `mem`, `statusfast`), the Aero bridge (`aeroget`, `aeroset`,
  `flightdump`), `setting`, `shot`.
- **`Invariants`** run in play too (once a second): anchor-alive, past-edge, ship-together, warp-state, on-rails,
  seated-frame, prediction; violations log `[ORBIT-INV] VIOLATION`.
- **Scripts** (`tools/harness`): `orb.sh` sends a command and waits for it to be taken; `testlib.sh` (log marks,
  `waitfor`, `check`, `send`, `stat`) and `launch.sh`; suites `test_smoke.sh` (~3 min), `test_all.sh` (joints, regress,
  perf, save, soak), plus scenario rounds (`scenarios.sh`, `burns.sh`, `airless.sh`, `hercules.sh`, `reentry_e2e.sh`,
  `fuzz.sh`, ...) and many one-off shot / A-B / diagnostic scripts.
- **Content tools:** `tools/blocks/gen_blocks.py` (sensor block definitions), `tools/globes/bake_globes.py` (proxy-globe
  textures for `PlanetMesh`).

## 14. Known mismatches between comments and code (2026-10-15, not fixed: docs only)

- The `ServerFrames` and `ClohessyWiltshire` comments say members get the CW acceleration; the code applies the exact
  differential gravity (`ServerFrames.UpdateGridFrame`).
- The `ServerFrames` header says arriving grids go "HighSpeed above the cap"; `TryMaterializeGrids` scales dynamic grids
  to the cap and gives only static grids a conic. The player's HighSpeed is unreachable (`FrameHost.EngageHighSpeed` has
  no caller); a saved player conic is restored onto rails (`FrameHost.RestoreHighSpeed`).
- In `ServerFrames`, the summary "Frame id -> the frame's acceleration A" sits on `_trueRel`; `AnchorAccel` is declared
  further down.
- `StarProxy`'s header calls `SunDriver` off by default; `SunDriver.Enabled` is true.
- Comments still giving the frame size as 20 km (it is 10 km, `ServerFrames.SlotRadius`): `RendezvousParams.ExitRangeMeters`,
  the `Invariants` header, `PlayerOrbit` (the split note).
- `ServerFrames.RockReach` is declared but unused (the rock rule it served was dropped 2026-10-07).
