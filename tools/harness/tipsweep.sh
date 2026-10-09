#!/usr/bin/env bash
# tipsweep.sh - the Hercules' wing tip trails: a sweep of particle size and spread, photographed (5 minutes or less; RELAUNCHES the game unless
# NOLAUNCH=1). Shots: $TEMP/tw_<case>_<n>.png
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shots() { for i in 1 2 3; do s=$(q "shot" | grep -o "[A-Z]:[^ ]*png" | tr "\\\\" "/"); for k in 1 2 3 4 5 6; do sleep 0.5; cp "$s" "$TEMP/tw_$1_$i.png" 2>/dev/null && break; done; sleep 0.7; done; }
if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); q "encon off" >/dev/null; q "unseat" >/dev/null; sleep 1
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
h=$(q "aeroget OrbitalMod.FrameHost.SeatGridServerId" | grep -o "[0-9]\{10\}"); echo "seated in $h"
q "griddamp $h off" >/dev/null
# the sweep: TipScale (particle size x) and TipSpread (backward speed x), each flown nose first at 200 m/s and photographed
for cfg in ${SWEEP:-3_6 15_0.01 15_6 40_6}; do cfg=${cfg//_/ }
  set -- $cfg; q "aeroset FlightField.TipScale $1" >/dev/null; q "aeroset FlightField.TipSpread $2" >/dev/null
  q "aeroset FlightField.TipsOn false" >/dev/null; sleep 0.3; q "aeroset FlightField.TipsOn true" >/dev/null
  q "gridlaunch $h 0.8 Verdure 150" >/dev/null; sleep 2; q "gridhold $h 200" >/dev/null; sleep 5
  echo "scale $1 spread $2: $(q 'aeroget AeroMod.FlightField.NearTipWhy' | cut -c1-120)"; shots "s$1_v$2"
  if [ -n "$TURN" ]; then q "gridspin $h 0 $TURN 0" >/dev/null; sleep 2; shots "s$1_v$2_turn"; q "gridspin $h 0 0 0" >/dev/null; fi
done
q "gridhold $h 0" >/dev/null
q "aeroset FlightField.TrailDebug false" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
