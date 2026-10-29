#!/usr/bin/env bash
# usage: relaunch.sh  -- kill the test game (no save), relaunch via OrbitalTestSE2, wait for the server,
# then run the test preamble (aero spike off, stop the save's leftover grid velocities).
D=/c/Users/slob/AppData/Local/Temp/OrbitalMod; SP="$(dirname "$0")"
. "$SP/game_owner.sh"; owner_check || exit 3
taskkill //F //IM SpaceEngineers2.exe >/dev/null 2>&1
for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2 || break; sleep 0.5; done   # (gone, not a fixed wait)
# content a build compiled while the game held its files (BUILD_ENV\pending): copied in now, with the game closed.
# The scheduled task launches the game directly, not through launch-se2.bat, so nothing else applies it (2026-10-26:
# a water test ran the old content)
python "${AERO_TOOLS:-$SP/../../../Aerodynamics Mod/tools/shared}/build_mod_content.py" --apply-pending | grep -v "^$"
rm -f "$D/cmd.txt" "$D/status.txt"
mkdir -p "$D"; touch "$D/dev.flag"   # the mod's dev harness only runs where this flag exists
powershell -NoProfile -Command "Start-ScheduledTask -TaskName 'OrbitalTestSE2'" >/dev/null
owner_write "$(cat "$SP/world.txt" 2>/dev/null)"
for i in $(seq 1 450); do sleep 1; grep -q "server=[0-9]" "$D/status.txt" 2>/dev/null && break; done
echo "game up after ${i} s"; owner_loaded
"$SP/orb.sh" "gridsstop" >/dev/null; sleep 3; "$SP/orb.sh" "gridsstop" >/dev/null; sleep 2
