#!/usr/bin/env bash
# aeroab.sh [label] - the aero mod's scripted flight (AeroFlightTest: cruise, climb, glide) on the world's most-winged grid,
# its glide L/D summarised (5 minutes or less; RELAUNCHES the game - the Steam launch, the aero mod's world, never saved -
# and closes it after). Run before and after a flight-model
# change and compare.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s); TAG="${1:-run}"; OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod/aeroab_$TAG.txt
# (the Steam launch, as globesun.sh: the world it opens has the aero mod - the test world's relaunch has not; never saved)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off" "unseat"; hold 3
[ -n "$PRESEND" ] && send "$PRESEND"
mark; send "aeroflight on ${SUBJECT:-f7a663bd}"   # (the jetliner - SUBJECT= for another)
if [ -n "$AEROOFF_AT" ]; then sleep "$AEROOFF_AT"; send "aero off"; echo "      aero off at +${AEROOFF_AT}s"; fi
waitfor "\[FLIGHT\] profile complete\|\[FLIGHT\] ABORT\|\[FLIGHT\] no subject" 200
[ -n "$AEROOFF_AT" ] && send "aero on"
since | grep -a "\[FLIGHT\] subject bound\|\[FLIGHT\] SETUP" | sed 's/.*\[FLIGHT\]/  /' | cut -c1-200
since | grep -a "\[FLIGHT\] t=" | sed 's/.*\[FLIGHT\] //' | awk '{print}' > "$OUT"
echo "  $TAG: $(wc -l < "$OUT") telemetry lines"
python "$(cygpath -w "$SP/aeroab_sum.py")" "$(cygpath -w "$OUT")" "$TAG"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
send "aeroflight off"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== aeroab $TAG: $(( $(date +%s)-T0 ))s (game closed)"; summary
