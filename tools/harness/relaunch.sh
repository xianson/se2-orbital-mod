#!/usr/bin/env bash
# usage: relaunch.sh  -- kill the test game (no save), relaunch via OrbitalTestSE2, wait for the server,
# then run the test preamble (aero spike off, stop the save's leftover grid velocities).
D=/c/Users/slob/AppData/Local/Temp/OrbitalMod; SP="$(dirname "$0")"
taskkill //F //IM SpaceEngineers2.exe >/dev/null 2>&1; sleep 5
rm -f "$D/cmd.txt" "$D/status.txt"
mkdir -p "$D"; touch "$D/dev.flag"   # the mod's dev harness only runs where this flag exists
powershell -NoProfile -Command "Start-ScheduledTask -TaskName 'OrbitalTestSE2'" >/dev/null
for i in $(seq 1 90); do sleep 5; grep -q "server=[0-9]" "$D/status.txt" 2>/dev/null && break; done
echo "game up after $((i*5)) s"
"$SP/orb.sh" "aerospike off" "gridsstop" >/dev/null; sleep 3; "$SP/orb.sh" "gridsstop" >/dev/null; sleep 2
