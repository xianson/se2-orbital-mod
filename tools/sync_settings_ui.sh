#!/usr/bin/env bash
# sync_settings_ui.sh <aero checkout> - copy the in-game settings menu (the "Mods" tab in the Options screen, settings
# DESIGN.md W4) from this mod into the Aerodynamics Mod. It is written ONCE, here; the two mods compile separately and share
# no code, so aero carries copies. Only the namespace line differs (OrbitalMod -> AeroMod: each mod's copy is its own types).
# Each mod's adapter (Scripts/Simulation/Ui/SettingsTab.Mod.cs: its id, title, files, registry calls) and test project file
# are its own and never copied.
#   tools/sync_settings_ui.sh "../Aerodynamics Mod"          write the copies
#   tools/sync_settings_ui.sh --check "../Aerodynamics Mod"  compare only (tools/check_contract.sh; exit 1 on a difference)
# Edit the shared files here, then sync, then run both mods' fast gates.
cd "$(dirname "$0")/.."
check=0; [ "$1" = "--check" ] && { check=1; shift; }
AERO_ROOT="${1:?usage: tools/sync_settings_ui.sh [--check] <path to the Aerodynamics Mod checkout>}"
[ -d "$AERO_ROOT/Scripts/Simulation" ] || { echo "not an Aerodynamics Mod checkout: $AERO_ROOT"; exit 2; }
SHARED="Scripts/Simulation/Ui/AvaloniaReflect.cs Scripts/Simulation/Ui/SettingsModel.cs Scripts/Simulation/Ui/SettingsTab.cs Tests/SettingsUiTests/Program.cs"
ADAPTER="Scripts/Simulation/Ui/SettingsTab.Mod.cs"
# the aero copy of a shared file (the one rewrite)
aero() { sed -e 's/^namespace OrbitalMod;/namespace AeroMod;/' -e 's/^using OrbitalMod;/using AeroMod;/' "$1"; }
rc=0
for f in $SHARED; do
  [ -f "$f" ] || { echo "missing here: $f"; rc=1; continue; }
  if [ $check -eq 1 ]; then
    [ -f "$AERO_ROOT/$f" ] || { echo "missing in aero: $f (run tools/sync_settings_ui.sh)"; rc=1; continue; }
    # (line ends aside: git checks these files out CRLF, a fresh copy may be LF)
    if ! diff -q <(aero "$f" | tr -d '\r') <(tr -d '\r' < "$AERO_ROOT/$f") >/dev/null; then
      echo "differs from this mod's: $f (edit it here, then run tools/sync_settings_ui.sh)"
      diff <(aero "$f" | tr -d '\r') <(tr -d '\r' < "$AERO_ROOT/$f") | head -6 | sed 's/^/        /'
      rc=1
    fi
  else
    mkdir -p "$AERO_ROOT/$(dirname "$f")"
    aero "$f" > "$AERO_ROOT/$f"
    echo "copied $f"
  fi
done
# the adapters: one each, and two sections may never share a Tag
for side in . "$AERO_ROOT"; do [ -f "$side/$ADAPTER" ] || { echo "no adapter: $side/$ADAPTER"; rc=1; }; done
id() { grep -o 'ModId = "[A-Za-z0-9]*"' "$1/$ADAPTER" 2>/dev/null | head -1; }
[ -n "$(id .)" ] && [ "$(id .)" != "$(id "$AERO_ROOT")" ] || { echo "the two adapters' ModId must differ: $(id .) / $(id "$AERO_ROOT")"; rc=1; }
exit $rc
