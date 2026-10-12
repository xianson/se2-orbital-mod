#!/usr/bin/env bash
# hintshot3.sh - the build hints on the F-7 Interceptor from four sides (spectator camera), the overlay on (5 minutes or
# less; RELAUNCHES the game - the Steam launch, never saved - and closes it after)
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 3
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 8
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-F-7_Interceptor}[^ ]*" | head -1 | cut -d= -f1)
echo "      grid: $g"
send "hints $g" "hints overlay 1"
for b in ${BRGS:-0 90 180 270}; do
  send "cam grid $g ${KM:-0.035} $b ${ELEV:-12}"; hold 5
  echo "      bearing $b: $(reply "hints status" | sed 's/.*=>  //' | cut -c1-200)"
  echo "SHOT b$b $(shot b$b)"
done
send "hints overlay 0" "hints off" "cam off" "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== hintshot3: $(( $(date +%s)-T0 ))s (game closed)"; summary
