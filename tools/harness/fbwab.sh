#!/usr/bin/env bash
# fbwab.sh - the F-7's pitch at speed, the aero-aware attitude law off (the game's gyros) then on (5 minutes or less; RELAUNCHES the game - the Steam launch,
# never saved - and closes it after): seated, put at ALT km and SPEED m/s (default 8 km, 550 m/s: ~Mach 1.7), hands off; the
# aero mod's [AERO-PITCH] line (10 a second) for 8 s each; the pitch rate's spread summarised by fbw_sum.py
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 3
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 8
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-F-7_Interceptor}[^ ]*" | head -1 | cut -d= -f1)
echo "      grid: $g"
send "pitchlog on"
for mode in off on; do
  send "aeroaware $mode"; mark
  echo "      aware $mode: $(reply "gridlaunch $g ${ALT:-8} Verdure ${SPEED:-550}" | sed 's/.*=>  //' | cut -c1-60)"; hold ${PRE:-2}
  [ -n "$GUST" ] && send "gridspin $g pitch $GUST"
  hold 8
  since | grep -a "\[AERO-PITCH\]" | sed 's/.*\[AERO-PITCH\] //' > "$OUT/fbw_$mode.txt"
  echo "      $(wc -l < "$OUT/fbw_$mode.txt") lines"
  python "$(cygpath -w "$SP/fbw_sum.py")" "$(cygpath -w "$OUT/fbw_$mode.txt")" "aware $mode"
done
send "pitchlog off" "aeroaware on"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== fbwab: $(( $(date +%s)-T0 ))s (game closed)"; summary
