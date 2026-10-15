# Frames/ - what is left of the old grab-bag

The 2026-10-15 reorg moved the rest into feature folders (`Server/`, `Host/`, `Entry/`, `Encounters/`, `Asteroids/`,
`Api/`, `Dev/`, `Flight/`, `Hud/`, `Map/`, `Render/`). Left here:

| File | What |
|---|---|
| `Chart.cs` | The rotating surface chart of a planet cell (Coriolis / centrifugal, inertial <-> chart) and `CaptureMath` (orbit capture: used by `ServerFrames`, `FrameHost`, `EncounterFrames`). |
| `BuildHints.cs` | The build and aero hints while building, and the seated flight gauges, from the Aerodynamics Mod's design probe and flight members (`Hud.BuildHints`). Owned by other work; it still finds aero with its own scan, not `Api/AeroLink` (noted by `tools/check_contract.sh`). |
| `BodyMarkers.cs` | Planet and moon markers in flight (`Map.BodyMarkers`), drawn with the game's GPS marker look; collected and drawn by `Hud/FrameMarkers`. Owned by other work. |

**Entry points / threading.** Client: `BuildHints.Draw` from `FrameHost.Tick` (guarded), `BodyMarkers.Collect` / `Draw` from `FrameMarkers.Tick`; `Chart` and
`CaptureMath` are pure and called from both halves.
