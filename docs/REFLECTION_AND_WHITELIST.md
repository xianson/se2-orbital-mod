# Reflection in the Orbital mod, and what the SE2 mod whitelist would need

**Game version:** SE2 2.4.0.77 (`D:\aero\decompiled\SE2_VERSION.txt`). **Written:** 2026-10-03 (moved here from `D:\aero\docs\` the same day).

**Companion document:** the Aerodynamics Mod's own reflection is in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md`. This document is the home of the **shared** sections: how the mod compiler gates code (§2), the combined whitelist ask to Keen (§5) and most risks (§6). The Aero document refers back here for those.

**Keep current:** every new or changed reflection use goes into this file (or the Aero one, for Aero code) in the same change.

**Method:** both mods' `Scripts\` trees were read in full. Every reflected member was then looked up in the decompiled engine (`D:\aero\decompiled\<Assembly>\...`). No code was changed and the game was not launched.

**Path prefixes used below:**
- `O/` = `C:\Users\slob\Documents\SpaceEngineers2\Mods\Orbital Mod\Scripts\Simulation\`
- `A/` = `C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\Scripts\Simulation\`
- `PRB` = `O/Render/PlanetRenderBridge.cs`
- Engine paths are relative to `D:\aero\decompiled\`.

**How to read the line numbers:** they are a snapshot of the working tree on 2026-10-03, so re-check before quoting them to anyone.

**What could not be verified:**
- VRage.UI is not decompiled, so view-model base types and `OnPropertyChanged` were not resolved.
- `VRage.Scripting.dll`, which holds `ScriptWhitelist` / `WhitelistDiagnosticAnalyzer`, is not decompiled either. Its namespace-matching semantics are therefore inferred from ModCheck behaviour (see §2).

---

## 1. Summary

### Size
- **Orbital Mod:** about 610 source lines call a reflection API, spread over 24 files.
- **Aerodynamics Mod:** about 40 such lines, in 5 files. Grep also hits `AeroWork.cs` and `DampedShadowedDragModel.cs`, but those are plain delegate `.Invoke`, and `O/Frames/MapMenu.cs:89` is the same kind of false positive.
- **Distinct reflected members:**
  - Orbital: roughly 190 engine members plus about 12 Aerodynamics Mod members.
  - Aerodynamics: 12 engine members.
  - The count is approximate because the shared helpers in PRB (`GetMember`, `SetMember`, `FindType`, `CallPrivate`, ...) take member names as strings from many callers.

### Engine assemblies reached
| assembly | can mods compile against it? | what the mods reach in it |
|---|---|---|
| **VRage.Render** | **no** | Almost everything render-related. It is the largest share. |
| **VRage.Game.Client** | **no** | Particle effects, `RootEntityRenderComponent`, `ModelRenderBaseComponent` |
| **VRage.Voxels / VRage.Voxels.Client** | **no** | Terrain clipmaps, planet radii, procedural voxel bodies |
| **VRage.Game** | **no** | `ProceduralGeneratorServerSessionComponent`, `ModelDefinition`, `MaxHealthComponentDefinition` (the CS0012 behind `CubeBlockComponent.Definition`) |
| **VRage.Multiplayer** | **no** | `NetworkStory<T>`, the return type of `EntityAdmin.TeleportPlayer` |
| **VRage.UI** (+ Avalonia) | **no** | Base classes of every Game2.Client view model |
| System.ObjectModel | **no** | `ObservableCollection<T>`, the base of Keen's `ObservableList<T>` |
| Game2.Client, Game2.Simulation, VRage.Core.Game, VRage.DCS, VRage.Library, VRage.Physics | yes | These are reached by reflection only for **private, protected or internal** members, or because a public member's signature drags in a type from a non-referenceable assembly. |

### Capability groups
Roughly in order of how much reflection each one uses:
1. **Render service and runtime render entities.** Root and model entities, transforms, render flags, per-entity GPU custom data, Dispose (VRage.Render). Shipped.
2. **Runtime meshes.** These are hand-built: `RuntimeMeshData<,,>` private fields are written on a boxed struct, and `Buffer<T>` holds render vertex types (VRage.Render). Shipped.
3. **Runtime materials.** The **internal** `MaterialSystem.AddRuntimeMaterial` / `RemoveMaterial`. Shipped.
4. **Immediate-mode UI drawing.** `UISystem` / `ImmediateDrawBatch` DrawLine / DrawString / DrawImage / DrawPath / DrawFill, and `Font.MeasureString` (VRage.Render). This runs on every frame of the map. Shipped.
5. **Colonization map takeover.** About 20 private members of `ColonizationMapSessionComponent`, `MapSectorsRenderComponent`, `MapMarkersRenderComponent` and `MapObjectRenderComponent`, plus private nested camera structs (Game2.Client). Shipped.
6. **Planet terrain, atmosphere and gravity.** Private clipmap internals (internal types), `PlanetEnvironmentEntity`, proxy atmospheres, and the private gravity data struct. Shipped.
7. **Procedural generator, asteroids and encounters.** Protected or internal generator methods, a private `Density` backing field, the discovery list. Shipped.
8. **Particle effects.** `TrySpawnEffect`, `ParticleEffectHandle` (`SetParameters` / `FixEffectTime` / `UpdateTransform` / Stop / Dispose) and `ParticleEffectUserParameters` (VRage.Game.Client and VRage.Render). These are **shipped in Aerodynamics (re-entry plasma)**; Orbital no longer uses them (its dev PlasmaSpike was deleted 2026-10-04).
9. **Spot lights.** `RenderContracts.CreateSpotLightEntity`. No longer used in Orbital (the dev PlasmaSpike was deleted 2026-10-04); Aerodynamics' flight field has its own.
10. **Game UI.** Notification cards, the HUD speed box, terminal tab injection, top-screen input check (Game2.Client view models over VRage.UI). Shipped. The numeric-input dialog is dev-only.
11. **Definition and prefab injection at load time.** The **internal** `EntityCompositeDefinition.Assign`, private `PrefabDefinition._entity`, and private-set definition properties. Shipped in both mods.
12. **Block and component definition access.** Private `_definition` fields on thrusters, gyros, the killing field and the atmosphere generator, plus `Definition.Guid`. Shipped.
13. **Admin teleport.** `EntityAdmin.TeleportPlayer`, which is public but returns a VRage.Multiplayer type. Shipped core.
14. **Cross-mod.** Orbital → Aerodynamics: one shipped delegate hook and many dev toggles.
15. **Dev only.** Render stats, engine screenshot, camera toggle, block power read, PlanetMesh.

### Headline findings
- **Reflection itself is whitelisted.** The mod whitelist allows every namespace of `System.Private.CoreLib`, including `System.Reflection` and `System.Reflection.Emit` (see §2). What blocks the mods is that **assemblies are missing from the compile references** and that **members are private or internal**. There is no rule against reflection.
- **The most common single ask** is that VRage.Render, and secondly VRage.Game.Client, be referenced and namespace-whitelisted. Nearly all the render calls target **public** members; they go through reflection only because the assembly cannot be referenced.
- **Several reflective calls have a public replacement today** (§5). Examples: `RuntimeMaterialHandle<T>` instead of the internal `AddRuntimeMaterial`, `Component.SetData<T>` being identical to `Entity.Data.Set`, `ThrustData` / `GyroscopeData` instead of private `_definition`, and the `IAccessible` member accessors instead of backing-field writes.
- **Silently broken today:** three reflective lookups never match. Details are in §6.

---

## 2. How the mod compiler gates code today

Mod script code has to pass three independent gates.

**Gate 1. Compile references (CS0012 / CS0246).**
- `GameApp.AddScripting` (`SpaceEngineers2/Keen.Game2/GameApp.cs:434-483`) sets `GameCompilationDescriptor.MetaDatas` to the assemblies of `typeof(MeshBuilder)` (VRage.Core), `StringId` (VRage.Library), `Entity` (VRage.DCS), `Blob` (VRage.Core.Game; `using Keen.VRage.Core.Game.Data`, GameApp.cs:43), `UpdateTime` (Game2.Simulation), `InputGameComponent` (Game2.Client) and `IPhysics` (VRage.Physics) (GameApp.cs:473-482).
- These are added in `GameCompilationDescriptor` (`Game2.Simulation/Keen.Game2.Simulation.Scripting/GameCompilationDescriptor.cs:15-23`), on top of the defaults: CoreLib, System.Runtime, System.Linq, System.Collections and System.Collections.Immutable (per `D:\aero\tools\ModCheck\README.md`).
- **Any other assembly is invisible to the compiler.** This includes VRage.Render, VRage.Game.Client, VRage.Game, VRage.Voxels(.Client), VRage.UI, VRage.Multiplayer and System.ObjectModel.
- The mods' `.vrgproj` files list `Game2.Game.dll` under `ReferencedAssemblies` (`Orbital Mod.vrgproj`, `Aerodynamics Mod.vrgproj`), but the in-game compile does not use that list. ModCheck uses only the 7 + 5 assemblies above and reproduces the in-game CS0012 on `CubeBlockComponent.Definition.Guid`.

**Gate 2. Namespace whitelist (VRS1001).**
- `GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies` (GameApp.cs:449-460) is `typeof(int)`, `typeof(Enumerable)` plus the seven types above.
- `ConfigureWhitelist` (`Game2.Simulation/.../GameWhitelistProvider.cs:56-77`) then calls `AllowNamespace` for **every namespace that has an exported type** in those assemblies, skipping only `[EditorBrowsable(Never)]` types.
- The whitelist works at **namespace** granularity. There are no per-member bans.
- Because `typeof(int)` is a seed, all of CoreLib's namespaces are allowed. `D:\aero\tools\ModCheck\AllowedSymbols.Mod.txt:39-41` lists `System.Reflection.*`, `System.Reflection.Metadata.*` and `System.Reflection.Emit.*`, and also `System.IO.*` and `System.Runtime.InteropServices.*`.
- `ModCompilationDescriptor` also enables `unsafe` (`.../ModCompilationDescriptor.cs:19`).
- **Known exception:** an array of a type declared in the mod is banned (VRS1001). ModCheck rule MODVAR1 catches it; see the ModCheck README.

**Gate 3. C# accessibility.**
- Private, protected and internal members are unreachable without reflection, even in a whitelisted, referenced assembly. No whitelist setting changes this. Keen would have to make the member public, add a public accessor, or use `InternalsVisibleTo`.

**ModCheck caveat.** `D:\aero\tools\ModCheck\WhitelistGen\Program.cs` seeds the whitelist with `VRage.Library` twice and **omits VRage.Core.Game**. Its comment says `Blob(VRage.Library again - the duplicate)`, but GameApp's `Blob` is `Keen.VRage.Core.Game.Data.Blob`. As a result, `AllowedSymbols.Mod.txt` contains no `Keen.VRage.Core.Game.*` namespace at all.
- This is the likely reason, or one of the reasons, for the "regenerated whitelist is incomplete" note in the ModCheck README, where Session and GetWorldTransform get flagged.
- **This has not been fixed or verified here.**

**What follows for the requests to Keen:** every request below is one of four things:
- **(R)** add an assembly to `MetaDatas` + `AllowedAssemblies`;
- **(P)** make a member public, or add a public accessor or API;
- **(N)** add a supported API for a capability that has none;
- **(M)** remove an `[EditorBrowsable(Never)]` / namespace exclusion. None of the current asks need this.

---

## 3. Inventory: shipped features

Columns: **site** (file:line) | **target** | **assembly** | **access** (type / member) | **purpose** | **failure behaviour**.

- "WarnOnce" means PRB's warn-once logger, which writes one `Log.Warning` per key.
- "Status" means a status string that only the debug panel or DevHarness shows, so nothing is logged.
- All types are public unless the table says otherwise.

### 3.1 Orbital: shared render plumbing (PRB)

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| PRB:519-531, 1090-1097 | `AppDomain.GetAssemblies()` scan for "VRage.Render"; `FindType` (name scan) | — | — | Find the render assembly | WarnOnce "VRage.Render not loaded" (530). All render features are off. The result stays latched with no retry. |
| PRB:533-536 | `Keen.VRage.Render.EngineComponents.RenderEngineComponent.Instance` (static field), `.RenderContracts` (abstract property; runtime type is the internal `Render12EngineComponent`, VRage.Render12) | VRage.Render | public / public | Entry point to `RenderContracts` | WarnOnce (536, 555) |
| PRB:538-541, 561-576 (callers: PRB:331, MapRingMesh:218-220, PlanetRings:408-410, PlasmaSpike:894-896, MapGlobes:51) | `RenderContracts.CreateRootEntity(string, in WorldTransform, bool)`, `CreateModelEntity(string, ResourceHandle, RelativeTransform, RootEntity?, RenderFlags, EntityType, PlanetEnvironmentEntity?)` | VRage.Render | public / public | Proxy globes, map globes, rings, spike | Returns null. 331 is caught (351, WarnOnce). **564 and 571 are not wrapped in try/catch.** |
| PRB:544-545 | `Keen.VRage.Render.Data.RenderFlags`, `EntityType` (via `Enum.ToObject` / `Enum.Parse("Map")`) | VRage.Render | public enums | Flag values are hard-coded (Visible = 1, SkipCulling = 2, ...) | `_renderOk = false` (549) |
| PRB:594-599 (callers: PRB:360, MapRingMesh:68, PlanetRings:347, PlasmaSpike:295) | `Keen.VRage.Render.Contracts.RootEntity.UpdateTransform(in WorldTransform)` | VRage.Render | public readonly struct / public | Move roots **every frame** | `?.` makes a missing method a silent no-op. 598 is not caught. |
| PRB:475, 579-583, 1015 | `ModelEntity.SetRenderFlagsState(RenderFlags, bool)` | VRage.Render | public / public | Show or hide | `?.` silent. GetMethod is not cached. |
| PRB:389-391 | `ModelEntity.UpdateModel(ResourceHandle)` | VRage.Render | public / public | Swap in the hi-res globe | WarnOnce (390) |
| PRB:586-591, 611-623 (callers: PRB:369, MapRingMesh:111, PlanetRings:350) | `ModelEntity.SetEntityCustomData<T>(in T)` via **MakeGenericMethod** on every call | VRage.Render | public / public generic | Per-entity GPU data | Missing: silent skip (369) or false (589). 590 is not caught. |
| PRB:546-547, 367-368 | `Keen.VRage.Render.Materials.Templates.ScaleCustomData.Scale` | VRage.Render | public struct / public field | Globe scale | Boxed-struct SetValue; caught at 375 |
| PRB:465, 487-488, 601-605 | `ModelEntity` / `RootEntity` / `RuntimeModel` / `PlanetEnvironmentEntity.Dispose()` | VRage.Render | public / public | Cleanup | 465 `catch {}`; 605 WarnOnce |
| PRB:1065-1076, 1078-1088 | `GetMember` / `SetMember` helpers (walk base types, public and non-public) | — | — | Used for most private-field reads below | `GetMember` returns null silently. `SetMember` throws `MissingMemberException`. |

### 3.2 Orbital: runtime meshes and runtime materials

These files hand-build a `RuntimeMeshData`: MapPipeline (map sectors), MapRingMesh (map rings), PlanetRings (proxy planet rings), and PlanetMesh and PlasmaSpike (both dev, §4).

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| MapPipeline:45-46, 75, 88, 122-124; MapRingMesh:141-142, 178-180; PlanetRings:284, 378 | `Keen.VRage.Render.Data.VertexFormat.VertexFormatPositionUV0Packed(Vector3, Vector2 / HalfVector2)`, `VertexFormatNormalTangentPacked(Vector3, Vector4)` constructors | VRage.Render | public readonly struct / public | Vertices: **one `Activator.CreateInstance` per vertex** | "mesh types not found" (MapPipeline:49, MapRingMesh:150) |
| same files; MapPipeline:56-57, 69-74, 88-100, 122-136 | `Keen.VRage.Library.Memory.Buffer<T>` constructor `(Allocator, string)`, `.Add(T)`, `.Count`, built with **MakeGenericType** over render vertex types | VRage.Library (T from VRage.Render) | public / public | Vertex and sub-part buffers | `Invoke` on a **boxed** `Buffer<T>`. Caught at MapPipeline:184 / MapRingMesh:73. |
| MapPipeline:47, 94-98, 130-134; MapRingMesh:143, 190-195; PlanetRings:285, 388-393 | `Keen.VRage.Render.Data.IRuntimeMeshData+SubPart` fields `Name`, `IndexStart`, `IndicesCount`, `Material`, `CullingCone` | VRage.Render | public nested struct / public fields | Sub-parts | Boxed-struct SetValue. NRE is caught (MapPipeline:184). |
| MapRingMesh:145, 195 | `BackfaceCullingCone.CullNone` | VRage.Render | public / public static | Two-sided rendering | null: **silently left at default** |
| MapPipeline:48-50, 145-154; MapRingMesh:146, 151, 202-212; PlanetRings:287-293, 396-400 | `Keen.VRage.Render.Data.RuntimeMeshData<TVS0,TVS1,TVS2>` (MakeGenericType + Activator; MapPipeline/MapRingMesh **skip the constructor**), `.AABB` setter | VRage.Render | public struct / public | The mesh | caught |
| MapPipeline:146-153, 246-256; MapRingMesh:203-210, 235-243; PlanetRings:397-399 | `RuntimeMeshData._vertexStream0/1/2`, `_indices`, `_subParts`, `_meshSections`, `_bones`, `_debugName` | VRage.Render | public struct / **private fields** | Fill the mesh | NRE or `MissingFieldException`, caught. **Breaks on any rename.** |
| MapPipeline:158-162; MapRingMesh:147-150, 215-216; PlanetRings:288-289, 403-404 | `RenderContracts.CreateRuntimeModel(IRuntimeMeshData, RenderRuntimeDataType, bool, bool)` | VRage.Render | public / public | Upload | MapPipeline:160 "not found". MapPipeline rescans on every ShowParts. |
| MapRingMesh:144, 149, 217; PlanetRings:290, 405 | `RuntimeModel.op_Implicit(RuntimeModel) → ResourceHandle` | VRage.Render | public / public | Model handle | caught |
| PlanetRings:455-463 (callers PlanetRings:381-385, 452, 481; MapRingMesh:94, 127) | `RenderContracts.GetMaterialSystem()` → `Keen.VRage.Render.Contracts.MaterialSystem.AddRuntimeMaterial(MaterialDefinition)` / `RemoveMaterial(MaterialDefinition)` (`VRage.Render/Keen.VRage.Render.Contracts/MaterialSystem.cs:67, 92`) | VRage.Render | public readonly struct / **internal** | Register the scaled ring material | WarnOnce, then "material registration failed" (MapRingMesh:127, PlanetRings:385) |
| MapRingMesh:104-111 | `Keen.VRage.Render.Materials.Templates.ColoringCustomData.ColoringHSV`, `.ColoringFlags` (`Flags.IgnoreColorMask = 1`) | VRage.Render | public / public fields | Ring tint | type missing: **returns true, so it silently stays uncoloured** (105) |
| PlanetRings:264-266 | `Keen.Game2.Client.GameSystems.Render.ProceduralVolumeRenderComponent.ModelEntity`, then `ModelEntity.IsValid` and `SetRenderFlagsState` | Game2.Client / VRage.Render | public / public get | Hide the game's own ring | WarnOnce (269) |
| PlanetRings:303-304, 318-323 | `ProceduralVolumeRenderComponent._runtimeMeshData`, `._entityCustomData`; `VertexFormatPositionUV0Packed.Position` / `.TexCoord`; `SubPart.Material` | Game2.Client / VRage.Render | public / **private fields**; public fields | Copy the game's ring mesh and data | `MeshError` Status (305, 324, 326) |
| PlanetRings:417-433 | `ProceduralFieldCustomData.Ring` / `.Field`, `AsteroidRing.InnerRadius` / `OuterRadius` / `HalfHeight`, `AsteroidField.Scale` | VRage.Render | public / public fields | Scale the ring's custom data | Nested boxed structs edited in place. WarnOnce (355), ring off. |

### 3.3 Orbital: immediate-mode UI drawing (map and HUD, per frame)

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| MapPipeline:306-310 | `RenderContracts.GetUISystem()`, `UISystem.CreateImmediateMainViewBatch(int, string)` | VRage.Render | public / public | Per-frame draw batch (**uncached, every frame**) | returns false; LastError (327) |
| MapPipeline:314-317, 557-569, 623-625 | `ImmediateDrawBatch.DrawLine(Vector2, Vector2, ColorSRGB, float, DashingTypeEnum, float, bool)` | VRage.Render | public / public | Lines | Skipped. Boxes arguments on every call. |
| MapPipeline:317, 635-640 | `ImmediateDrawBatch.DrawString(Font, Vector2, ColorSRGB, string, float, bool, float?, float)` | VRage.Render | public / public | Text | `catch {}` (527, 890), silent |
| MapPipeline:664-671, 620, 729 | `ImmediateDrawBatch.DrawPath(ReadOnlySpan<QuadraticBezier2>, ...)` via **Delegate.CreateDelegate**, rebuilt every UiBegin | VRage.Render | public / public | Smooth curves | Falls back to DrawLine, silently |
| MapPipeline:668, 672-681, 751 | `ImmediateDrawBatch.DrawFill(...)`: `MakeGenericType` of a delegate over `GradientFillData?`, plus `MakeGenericMethod` (**every frame**) | VRage.Render | public / public | Filled shapes | VectorStatus. Falls back to dots. |
| MapPipeline:669, 773-829 | `ImmediateDrawBatch.DrawImage(ResourceHandle, in BoundingBox2, ColorSRGB, bool, ResourceHandle?, in BoundingBox2I?)` | VRage.Render | public / public | Icons | After one exception `_drawImageM = null` and is never rebound (664). **Icons stay off for the rest of the session, silently.** |
| MapPipeline:472-484 | `Keen.VRage.Render.Contracts.Font.MeasureString(ReadOnlySpan<char>)` via CreateDelegate | VRage.Render | public / public | Text width | Falls back to an estimate |
| MapPipeline:321-323; GameMap:138; FrameMarkers:98 | `ColonizationMapSessionComponent._configuration` → `ColonizationMapConfiguration.Font` (`FontDefinition`) → `FontDefinition.op_Implicit → Font` | Game2.Client / VRage.Render | public / **private** field; public | Map font | Text silently not drawn |
| MapPipeline:782-795 | `ColonizationMapConfiguration.PlayerIcon` + `ResourceHandle<T>.op_Implicit` | Game2.Client / VRage.Library | public / public | Player icon | Fallback icon. The :792 lookup never matches (`in` parameter); :789 works. |
| MapPipeline:1003-1059 | `GPSMarkerRenderSessionComponent._definition`, `._cameraComponent` (**private**); `GPSMarkerHelpers.DrawSingleMarker`, nested `MarkerDrawSettings` (16 public fields, boxed); `GPSMarkersSessionComponentDefinition.*` (incl. the engine typo `RegualarFontSize`); `CameraComponent.ToScreenSpace`; `LevelsOfDetail.Full` (`Enum.Parse`) | Game2.Client (+ Game2.Simulation enum) | public / mixed | Draw markers exactly as the HUD does | GpsError Status. **A new `object[10]` per marker per frame**, and a persistent failure retries the build every frame. |

### 3.4 Orbital: colonization map takeover (Game2.Client privates)

| site | target | access | purpose | failure |
|---|---|---|---|---|
| GameMap:134, 138, 206, 250, 294, 301, 471; MapView:183 | `ColonizationMapSessionComponent.SectorsRenderer` | **private** property | Draw through the game's sector renderer | silent / `FrameHost.Guard` logs |
| GameMap:233-234 | `ColonizationMapSessionComponent._windows`, then `IPlatformWindow.ClientMousePosition` | **private** field | Mouse position for picking (per frame) | Picking silently off |
| GameMap:250, 472; MapView:184 | `MapSectorsRenderComponent.SectorIds`, `.SelectedSector` | public (base type in VRage.Game.Client) | Picking, selection | caught |
| GameMap:255-256 | `ColonizationMapSessionComponent._sectorIndexUnderCursor`, `._hoverIndex` | **private** int | Feed our pick to the game | caught |
| GameMap:304-310 | `MapSectorsRenderComponent._proceduralSectorCollider` | **private** | Turn off the game's sector raycast | **silent** (`catch {}` 315) |
| GameMap:283-285, 318-321 | `ColonizationMapSessionComponent.MarkersRenderer` (private) → `MapMarkersRenderComponent.Enabled` | private / public | Hide the game's markers | silent |
| GameMap:267-268, 327-328 | `MapMarkersRenderComponent._playerControllerComponent` | **private** | Suppress "You are here". **Stash and restore, safety-critical** (comment at 273-276) | silent |
| GameMap:334, 346, 396 | `ColonizationMapSessionComponent._discoveredPlanets`, `._mainStar` | **private** | Hide the game's globes and star | silent |
| GameMap:358-359, 455 | `MapObjectRenderComponent.Deactivate()` / `Activate(bool)` / `SetLabelVisible(bool)` | public | Star glow, labels | silent |
| GameMap:453-459 | `MapObjectRenderComponent._name` | **private** | Blank labels | silent |
| PRB:1036-1045 (caller GameMap:340) | `MapObjectRenderComponent._scale` (**private**), `UpdateScale(float)` | private / public | Shrink the game's globes | WarnOnce |
| PRB:1011-1015 (callers GameMap:134, 294) | `Keen.VRage.Game.Client.Render.ModelRenderBaseComponent.RenderModelEntity` → `ModelEntity.SetRenderFlagsState` | VRage.Game.Client, public get | Hide the game's sector mesh | WarnOnce |
| MapPipeline:66, 165-201, 212-238 | `MapSectorsRenderComponent.BaseMaterial` (**private**), `._proceduralSectorModel` (**private**), `.ModelHandle` (**protected** override), `._highlights` (**private readonly**), `.UpdateSectorParameters()` (**private**); `MeshEffectSystem.CreateHighlight`, `MeshEffectHandle.Dispose` (VRage.Render, public) | mixed | Put our own sector mesh into the game's renderer and restore it afterwards; rebuild highlights | LastError; several `?.` silent paths. `UpdateSectorParameters` missing leaves the sectors uncoloured. |
| PRB:793-872 (callers MapCamera:71, 113; GameMap:67, 211) | `ColonizationMapSessionComponent+CameraData` {`TargetDistance`, `FocusPointOffset`, `Distance`}, `+CameraNeedsUpdateTag` (**private nested structs**), via `DEntityContext.Get<T>` / `Set<T>` / `TryRemove<T>` + MakeGenericMethod | Game2.Client, VRage.DCS | private / public | Zoom and clamp the map camera (**`TryGetMapCamera` runs per frame**) | false; WarnOnce |
| GameMap:138, 147-148; MapView:391-392; StarProxy:30-31 | `_configuration.StarVisualPrefab`, `.PlanetScale` | private field / public | Star prefab, globe scale | Scale falls back to 1.0 |
| FrameMarkers:133-134, 156-161 | `GPSMarkerRenderSessionComponent._antennas` (private), `AntennaMarkers.ActiveMarkers`, `GPSMarkersData.GPSMarkers` / `.MarkersGroups`, `Group.Markers`, `ContractPerPlayerData.HUDMarkers` | Game2.Client / Game2.Simulation | private / public | Avoid double-marking. Reflected **only because `ObservableList<T>` derives from System.ObjectModel** (comment at FrameMarkers:152). | silent |

### 3.5 Orbital: planet terrain, atmosphere, gravity, prefab

All rows are in PRB unless noted.

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| 129 | `PlanetEnvironmentRenderComponent._voxelPlanetRender` | Game2.Client | **private** | Terrain render component | WarnOnce (144), `Terrain = null` |
| 130-133 | `Keen.VRage.Voxels.Client.Components.VoxelRenderComponent._clipmap`, `._lowResClipmap` (value type `VoxelClipmap` is **internal**) | VRage.Voxels.Client | **private** | Clipmaps | Terrain hiding silently off |
| 267 | `Keen.VRage.Voxels.Client.Clipmap.VoxelClipmap._visible` | VRage.Voxels.Client | **internal class / private field** | Freeze the clipmap without its setter's side effects | Logs the first 5 failures (288); retries |
| 273-274 | `VoxelClipmap.RootEntityProvider` → `DistributedRootEntityProvider._rootEntities` (`Dictionary<Vector3I, RootEntity>`), taken under `lock` | VRage.Voxels.Client | **internal / internal struct / private readonly** | Deactivate the per-km render roots | silent `continue` |
| 282-301 | `RootEntity.IsValid`, `.Activate()` / `.Deactivate()` | VRage.Render | public | as above | logged ×5 |
| 137-141, 164-166 | `PlanetEnvironmentRenderComponent._planet` (**private**) → `Keen.VRage.Voxels.Components.VoxelPlanetComponent.Radius` / `RadiusWithMaxHills` / `AtmosphereRadius` / `SpherizeRadius` | Game2.Client / VRage.Voxels | private / public | Planet radii | WarnOnce; radius 0 |
| 149-155 | `PlanetEnvironmentRenderComponent.PlanetEnvironmentEntity` / `.AtmosphereDefinition` / `.CloudDefinition` (public), `._planetEnvRenderDefinition` (**private**) → `.Spherization` | Game2.Client | mixed | Save the atmosphere's show and hide arguments | WarnOnce (175, 178). The handle is a boxed struct cached once and **may go stale**. |
| 158-162 | `Keen.VRage.Render.Materials.SpherizationOverrideHelper.GetSpherizationData(...)`; `VoxelPlanetRenderComponent.Definition.PlanetOverlay` | VRage.Render / VRage.Voxels.Client | public | Spherization data | WarnOnce |
| 150, 309, 444-449 | `PlanetEnvironmentEntity.SetParameters` (7 parameters) | VRage.Render | public | Hide the real atmosphere and clouds; resize the proxy atmosphere | WarnOnce (312); 446 silent on a parameter-count mismatch |
| 425-439 | `RenderContracts.CreatePlanetEnvironmentEntity` (12 parameters) | VRage.Render | public | Proxy atmospheres. **Rebuilt about every frame during warp.** | Feature off for the session, WarnOnce |
| 194, 199 (caller ServerPlanetBeacon) | `EntityCompositeDefinition.Definitions`; `Keen.VRage.Game.Models.ModelDefinition.Model` | VRage.DCS / **VRage.Game** | public | Proxy model from the map-visual prefab | WarnOnce; `HasProxyModel = false` |
| 632 (caller ServerPlanetBeacon:117) | `DiscoverablePlanetComponent._mapVisualPrefab` | Game2.Simulation | **private** | as above | WarnOnce |
| 650-671 (callers ServerPlanetBeacon:131, 200) | `GravityGeneratorComponent.Serialize(GravityGeneratorObjectBuilder)` | Game2.Simulation | **protected** | Read `AccelerationDistance` and `FallOffPower` | WarnOnce; false |
| 685-710 (caller SystemHost:377) | `GravityGeneratorComponent+GravityGeneratorData` {`FallOffPower`, `AffectDistance`} via `Component.GetData<T>` / `SetData<T>` (**protected**, MakeGenericMethod with a **private** type argument) | Game2.Simulation / VRage.DCS | **private nested struct** | Rewrite the gravity law (inverse square) | `Log.Warning` (698, 710) |
| ServerPlanetBeacon:126 | `GeneratorBaseComponent._definition` → `AtmosphereGeneratorDefinition.Density` | Game2.Simulation | **private** / public | Does the planet have an atmosphere | **silent** (null / NaN) |
| 988-998 (callers FrameHost:928, 943; every frame transition) | `Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin.TeleportPlayer(Session, WorldTransform, Entity?, bool, bool)` | Game2.Simulation | public static | Teleport player or ship. Late-bound only because it returns `NetworkStory<EntityId?>` (**VRage.Multiplayer**). | WarnOnce / `Log.Warning`. The result is not awaited, so its errors are lost. |

### 3.6 Orbital: procedural generator, asteroids, encounters, environment

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| AsteroidFrames:316-322 | `ProceduralGeneratorServerSessionComponent.Deactivate(bool)` / `.CreateTriggers()` (base `ProceduralGeneratorSessionComponent.cs:839, 845`) | VRage.Game (base VRage.Core.Game) | **protected virtual** | Rebuild sectors after registering our volume | `Warn` (320, 322) |
| AsteroidFrames:515, 555-563; AsteroidBridge:234-244 | `VolumeDefinition.<Density>k__BackingField` (property `Density { get; private set; }`) | VRage.Core.Game | **private compiler backing field** | Belt density / suppress samples | Missing: `Warn("density")`, then **the belt gets no rocks**. Restore is `catch {}`. Mutates shared definitions. |
| AsteroidFrames:682-713 | `DiscoveriesServerSessionComponent._providerEntities`, `.ProviderRemoved(Entity)` | Game2.Simulation | **private** | Remove our volumes from "Discovered Objects" | `Warn` |
| AsteroidFrames:804-805 (callers 437, 474, 501) | `ProceduralGeneratorSessionComponent._spawner` (`[Service] IEntitySpawner`) | VRage.Core.Game | **protected** | Spawn and delete volumes | 438 `Warn`; 474 / 501 **silent no-op**, so the volume is never deleted |
| AsteroidFrames:341 | `ProceduralGeneratorSessionComponent.MaxSectorSpawnSamples` | VRage.Core.Game | **public** | Diagnostics text | **Not needed**: `gen` is statically typed |
| AsteroidBridge:177-181 | `VoxelOperationsComponent` (`FindType`) + `Session.GetEntitiesOfType<T>()` (MakeGenericMethod) | Game2.Simulation (base in VRage.Voxels) | public | Asteroid anchors | `catch {}`, silent |
| AsteroidBridge:220-228 | `ProceduralVoxelEntityComponent` + `Entity.TryGet<T>()` **with 0 parameters**, then `.IsManual` | VRage.Voxels / VRage.DCS | **NOT FOUND**: only `TryGet<T>(StringId tag = default)` exists (`VRage.DCS/Keen.VRage.DCS.Components/Entity.cs:133`) | Is the body player-edited | **Broken: always returns "manual"** (224). See §6. |
| SpawnGuard:42-44 | `ProceduralGeneratorSessionComponent._encounters` | VRage.Core.Game | **protected readonly** | Encounters near a berth | Returns 0 silently; Status |
| SpawnGuard:43, 51 | `ProceduralGeneratorServerSessionComponent.DeleteEncounter(in SpaceEncounterId)` | **VRage.Game** | public | Despawn an encounter | silent 0 |
| SpawnGuard:66-94 | `ProceduralGeneratorSessionComponent.RegisterLocalSpawnPreventionShape(SpawnPreventionAreaBase)` / `RemoveLocalSpawnPreventionShape(int)` | VRage.Core.Game | **protected internal** | No-spawn zones over border berths | Status only. The engine asserts `IsCurrentThreadDCS`; the calling thread was not checked. |
| DelfosHeat:38-62; EncounterFrames:206-207 | `KillingFieldComponent._componentDefinition` (**private**); `object.MemberwiseClone()` (**protected**); `KillingFieldComponentDefinition.Radius` / `DamageRadius` / `MaxDamageRadius` / `MinDamage` / `MaxDamage` (public get, **private set**) | Game2.Simulation | mixed | Scale the brown-dwarf heat field to our star | `?.` setters are **silent no-ops** on a rename. Status only, no log. EncounterFrames falls back to 7e5 silently. |

### 3.7 Orbital: game UI (Game2.Client view models over VRage.UI)

| site | target | access | purpose | failure |
|---|---|---|---|---|
| GameUi:234-258 | `Session.Get<T>()` (**does not exist**; the loop never matches), then an **AppDomain-wide `GetTypes()` scan** for `GameEntityExtensions.Get<T>` / `TryGet<T>(this Session)` + MakeGenericMethod | public | Find `InGameUI` / `SharedUIComponent` | null. **Uncached full type scan on each call.** |
| GameUi:29-40 | `InGameUI.DisplayNotification(NotificationViewModel)`; `NotificationViewModel` constructor `(string, string?, TimeSpan?, ResourceHandle<GUIAsset>?)` | public | Orbit card (OrbitHud:268) | LastError, not logged |
| GameUi:49-62 | `NotificationViewModel.Title` / `.Content` setters, `.ScreenViewModel` | public | Update the card in place | caught |
| GameUi:150-166 | `CockpitHUDComponent._hud` / `CharacterHUDComponent._hud` | **private** | HUD root | SpeedStatus |
| GameUi:171-193 | Bounded BFS over properties and Keen-typed fields (public and non-public) to find `MovementHUDScreenViewModel` | VRage.UI internals, **not verified** | Find the SPD box view model | silent "not found". **400 nodes every 2 s, fragile.** |
| GameUi:154, 157 | `MovementHUDScreenViewModel.Speed` setter | public | Write orbital speed into the SPD box (**every frame**, boxed float) | caught |
| GameUi:225-231 | `SharedUIComponent.TryGetTopScreenNeedingInput()` | public | Block raw keys while UI has input (**every frame**) | Retry every 5 s |
| RendezvousView:56-123 | `TerminalScreenViewModel.SelectedSubScreen` (public get/set, **per frame**), `.SubScreens` (**private set**, via `GetSetMethod(true)`); `TerminalSubScreen` constructor `(…6 parameters)`, `.Id.Value`, `.Content`, `.Category`, `.ControlHints` (**init-only**); `TerminalScreenId` constructor; `LocKey.FromString`; base `OnPropertyChanged(string)` (**NOT FOUND**, in VRage.UI) | public / private set / init / unknown | Inject a third map tab (Rendezvous) | Falls back to a drawn tab (67); caught at 123 |

### 3.8 Orbital: load-time definition injection

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| Injection/Injections.cs:40 | `EntityCompositeDefinition.Assign(Builder)` (`VRage.DCS/.../EntityCompositeDefinition.cs:570`), via Keen's `ReflectionUtils.TryInvokeMethod` | VRage.DCS | **internal** | Install the new composition with our components | **Return value ignored, so failure is silent**, and the object builder is still added (43 / 45), leaving them **inconsistent** |
| Injection/Injections.cs:45 | `PrefabDefinition._entity`, via `ReflectionUtils.GetFieldInfo(...)!` | VRage.Core.Game | **private** | Write back the patched object builder | NRE, **no try/catch** into the definition pipeline |
| Injection/SpeedLimitInjector.cs:22-28 | `PhysicsSessionConfiguration.MaximumSpeedLinear`, `.MaximumCharacterSpeedLinear` | Game2.Simulation | public get / **private set** | 1000 m/s caps | `?.` silent. The before/after log (26) shows it. |
| Injection/SpeedLimitInjector.cs:40-46 | `CharacterPhysicsComponentDefinition.MaxVelocity` | VRage.Physics | public get / **private set** | Character cap | logged |
### 3.9 Aerodynamics Mod

Moved to the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md` (§3). Orbital's calls **into** Aero are below (§3.10).

### 3.10 Cross-mod (Orbital reading Aerodynamics)

All of these go through `AppDomain.GetAssemblies()` + `GetType(name)`, with **no caching**: `ForeignValue` (PRB:902-931), `SetForeignStatic` (PRB:935-952) and `SetForeignFlag` (PRB:954-971).

| site | Aero member | Aero access | shipped/dev | failure |
|---|---|---|---|---|
| O/Frames/EntryHost.cs `HookAero` (replaced the `External` poke 2026-10-03) | Aero's public API **`AeroMod.AeroApi`, version 2 or later (3 since 2026-10-03)** (`Aerodynamics Mod/docs/API.md`): `AeroApi.RegisterEntrySource(string, Delegate)` (returns the entry-source contract, `AeroEntryFx.EntrySourceContract` = 2, which `EntryHost.AeroEntryContract` must equal; the API version itself may grow), `HasEntrySource(string, Delegate)`; the source is a `Func<Entity, ValueTuple<Vector3, float>>` (air past the grid, world m/s; strength 0..1). Found once per change of the loaded-assembly count (no scan otherwise), then a cheap "still registered?" every 600 ticks; re-registers on a new session / reloaded Aero. Contract numbers and shapes checked from source by Orbital's fast gate (`tools/check_contract.sh`: API version >= 2, entry-source contract equal, the member shapes). | public static | **shipped** | `EntryHost.GlowWhy` says why not hooked (Aero absent, contract mismatch, throw). A source that throws is dropped by Aero and re-registered at the next check. |
| O/Render/PlasmaSpike.cs:464-476, 1394-1431 | `AeroGridComponent._chunks` (**private**, AeroGridComponent.cs:41); `ChunkedTable.Occluders`, `.N`, `.ChunkCount`, `.Bounds`, `.GetShare(int)`, `.HasShare(int)`; `P16.V`. Hard-codes stride 13 (1411, 1418). | private / public | dev | "why" strings. Drag weighting silently becomes uniform. |
| O/Frames/DevHarness.cs:347, 357, 360, 363, 366, 1044 | `ThrustTorque.FlamesEnabled`, `AeroExport.Enabled`, `AeroGridComponent+AeroFlightTest.Enabled` (**internal** nested class), `AeroCost.Log`, `AeroSwitch.Enabled`, `AeroSpeedSpike.Enabled` | public static bool | dev | strings |
| DevHarness.cs:917-920 | `AeroMod.PhysicsHack.GetGravityDirection(DEntityContext)` | public static | dev | strings |
| O/Render/PlasmaSpike.cs:1675, 1684 (added 2026-10-03, `VapourFlow`) | `AeroMod.AeroEntryFx.TryGetFlow(Vector3D, out Vector3, out float, out float, out float, out Vector3, out float)` (A/Integration/AeroEntryFx.cs): the grid's travel in its own frame, Mach, density, speed, and (since 2026-10-03, later) its lift direction and lift strength. Found once by an assembly scan for `AeroMod.AeroEntryFx`, the `MethodInfo` cached; called 4 times a second with a 7-element `object[]` (the outs read back). | public static (added for this) | dev (Mode 6 vapour; the shipped vapour would live in the Aero mod itself) | "why" string in the status; the vapour stays off. |
| O/Render/PlasmaSpike.cs `AeroWingTips` (added 2026-10-03) | `AeroMod.AeroEntryFx.WingTips(Entity, List<Vector3>, List<Vector3>)` (A/Integration/AeroEntryFx.cs): the aero mod's detected wings' tips (trailing edge, grid-local m) and their lift normals, for the wingtip vapour lines. Same assembly scan, `MethodInfo` cached; called once per vapour placement (not per frame). | public static (added for this) | dev (Mode 6 vapour) | `TipWhy` in the status; falls back to the outline-based tip guess. |
| O/Render/PlasmaSpike.cs `FlightPlasma` (added 2026-10-03) | `AeroMod.AeroEntryFx.EntryStrength(float, float)` (A/Integration/AeroEntryFx.cs): the plasma strength 0..1 at a Mach and density (Mach-2 onset, log density scale), for flight mode (7) driving the plasma lines' held time and the spot light. Same assembly scan, `MethodInfo` cached; called 4 times a second. | public static (added for this) | dev (Mode 7 flight) | strength 0: no plasma. |
| DevHarness.cs:1033-1040 | any `AeroMod.*` static field or property (`aeroset` / `aeroget`) | any | dev | strings |

**Not a Keen ask.** It could become a typed contract if SE2 let one mod reference another's script assembly, perhaps through the existing `ProjectDependencies`. That is **unverified**: both mods depend only on GUID `56dacb76-...`.

---

## 4. Inventory: dev-only reflection

This code is compiled into the shipped mod but **only runs when `%TEMP%\OrbitalMod\dev.flag` exists** (`OrbitalConfig.DevHarness`; `O/Frames/PlanetFrameComponent.cs:82-87`). Players never run it, so it should not drive whitelist requests on its own. It does, however, show which capabilities the mods want next.

| file | what it reflects | notes |
|---|---|---|
| **O/Render/PlasmaSpike.cs** (entry points DevHarness:82, 905-911; off until the `plasmaspike` command, 274) | <ul><li>Runtime mesh: vertex formats, `Buffer<T>`, `SubPart`, `RuntimeMeshData` private fields, `CreateRuntimeModel`, `UpdateModel` (395-421, 744-745, 862-904).</li><li>Root and model entities.</li><li>`SafeZoneMeshData` / `ColoringCustomData` via `SetEntityCustomData<T>` (919-941).</li><li>**Internal** `MaterialSystem.AddRuntimeMaterial` (333, 381).</li><li>`ParticleEmitterDefinition.AnimationParameters` + `ParticleAnimationParameters.AtlasTexture` / `NumFramesInAtlas` / `FirstFrameIndex` / `NumFramesInAnimation` / `AnimationFramerate` (359-370).</li><li>**Particle effects**: `TrySpawnEffect` (986-1016), `ParticleEffectUserParameters` (1005-1006, 1283-1288, 1372-1374, 1705-1736), `ParticleEffectHandle.SetParameters` / `FixEffectTime` / `Dispose` (1022, 1295-1296, 1375-1376, 1722-1738).</li><li>**Spot light** `RenderContracts.CreateSpotLightEntity` (1319-1337, parameters matched by name).</li><li>Cross-mod `_chunks` (§3.10).</li></ul> | About 4 reflective calls and 3 allocations per vertex, at 8-24 rebuilds a second. `CopyMaterial` (315-335) is dead code. Silent paths: 869, 903, 920, 936, 1023. |
| **O/Render/PlanetMesh.cs** (`Enabled = false`, line 33; DevHarness:769) | <ul><li>`PlanetOverlayDefinition.OverlayColorMetal` (216).</li><li>`RuntimeMaterialHandle<PBREmissiveMaterialDefinition>` constructor via MakeGenericType (308, 320). This is the **public** route to runtime materials.</li><li>The full runtime-mesh set (339-494).</li><li>`RuntimeModel.Dispose(bool)` (184).</li></ul> | The material handles are never disposed (112). |
| PRB:729-767 `FrameStats` (DevHarness:831) | `StatsRecorderSessionComponent.ImmediateStats`, `StatsRecorderEngineComponent._statsRecorders` (**private**), `StatCollection.GpuTimeWork` / `RenderThreadTimeWork` / `MainThreadTime`, `LogStat.Calculate` | **Broken** (§6) |
| PRB:880-892 `EngineScreenshot` (DevHarness:241) | `RenderContracts.GetMainTarget()` → `MainRenderTarget.TakeScreenshotAsync(...)` | VRage.Render, public |
| PRB:771-781 `CallPrivate` (DevHarness:133) | `CameraSystemComponent.ToggleCameraView()` | Game2.Client, **private** |
| PRB:503-515 | `RenderSystem.SetDraw3DMap(bool)` | **Dead code, no callers** |
| GameUi:95-124 (DevHarness:619) | `SharedUIComponent.DialogsConfiguration.NumericInputDialog`, `TextInputDialogViewModel` constructor plus **init-only** `ConfirmAction` / `InputValidator` / `TitleOverride`, `SharedUIComponent.ShowDialog` | Number dialog |
| O/Frames/SensorBlocks.cs:36, 110 | `BlockKindDefinition.Name` (public, so not needed); `PowerableBlockComponent._appliedConsumption` (**private**) | Radar power read |
| FrameMarkers:246 (via PRB:1053-1063 `CallAdd`) | `ObservableList<GPSMarker>.Add` | Clone a marker |
| MapRingMesh `Reset` (DevHarness:867-870) | `MaterialSystem.RemoveMaterial` (**internal**) | Tuning knobs |
| §3.10 dev rows | Aero flags, `GetGravityDirection`, `aeroset` / `aeroget` | — |

Own-code metadata only, with no engine dependency: `O/Frames/Prewarm.cs:32-47` (`GetMethods` + `RuntimeHelpers.PrepareMethod` to pre-JIT the map). It is shipped and harmless.

---

### 4.x Changes since the first audit (kept current per the standing rule)

- **2026-10-03 DEV generated grids (`Frames/GridGen.cs`, `gen`) and hinge driving (`Frames/HingeRig.cs`, `hinge`)**: no engine reflection - the game's public `GridBuilder` (Game2.Simulation.Utils), `HingeComponent` (`RequestSpawnTop`, `SetVelocity`, limits, `HingeData`), `IntegerOrientation`, `AngularControlData`. Cross-mod (dev): `gen aero` reads `AeroMod.AeroApi.TryGetFlight` / `TryGetForces` / `TryGetLift` / `TryGetGroundHeight` by reflection (found once, cached; listed in `tools/check_contract.sh`). Threads: spawning and flap growth run on the client thread (spawning in the server's job failed - "Data not found"); hinge writes, the hold loop, steering and `gen clear` run in the server's job (ServerPlanetBeacon) - a hinge driven, or a grid deleted, from the client thread crashed the game (a DCS entity-storage race). `gen air N dir` writes `AeroMod.AeroEntryFx.TestWorld` through `PlanetRenderBridge.ForeignValue` (by reflection, as `aeroset`). `gen mounts` reads the public `ComplexTopologyAggregator.GetMountPointsGroupsPerDirection` (no reflection).

