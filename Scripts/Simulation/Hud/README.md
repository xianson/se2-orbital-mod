# Hud/ - what you see in flight (client)

| File | What |
|---|---|
| `OrbitDisplay.cs` | Computes the in-flight orbit (state vector, elements, the relative plot about a frame's anchor). |
| `OrbitHud.cs` | Shows it: the orbit card, the orbit disc and the relative plot by the speedometer. |
| `OrbitInfo.cs` | Orbital elements as panels (the map's target panel and hover card use them too). |
| `HudPanel.cs` | Shared panel, tag and label drawing (HUD and map). |
| `HudMath.cs` | Shared `IsFinite` / `Wall` (taken with `using static OrbitalMod.HudMath`). |
| `FrameMarkers.cs` | GPS markers across frames; opens the HUD draw batch (`BeginHud`); ticks `Contacts` and `ContactsTab`. |
| `Contacts.cs` | Contacts by sight and sensor blocks: what you know of (saved with the world). |
| `Targets.cs` | What can be targeted and where it is (the key in `Maneuvers.Target`). |
| `CrossingFade.cs` | The fade across a seated arrival into a planet frame. |
| `SpdIcon.cs` | The speedometer icon in the cockpit's SPD box. |
| `HudGeom.cs` | The game's HUD places, read off its controls (UI thread). |
| `NativeHints.cs` | Our key hints as the game's own hint line (UI thread). |
| `UiThread.cs` | Avalonia by reflection: dispatcher, visual tree, bounds, held transforms. |

**Entry points / threading.** Client only, from `FrameHost.Tick` (the planet frame job, once a frame): `FrameMarkers.Tick`,
`OrbitHud.Draw`, `SpdIcon.Draw`, `CrossingFade.Tick`; `OrbitDisplay.DrawFrameOrbit` from `FrameHost` itself, and
`OrbitDisplay.Consider` from each planet's `PlanetFrameComponent` tick (in no frame, map closed). Drawing goes
through `Render/MapPipeline` inside a `UiBegin`/`UiEnd` batch. `HudGeom`, `NativeHints` and anything touching a control run
on the UI thread, inside the job `AlignButton` posts (`UiThread.Post`, its only caller); `HudGeom`'s results are `volatile` arrays the draw reads.
