# Flight/ - flying your orbit (client)

Maneuver nodes and the things that fly them, plus the warp gauge.

| File | What |
|---|---|
| `Maneuvers.cs` (+ partials) | Maneuver nodes: the node list, warp lead. `.Trajectory` the patched-conic path with the nodes applied (cached); `.MapEditor` the map editor (handles, drag, slide); `.MapMarks` its colours, atmosphere rings, navball icons; `.Target` the target's closest approach, rendezvous, sector crossings; `.Lagrange` the Lagrange-sector preview; `.Hud` flying a node (HUD marker, burn countdown, finite burn); `.Harness` dev drivers, save lines, dev finders; `.Exact` the exact-values dialog (`EditExact`); `.Entry` re-entry on the plan (`PlannedEntries`, `Landing`, `AirGapLine`). `BurnTag` / `BurnSeconds`: the next burn told. Design: `docs/design-maneuver-nodes.md`. |
| `AutoBurn.cs` | An armed node flies itself (thrusters seated, jetpack on foot); gives up with a reason (`Core/Flight/BurnWatch`), pauses out of the seat; says "Burn done" for any completed burn. |
| `AttitudeHold.cs` | KSP-style SAS holds (prograde, normal, radial...) through the cockpit's target orientation. |
| `HoldActions.cs` | DISABLED prototype: the holds as cockpit toolbar actions. Its `[BlockDetailProvider]` was removed 2026-10-08 (it crashed the game), so the model is never registered; `Hook` / `ClientTick` still run. |
| `AlignButton.cs` | The align button in the cockpit HUD's toggle row (Avalonia by reflection: `Hud/UiThread.cs`). |
| `WarpBar.cs` | The time-warp gauge (in flight and on the map; clickable on the map). |
| `QuickLaunch.cs` | DEV only since 2026-10-18: the harness key `quicklaunch` asks the AERO mod's creative quick launch (`AeroApi.RequestQuickLaunch`) - the player feature (Ctrl+T, its settings and toasts) moved there; this mod moves the ship for it (`OrbitalApi.TryQuickMove`, `Server/ServerFrames.QuickLaunch.cs`, `Server/QuickMoveRules.cs`). |

**Entry points / threading.** Everything ticks from `FrameHost.Tick` on the client (the planet frame job, once a frame,
each system in its own `Guard`): `Maneuvers.HudTick`, `AutoBurn.Tick`, `AttitudeHold.ClientTick`, `AlignButton.Draw`,
`WarpBar.DrawHud`, `QuickLaunch.ClientTick` (dev key only); `HoldActions.Hook` / `ClientTick` are called directly (no `Guard`). The map side of `Maneuvers` (`MapDraw`) runs inside the map's UI batch
(`Map/CleanMap`). `AlignButton` posts its control work to the Avalonia UI thread (`UiThread.Post`) and reads the result
on a later tick. `HoldActions`' `[Button]` methods would run on the server. `Maneuvers.Nodes` is locked (`lock (Nodes)`).
