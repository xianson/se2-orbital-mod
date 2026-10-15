#!/usr/bin/env bash
# globesun.sh - one sun across frames, the terrain warm-up, and the hi-res globe against the real planet (5 minutes or less;
# RELAUNCHES the game: not while someone plays).
#  S1 seated in the Blue Fighter, out of Verdure's frame (gridlaunch 15 km at 800 m/s): the light in the ship's axes kept
#  S2 back down into Verdure's frame (gridlaunch 8 km): kept again - now the same inertial sun turned into the chart, not the
#     game's own (they disagreed where the game's sun axis or its midnight wrap differed from the planet's spin)
#  W  the real planet shown after its terrain warmed ("terrain warmed in" in the log)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-300; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }
vec() { echo "$1" | grep -o "light local ([^)]*)" | grep -o "[-0-9.]*,[-0-9.]*,[-0-9.]*"; }
ang() { awk -v a="$1" -v b="$2" 'BEGIN{split(a,x,",");split(b,y,",");d=x[1]*y[1]+x[2]*y[2]+x[3]*y[3];na=sqrt(x[1]^2+x[2]^2+x[3]^2);nb=sqrt(y[1]^2+y[2]^2+y[3]^2);if(na*nb==0){print 99;exit}c=d/(na*nb);if(c>1)c=1;if(c<-1)c=-1;printf "%.3f", atan2(sqrt(1-c*c),c)*180/3.14159265}'; }
shot() { timeout 40 "$O" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids"); bf=$(echo "$G" | tr ' ' '\n' | grep "=Blue_Fighter\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null; sit $bf; q "griddamp $bf off" >/dev/null; sleep 2

# S1 - out of the planet's frame
r0=$(q "sunrel $bf"); echo "      before: $r0" | cut -c1-260
q "gridlaunch $bf 15 Verdure 800" >/dev/null
until_ 30 "q 'sunrel $bf' | grep -q 'frame #'"
r1=$(q "sunrel $bf"); echo "      after:  $r1" | cut -c1-260
a1=$(ang "$(vec "$r0")" "$(vec "$r1")")
ok "S1 out of Verdure's frame: the light in the ship's axes kept (moved ${a1:-?} deg)" $(awk -v a="${a1:-99}" 'BEGIN{print (a < 2) ? 1 : 0}')

# S2 - back into the planet's frame: the same sun, through the chart
sleep 3; r2=$(q "sunrel $bf")
q "gridlaunch $bf 8 Verdure" >/dev/null   # (G and W need it seated in Verdure's frame)
until_ 30 "q 'sunrel $bf' | grep -q \"chart\|frame planet\""
r3=$(q "sunrel $bf"); echo "      back:   $r3" | cut -c1-260
# (the dev launch places the ship anew - its own axes say nothing here: the drawn sun against the game's own instead, just
#  after arrival - the same sun through the chart and back; the spin moved it a fraction of a degree since)
azel() { echo "$r3" | grep -o "game sun az [-0-9.]* el [-0-9.]* -> az [-0-9.]* el [-0-9.]*" | awk '{print $4, $6, $9, $11}'; }
a2=$(azel | awk '{pi=3.14159265; a=$1*pi/180; e=$2*pi/180; b=$3*pi/180; f=$4*pi/180;
  x1=-cos(e)*sin(a); y1=sin(e); z1=-cos(e)*cos(a); x2=-cos(f)*sin(b); y2=sin(f); z2=-cos(f)*cos(b);
  c=x1*x2+y1*y2+z1*z2; if(c>1)c=1; if(c<-1)c=-1; printf "%.3f", atan2(sqrt(1-c*c),c)*180/pi}')
ok "S2 back in Verdure's frame: the drawn sun is the game's own (${a2:-?} deg apart)" $(awk -v a="${a2:-99}" 'BEGIN{print (a < 2) ? 1 : 0}')
ok "S2 ... the planet frame draws the chart's sun" $(echo "$r3" | grep -q "chart" && echo 1 || echo 0) "$(echo "$r3" | grep -o "Verdure's chart.*" | cut -c1-160)"

# W - the warm-up
sleep 4; w=$(grep -a "terrain warmed in" "$(LOG)" | tail -1 | sed 's/.*\[ORBIT\] //')
ok "W the real planet shown once its terrain caught up (not at the cap)" $([ -n "$w" ] && ! echo "$w" | grep -q capped && echo 1 || echo 0) "$w"

# restore
q "mode Frame" >/dev/null; q "cam off" >/dev/null; q "encon on" >/dev/null
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== globesun: $P passed, $F failed in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
