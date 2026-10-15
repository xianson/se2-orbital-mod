#!/usr/bin/env bash
# loacdiag.sh - the predicted loss-of-control warning against what the ship really does (3 minutes or less; RELAUNCHES the
# game - the Steam launch, its last world, never saved - and closes it after): seated in the Tu-134, level at cruise
# (flytest), the warning's diagnostic line a few times - its torque predicted at the attitude now against the torque
# applied this tick, and the reach by source.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
get() { reply "aeroget $1" | sed 's/.*=>  //' | cut -c1-${2:-400}; }
[ -z "$NOLAUNCH" ] && launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-Tupolev_Tu-134_Big}" | head -1 | cut -d= -f1)
send "unseat" "gridhold $g 1"; hold 2; send "tpgrid $g 15"; hold 1
echo "      seat: $(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80)"; hold 1
send "gridhold $g 0" "key flytest"; hold 2; send "gridspin $g 0 0 0"; hold 5
for i in 1 2 3 4; do echo "      $(get LoacWarning.Last)"; hold 2; done
echo "      stall: $(get StallWarning.Last 200)"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== loacdiag: $(( $(date +%s)-T0 ))s"; summary
