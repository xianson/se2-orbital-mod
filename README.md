# Orbital Mod (SE2)

Orbital mechanics for Space Engineers 2, the SE2 counterpart of the SE1 project at
`D:\SE-Aerospace`. Status as of 2026-09-25, SE2 **2.4.0.77**. Separate from the Aerodynamics Mod on purpose:
the two share almost nothing, and players can run either alone.

## What works (verified in game, see `docs/screenshots/`)

1. **Planet frames with proxy globes.** Each planet has a frame sphere, its gravity reach, with 10%
   exit hysteresis. Inside it the real voxel planet is drawn. Outside it the real planet's terrain,
   atmosphere and clouds are hidden and the planet's own colonization-map globe is drawn in its place,
   in the normal world view, at true direction and size. Hide and restore were both verified at close range (88 km).
2. **Orbital core ported.** The game-free SE-Aerospace core (Orbital, Rendezvous, Time, SystemDef;
   about 5.8k lines) runs on SE2's math library. `Tests/` holds the Time, SystemDef and Rendezvous suites:
   76 + 88 + 97 checks, all passing against SE2's `VRage.Library`.
3. **Orbit display.** The first in-game use of the core. It picks the planet whose gravity dominates at the
   camera, fits Keplerian elements to the camera's state and draws the conic plus a readout. It
   renders with a UI3D MeshBuilder, so it needs no reflection. The readout is verified. The conic needs a moving
   player, see open items.
4. **Dev harness.** An external tool can teleport the player, aim the view and switch modes, and take
   screenshots. See below.

## How it is wired

| Piece | File | Notes |
|---|---|---|
| Entry | `Injection/PlanetInjector.cs` | Injects components into planet **prefab compositions** at definition load, the same trick as the aero mod. The content pipeline is dead until the Mod SDK catches up. |
| Server half | `Frames/ServerPlanetBeacon.cs` | On server planet compositions (`DiscoverablePlanetComponent`). Publishes centre, map-globe prefab and gravity law. |
| Client half | `Frames/PlanetFrameComponent.cs` | On client planet compositions (`PlanetEnvironmentRenderComponent`). Frame decision, hide/show, proxy, orbit display. Pairs with its beacon by position. |
| Pure math | `Frames/FrameMath.cs` | Hysteresis, angular-size-preserving proxy projection, `GravityLaw`. |
| **All reflection** | `Render/PlanetRenderBridge.cs` | The only file that uses reflection. A reflection ban breaks this file and nothing else. |
| Orbital core | `Core/**` | Literal SE-Aerospace sources. Namespaces kept as `SEAerospace.*` for traceability. |

SE2 keeps **separate client and server compositions** per entity. The first version injected
everything on the server side and never ran on the client. Keen's validator rejects client types on server
compositions, and the binding between the two lives in `VRage.Multiplayer`, which mods cannot see.
Hence the beacon/frame split. The beacon pairing only works where both scenes share a process: single
player and listen host. A dedicated-server client needs a different pairing (open item).

## Findings worth keeping

- **SE2 vanilla gravity is a linear shell, not inverse-square.** Verdure is 1 g out to 63 km and falls
  linearly to 0 at 81 km (falloff power -1). Kemik is 1 g to 52.5 km and 0 at 67.5 km. Stable orbits are
  physically impossible in vanilla. Real orbits need `FallOffPower = 2` and a large `AffectDistance`
  set on the planet's gravity generator object builder at spawn, capped by `MaxAffectDistance`.
- **Hiding terrain:** `VoxelClipmap.Visible` and Keen's own debug "HidePlanet" do not work on a built
  planet. Queued cell transitions are only committed inside `Update`, which exits early once hidden.
  The mod deactivates the clipmaps' render root entities instead, one per 1 km block, under Keen's own
  lock, for both the detailed and low-res clipmaps, then freezes the clipmap after 20 frames.
- **Whitelist quirk (VRS1001):** `new T[n]` of a script-defined `T` is banned, because array types miss the
  own-assembly exemption. Use `List<T>` plus `ToArray()`.
- **Freezes during testing were not this mod.** The test world loads the Aerodynamics Mod from its mod
  list, and its AeroSpeedSpike froze the main thread about 40 s after engaging, with or without the Orbital Mod.

## Dev harness (visual testing without a human)

Off switch: `OrbitalConfig.DevHarness`. **It must be `false` in anything shipped.** The mod polls
`%TEMP%\OrbitalMod\cmd.txt` and writes `%TEMP%\OrbitalMod\status.txt`. The full command list is in
`Frames/DevHarness.cs`:

```
planets                      view Verdure 200 [keep|sun|+x..-z]    lookat Kemik
tp x y z   look x y z        mode Frame|AlwaysProxy|AlwaysReal|Alternate
hide on|off   front on|off   orbit on|off
```

`tools/harness/` holds the driver side:
- `launch-orbital.ps1` launches the game with this mod.
- `shot.ps1` captures the screen.
- `orb.sh` sends commands, waits for them to be consumed, and optionally captures a screenshot.

Both `.ps1` scripts must run as **scheduled tasks with an Interactive principal** (`OrbitalTestSE2`,
`OrbitalShotSE2`). The agent shell's window station cannot launch the GUI or capture the screen.

```
tools/harness/orb.sh --shot 5 "view Verdure 200"     # then look at the PNG it names
```

Compile check without launching the game: `D:\aero\tools\ModCheck` (same references, generators
and whitelist analyzer as the game).

## Open items, highest value first

1. **Proxy fidelity.** The map globe is low-poly and pale, and has no atmosphere rim (compare screenshots 02 and 03).
   It is also sized from the base radius, about 5% small next to the real limb. Options: size from the surface
   radius, a denser runtime mesh, or reusing the planet's own low-res clipmap far away.
2. **Real orbital gravity.** Spawn or patch planets with inverse-square falloff, then the orbit display becomes exact.
3. **Orbit display needs velocity.** The jetpack dampeners hold the player still. Add a harness `vel` command
   (set `RigidBodyData` server-side), or test from a moving grid.
4. **Dedicated-server pairing** of client planets to their server data.
5. **SE-Aerospace's frames and rails** (berths, time warp, rails teleport). The core is here. The runtime
   still needs its SE2 design.