- **2026-10-03 `PlasmaSpike.SpawnEffect` (PlasmaSpike.cs:1035)**: the boxed `ParticleEffectUserParameters` is now filled by an `init` callback **before** `TrySpawnEffect`. The engine reads `GravityForce` (and its controller copy) only in `InitializeEffect`, so values set after the spawn never reached the GPU. No new member is reflected; `VelocityDirection` and `ParticleAspectRatio` (public fields) are now also set.
- **2026-10-03 `PlanetRenderBridge.FrameStats` (PlanetRenderBridge.cs:741)**: falls back to the engine's `StatsRecorderEngineComponent` (`VRage.Render`, public sealed) through `VRageCore.Instance.Engine.Get<T>()` (generic method found by reflection) and its **private** `_statsRecorders` list. In this build the component is not registered, so the call returns "no stats". Dev only.
- **2026-10-03 Aero `AeroEntryFx.TryGetFlow` (now with lift outs) / `Flow` / `WingTips` / `EntryStrength`** (plus internal `AeroGridComponent.FxWings` / `FxCellGeo`) are new members written for the cross-mod read in §3.10 (not reflection inside Aero).
- **2026-10-04 `FrameHost.QuietCrossingShake`**: engine reflection, client - `Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent._previousSpeedAverage` (private `float?`), set to null after the public `ClearSamples()` on every such component, each frame for 30 frames after a grid crossed a planet's chart border (`ServerFrames.ChartCrossings` changed). The game shakes the camera on the observed grid's speed change; a crossing steps the speed 0 <-> ~1 km/s in a tick (a grid on rails sits still in its berth) - a full shake, 19 deg / 13 m in one frame (DevReentry). `ClearSamples` alone leaves the previous average at 0 (a full shake again at speed); null makes the first full window compare with itself. FieldInfo looked up once; absent (the game changed): logged once, crossings may shake. Public API used: `ClearSamples()`.
- **2026-10-04 DEV `reentrytest <id>` (`Frames/DevReentry.cs`)**: reentry checked top to bottom every server tick. Cross-mod, dev only: `AeroMod.AeroApi.TryGetFlight`, `TryGetEntry` and `TryGetAtmosphere` by reflection (found once, cached; all in `tools/check_contract.sh`), called per tick only while armed and the grid is in an atmosphere. No engine reflection. The arrival (`ServerFrames.TryMaterializeGrids`) calls `DevReentry.Expect` per grid (a no-op unless armed on that grid).
- **2026-10-04 DEV `flightdump <id> <name>` (`Frames/DevHarness.cs`)**: cross-mod, dev only - finds `AeroMod.FlightFixture` in the loaded assemblies and invokes its public static `Build(Entity, string)` by reflection (looked up per call: a harness command, not a hot path). Returns the grid as Aero's thruster steering sees it (mass properties, bounds, gyro limits, the game's `MaxThrustData`, every cached thruster, the hull's `PredictHullForces` over 169 directions) for the offline flight tests (`Aerodynamics Mod/Tests/ThrustTests`, fixtures written by `Aerodynamics Mod/tools/flightdump.sh`). No engine reflection: Aero reads the engine's public data components itself. Not part of the versioned `AeroApi` contract (a test tool); absent: the command says so.
- **2026-10-03 `AeroMod.AeroApi` (Aero's public API, version 2)**: one versioned surface for other mods - flight (airspeed, Mach, density, q), forces, lift, entry heating, wing tips, entry sources, effect suppression - all signatures in .NET / engine types for reflection callers. Orbital's EntryHost now registers through it; its other Aero reads are dev-only (PlasmaSpike).
- **2026-10-03 cross-mod hook hardened**: `AeroEntryFx.External` (a single settable static) is **gone**, replaced by the entry-source contract (§3.10); Orbital registers through it. `tools/check_contract.sh` in Orbital's fast gate fails on a contract or shape mismatch.
- **2026-10-03 vapour effects (Aero `tools/particles/gen_vapor.py`, Orbital PlasmaSpike Mode 6)** use the same particle APIs as §5.5 (`TrySpawnEffect`, `SetParameters`, `FixEffectTime`); no new engine members.
- **2026-10-03 Aero `AeroApi` version 3**: the entry-source contract is now its own number (`AeroEntryFx.EntrySourceContract = 2`), separate from `AeroApi.Version` (3), so aero's API can grow without unhooking Orbital. `EntryHost` is unchanged in behaviour (it compares `RegisterEntrySource`'s return with `AeroEntryContract = 2`); `tools/check_contract.sh` now checks the API version is at least 2 and the entry-source numbers agree. Aero's own new reflection (the atmosphere point query) is in the Aero document.
- **2026-10-03 Orbital's public API and settings (`Frames/OrbitalApi.cs`, `Frames/OrbitalSettings.cs`)**: no reflection. Other mods reach `OrbitalMod.OrbitalApi` by reflection on their side (as Orbital reaches `AeroApi`); `docs/API.md`. World settings ride in the existing `ServerPlanetBeacon` builder (key `settings:`); client settings go to `%APPDATA%\SpaceEngineers2\ModSettings\` through `System.IO` (whitelisted).
- Aero-only changes (the flight field moving into Aero, its own particle and spot-light reflection) are logged in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md`.

## 5. Whitelist demands (for Keen)

Each group gives: the assembly and namespaces to reference and whitelist **(R)**, the member changes **(P / N)**, why the mods need it, and **what we can already do without Keen**.

### 5.0 The assembly-level ask
Add these to both `GameCompilationDescriptor.MetaDatas` and `GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies` (GameApp.cs:449-482). They are in priority order; the reason for each is in the groups below.
1. **VRage.Render** (Keen.VRage.Render.Contracts, .Data, .Data.VertexFormat, .Materials, .Materials.Templates, .EngineComponents, .UI). This alone would turn most of the shipped reflection into direct calls, because the targets are already public.
2. **VRage.Game.Client** (Keen.VRage.Game.Client.Effects.Components, .Render).
3. **VRage.Game.** This fixes the CS0012 on `CubeBlockComponent.Definition` (`MaxHealthComponentDefinition`), plus `ModelDefinition` and the server generator.
4. **VRage.Voxels, VRage.Voxels.Client.**
5. **VRage.UI**, and the Avalonia base types it exposes. This is needed for any Game2.Client view model to compile.
6. **VRage.Multiplayer** (`NetworkStory<T>`).
7. **System.ObjectModel** (`ObservableCollection<T>`, the base of Keen's `ObservableList<T>`).

A whole-assembly reference may be too broad for Keen. If so, the per-group member lists below are the minimum we use.

### 5.1 Render service and runtime render entities (shipped: map, proxies, rings)
- **(R) VRage.Render.**
- **(N) A supported way to get the contracts**, such as `session.Get<RenderContracts>()`. The engine itself does this (`VoxelClipmap`, per the PRB agent; not re-verified). Today we read `RenderEngineComponent.Instance.RenderContracts`.
- **Members used (all already public):**
  - `RenderContracts.CreateRootEntity`, `CreateModelEntity`, `CreateRuntimeModel`, `CreatePlanetEnvironmentEntity`, `GetMaterialSystem`, `GetMeshEffectSystem`, `GetUISystem`, `GetMainTarget` (dev), `GetRenderSystem`;
  - `RootEntity.UpdateTransform` / `Activate` / `Deactivate` / `IsValid` / `Dispose`;
  - `ModelEntity.UpdateModel` / `SetRenderFlagsState` / `SetEntityCustomData<T>(in T)` / `IsValid` / `Dispose`;
  - `RuntimeModel` (implicit → `ResourceHandle`, `Dispose`);
  - `PlanetEnvironmentEntity.SetParameters` / `Dispose`;
  - `RenderFlags`, `EntityType`;
  - custom-data structs `ScaleCustomData`, `ColoringCustomData`, `ProceduralFieldCustomData` (+ `AsteroidRing`, `AsteroidField`), `SafeZoneMeshData`;
  - `MeshEffectSystem.CreateHighlight`, `MeshEffectHandle.Dispose`.
- **Why:** proxy planets and stars, the 3D map's globes and rings, and the hiding of the game's own rings and atmospheres. Every frame, the mods pay for reflection on these public members: boxing, `object[]` allocations and `MakeGenericMethod`.
- **Without Keen:** nothing beyond caching. `RenderRuntimeDataType` is already in VRage.Core, so the `Enum.ToObject` at MapPipeline:161, MapRingMesh:215, PlanetMesh:492, PlanetRings:403 and PlasmaSpike:889 can go.

### 5.2 Runtime mesh construction
- **(R) VRage.Render**, which gives `RuntimeMeshData<TVS0,TVS1,TVS2>`, its public constructor (`RuntimeMeshData.cs:128`), `IRuntimeMeshData.SubPart`, `BackfaceCullingCone`, `VertexFormatPositionUV0Packed` and `VertexFormatNormalTangentPacked`.
  - `VertexFormatNull`, `MeshData.Section`, `ModelBone` and `Buffer<T>` are already referenceable.
  - With a typed reference, the **private-field writes** (`_vertexStream0/1/2`, `_indices`, `_subParts`, `_meshSections`, `_bones`, `_debugName`) should become unnecessary. The public `VertexStream0` returns `BufferReference<TVS0>` (`RuntimeMeshData.cs:52`), a `ref struct` (`VRage.Library/.../BufferReference.cs:16`) that typed code can use but reflection cannot box. *Not compiled to confirm.*
- **(P) If the reference is refused:** a non-generic factory, for example `RenderContracts.CreateRuntimeModel(positions, uvs, normals, tangents, indices, subParts…)` taking VRage.Core / Library types.
- **Why:** the map's sector mesh, map rings and proxy planet rings. The same applies to the dev-only planet mesh and plasma.

### 5.3 Runtime materials
- **(R) VRage.Render**, then use **`Keen.VRage.Render.Materials.RuntimeMaterialHandle<T>`**: a public constructor `(T material, RenderContracts)` that calls `AddRuntimeMaterial`, and `Dispose()` that calls `RemoveMaterial` (`VRage.Render/Keen.VRage.Render.Materials/RuntimeMaterialHandle.cs:10-27`).
  - This is the **existing public API** for runtime materials. It requires `T : MaterialDefinition, IRuntimeMaterial<T>`, and the material definitions themselves are in VRage.Core.
- **(P) Alternative:** make `MaterialSystem.AddRuntimeMaterial` / `RemoveMaterial` (`MaterialSystem.cs:67, 92`) public.
- **Without Keen (do now):** switch PlanetRings, MapRingMesh and PlasmaSpike from the **internal** method to `RuntimeMaterialHandle<T>` (still reflective, but public and documented). PlanetMesh already does this (308). *Check first* that the ring material type implements `IRuntimeMaterial<T>`.

### 5.4 Immediate-mode UI drawing (map overlay, HUD)
- **(R) VRage.Render**, for `UISystem.CreateImmediateMainViewBatch`, `ImmediateDrawBatch.DrawLine` / `DrawString` / `DrawImage` / `DrawPath` / `DrawFill`, `Font.MeasureString`, `FontDefinition` (implicit → `Font`), `DashingTypeEnum`, `GradientFillData`.
- **Why:** this is the hottest reflection in the mods, running every frame while the map is open. It uses span-taking methods that need `Delegate.CreateDelegate` tricks, and it allocates per call.
- **(P) Also needed:** the GPS marker drawing settings, private `GPSMarkerRenderSessionComponent._definition` / `_cameraComponent`. Alternatively, a public `GPSMarkerHelpers.DrawSingleMarker` overload that does not need them.

### 5.5 / 5.6 Particle effects and spot lights
- Shipped in Aero (re-entry plasma, the flight field); in Orbital only the dev PlasmaSpike uses them (§4). The member lists and asks are in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md` (§5.5, §5.6).

