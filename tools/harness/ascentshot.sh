#!/usr/bin/env bash
# ascentshot.sh - the orbit plot on climbs: the F-7 (seated) put at altitudes and speeds an ascent passes through, the plot
# shot and the HUD's own check each time (5 minutes or less; RELAUNCHES the game - the Steam launch, never saved - closes it)
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 3
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 8
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=F-7_Interceptor[^ ]*" | head -1 | cut -d= -f1)
echo "      grid: $g"
if [ -n "$RAMP" ]; then
  echo "      launch 40 km at 1200 m/s: $(reply "gridlaunch $g 40 Verdure 1200" | sed 's/.*=>  //' | cut -c1-80)"; hold 6
  for v in 1400 1600 1800 2000 2200; do
    send "gridhold $g $v"; hold 3
    echo "SHOT v$v $(shot v$v)"
  done
  send "gridhold $g 0"
fi
for c in ${CASES:-"30:600 60:1500 80:2000"}; do
  set -- ${c/:/ }
  echo "      launch $1 km at $2 m/s: $(reply "gridlaunch $g $1 Verdure $2" | sed 's/.*=>  //' | cut -c1-120)"; hold 6
  echo "      hud: $(reply "hudcheck" | sed 's/.*=>  //' | cut -c1-260)"
  echo "SHOT c$1 $(shot c$1)"
  hold 3; echo "SHOT c$1b $(shot c$1b)"
done
send "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== ascentshot: $(( $(date +%s)-T0 ))s (game closed)"; summary
