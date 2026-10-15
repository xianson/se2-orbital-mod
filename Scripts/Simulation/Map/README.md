# Map/ - the orbital map (client)

The game's colonization map, taken over: a KSP-style map drawn through the game's own map pipeline.

| File | What |
|---|---|
| `MapView.cs` | Entry: our map inside the game's (open/close, globes, modes). |
| `GameMap.cs` | The game's map as we use it: its objects hidden, sectors classified, mouse, zoom limits; calls `CleanMap.Draw`. |
| `CleanMap.cs` (+ partials) | The map itself. `.Bodies` planets, moons, rings; `.Sectors` sector areas, Lagrange marks, asteroids; `.Ui` sector list, hints, context menu, overlay pins; `.Targeting` targets under the cursor, target list; `.Focus` focus and breadcrumb; `.Cards` hover card, info cards, target panel. |
| `MapCamera.cs` | Orbit and pan on top of the game's map camera. |
| `MapInput.cs` | Raw mouse buttons (edges) for the map's editors. |
| `MapLayout.cs` | Screen places at any resolution (the arithmetic is Core's `TerminalLayout`, tested offline). |
| `MapMenu.cs` | The right-click context menu. |
| `MapStyle.cs` | The line language (solid / dashed / dotted). |
| `ContactsTab.cs` | The GPS tab turned into the Contacts list. |
| `RendezvousView.cs` | The Rendezvous tab (your path relative to the target). |
| `Porkchop.cs` | The porkchop plot (Rendezvous tab, planet and moon targets). |

**Entry points / threading.** Client only. `FrameHost.Tick` calls `MapView.Tick`, which runs `GameMap` and `MapCamera`;
`GameMap` calls `CleanMap.Draw`, which opens the UI batch (`MapPipeline.UiBegin`/`UiEnd`) and draws everything else in it,
`Maneuvers.MapDraw`, `RendezvousView` and `WarpBar` included. `ContactsTab.Tick` runs from `FrameMarkers.Tick`. The
reflection on the game's view models (Contacts and Rendezvous tabs) is listed in `docs/REFLECTION_AND_WHITELIST.md`.