### 5.7 Colonization map extension (shipped; the most private-member-heavy group)
- **(R) VRage.Game.Client**, for `ModelRenderBaseComponent`, the base of `MapSectorsRenderComponent` and `MapObjectRenderComponent` (CS0012 today).
- **(P) Private members of `ColonizationMapSessionComponent`:** `SectorsRenderer`, `MarkersRenderer`, `_configuration`, `_windows`, `_sectorIndexUnderCursor`, `_hoverIndex`, `_discoveredPlanets`, `_mainStar`, and the nested `CameraData` / `CameraNeedsUpdateTag`.
- **(P) Private members of the render components:**
  - `MapSectorsRenderComponent`: `BaseMaterial`, `_proceduralSectorModel`, `ModelHandle`, `_highlights`, `UpdateSectorParameters()`, `_proceduralSectorCollider`;
  - `MapMarkersRenderComponent._playerControllerComponent`;
  - `MapObjectRenderComponent._name`, `._scale`.
- **(N) Better:** a small supported "map layer" API. It would let a mod:
  - set or zoom the map camera;
  - hide the built-in sector mesh, markers, globes and labels;
  - supply its own sector mesh or picking;
  - get the cursor position and pick result.
- **Without Keen:**
  - `map.MapEntity` (public) followed by `.Get<MapMarkersRenderComponent>()` replaces the private `MarkersRenderer`;
  - `MapMarkersRenderComponent.PlanetScale` (public) replaces the `_configuration.PlanetScale` read;
  - `MapSectorsRenderComponent.Collider` and `MapObjectRenderComponent.Name` cover the **reads** of `_proceduralSectorCollider` and `_name`.

  These are from the Frames agent's reading of the decompile; *not compiled*.

