#!/usr/bin/env bash
# game_owner.sh - the game slot for the harness launchers (sourced by relaunch.sh / launch.sh). The shared slot broker
# (Aerodynamics Mod/tools/shared/slot.py, SLOTS_DESIGN.md) decides who may kill / start the game: set GAME_OWNER to your
# session name (anim, se2aerospace, britannica, planeteditor). Without the game slot a launcher tries ONE non-blocking
# take; when that fails it refuses (exit 3) - wait for the slot with `slot.py take game --who $GAME_OWNER` in the
# background (it exits the moment the slot is yours). The user's game and the user's input always win.
SLOT_PY="${AERO_TOOLS:-$(dirname "${BASH_SOURCE[0]}")/../../../Aerodynamics Mod/tools/shared}/slot.py"
OWNER_FILE="${LOCALAPPDATA:-/c/Users/slob/AppData/Local}/OrbitalModBuild/game.owner"
owner_check() {
  python "$SLOT_PY" check game --who "${GAME_OWNER:-unknown}" >/dev/null 2>&1 && return 0
  python "$SLOT_PY" take game --who "${GAME_OWNER:-unknown}" --world "$(cat "$(dirname "${BASH_SOURCE[0]}")/world.txt" 2>/dev/null)" --timeout 0.01 >/dev/null 2>&1
  python "$SLOT_PY" check game --who "${GAME_OWNER:-unknown}"
}
# owner_write <world>: after the start - the game's pid is this holder's (not the user's), and the owner file for the
# scripts that read it (BritannicaCutTest's gate)
owner_write() {
  mkdir -p "$(dirname "$OWNER_FILE")"; printf '%s|%s|%s\n' "${GAME_OWNER:-unknown}" "$1" "$(date +%s)" > "$OWNER_FILE"
  local pid=""
  for i in $(seq 1 60); do
    pid=$(powershell -NoProfile -Command "\$all=@(Get-CimInstance Win32_Process -Filter \"name='SpaceEngineers2.exe'\"); \$ids=\$all|%{\$_.ProcessId}; (\$all|?{\$ids -notcontains \$_.ParentProcessId}|Sort-Object CreationDate|Select-Object -Last 1).ProcessId" 2>/dev/null | tr -d '\r')
    [ -n "$pid" ] && break; sleep 1
  done
  [ -n "$pid" ] && python "$SLOT_PY" launched game --who "${GAME_OWNER:-unknown}" --pid "$pid" >/dev/null
}
owner_loaded() { python "$SLOT_PY" loaded game --who "${GAME_OWNER:-unknown}" >/dev/null; }
