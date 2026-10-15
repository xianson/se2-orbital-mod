#!/usr/bin/env bash
# vapourab.sh - the transonic vapour at a few settings, side by side: the Hercules held 2 km up in Verdure's air at
# Mach ~0.95; per setting (VapourGain,VapourSpeed) the vapour switched off long enough for its emitters to park,
# then on again (re-spawned with the new parameters), and an engine shot. RELAUNCHES unless NOLAUNCH=1. 5 min or less.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
SETS=${SETS:-"1,10 2.5,10 2.5,20 4,25"}; SPEED=${SPEED:-320}   # (SETS: VapourGain,VapourSpeed at the tight size)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shot() { timeout 30 "$O" --eshot 0 "fxcheck" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "aeroset AeroEntryFx.TestAxis 0,-0.0872,-0.9962" >/dev/null; q "aeroset AeroEntryFx.TestSpeed $SPEED" >/dev/null
q "cam grid $h 0.045 25 12" >/dev/null; sleep 8
for st in $SETS; do
  g=${st%,*}; v=${st#*,}
  q "aeroset FlightField.VapourOn false" >/dev/null; sleep 3
  q "aeroset FlightField.VapourGain $g" >/dev/null; q "aeroset FlightField.VapourSpeed $v" >/dev/null
  q "aeroset FlightField.VapourOn true" >/dev/null; sleep 4
  echo "gain $g speed $v: $(shot)"
done
q "aeroset FlightField.VapourGain 4" >/dev/null; q "aeroset FlightField.VapourSpeed 20" >/dev/null
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "aeroset AeroEntryFx.TestAxis -z" >/dev/null; q "cam off" >/dev/null
q "gridhold $h 0" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== vapourab in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
