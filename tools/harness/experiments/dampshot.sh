#!/usr/bin/env bash
# dampshot.sh - the F-7's pitch wobble, measured (5 minutes or less; RELAUNCHES the game - the Steam launch, never saved -
# and closes it after):
#  1. its blocks exported for AeroBench (aeroexport on; aero cycled off/on: every grid rebuilds and writes its .boxes)
#  2. hands off at speeds over Mach 1 (gridlaunch: altitude, speed), its attitude and spin sampled 4 times a second for 10 s
#     into %TEMP%/OrbitalMod/damp_<km>_<speed>.txt (q and world spin: the pitch rate offline, damp_sum.py)
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 8
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-F-7_Interceptor}[^ ]*" | head -1 | cut -d= -f1)
echo "      grid: $g"
# 1. the export
send "aeroexport on"; hold 1; send "aero off"; hold 2; send "aero on"; hold 12; send "aeroexport off"
ls -t /c/Users/slob/AppData/Local/Temp/AeroMod/*.boxes | head -3 | sed 's/^/      exported: /'
# 2. hands off, over Mach 1
for c in ${CASES:-"8:550 12:650"}; do
  set -- ${c/:/ }
  f="$OUT/damp_$1_$2.txt"; : > "$f"
  echo "      launch $1 km at $2 m/s: $(reply "gridlaunch $g $1 Verdure $2" | sed 's/.*=>  //' | cut -c1-80)"; hold 3
  for i in $(seq 1 40); do echo "$(date +%s.%N) $(reply "gridatt $g" | sed 's/.*=>  //')" >> "$f"; sleep 0.2; done
  echo "      $(wc -l < "$f") samples -> $f"
done
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== dampshot: $(( $(date +%s)-T0 ))s (game closed)"; summary
