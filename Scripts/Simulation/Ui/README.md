# Ui/ - the in-game settings menu (client)

The Options screen's **Mods** tab (pause menu -> Settings -> Mods), generated from the settings registry
(`Host/OrbitalSettings*.cs`). Written once here; `tools/sync_settings_ui.sh "../Aerodynamics Mod"` copies the shared files
into the Aerodynamics Mod (only the namespace line differs) and `tools/check_contract.sh` checks the copies.

| File | What | Shared |
|---|---|---|
| `SettingsTab.cs` | The tab: notice the Options screen, add the shared tab / this mod's section (`Tag` contract `ModSettings/v1`), the rows, the pump, the failure toast, the harness's `Dev`. | yes |
| `SettingsModel.cs` | Engine-free: groups and tiers, each row's control, slider steps (linear, or logarithmic over 200 steps), value text, lock reasons, when an edit applies. **The menu's defaults** (the user's answers, 2026-10-15) are at its top. | yes |
| `AvaloniaReflect.cs` | Avalonia and the game's screens by reflection (types, properties, dispatcher, the open screen, Click). | yes |
| `SettingsTab.Mod.cs` | This mod's adapter: its id and title, files, log tag, the registry's API. | no |

**Entry points / threading.** `SettingsTab.Tick(session)` from `FrameHost.Tick` (any game thread, every frame: a timestamp
compare; every 250 ms it finds `SharedUIComponent` and posts `UiTick`). Everything else runs on Avalonia's UI thread: find
the open `OptionsScreen`, attach once per screen instance, and while the tab is selected (~4 Hz) read the controls, `Set`
through the registry, refresh on a `Version` change. Offline tests: `Tests/SettingsUiTests` (`-- dump` prints the menu).
In game (dev): DevHarness `settingsui open | select | report | close | flip <setting> | expand all|none|section|advanced |
scroll top|bottom|<px>|<setting>` (the last two: for screenshots of every part, with `shot <name>`).

**The look** uses the game's own styling, never our colours: the `Flat` expander, the `TextBlock` classes of its Options
views (`OptionTitle`, `SubTitle` `Bright`, `OptionSubTitle`, `Small`), its resources (`SpaceWidth`, `ValueWidth`, the text
brushes) looked up from the open screen, one fixed label column per section (its widest label in the game's font:
`SettingsModel.LabelWidth`; Advanced rows less the expander's inset - every control at one x); the Slider template's raw
number hidden.
The keys and their fallbacks are at `SettingsModel`'s top.
