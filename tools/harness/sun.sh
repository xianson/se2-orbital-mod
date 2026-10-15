#!/usr/bin/env bash
# sun.sh - the sun across frames (5 minutes or less; RELAUNCHES the game: not while someone plays). 'sunrel' gives the light
# in the ship's own axes, the game clock (spin) and the universe clock:
#  S1 seated in the Blue Fighter, sent out of Verdure's frame on a climbing arc (gridlaunch 15 km at 800 m/s: the real stow, turned out of the
#     planet's chart): the light in the ship's axes does not jump (under 2 deg)
#  S2 in the orbit frame, warp x10 for ~10 s: the planet's spin and the light follow the game clock, not warp - the spin
#     angle moves by the game seconds that passed (not 10x), the light by under 2 deg
#  S3 back down into Verdure's frame (gridlaunch 8 km): the game's own sun again
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-300; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
vec() { echo "$1" | grep -o "light local ([^)]*)" | grep -o "[-0-9.]*,[-0-9.]*,[-0-9.]*"; }
fld() { echo "$1" | grep -o "$2 [-0-9.a-z#]*" | head -1 | awk '{print $2}'; }
ang() { awk -v a="$1" -v b="$2" 'BEGIN{split(a,x,",");split(b,y,",");d=x[1]*y[1]+x[2]*y[2]+x[3]*y[3];na=sqrt(x[1]^2+x[2]^2+x[3]^2);nb=sqrt(y[1]^2+y[2]^2+y[3]^2);c=d/(na*nb);if(c>1)c=1;if(c<-1)c=-1;printf "%.3f", atan2(sqrt(1-c*c),c)*180/3.14159265}'; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); bf=$(echo "$G" | tr ' ' '\n' | grep "=Blue_Fighter\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null; sit $bf; q "griddamp $bf off" >/dev/null; sleep 2   # (off: dampeners braked the escape - it never left)

# S1 - out of the planet's frame
r0=$(q "sunrel $bf"); echo "      before: $r0" | cut -c1-260
q "gridlaunch $bf 15 Verdure 800" >/dev/null   # (just past the frame's border - the air's top, 9.5 km - on an arc that climbs out: the real stow; 250 km was outside the cell, where nothing stows)
until_ 30 "q 'sunrel $bf' | grep -q 'frame #'"
r1=$(q "sunrel $bf"); echo "      after:  $r1" | cut -c1-260
a1=$(ang "$(vec "$r0")" "$(vec "$r1")")
ok "S1 out of Verdure's frame: the light in the ship's axes kept (moved ${a1:-?} deg)" $(awk -v a="${a1:-99}" 'BEGIN{print (a < 2) ? 1 : 0}') "$(echo "$r1" | grep -o '| frame.*' | cut -c1-200)"

# S2 - warp in the orbit frame
w0=$(q "sunrel $bf"); q "warp 10" >/dev/null; sleep 10; q "warp 1" >/dev/null; w1=$(q "sunrel $bf")
ds=$(awk -v a="$(fld "$w0" spin)" -v b="$(fld "$w1" spin)" 'BEGIN{printf "%.1f", b-a}')
dn=$(awk -v a="$(fld "$w0" now)" -v b="$(fld "$w1" now)" 'BEGIN{printf "%.1f", b-a}')
a2=$(ang "$(vec "$w0")" "$(vec "$w1")")
ok "S2 warp x10: the spin clock ran ${ds} s while the universe ran ${dn} s" $(awk -v s="$ds" -v n="$dn" 'BEGIN{print (s > 0 && s < 20 && n > 3 * s) ? 1 : 0}')
ok "S2 ... and the light in the ship's axes kept (moved ${a2:-?} deg)" $(awk -v a="${a2:-99}" 'BEGIN{print (a < 2) ? 1 : 0}') "$(echo "$w1" | grep -o '| spin.*' | cut -c1-220)"

# S3 - back into the planet's frame
q "gridlaunch $bf 8 Verdure" >/dev/null
until_ 30 "q 'sunrel $bf' | grep -q 'frame planet'"
r3=$(q "sunrel $bf")
ok "S3 back in Verdure's frame: the game's own sun" $(echo "$r3" | grep -q "the game's sun" && echo 1 || echo 0) "$(echo "$r3" | grep -o '| frame.*\|game.s sun.*' | head -1 | cut -c1-160)"

q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== sun: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
