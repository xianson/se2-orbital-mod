#!/usr/bin/env bash
# combo.sh - one launch, 5 minutes or less (RELAUNCHES the game: not while someone plays):
#  C1 gravity is 2x (server and client physics)
#  C2 the cockpit hold actions loaded without errors (no exception naming them; the G-menu itself needs a person)
#  C3 trails: seated in the Blue Fighter at 150 m/s, 3 km up: the tips placed (2, at the span's ends), effects consistent
#  C4 the orbit display hidden on a low arc (apoapsis inside the air)
#  C5 warp x1000 in the fighter's own orbit frame for ~8 s: no limit from a frame you are not in, and no burst of new
#     grids (ships parked at stations are held, not flung into them)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-300; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); bf=$(echo "$G" | tr ' ' '\n' | grep "=Blue_Fighter\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null

# C1
gl=$(timeout 20 "$O" "status" | grep -a "gravityMultiplier" | head -1)
ok "C1 gravity 2x" $(echo "$gl" | grep -q "client=2" && echo 1 || echo 0) "$(echo "$gl" | sed 's/.*=>//' | cut -c1-120)"
# C2
ex=$(grep -a "OrbitalHoldDetailModel\|HoldActions\|BlockDetail" "$(LOG)" | grep -ac "Exception\|rror")
ok "C2 the hold actions loaded with no errors naming them" $([ "${ex:-1}" = 0 ] && echo 1 || echo 0) "($ex error lines)"

# C3
sit $bf; q "griddamp $bf off" >/dev/null
q "gridlaunch $bf 3 Verdure 150" >/dev/null; sleep 3; q "fxcheck reset" >/dev/null; sleep 8
tw=$(q "aeroget AeroMod.FlightField.TipWhy"); fx=$(q "fxcheck reset")
ok "C3 trails: two tips at the span's ends" $(echo "$tw" | grep -q "2 tips at the span" && echo 1 || echo 0) "$tw"
ok "C3 ... the effects held (no pops, no field lost)" $(awk -v p="$(num "$fx" pops)" -v l="$(num "$fx" lost)" -v f="$(num "$fx" "fx frames")" 'BEGIN{print (f > 60 && p == 0 && l <= 3) ? 1 : 0}') "$fx"
# C4 (same flight: a 3 km hop - the apoapsis is inside the air)
ec=$(q "exitcheck $bf")
ok "C4 the orbit display hidden on a low arc" $(echo "$ec" | grep -q "graph hidden True" && echo 1 || echo 0) "$(echo "$ec" | grep -o 'graph hidden [A-Za-z]*')"

# C5
q "gridorbit $bf Verdure 300 300 0 0" >/dev/null; until_ 20 "q 'frameof $bf' | grep -q '#'"
mine=$(q "frameof $bf" | grep -o "#[0-9]*" | head -1)
M=$(date +%H:%M:%S); n0=$(grep -ac "joins frame\|loose grid" "$(LOG)")
q "warp 1000" >/dev/null; sleep 8; q "warp 1" >/dev/null; sleep 1
lim=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -a "warp x[0-9]* -> x[0-9]*: a member" | grep -v "in frame $mine\$" | grep -o "in frame #[0-9]*" | sort | uniq -c | tr '\n' ' ')
ok "C5 warp x1000 in your frame ($mine): no limit from frames you are not in" $([ -z "$lim" ] && echo 1 || echo 0) "${lim:-none}"
nb=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -ac "loose grid .* joins\|new grid")
ok "C5 ... no burst of new grids (nothing broken off)" $([ "${nb:-99}" -lt 3 ] && echo 1 || echo 0) "($nb new or loose grids during the warp)"

q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== combo: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -8
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
