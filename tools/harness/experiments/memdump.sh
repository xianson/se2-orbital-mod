#!/usr/bin/env bash
# memdump.sh - what fills the managed heap after joining an orbit frame (RELAUNCHES the game - the Steam launch, its last
# world, never saved - and closes it after): the Blue Fighter on a 300 km orbit, 25 s, then the threads' stacks
# (dotnet-stack) and a heap dump (dotnet-gcdump) into %TEMP%/OrbitalMod.
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
mem() { echo "      $(date +%H:%M:%S) $1: $(timeout 25 "$SP/orb.sh" mem | grep -a " => " | tail -1 | sed 's/.*=>  //')"; }
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded"; send "encon off"; hold 6
pid=$(powershell -NoProfile -Command "(Get-Process SpaceEngineers2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1).Id" | tr -d '\r')
echo "      pid $pid"
bf=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=Blue_Fighter" | head -1 | cut -d= -f1)
send "unseat"; hold 2; send "tpgrid $bf 15"; hold 2; send "seatid $bf"; hold 3
send "gridorbit $bf Verdure 300 300"; hold 20
mem "in the frame"
timeout 60 dotnet-stack report -p $pid > "$OUT/stack.txt" 2>&1; echo "      stacks: $(wc -l < "$OUT/stack.txt") lines"
timeout 240 dotnet-gcdump collect -p $pid -o "$OUT/frame.gcdump" > "$OUT/gcdump.txt" 2>&1; echo "      gcdump: $(tail -1 "$OUT/gcdump.txt")"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== memdump: $(( $(date +%s)-T0 ))s (game closed)"
