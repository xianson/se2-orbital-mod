#!/usr/bin/env bash
# fx.sh - the effects judged frame by frame, not by screenshot (5 minutes or less; RELAUNCHES the game: not while someone plays).
# Seated in the Blue Fighter (the camera on it), thrown through Verdure's air; the aero mod counts every drawn frame of every
# flight field (AeroApi.GetFxConsistency via 'fxcheck'):
#  F1 hypersonic (1000 m/s, 12 km): the plasma lit, no pops (a one-frame step over 0.05), no field lost
#  F2 the same while it turns (pitch and roll): no rebuild while lit beyond one per 10 deg of turn, no pops
#  F3 transonic (330 m/s, 4 km): vapour and tip trails come up and hold - no group flicker
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-260; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }
# judge <name> <report> <maxPops> <maxRebuilds> <maxLost> <maxToggles> <minPeak>
judge() {
  local r="$2" pops=$(num "$2" pops) reb=$(num "$2" rebuilds-while-lit) lost=$(num "$2" lost) tog=$(num "$2" toggles) pk=$(num "$2" peak) fr=$(num "$2" "fx frames")
  ok "$1" $(awk -v p="${pops:-99}" -v b="${reb:-99}" -v l="${lost:-99}" -v t="${tog:-99}" -v k="${pk:-0}" -v f="${fr:-0}" \
    -v P=$3 -v B=$4 -v L=$5 -v T=$6 -v K=$7 'BEGIN{print (f > 60 && p <= P && b <= B && l <= L && t <= T && k >= K) ? 1 : 0}') "$r"
}

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); bf=$(echo "$G" | tr ' ' '\n' | grep "=Blue_Fighter\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null; sit $bf; q "griddamp $bf off" >/dev/null

# F1 - hypersonic, straight
q "gridlaunch $bf 12 Verdure 1000" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null; sleep 12
judge "F1 hypersonic: plasma lit, no pops, no field lost" "$(q "fxcheck reset")" 0 0 3 2 0.3

# F2 - hypersonic, turning (0.3 rad/s pitch then roll: ~70 deg in 8 s)
q "gridlaunch $bf 12 Verdure 1000" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null
q "gridspin $bf 0.15 0 0.15" >/dev/null; sleep 8; q "gridspin $bf 0 0 0" >/dev/null; sleep 2
judge "F2 hypersonic, turning: no pops, few rebuilds" "$(q "fxcheck reset")" 0 7 3 4 0.3

# F3 - transonic: vapour and tips
q "gridlaunch $bf 4 Verdure 330" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null; sleep 10
judge "F3 transonic: vapour and trails hold (no flicker)" "$(q "fxcheck reset")" 0 0 3 2 0

q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== fx: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
