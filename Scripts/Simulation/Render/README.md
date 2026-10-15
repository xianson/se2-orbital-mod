# Render/ - planets, proxies and drawing (client)

The engine's render side, reached mostly by reflection (VRage.Render and friends are not referenceable). Every use is
listed in `docs/REFLECTION_AND_WHITELIST.md`.

| File | What |
|---|---|
| `PlanetFrameComponent.cs` | Injected into every planet: each frame decides real planet or proxy globe; its job is the client's heartbeat (it calls `FrameHost.Tick`). |
| `PlanetRenderBridge.cs` (+ partials) | Render reflection and the shared helpers (`GetMember`, `SetMember`, `FindType`, `CallAdd`, `SetBoxedField`, `WarnOnce`). `.Planets` real terrain and atmosphere visibility; `.Proxies` proxy globes and atmospheres; `.Game` map visual, gravity law, map camera, teleport; `.Dev` harness helpers. |
| `PlanetMesh.cs` | The hi-res proxy globe (a runtime cube-sphere with the planet's own overlay maps). |
| `PlanetRings.cs` | Rings on the proxies (`OrbitalRingComponent` registers them). |
| `MapPipeline.cs` (+ partials) | The map's and the HUD's drawing: UI batch, text, lines. `.Sectors` the sector mesh; `.Occlusion` what hides lines; `.Vector` paths, fills, icons; `.Hud` HUD markers and the GPS look. |
| `MapGlobes.cs`, `MapRingMesh.cs`, `MapIcons.cs` | Map-type globes, map rings, the map's icons. |
| `GameUi.cs` | The game's real UI: notification card, toasts, HUD speed. |
| `StarProxy.cs` | Delfos in the sky in flight. |
| `SunDriver.cs` | One sun, consistent across frames (on: `SunDriver.Enabled`). |
| `EffectCull.cs` | Far looping block effects hidden (off by default). |
| `SpecCam.cs`, `ParticleCensus.cs` | DEV: spectator camera, particle census. |
| `SpawnGuard.cs`, `AsteroidBridge.cs` | Spawn control (owned with the server code). |

**Entry points / threading.** `PlanetFrameComponent`'s `[OnPlanetFrame]` job (client; it may run on a worker thread) takes
`PlanetRenderBridge.Lock`, runs `FrameHost.Tick`, then its own planet's proxy logic: every client render-side call is under
that lock (the server beacon calls `PlanetRenderBridge.SetGravityLaw` / `GetMapVisualPrefab` outside it). `MapPipeline`'s batch and its reused argument boxes are client-only, one batch at a time (`UiBegin`/`UiEnd`).
