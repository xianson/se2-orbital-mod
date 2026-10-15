#!/usr/bin/env bash
# structprof.sh - what the aero mod's structure and hull heat cost (5 minutes or less; RELAUNCHES the game - the Steam
# launch, its last world, never saved - and closes it after; NOLAUNCH=1: the running game). Every few seconds, over
# the last second: the simulation thread's time in it (us per second, every grid; and its worst single tick), the
# background's (ms per second), evaluations per second and grids evaluated. Seated in the Tu-134 at once (the world's
# Tu-134 glides from load and crashes within minutes): flying (flytest), pulling; then unseated, the world idle.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
get() { reply "aeroget StructureLoads.$1" | sed 's/.*=//'; }
cost() { echo "      $1: sim $(get SimUsPerSec) us/s (worst tick $(get SimMaxUs) us), background $(get BgMsPerSec) ms/s, $(get EvalsPerSec) evals/s, $(get GridsEvaluated) grids evaluated, $(get GridsTicked) ticked, last eval $(get LastEvalMs) ms, builds $(get Builds), wears $(get Wears), breaks $(get Breaks)"; }
[ -z "$NOLAUNCH" ] && launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off" "aeroset StructureLoads.Enabled true"
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-Tupolev_Tu-134_Big}" | head -1 | cut -d= -f1)
echo "      grid $g; $("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=" | wc -l) grids"
# seated at once, put level at cruise
# (it glides at ~200 m/s: held nearly still first, or it is hundreds of metres off by the time the seat is tried)
send "unseat" "gridhold $g 1"; hold 2; send "tpgrid $g 15"; hold 1
echo "      seat: $(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80)"; hold 1
send "gridhold $g 0" "key flytest"; hold 2; send "gridspin $g 0 0 0"; hold 6
for i in 1 2 3; do cost "seated, flying"; hold 3; done
send "gridspin $g pitch 0.3"; hold 2
for i in 1 2; do cost "pulling"; hold 2; done
send "gridspin $g 0 0 0" "unseat"; hold 6
for i in 1 2 3; do cost "unseated, world idle"; hold 3; done
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== structprof: $(( $(date +%s)-T0 ))s"; summary
