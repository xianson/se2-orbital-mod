# Changelog - Orbital Mod

What changed for players and for modders. Claims come from the commits (hashes on the integration branch) and the code;
nothing here was flown in game for this release (the one-run in-game check, `tools/harness/test_integrate.sh`, is
written but has not been run).

## 1.0.2 (Unreleased)

The UI audit's fixes (2026-10-18; offline-tested, not yet flown in game). Flight / data side (branch fix/fx-flight):

- **Landed ships get no orbit** (B1). A ship on the ground was given the orbit of a fall through the planet ("Ap -1.4 km,
  Pe -63 km, Period 2 min") and one read at the planet's centre "Ap -63 km, Period 0 s, 96,955 m/s"; the HUD's closest
  approach came from them. `SurfaceRule` (Core/Flight) / `ShipSurface` (Server/): Orbit, Landed (in the ground band,
  under 5 m/s over the ground), Low (a hop - the player's own "grounded" rule, now shared - or a path under the surface
  near it) or Unknown (deep inside: never a target state). A landed ship is a point on its ground turning with the
  planet: the closest approach is measured to it. `ServerFrames.Landed`; `GridView.Velocity` / `HighSpeed`. The API's
  `SetTarget` / `TryGetTargetState` follow it; `TryGetClosestApproach` is false for a target on the ground.
- **Auto-burn stops with a reason** (B2): no thrust along the burn (no progress in 6 s, or 0 N along it), the burn
  going the wrong way, can't turn (20 s without the angle shrinking; not judged in warp), far too long; the node is
  disarmed and kept. Out of the seat it pauses and goes on when you sit back. `BurnWatch` (Core/Flight).
- **"Burn done, 0.04 m/s left over"** when a burn completes (flown by hand or auto); **"Warp stopped: burn in 30 s"**.
- **One notice channel** (B3): `Messages.Say(kind, text)` - a toast titled by its kind (Target, Burn, Warp, Contacts,
  Entry) in flight, and a line of a board the map draws (`Messages.MapLines`); every `WarpControl.Say` caller goes
  through it (kind from its words). Before, nothing showed with the map open and every toast was titled "Time warp".
- **Exact values for a maneuver** (B4): `Maneuvers.EditExact` - the game's number dialog, "prograde, normal, radial".
- **Burn and entry data for the card and map** (U2/U3): `Maneuvers.BurnSeconds`, `Maneuvers.BurnTag` ("in 2:30" /
  "late 1:20" / "stopped: no thrust"), `AutoBurn.Tooltip`, `Maneuvers.PlannedEntries` (entry on planned legs),
  `Maneuvers.Landing` ("Lands on Verdure near Outpost RHA99 (12 km) · 12°N 45°E"), `EntryHost.CardTag` ("ENTRY 2:46 ·
  1,669 → 990 m/s · heat 82%", also from the plan), `EntryHost.AirGapLine` ("Pe 84 km: above the air (needs < 19 km)"),
  `EntryHost.HeatHelp`.
- **Entry predicted from far out** was wrong: `Reentry.Predict` jumped toward the band by today's speed - from an
  apoapsis past the whole band, so the entry sat at the border with no drag. Bounded by the speed it can have at the
  band (`Reentry.FastestTo`); in high warp the rails now brake through the band instead of clamping at the border.
- Tests: `Tests/FlightTests` (new, 64), `Tests/EntryTests` +15.

## 1.0.1 (2026-10-18)

- **Quick launch moved to the Aerodynamics Mod** (the user: "ctrl+T should be in aero creative"; its branch
  feat/a-quicklaunch, aero 1.0.1). Gone from here: the key and its client settings `QuickLaunch.Key` / `.Modifier`, the
  world settings `QuickLaunch.Creative` / `.AltitudeM` / `.SpeedMps` (and their groups "Quick launch", "Controls"), the
  toasts, `ServerFrames.QuickLaunchRequests` / `DoQuickLaunch`, `QuickLaunchRules` (decisions, placement, speed: aero's
  `Launch/QuickLaunchRules.cs` now), WorldReset's step, `Tests/QuickLaunchTests` (ported to aero). Old settings lines are
  skipped on load. Kept and new:
  - **`OrbitalApi.TryQuickMove`** (added within version 3, `Api/OrbitalApi.Grids.cs` -> `ServerFrames.QuickMove`): the
    aero mod's quick launch moved by this mod - `MoveGrid` under the frame lock (the teleport phase with the joined set,
    statics refused), refused on rails / outside a planet's cell, clamped under the space border by at most the caller's
    clearance room (`Server/QuickMoveRules.cs`, moved from `Flight/QuickLaunchRules.cs`; `Tests/QuickMoveTests`, moved
    from `Tests/QuickLaunchTests`), aero told after the lock. Aero binds it in its `OrbitalLink`; `tools/check_contract.sh`
    checks it (and `AeroApi.RequestQuickLaunch`). `Tests/ApiTests` allows `Quaternion` in the API.
  - The harness key `quicklaunch` (dev flag): `Flight/QuickLaunch.cs` asks aero's `AeroApi.RequestQuickLaunch`. The fly
    test (`flytest`) is unchanged.
- **Quick launch** *(superseded the same day: moved to the aero mod, the entry above)* (`Flight/QuickLaunch.cs`, `Flight/QuickLaunchRules.cs`, `Server/ServerFrames.QuickLaunch.cs`): the dev
  fly test as a player key, creative only. The client's key (client settings `QuickLaunch.Key` / `QuickLaunch.Modifier`,
  Ctrl+T) queues `ServerFrames.QuickLaunchRequests`; the server judges from its own state (`QuickLaunch.Creative`,
  `GameModeSessionComponent.IsCreative` of its character, `PlayerSeatGrid`, static asked live, rails, a planet cell) and
  moves through `MoveGrid` (joined set, teleport phase, statics refused). Where: `QuickLaunch.AltitudeM` over the aero
  ground ray (else over the ship's place), never lower, clamped 500 m under `PlanetBerths.ShellRadius`; speed
  `QuickLaunch.SpeedMps` (0: aero cruise at 3/4 throttle, 150 m/s fallback), at most `EntryHost.Cap`. The answer is a
  toast (`QuickLaunch.PendingSay`). Offline: `Tests/QuickLaunchTests`. The dev fly test keeps the harness key `flytest`
  (dev flag); its Ctrl+T is gone (it would have fired with quick launch). Harness key `quicklaunch`: the player path.
- The Radar's idle draw is 20 (`Radar_PowerableBlockDefinition.partialdef`; 200 before, 20 in the Workshop upload).
- `Orbital Mod.vrgproj` Version "1.0.1" (it was null; the game reads it as `System.Version`).
- Docs: BuildHints binds aero through `AeroLink` (the contract check's exception dropped); the 5 s warp lead before an
  armed burn needs the ship within 5 degrees of it.

## Release prep (2026-10-17)

- **A quiet game log.** The narrated lines (`[ORBIT-FRAME]` frame events, `[ORBIT-ENC]`, `[ORBIT-ROIDS]` keeps,
  `[ORBIT-CONTACT]`, `[HITCH]`, per-prefab / per-planet `[ORBIT]` setup lines) are written only where the dev flag is
  (`Host/OrbitalLog.cs`; every harness launcher sets it). A player's log keeps the startup summary, warnings, failures
  (`FrameHost.Problem`), lost save data, asteroid deletions and the `[ORBIT-HAVOK]` cluster audit's mismatches (first 20
  per world). One hour of play: ~520 Orbital lines before, ~20 after.
- **The fly test (Ctrl+T seated) needs the dev flag** like every other dev tool.
- **en-US texts** for the Telescope and Radar (`Assets/Texts`): the definition-load "Unrecognized value" warnings.
- Player docs: `README_PLAYERS.md`, `RELEASE_NOTES.md`, `WORKSHOP_DESCRIPTION.md` (Steam BBCode); the ModHub summary and
  tags (`ModProject.modhub-metadata`).

## Unreleased (2026-10-15 cleanup)

A big cleanup of the scripts (feature folders, no behaviour change of its own), plus an in-game settings menu for both
mods, structural-failure alerts from the Aerodynamics Mod, OrbitalApi versions 2 and 3, one per-world reset and about 30
bug fixes (warp billing, orbit prediction, world changes, threading).

### New features

- **Settings in game.** Pause menu -> Settings -> **Mods**: one tab for both mods, this mod's section "Orbital" beside
  "Aerodynamics". Built from the settings themselves: basic settings shown, each group's advanced ones under a collapsed
  "Advanced", a switch, a choice or a slider (with value and unit; a logarithmic one for the planet day) per setting, the
  description in the screen's panel, "Reset to defaults" per group. World settings can be changed by the host / in single
  player only (read-only with the reason otherwise); one the host's `OrbitalMod.world.cfg` forces is locked with the
  file's path; a changed world setting is saved with the world's next save. Only inside a world; if the tab cannot be
  added, one toast per session points to the `.cfg` files. The tab is written once here and copied into the
  Aerodynamics Mod. (`Ui/`, 8a7f947, 43cf5bc)
- **24 more settings** (defaults unchanged): `Warp.MaxLevel` (a world's highest time warp - the keys, the warp bar and other
  mods stop there; warp above a lowered cap drops to it), `Warp.CostsEnergy`, `Warp.StopLead`, `Warp.SafetySpeed`,
  `Autopilot.HoldTurnRate`, `Autopilot.BurnAlignLead`, `World.SectorAnchors`, `Proxy.Atmospheres`, `Proxy.Rings`,
  `Proxy.GlobeDetail`, `Proxy.NormalMaps`, `Proxy.WarmBeforeShow`, `Fx.ArrivalFade`, `Hud.AlignButton`, `Hud.BuildHints`,
  `Hud.ContactsNeedDetection`, `Hud.FrameOrigin`, `Hud.SpeedIcon`, `Map.Orbital`, `Map.ContactsTab`, `Map.FreeCamera`,
  `Map.HideGameSectors`, `Map.Rings`, `Map.TextScale`. Every setting has a menu label, group and tier. (9f80b6d, d732822,
  355f596)
- **Structural failure alerts.** With the Aerodynamics Mod (AeroApi 7+), a failure on the ship you are seated in shows as
  a toast - "Structural failure: 2 sections broke away, 41 blocks", "Burn-through: 3 blocks burnt through" - and an
  `[ORBIT-STRUCT]` log line. Polled every 0.5 s while seated (and for 3 s after a split re-seats you); nothing asked while
  unseated. (`Hud/StructureAlerts.cs`, c5302d5, e2583e6)

### Changed behaviour you will feel

- **Warp is billed once per frame, for the frame's game time.** With several planets, a second planet's job could bill the
  same frame again (batteries and tanks drained 2x, warp cut early); each bill was a fixed 1/60 s, so at 30 fps half the
  warp went unbilled and a paused game kept billing. Now one claim per game frame, surplus = (warp - 1) x the frame's game
  seconds. (41be7f1, f419a17)
- **The map no longer draws your path through a moon.** A low pass over a small body (15-40 s at the surface) fell
  between the 240 samples of a leg and was drawn as an escape; the impact is now solved on the conic. (ec98a0a)
- **Short sphere-of-influence fly-throughs are found**: a quick pass through a small moon's sphere between two samples
  (~3400 s apart over 10 days) showed no encounter, gave no SOI-change time, and warp did not stop. (0c534f0)
- **SOI handover with hysteresis.** An orbit with its apoapsis at a moon's sphere no longer flips parent every pass (up
  only past 1.02 x the SOI); at x10,000 a whole sphere passed between two polls is patched in and carried through; the
  velocity is in the new parent's frame. (9d8c173)
