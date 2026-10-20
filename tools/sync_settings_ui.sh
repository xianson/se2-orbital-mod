#!/usr/bin/env bash
# sync_settings_ui.sh - the in-game settings menu (the "Mods" tab in the Options screen, settings DESIGN.md W4) is no longer
# written here: since 2026-10-19 it is written ONCE in the Aerodynamics Mod's tools/shared/modsrc/files/ (the shared dev
# tools' home: ModCheck, Publish) and copied into BOTH mods by its sync.sh. This is a wrapper of that, for this checkout.
# The two mods compile separately and share no code; each compiles its own copy (only the namespace line differs). Each
# mod's adapter (Scripts/Simulation/Ui/SettingsTab.Mod.cs) and test project file are its own and never copied.
#   tools/sync_settings_ui.sh "../Aerodynamics Mod"          write the copies: this mod's and that aero checkout's
#   tools/sync_settings_ui.sh --check "../Aerodynamics Mod"  compare only (exit 1 on a difference; tools/check_contract.sh)
# The shared source is the one in AERO_TOOLS (a tools/shared) when set, else the aero checkout's own tools/shared.
# Edit the files THERE (tools/shared/modsrc/files/), then sync, then run both mods' fast gates.
cd "$(dirname "$0")/.."
check=""; [ "$1" = "--check" ] && { check="--check"; shift; }
AERO_ROOT="${1:?usage: tools/sync_settings_ui.sh [--check] <path to the Aerodynamics Mod checkout>}"
SYNC="${AERO_TOOLS:-$AERO_ROOT/tools/shared}/modsrc/sync.sh"
[ -f "$SYNC" ] || { echo "no shared source: $SYNC (an Aerodynamics Mod checkout with tools/shared/modsrc, or AERO_TOOLS=)"; exit 2; }
exec bash "$SYNC" $check --aero "$AERO_ROOT" --orbital "$PWD"
