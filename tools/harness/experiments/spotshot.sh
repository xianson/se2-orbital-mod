#!/usr/bin/env bash
# spotshot.sh - is the entry spot light there: the Hercules held 2 km up in Verdure's air with puppet airflow at entry
# speed, the field's status (spot on/off, where, how bright, aimed where), and engine shots with the spot normal, x10 and
# off. The grid is HELD for the whole run (released last - it then falls, as any unpiloted ship). RELAUNCHES unless
# NOLAUNCH=1. 5 minutes or less.
H="$(cd "$(dirname "$0")/.." && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
SPEED=${SPEED:-900}; DIST=${DIST:-0.05}   # (km)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shot() { timeout 30 "$O" --eshot 0 "fxcheck" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/../launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
echo "seated: $(q "exitcheck $h" | grep -o 'seated [A-Za-z]*')"
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "aeroset AeroEntryFx.TestAxis 0,-0.5,-0.866" >/dev/null; q "aeroset AeroEntryFx.TestSpeed $SPEED" >/dev/null
q "cam grid $h $DIST 30 15" >/dev/null; sleep 6
echo "status: $(q 'aeroget AeroEntryFx.FlightStatus' | sed 's/.*FlightStatus=//' | cut -c1-400)"
echo "spot: $(shot)"
q "aeroset FlightField.SpotOn false" >/dev/null; sleep 2; echo "spot off: $(shot)"; q "aeroset FlightField.SpotOn true" >/dev/null; sleep 2
# the flow turned 40 deg (from the side): the light re-aimed in place - not made again
q "fxcheck reset" >/dev/null; q "aeroset AeroEntryFx.TestAxis 0.6,-0.4,-0.69" >/dev/null; sleep 3
echo "turned: $(q 'aeroget AeroEntryFx.FlightStatus' | sed 's/.*FlightStatus=//' | grep -o 'spot [^|]*')"; echo "spot turned: $(shot)"
echo "spots made while turning: $(q 'fxcheck' | grep -o 'spots-made [0-9]*')"
q "aeroset FlightField.SpotOn true" >/dev/null; q "aeroset FlightField.Spot 3000000" >/dev/null
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "aeroset AeroEntryFx.TestAxis -z" >/dev/null; q "cam off" >/dev/null
q "gridhold $h 0" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== spotshot in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