- **A new world starts clean.** The universe clock, warp, contacts, grid ids, the pending save, the player caches, hidden
  GPS markers and the planet globes no longer carry over from the last world loaded in the session (a world without saved
  orbital state used to start at the last one's time; world A's contacts appeared in and were saved into world B; quitting
  A and loading B within 2 min applied A's clock and frames to B). (bc29ba8, 6859b0b, 635cd9c, 84b71c0, 5a967df, 3195026)
- **Re-entry wear works after the first arrival.** A grid whose first entry ended by arrival took no heat wear again for
  the rest of the world. (58ef6c7)
- **Listen host: the local player's own effects.** The rider velocity, suit bill and Delfos heat could go to a remote
  player's character; now found by the local player's identity. (4427c7f)
- **One over-the-cap test for arrivals** (over by more than 1 m/s, against the entry's cap) on both the client and the
  server arrival. (45614c0)
- **Airless bodies show no atmosphere ring** on the orbit radar (Palatine). (067009e)
- **Ring rocks keep their own berth** ("a mined rock comes back to the same berth"); a refused slot goes to the rock's own
  overflow slot, never the next rock's. (d29af45)
- **Lagrange rides** no longer jump a frame after a failed substep. (ebfa3b4)
- **A phasing plan reports its flown arrival speed** (was 0; ~68 m/s in the test case). (fbb113d)
- **Stray generated asteroids are removed again, safely.** The bridge that switches the game's own asteroids off was meant
  to delete procedural rocks that slipped through, but its "is it player-edited" lookup never found its type, so it kept
  every one. Now it deletes a generator rock only when it is provably untouched and procedural and nobody is at it: never
  one a player edited (or saved with the world), an asteroid frame's, one with a grid within 100 m or a player within
  300 m, or anything it cannot classify. Each deletion is logged (`[ORBIT-ROIDS] deleted generator body ...`). The
  encounter clean-up (a rock left when an encounter moves off a planet's border) uses the same rule (before, it deleted
  anything in reach). New world setting `World.DeleteGeneratedAsteroids` (Advanced, default on) turns it off.

### Fixes

**Server step and threads**
- One server frame step at a time: a second planet's job no longer runs the step alongside a slow one ("Collection was
  modified"); the step is claimed by game time, so its head (sweeps, warm berths, the settings poll) no longer runs twice
  a frame. (306c13e, b42857e)
- Grid positions are refreshed every tick (every 5th tick they were a tick stale for the seated player's matching).
  (c4cc65d)
- Saving while editing the Contacts tab, or with GPS markers hidden, no longer fails the save (locked against the save).
  (a67dcd3, 3195026)
- Alive checks before transform reads that could throw (~200 ms each): warm berths, `OrbitalApi.GetMemberId`, the saved
  radar settings, `OrbitalApi.TryGetGridState` (a dead grid answers `false`). (77540ca, 0e092a9)
- The frame snapshot (`Peek*`): a throwing writer no longer leaves readers spinning forever; readers give up after 64
  tries and answer "no data". (386bd6f)

**State that outlived its frame or grid**
- A frame leaving the registry (dissolved, merged in) drops its anchor acceleration, entry state and per-frame
  bookkeeping everywhere (one rule, `FrameRegistry.Retiring`); a deleted grid drops every per-grid set. (58ef6c7)
- Grid tombstones belong to their session (a grid in the next world could take a dead one's id). (635cd9c)

**Orbits and maths**
- `EntryHost` rejects an infinite mean motion (a = 0). (be8e78b)
- Berth slot centres out of range are "nowhere" instead of a throw; angles wrap into [0, 2 pi) (never exactly 2 pi); the
  map's "body you are about" uses the frames' strict SOI edge. (083caf3)

**The Aerodynamics link**
- One binding rule for the Aerodynamics Mod (`Api/AeroLink.cs`): the last loaded copy, bound again after an aero hot
  reload (the entry glow used to register into the stale copy and look hooked); one thread refreshes it at a time.
  (196d923, 6e55839)
- The dev `gen aero` readout survives an aero build without `TryGetForces` / `TryGetLift`. (e960954)

**Settings**
- A harness command's direct write (cross-fade, map camera, contacts, rings ...) is never saved as the player's setting.
  (8a99fbd)
- `Proxy.Atmospheres` is a real setting now: a failure turns the globes' atmospheres off for the session without saving
  "off". (355f596)
- The settings files are read off the tick threads and applied on the next tick; a change during a file write is kept.
  (67c9b0d)
- A float setting at its range bound survives its own file's round trip; a forced name is refused even at its value.
  (6d2456e)

### For modders

- **OrbitalApi version 3** (was 1). Members only added; version 1 unchanged. Reference: `docs/API.md`.
  - **Version 2** - lock-free `Peek*` reads of the frames (a snapshot, never blocking, never allocating), the bodies
    (system state, orbit, spin, the planet cell) and world <-> body conversion, a grid's own state / orbit / summary /
    status, conic math on any element set (eight doubles), time warp (levels, request, stop time), the local player's
    plan (orbit, nodes, next burn, attitude hold, target, closest approach), sites and contacts. (a1832ae, e0608eb)
  - **Version 3** - what a settings screen needs: `TryGetSettingUi`, `IsSettingForced`, `CanEditWorldSettings`,
    `ResetSettingsGroup`, `AreWorldSettingsUnsaved` (the same shape as AeroApi 8). `GetWarpLevels` / `RequestTimescale`
    stop at `Warp.MaxLevel`. (d732822, 6d2456e)
- **The settings menu contract `ModSettings/v1`** - any mod can add a section to the shared Mods tab with no code shared:
  the `TabItem` tagged `"ModSettings/v1"` in `OptionsScreen.PART_TabControl.Items` (or add it: `Header` "Mods", `Content`
  a `ScrollViewer` around a `StackPanel` tagged `"ModSettings/v1/host"`), then an `Expander` tagged
  `"ModSettings/v1/<YourModId>"`. `docs/API.md` (Settings). The shared files: `tools/sync_settings_ui.sh <aero>` copies
  them, `--check` compares.
- **One cross-mod link each way.** This mod finds aero's types only through `Api/AeroLink.cs`; aero names `OrbitalApi`
  only in its `Api/OrbitalLink.cs`. `tools/check_contract.sh` checks both directions over every API partial (names,
  signatures, explicit parameter types, no overloads), the dev harness's `AeroMod` flags, the settings API parity, the
  shared settings-tab copies and the one-link rule; `AERO=<aero Scripts>` checks against another checkout.

### Internal

- **Feature folders.** `Frames/` split into `Server/`, `Host/`, `Entry/`, `Encounters/`, `Asteroids/`, `Api/`, `Dev/`,
  `Flight/`, `Hud/`, `Map/`, `Render/`, plus `Ui/` (each with a README); `Frames/` keeps `Chart`, `BuildHints` and
  `BodyMarkers`. Big files split into partials (`ServerFrames`, `FrameHost`, `DevHarness`, `Maneuvers`, `CleanMap`,
  `PlanetRenderBridge`, `MapPipeline`, `EncounterFrames`, `AsteroidFrames`, `OrbitalSettings`). Public names unchanged.
- **Dead code out**: the player's HighSpeed (`EngageHighSpeed` had no caller), dead HUD / map lines (`Contacts.LoudLine`,
  `Maneuvers.RingLine`, `AttitudeHold.HudLine`, ...), `PlanetRenderBridge.SetDraw3DMap` / `SetForeignStatic`, unused core
  members; runtime-dead but tested Core types marked NOT WIRED; unused usings; ~20 drifted doc comments put back.
- **Shared helpers**: `Host/Num.cs` (`IsFinite`, `Wall`, `Alive`), `PlanetRenderBridge.SetBoxedField`; Core/Time
  `FrameClaim` (one claim per game frame) for the warp bill and the server step; `Host/WorldReset.cs` as the one per-world
  reset.
- **Tests**: new suites `ApiTests` (snapshot, orbit queries, the API's shape), `SettingsTests` (the real registry over
  stand-ins seeded from the source), `SettingsUiTests`; new cases in OrbitalTests (O15, O16, O17, O25, frame-registry
  lifecycle), TimeTests (`FrameClaim`, stragglers), RendezvousTests (O24), EntryTests (over the cap). The fast gate runs
  about 4,480 checks.
- **Tooling**: `check_contract.sh` finds files by name and covers partials, settings and the one-link rule;
  `test_fast.sh` compiles this checkout (`MODCHECK` overridable); the harness's one-off scripts moved to
  `tools/harness/experiments/`, a shared `testlib.sh` (`LOGF`, `waitloaded`), `README.md`, `COMMANDS.md` (every name the
  scripts depend on), `RUNBOOK.md`, `test_integrate.sh` (one <= 5 min in-game run over a whole batch, not yet run),
  `settingsui.sh`; `WarpBill` counters for it.
- **Removed** (approved 2026-10-15): `docs/screenshots/` (65 old proxy / map JPEGs nothing linked to);
  `Hud/HudMath.cs` (a forwarder to `Host/Num.cs` with no callers; HUD files take `using static OrbitalMod.Num`).
- **Docs**: `docs/ARCHITECTURE.md` (new), `docs/API.md` versions 2-3, folder READMEs, this changelog.