### 5.8 Game UI (shipped)
- **(R) VRage.UI** (+ Avalonia base types), so that `NotificationViewModel`, `TerminalScreenViewModel`, `TerminalSubScreen`, `MovementHUDScreenViewModel` and `TextInputDialogViewModel` can be named at all.
- **(N/P) Specific needs:**
  - A supported way to **add a terminal sub-screen (tab)**. `TerminalScreenViewModel.SubScreens` has a private setter.
  - A public accessor for the **HUD movement view model**, or a "speed override" hook. Today we reach it through private `_hud` and a BFS over internals.
- **Without Keen:**
  - `GameEntityExtensions.TryGet(this Session, Type)` (VRage.Core.Game, public, non-generic) replaces GameUi's whole `Service()` type scan.
  - `LocKey.FromString` and `new TerminalScreenId(string)` are directly callable.

### 5.9 Planet terrain, atmosphere and gravity (shipped)
- **(R) VRage.Voxels, VRage.Voxels.Client.**
- **(P/N) Terrain:**
  - Public accessors from `PlanetEnvironmentRenderComponent` to its `VoxelPlanetComponent` (`_planet`), `VoxelPlanetRenderComponent` (`_voxelPlanetRender`) and `PlanetEnvironmentRenderDefinition` (`_planetEnvRenderDefinition`).
  - A supported **"hide terrain now"** call. Today we reach the **internal** `VoxelClipmap._visible` and `DistributedRootEntityProvider._rootEntities`. Per the PRB header comment, the public `VoxelRenderComponent.SetVisible(bool)` and `FreezeClipmap` were not enough.
