#!/usr/bin/env bash
# lowresui.sh - our HUD against the game's at whatever resolution the game runs (5 minutes or less; RELAUNCHES the game -
# the Steam launch, its last world, never saved - and closes it after):
#  1. seated as loaded (in air): the align button, its hints, the flight boxes, the SPD icon - alignbtn's HudGeom line
#  2. the align hold on (Level), a mode cycled (Ctrl+; by the dev key), the warp bar idle fade
#  3. the Blue Fighter on a 300 km orbit: the orbit plot, warp 10x (the warp bar back), the map
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 12

echo "== 1 flight"
echo "      $(reply "alignbtn" | sed 's/.*=>  //' | cut -c1-400)"
echo "SHOT flight $(shot lr_flight)"
echo "== 2 align"
send "hold Level"; hold 3
echo "      $(reply "alignbtn" | sed 's/.*=>  //' | cut -c1-200)"
echo "SHOT align-level $(shot lr_level)"
send "hold Off"; hold 7
echo "SHOT warp-faded $(shot lr_faded)"

echo "== 3 orbit"
bf=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=Blue_Fighter" | head -1 | cut -d= -f1)
send "unseat"; hold 3; send "tpgrid $bf 15"; hold 3
echo "      seat: $(reply "seatid $bf" | sed 's/.*=>  //' | cut -c1-120)"; hold 4
echo "      orbit: $(reply "gridorbit $bf Verdure 300 300" | sed 's/.*=>  //' | cut -c1-160)"; hold 10
echo "SHOT orbit-plot $(shot lr_orbit)"
send "warp 10"; hold 3
echo "SHOT orbit-warp $(shot lr_warp)"
send "warp 1"; send "map on"; hold 4
echo "SHOT map $(shot lr_map)"
send "map off" "shotui off"; hold 2
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|NoMatchingLocalPairing"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== lowresui: $(( $(date +%s)-T0 ))s (game closed)"; summary
