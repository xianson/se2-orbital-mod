#!/usr/bin/env bash
# trails.sh - the wing tip trails, judged on what we own (5 minutes or less; RELAUNCHES the game: not while someone plays).
# The aero mod's trail tracker (FlightField.TrackTips) records each tip emitter's world path every frame - with no motion
# inheritance a tip particle stays where it was born, so that path is the trail - and the overlay draws it (yellow), the
# emitter (red) and the hull's true tip (green). Screenshots go to $TEMP/trails_*.png.
#  T1 the FireFly (a multi-grid VTOL), 150 m/s, 3 km up, straight: 2 tips, each within 1 m of the hull's true tip, none re-placed
#  T2 the same, turning (pitch then yaw ~6 s): still 2, still on the tips, none re-placed
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-320; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }
shots() { for i in 1 2 3; do s=$(q "shot" | grep -o "[A-Z]:[^ ]*png" | tr "\\\\" "/"); for k in 1 2 3 4 5 6; do sleep 0.5; cp "$s" "$TEMP/trails_$1_$i.png" 2>/dev/null && break; done; done; }
judge() {   # judge <name> <report>
  local r="$2"
  ok "$1" $(awk -v t="$(num "$r" tips)" -v g="$(num "$r" max-gap)" -v rp="$(num "$r" replaced)" -v f="$(num "$r" "trail frames")" \
    'BEGIN{print (f > 60 && t == 2 && g < 1.0 && rp == 0) ? 1 : 0}') "$r"
}

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); ship=$(echo "$G" | tr ' ' '\n' | grep -i "=MC-82_FireFly\$\|FireFly" | head -1 | cut -d= -f1)
[ -z "$ship" ] && ship=$(echo "$G" | tr ' ' '\n' | grep "=Blue_Fighter\$" | head -1 | cut -d= -f1)
echo "ship $ship: $(echo "$G" | tr ' ' '\n' | grep "^$ship=" | cut -d= -f2)"
q "encon off" >/dev/null; q "unseat" >/dev/null; sit $ship; q "griddamp $ship off" >/dev/null
q "aeroset FlightField.TrailDebug true" >/dev/null

# T1 straight
q "gridlaunch $ship 3 Verdure 150" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null; sleep 4; shots straight; sleep 1
r1=$(q "fxcheck reset"); tw1=$(q "aeroget AeroMod.FlightField.TipWhy")
judge "T1 straight: 2 tips, on the hull's tips, none re-placed" "$(echo "$r1" | sed 's/.*| //')"
echo "      $tw1" | cut -c1-200

# T2 turning
q "gridlaunch $ship 3 Verdure 150" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null
q "gridspin $ship 0.2 0 0" >/dev/null; sleep 3; shots turning; q "gridspin $ship 0 0.2 0" >/dev/null; sleep 3; q "gridspin $ship 0 0 0" >/dev/null
r2=$(q "fxcheck reset"); echo "      full: $r2" | cut -c1-300
judge "T2 turning: still 2, on the tips, none re-placed" "$(echo "$r2" | sed 's/.*| //')"

q "aeroset FlightField.TrailDebug false" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== trails: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
ls "$TEMP"/trails_*.png 2>/dev/null | head -6