- **(P) Discoverables:** `DiscoverablePlanetComponent._mapVisualPrefab` needs a public getter.
- **(P) Gravity:** `GravityGeneratorComponent` needs public `FallOffPower` / `AccelerationDistance` get and set. Today we use the protected `Serialize`, the private `GravityGeneratorData` and the protected `GetData<T>` / `SetData<T>`. `AffectDistance` and `GravitationalAcceleration` are already public.
- **(P) Atmosphere:** `GeneratorBaseComponent._definition` needs a public `Definition` getter.

### 5.10 Procedural generator, asteroids, encounters (shipped)
- **(R) VRage.Game**, for `ProceduralGeneratorServerSessionComponent.DeleteEncounter(in SpaceEncounterId)`, which is already public.
- **(P) VRage.Core.Game, protected and internal members:**
  - `ProceduralGeneratorSessionComponent.Deactivate(bool)` / `CreateTriggers()`. A public "refresh sectors" would do.
  - `RegisterLocalSpawnPreventionShape` / `RemoveLocalSpawnPreventionShape` (protected internal).
- **(P) Game2.Simulation:** `DiscoveriesServerSessionComponent` should remove a provider fully, including the private `_providerEntities` and `ProviderRemoved`. The public `OnDiscoverableProviderRemoved` is only partial.
- **Without Keen:**
  - `gen.MaxSectorSpawnSamples` directly (AsteroidFrames:341).
  - `session.Get<IEntitySpawner>()` instead of protected `_spawner`; the server generator does the same.
  - The public `ProceduralGeneratorSessionComponent.Encounters` instead of protected `_encounters` (SpawnGuard:42).
  - `VolumeDefinition` "Density" through the public `IAccessible` member accessor (`GetTypeInfo().Members["Density"].Set(...)`, VRage.Library) instead of `<Density>k__BackingField`. Still string-keyed, but not System.Reflection, and it does not depend on the compiler's naming. *Not compiled.*
  - For `IsManual`, use `entity.Components` + `is ProceduralEntityComponentBase p && p.MarkedAsManual`. *Whether that type is public was not checked.*

