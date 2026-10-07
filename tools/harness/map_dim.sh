#!/usr/bin/env bash
# map_dim.sh - the map in a world without colonization sectors: not dimmed (the terminal's backdrop hidden), the
# Rendezvous tab keeps the GPS panel. RELAUNCHES the game; never saves. Shots: docs/shots-work/mapdim_*.jpg.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; E="$H/eshot.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
q "shotui on" >/dev/null
q "map on" >/dev/null; sleep 5
timeout 90 "$E" "mapdim_1map" 3 "shotui on" >/dev/null 2>&1
q "rvtab on" >/dev/null; sleep 3
timeout 90 "$E" "mapdim_2rvtab" 3 "shotui on" >/dev/null 2>&1
q "rvtab off" >/dev/null; sleep 3
timeout 90 "$E" "mapdim_3back" 3 "shotui on" >/dev/null 2>&1
q "map off" >/dev/null; sleep 3
q "map on" >/dev/null; sleep 5
timeout 90 "$E" "mapdim_4reopen" 3 "shotui on" >/dev/null 2>&1
q "map off" >/dev/null; sleep 4
echo "hudcheck (as spawned): $(q "hudcheck")"
q "unseat" >/dev/null; sleep 5
echo "hudcheck (on foot): $(q "hudcheck")"
echo "--- log:"; grep -a "backdrop\|sectorless map scene\|map scene toggle" "$(LOG)" | cut -c1-220
