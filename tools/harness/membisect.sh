#!/usr/bin/env bash
# membisect.sh - the managed-memory runaway on joining an orbit frame, bisected (5 minutes or less; RELAUNCHES the game -
# the Steam launch, its last world, never saved - and closes it after): today's new features off (the align button, the
# far-effect cull, the aero-aware law, wind), the Blue Fighter put on a 300 km orbit (the player joins its frame), memory
# sampled; then each feature on in turn, memory sampled after each.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
mem() { echo "      $(date +%H:%M:%S) $1: $(timeout 25 "$SP/orb.sh" mem | grep -a " => " | tail -1 | sed 's/.*=>  //')"; }
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded"; send "encon off"; hold 8
send "alignbtn off" "fxcull off" "aeroaware off" "aeroset AeroWind.Enabled false"
mem "all off, in air"
bf=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=Blue_Fighter" | head -1 | cut -d= -f1)
send "unseat"; hold 2; send "tpgrid $bf 15"; hold 2; send "seatid $bf"; hold 3
echo "      orbit: $(timeout 25 "$SP/orb.sh" "gridorbit $bf Verdure 300 300" | grep -a " => " | tail -1 | sed 's/.*=>  //' | cut -c1-120)"
for i in 1 2 3; do hold 5; mem "all off, in the frame $i"; done
send "alignbtn on"; for i in 1 2; do hold 5; mem "+ align button $i"; done
send "fxcull on"; for i in 1 2; do hold 5; mem "+ fx cull $i"; done
send "aeroaware on" "aeroset AeroWind.Enabled true"; for i in 1 2; do hold 5; mem "+ aero-aware, wind $i"; done
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== membisect: $(( $(date +%s)-T0 ))s (game closed)"