### 5.11 Definitions at load time (shipped in both mods)
- **(P/N) `EntityCompositeDefinition.Assign(Builder)`** is **internal**, and `PrefabDefinition._entity` is **private**. We need a supported way for a definition post-processor to **add components to a prefab's composition**. Both mods do this. No public equivalent was found. `EntityCompositeDefinition.IBuildStrategy` is a possible hook, *unverified*.
- **Private setters** on `PhysicsSessionConfiguration.MaximumSpeedLinear` / `MaximumCharacterSpeedLinear` / `GravityMultiplier`, `CharacterPhysicsComponentDefinition.MaxVelocity` and `KillingFieldComponentDefinition.Radius` / `DamageRadius` / `MaxDamageRadius` / `MinDamage` / `MaxDamage`.
  - **Without Keen:** these types are `IAccessible` with generated setters, so use `GetTypeInfo().Members[name].Set(...)`. For the session caps, the Aero mod's `.def` already does the job.
  - Use `KillingFieldComponentDefinition.DeepClone()` (public) instead of `MemberwiseClone`.

### 5.12 Block and component definitions
- **(P) Public `Definition` getters** on `KillingFieldComponent` (DelfosHeat, §3.6) and `GeneratorBaseComponent` (ServerPlanetBeacon, §3.5). The thruster / gyro side (Aero) is in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md` (§5.12).

### 5.13 Admin teleport (shipped core of Orbital)
- **(R) VRage.Multiplayer**, so that `EntityAdmin.TeleportPlayer` (already public, Game2.Simulation) can be called and its `NetworkStory<EntityId?>` awaited.
- **(N) Alternative:** a fire-and-forget overload returning `Task` or `void`.

### 5.14 Markers and lists
- **(R) System.ObjectModel**, so that `ObservableList<T>` members (`GPSMarkersData.GPSMarkers`, `AntennaMarkers.ActiveMarkers`, `ContractPerPlayerData.HUDMarkers`) compile.
- **(P)** `GPSMarkerRenderSessionComponent._antennas` (private).

### 5.15 Dev-only (low priority; listed so Keen sees the whole picture)
- `StatsRecorderSessionComponent.ImmediateStats` (public, VRage.Render).
- `MainRenderTarget.TakeScreenshotAsync` (public, VRage.Render).
- `CameraSystemComponent.ToggleCameraView()` (private; a public toggle would do).
- `PowerableBlockComponent._appliedConsumption` (private; a public getter).

---

## 6. Risks

### Silently broken right now (found during this audit)
1. **`AsteroidBridge.IsManual`** (O/Render/AsteroidBridge.cs:220-228).
   - It searches for a generic `Entity.TryGet<T>()` with **0** parameters. The real method is `TryGet<T>(StringId tag = default)` (`VRage.DCS/Keen.VRage.DCS.Components/Entity.cs:133`), so nothing matches. Line 224 then returns true.
   - Result: **every generator body counts as manual**. None is deleted at 198, and all are added as anchors at 168.
   - Nothing is cached, so the lookup re-runs per entity every 2 s.
2. **`PRB.FrameStats`** (PRB:733, 745) has the same 0-parameter search for `Entity.TryGet<T>` / `Get<T>` (`Entity.cs:119, 133`). It always returns "no stats". Dev only.
3. **`GameUi.Service`** (GameUi:238-240) looks for `Session.Get<T>()`, which does not exist. It always falls through to an AppDomain-wide `GetTypes()` scan, uncached, on every `ShowCard` / `TopScreen` resolve.
4. **`MapPipeline` DrawImage** (664, 807). After one exception, `_drawImageM` is nulled and never rebound while `_drawPathM` is set. **Map icons stay off for the rest of the session.**
5. **Load-time injection** (O and A `Injection/Injections.cs:40-45`). The `TryInvokeMethod` result for the internal `Assign` is ignored. If Keen renames it, the composition stays unchanged but `_entity` is still overwritten, leaving an inconsistent prefab. A missing `_entity` throws an NRE that is not caught.

### Fragile across game updates (private, internal or compiler-generated)
- **Compiler-generated name:** `<Density>k__BackingField` (AsteroidFrames:555, AsteroidBridge:234). If it breaks, **belts get no rocks**.
- **Internal types or members:**
  - `MaterialSystem.AddRuntimeMaterial` / `RemoveMaterial`;
  - `EntityCompositeDefinition.Assign`;
  - `VoxelClipmap` and its `_visible`;
  - `DistributedRootEntityProvider._rootEntities` (taken under Keen's lock);
  - `AeroFlightTest` (Aero's own internal nested class).
- **Private fields:** about 35 across `RuntimeMeshData`, the colonization map components, `PlanetEnvironmentRenderComponent`, `VoxelRenderComponent`, `GravityGeneratorComponent`, the generator, discoveries, the killing field, thrusters, gyros, `PrefabDefinition`, the HUD and GPS. Lists are in §3.
- **Private nested types used as generic arguments:** `GravityGeneratorData`, `ColonizationMapSessionComponent.CameraData`, `CameraNeedsUpdateTag`. If one is renamed, `MakeGenericMethod` gets null.
- **Matching by parameter count or name:**
  - `CreateSpotLightEntity` arguments are matched by parameter **name** (PlasmaSpike:1319-1337).
  - `PlanetEnvironmentEntity.SetParameters` / `CreatePlanetEnvironmentEntity` are matched by count (PRB:425-449).
  - `TerminalSubScreen`'s constructor is matched by 6 parameters (RendezvousView:96-100).
- **Type identity by string name:** `GetType().Name == "ModelEntity"` (PRB:1011-1015), `== "TerminalScreenViewModel"` (RendezvousView:56), `.Contains("Server")` (AsteroidBridge:212).
- **Cross-mod:** Aero's private `_chunks` and the hard-coded `ForceTable` stride 13 (dev PlasmaSpike only, now superseded by Aero's own FlightField). The shipped hook is the versioned entry-source contract, checked from source in Orbital's fast gate.

### Boxed-struct and generic tricks
These are correct today but easy to break.
- **Mutating boxed structs** through `FieldInfo.SetValue` / `MethodInfo.Invoke`, then passing the same box on:
  - `Buffer<T>.Add` on a boxed `Buffer<T>`;
  - `RuntimeMeshData` private fields;
  - `SubPart`, `ParticleEffectUserParameters`, `ScaleCustomData`, `ColoringCustomData`, `ProceduralFieldCustomData`.`Ring`/`.Field`;
  - `CameraData`, `GravityGeneratorData`, `MarkerDrawSettings`.

  If any engine method copies the struct, or if a call site unboxes in between, the write is lost **silently**.
- **`Activator.CreateInstance` on a struct** runs an explicit parameterless constructor if there is one, which is how `ParticleEffectUserParameters` gets its defaults (EntryFxBridge, PlasmaSpike:1006). In other places it **skips** the real constructor: `RuntimeMeshData` in MapPipeline / MapRingMesh / PlanetMesh never sets `_boundingBox = BoundingBox.Invalid` (`RuntimeMeshData.cs:139`).
- **`MakeGenericType` / `MakeGenericMethod` every call:** `SetEntityCustomData<T>`, Aero `SetData<T>`, MapPipeline `DrawFill` (every frame).
- **Array dodges:** `Unsafe.As<Array, object[]>` (EngineOverrides.cs) and `Array.CreateInstance` (RendezvousView:109-112, PRB:425-439) avoid VRS1001 on `T[]`.
- **Reflection setting `init`-only accessors** (RendezvousView:108; GameUi:116-118) and a private setter through `GetSetMethod(true)` (RendezvousView:113).
- **Stale handle:** a cached boxed `PlanetEnvironmentEntity` (PRB:149) goes stale if the component recreates its entity.

### Performance, given SE2's exception cost (about 200 ms) and GC pauses
- **Per-frame reflection:**
  - `RootEntity.UpdateTransform` for every proxy, ring and globe;
  - the `ImmediateDrawBatch` draws, `GetUISystem`, `DrawPath`/`DrawFill` delegate rebuilds, and the GPS marker `object[10]` per marker;
  - `TryGetMapCamera`, `SectorsRenderer` walks, mouse position and `SectorIds` (allocates a new list per call);
  - `GameUi.SetHudSpeed` and `TopScreenNeedingInput`;
- **Per-vertex reflection on rebuilds:** MapRingMesh about 4k vertices, PlanetMesh about 25k (dev), PlasmaSpike about 11k at 8-24 Hz (dev).
- **Repeated assembly scans:** `SetForeignStatic` every 600 ticks forever without the Aero mod; `GameUi.Service` type scans.

### Silent failure, a summary
Failures mostly turn the feature off with a WarnOnce, which is acceptable. The places that fail **with no log at all** are:
- `DelfosHeat` setters;
- `SpawnGuard` and `AsteroidBridge`;
- `GameMap` `catch {}` blocks;
- `ColoringCustomData` "returns true when missing";
- `CullNone` skip;
- `ModelEntity.UpdateModel` `?.` (PlasmaSpike:903 then disposes the model still in use);
- `Injections.Assign`;
- the cross-mod hooks.

Aero-only risks (particle matching, `SetData<T>`, the array dodge, its repeat-throw and silent paths) are in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md` (§6).

