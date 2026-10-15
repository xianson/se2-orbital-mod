#!/usr/bin/env bash
# structtest.sh - the aero mod's structure, hull heat and flight warnings in game (5 minutes or less; RELAUNCHES the game -
# the Steam launch, its last world, never saved - and closes it after; NOLAUNCH=1: the running game). Seated in the
# Tu-134 at once (the world's Tu-134 glides from load and crashes within minutes), traced ([AERO-STRUCT-T]):
#  1. the build (block masses against the physics mass), the game's THRUST OVERLOAD warning removed, level cruise (flytest);
#  2. a hard pull (pitch rate forced): STALL / CONTROL LIMIT / LOSS OF CONTROL, the gauges - screenshots;
#  3. fast and low (gridhold 900 m/s): the hull heating;
#  4. damage on, a hard pull: what breaks.
# Overrides released after (trace, damage, holds, spin).
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 2 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
get() { reply "aeroget $1" | sed 's/.*=>  //' | cut -c1-${2:-200}; }
trace() { since | grep -a "\[AERO-STRUCT" | sed 's/.*\[AERO-STRUCT/[AERO-STRUCT/' | cut -c1-330 | tail -${1:-4} | sed 's/^/      /'; }
warn() { echo "      stall: $(get StallWarning.Last 170)"; echo "      loac: $(get LoacWarning.Last 170)"; }
[ -z "$NOLAUNCH" ] && launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off"
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-Tupolev_Tu-134_Big}" | head -1 | cut -d= -f1)
echo "      grid: $g"
# seated at once (it glides at ~200 m/s: held nearly still first, or it is hundreds of metres off by the seat)
send "unseat" "gridhold $g 1"; hold 2; send "tpgrid $g 15"; hold 1
echo "      seat: $(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80)"; hold 1
send "gridhold $g 0" "aeroset StructureLoads.Trace true" "key flytest"; hold 2; send "gridspin $g 0 0 0"; mark; hold 7
echo "== 1 build, level cruise"
grep -a "\[AERO-STRUCT\] built" "$(LOGF)" | sed 's/.*built //' | tail -3 | sed 's/^/      /'
echo "      game stall warning (THRUST OVERLOAD): $(get GameStallWarning.Status)"
trace 2; warn
echo "SHOT level $(shot struct_level)"
echo "== 2 hard pull"
mark; send "gridspin $g pitch ${PULL:-0.6}"; hold 2
trace 3; warn
echo "SHOT pull $(shot struct_pull)"
send "gridspin $g pitch ${PULL:-0.6}"; hold 1
echo "SHOT pull2 $(shot struct_pull2)"
echo "== 3 fast and low (900 m/s)"
send "key flytest"; hold 2; send "gridspin $g 0 0 0"; mark; send "gridhold $g 900"; hold ${HOT:-15}
trace 2
echo "SHOT hot $(shot struct_hot)"
send "gridhold $g 0"
echo "== 4 damage on, hard pull"
send "key flytest"; hold 2; send "gridspin $g 0 0 0"; mark
send "aeroset StructureLoads.Damage true"; send "gridspin $g pitch ${PULL4:-0.8}"; hold 3
since | grep -a "\[AERO-STRUCT\].*broke\|AERO-STRUCT\] damage" | sed 's/.*\[AERO-STRUCT\] //' | head -8 | sed 's/^/      /'
trace 2
echo "SHOT damage $(shot struct_damage)"
echo "      breaks $(get StructureLoads.Breaks), wears $(get StructureLoads.Wears)"
send "aeroset StructureLoads.Damage false" "aeroset StructureLoads.Trace false" "gridspin $g 0 0 0" "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== structtest: $(( $(date +%s)-T0 ))s"; summary
