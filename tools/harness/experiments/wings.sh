#!/usr/bin/env bash
# wings.sh - find a ship with real wing tips and photograph its trails (5 minutes or less; RELAUNCHES the game: not while
# someone plays). Each candidate: seated (any of its grids with a seat), flown at 150 m/s 3 km up, its own tip report
# (FlightField.NearTipWhy - the ship the camera is on). The first with 2 tips: the overlay on, shots at 150 and 250 m/s
# into $TEMP/wings_<speed>_<n>.png.
H="$(cd "$(dirname "$0")/.." && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shots() { for i in 1 2; do s=$(q "shot" | grep -o "[A-Z]:[^ ]*png" | tr "\\\\" "/"); for k in 1 2 3 4 5 6; do sleep 0.5; cp "$s" "$TEMP/wings_$1_$i.png" 2>/dev/null && break; done; sleep 0.5; done; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/../launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
fi
G=$(q "findgrids"); q "encon off" >/dev/null
winged=""
for name in UNSC_Pelican SZ1_-_Hercules H2_Prototype Jetliner Red_Ship; do
  [ $(( $(date +%s) - T0 )) -gt 200 ] && break
  for s in $(echo "$G" | tr ' ' '\n' | grep "=$name\$" | cut -d= -f1 | head -6); do
    q "unseat" >/dev/null; sleep 0.5; q "tpgrid $s 15" >/dev/null; sleep 1
    q "seatid $s" | grep -q "seating in" || continue
    until_ 10 "seated $s" || continue
    q "griddamp $s off" >/dev/null; q "gridlaunch $s 3 Verdure 150" >/dev/null; sleep 5
    tw=$(q "aeroget AeroMod.FlightField.NearTipWhy"); echo "$name ($s): $tw" | cut -c1-360
    echo "$tw" | grep -q "=2 tips" && winged=$s && wname=$name
    break
  done
  [ -n "$winged" ] && break
done
if [ -n "$winged" ]; then
  q "aeroset FlightField.TrailDebug true" >/dev/null
  q "gridlaunch $winged 3 Verdure 150" >/dev/null; sleep 4; shots 150
  q "gridlaunch $winged 3 Verdure 250" >/dev/null; sleep 4; shots 250
  echo "fx: $(q 'fxcheck' | cut -c1-300)"
  q "aeroset FlightField.TrailDebug false" >/dev/null
  echo "WINGED $wname $winged"
else echo "no ship with real wing tips found"; fi
q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== wings in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
ls "$TEMP"/wings_*.png 2>/dev/null