---

## 7. Quick wins that need nothing from Keen
1. Fix the 0-parameter generic lookups: match `GetParameters().Length == 1` and pass `default(StringId)`. Sites: AsteroidBridge:223, PRB:733, PRB:745.
2. Runtime materials: use the public `RuntimeMaterialHandle<T>` instead of the internal `MaterialSystem` methods.
3. Use `IAccessible` member accessors instead of `<Density>k__BackingField` and the private-setter reflection.
4. Make direct calls where the reflection was never needed:
   - `gen.MaxSectorSpawnSamples`, `kd.Name`;
   - `session.Get<IEntitySpawner>()`, `Encounters`;
   - `GameEntityExtensions.TryGet(session, Type)`, `CameraComponent.ToScreenSpace`;
   - `LevelsOfDetail.Full`, `RenderRuntimeDataType`;
   - `KillingFieldComponentDefinition.DeepClone()`.
5. Check the result of `TryInvokeMethod(... "Assign" ...)` and abort the injection consistently. Wrap `_entity` in a null check (Aero has the same code).
6. ModCheck: seed `WhitelistGen` with VRage.Core.Game (the `Blob` assembly) instead of the second VRage.Library.
7. Aero's quick wins are in the Aerodynamics Mod's `docs/REFLECTION_AND_WHITELIST.md` (§7).

