#!/usr/bin/env bash
# bakeprof.sh - where the baked field's bake time goes: the Hercules held 2 km up in Verdure's air at entry speed for
# HOLD s, then the recorder's bake profile (stage totals in ms, on-demand bakes, worst frame). RELAUNCHES unless
# NOLAUNCH=1. Releases its overrides.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s); HOLD=${HOLD:-20}
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridhold $h 0.5" >/dev/null
q "fxcheck reset" >/dev/null; q "aeroset AeroEntryFx.TestSpeed 900" >/dev/null; sleep "$HOLD"
q "fxcheck reset" | grep -o "bake ms.*baked-ms avg [0-9.]* max [0-9.]*"
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "gridhold $h 0" >/dev/null
echo "== bakeprof in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
