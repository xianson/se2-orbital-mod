#!/usr/bin/env bash
# fxlook.sh - the Hercules' vapour and plasma by speed, photographed (5 minutes or less; RELAUNCHES the game unless
# NOLAUNCH=1). Shots: $TEMP/fl_<case>_<n>.png
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shots() { for i in 1 2 3; do s=$(q "shot" | grep -o "[A-Z]:[^ ]*png" | tr "\\\\" "/"); for k in 1 2 3 4 5 6; do sleep 0.5; cp "$s" "$TEMP/fl_$1_$i.png" 2>/dev/null && break; done; sleep 0.7; done; }
if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); q "encon off" >/dev/null; q "unseat" >/dev/null; sleep 1
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
h=$(q "aeroget OrbitalMod.FrameHost.SeatGridServerId" | grep -o "[0-9]\{10\}"); echo "seated in $h"
q "griddamp $h off" >/dev/null
# the effects by speed: vapour (transonic) and plasma (entry), nose first, photographed
# PRESETS: space-separated runs, each a comma list of Type.Member=value set (aeroset) before the flights - e.g.
# "FlightField.SparkAspect=0.1 FlightField.SparkAspect=0.3"; one empty run when unset
for preset in ${PRESETS:-none}; do
  tag=""; if [ "$preset" != none ]; then for c in ${preset//,/ }; do q "aeroset ${c/=/ }" >/dev/null; tag="${tag}_${c##*=}"; done; fi
  for v in ${SPEEDS:-300 900}; do
    q "gridlaunch $h ${ALT:-0.8} Verdure 150" >/dev/null; sleep 2; q "gridhold $h $v" >/dev/null; sleep 5
    echo "$v m/s $preset: $(q 'aeroget AeroMod.AeroEntryFx.FlightStatus' | cut -c1-160)"; shots "v$v$tag"
  done
done
q "gridhold $h 0" >/dev/null
q "aeroset FlightField.TrailDebug false" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