*The items in 2-5 marked "not compiled" in §5 should be run through ModCheck before relying on them.*
- **2026-10-03 aerobraking (`Frames/EntryHost.BetaOf`)**: each railed frame's ballistic coefficient from `AeroMod.AeroApi.PredictHullForces(Entity, Vector3, float, float, out Vector3, out Vector3, out float)` by reflection (and `RequestForceTable(Entity)` when it has no table yet: a grid on rails in space has had no air to build one) (found in `HookAero` with the entry-source methods, cached `MethodInfo`; listed in `tools/check_contract.sh`). Called at unit density and 1000 m/s from 14 directions in the grid's frame (axes + diagonals; orientation-free: the mean drag over q gives Cd x A). Measured at most every 30 s per frame (`BetaRefresh`); absent aero or no table yet: `Reentry.DefaultBeta`. Exceptions swallowed per grid (not a hot path: once per refresh).
- **2026-10-03 the map in worlds without colonization sectors (`Frames/MapView.SectorlessScene` / `Toggle`)**: the game builds its colonization map only where `SectorsSessionComponent.Sectors.Count > 0` (`TerminalScreenViewModel` / `MapTabViewModel.ColonizationMapAvailable`), so a scenario world's Map tab is the GPS list alone. Reflection: `TerminalControllerSessionComponent._openVM` (private field) and its `IsMapTabSelected` (public property; the view model's type is in VRage.UI, which scripts cannot reference) - while the Map tab is open in a sectorless world the mod shows the map scene itself with the public `ColonizationMapSessionComponent.ToggleMap()`. That call switches the camera (closes an entity) and must not run inside a scene job (it asserted and crashed the game - and the engine's `SynchronizationContext.Post` runs inline, in the job), so it is posted to Avalonia's `Dispatcher.UIThread` (`Avalonia.Base`, found by assembly name; its `Post(Action, ...)` overload by reflection, defaults for the rest). Failures go to `MapView.Status`; nothing thrown per frame. Before showing: the public `MapEntity` (spawns the diorama entity - only the colonization tab ever did) and `ToggleSectorSelection(false)`; the private `_discoveriesPlayerData` gets an empty `DiscoveriesPlayerData` (public constructor) when null. If `ShowMap` still throws (it switches the camera first), the private `IsVisible` setter is invoked by reflection and `HideMap()` restores; three failures and it stops trying. The game's sector renderer is given one tiny placeholder part (`CleanMap`: `MapPipeline.ShowParts`, as for empty sectors) so its placeholder ring does not show. The GPS page's `MapDataUnavailable` text is blanked through `TextManager._textSource`'s string tables (as `AeroMod.MachHud` patches the speedometer). Known left: the terminal's `PART_ItemDropArea` rectangle (black, `DynamicUIBackgroundOpacity`, visible when `!IsMapTabSelected || !ColonizationMapAvailable`) still dims the map in such a world.

