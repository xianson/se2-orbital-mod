#!/usr/bin/env bash
# tipab.sh - which ships get wing tip trails (only a thin, sharp tip), and an A/B of the trail clumping (5 minutes or less;
# RELAUNCHES the game: not while someone plays). Screenshots: $TEMP/tipab_<case>_<n>.png
#  A1 the Blue Fighter (a brick): no tips
#  A2..: the Jetliner and the UNSC Pelican: their tips (or why not)
#  B  the first ship with tips: screenshots with the effect time rewritten every 0.1 s (old) and only on change (new)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-400; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "unseat" >/dev/null; sleep 1; q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
shots() { for i in 1 2; do s=$(q "shot" | grep -o "[A-Z]:[^ ]*png" | tr "\\\\" "/"); for k in 1 2 3 4 5 6; do sleep 0.5; cp "$s" "$TEMP/tipab_$1_$i.png" 2>/dev/null && break; done; done; }
fly() { q "griddamp $1 off" >/dev/null; q "gridlaunch $1 3 Verdure 150" >/dev/null; sleep 4; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); id() { echo "$G" | tr ' ' '\n' | grep "=$1\$" | head -1 | cut -d= -f1; }
q "encon off" >/dev/null

bf=$(id Blue_Fighter); sit $bf; fly $bf; tw=$(q "aeroget AeroMod.FlightField.TipWhy")
ok "A1 the Blue Fighter (a brick): no tips" $(echo "$tw" | grep -q "^.*=0 tips" && echo 1 || echo 0) "$tw"
winged=""
for name in Jetliner UNSC_Pelican Red_Ship; do
  s=$(id $name); [ -z "$s" ] && continue
  if sit $s; then fly $s; tw=$(q "aeroget AeroMod.FlightField.TipWhy"); echo "      $name ($s): $tw" | cut -c1-400
    if [ -z "$winged" ] && echo "$tw" | grep -q "=2 tips"; then winged=$s; wname=$name; fi
  else echo "      $name ($s): no seat"; fi
done
if [ -n "$winged" ]; then
  q "aeroset FlightField.TrailDebug true" >/dev/null
  sit $winged; q "aeroset FlightField.FixTimeEvery 1" >/dev/null; fly $winged; sleep 2; shots old
  q "aeroset FlightField.FixTimeEvery 0" >/dev/null; fly $winged; sleep 2; shots new
  q "aeroset FlightField.TrailDebug false" >/dev/null
  echo "      B screenshots of $wname: old cadence tipab_old_*, on change tipab_new_*"
else echo "      B: no ship here has a thin, sharp tip - no A/B"; fi
q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== tipab: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
