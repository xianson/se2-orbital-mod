#!/usr/bin/env bash
# fxramp.sh - do the effects flicker, pop or hang? (5 minutes or less; RELAUNCHES the game unless NOLAUNCH=1: not while
# someone plays). The Hercules is put in Verdure's air, levelled, held still, you seated; the air is the aero mod's test
# airflow (TestSpeed along TestAxis, 5 deg AoA). Every drawn frame is judged by the aero mod's recorder (fxcheck):
#  R1 a smooth ramp up 200 -> 900 m/s (20 s) and down again (20 s): every group comes on once and goes off once, no pops,
#     no flicker, strengths stepping less than 0.05 a frame
#  R3 the ramp again with the pool warm: no emitter spawned, every one reused
#  R2 dithered +-3 % about the vapour band's edge (Mach 1.15) for 8 s, then about the plasma onset (Mach 2.0) for 8 s:
#     no flicker (no group on and off again within 2 s)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
AX=${AX:-0,-0.0872,-0.9962}
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-330; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }
setv() { "$O" "aeroset AeroEntryFx.TestSpeed $1" >/dev/null 2>&1 & }   # (fire and forget: a harness call takes 1-2 s)

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1); echo "Hercules $h"
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "aeroset AeroEntryFx.TestAxis $AX" >/dev/null; setv 200; sleep 4

# R1 - the ramp
q "fxcheck reset" >/dev/null
for i in $(seq 0 80); do setv $(( 200 + 700 * i / 80 )); sleep 0.25; done
sleep 3
for i in $(seq 0 80); do setv $(( 900 - 700 * i / 80 )); sleep 0.25; done
sleep 3
r1=$(q "fxcheck reset")
ok "R1 ramp up and down: no pops, no flicker" $(awk -v p="$(num "$r1" pops)" -v f="$(num "$r1" flicker)" 'BEGIN{print (p == 0 && f == 0) ? 1 : 0}') "$r1"
ok "R1 ... strengths step under 0.05 a frame" $(awk -v a="$(num "$r1" max-step)" -v b="$(num "$r1" edge-step)" -v c="$(num "$r1" tip-step)" 'BEGIN{print (a < 0.05 && b < 0.05 && c < 0.05) ? 1 : 0}')

# R3 - the same ramp again, the pool warm: nothing new spawned (every emitter reused)
for i in $(seq 0 80); do setv $(( 200 + 700 * i / 80 )); sleep 0.25; done
sleep 3
for i in $(seq 0 80); do setv $(( 900 - 700 * i / 80 )); sleep 0.25; done
sleep 3
r3=$(q "fxcheck reset")
ok "R3 the ramp again: no emitter spawned, all reused" $(awk -v s="$(num "$r3" spawned)" -v r="$(num "$r3" reused)" 'BEGIN{print (s == 0 && r > 0) ? 1 : 0}') "$r3"

# R2 - dithered about the thresholds (Mach 1.15: 391 m/s; Mach 2.0: 680 m/s)
for c in 391 680; do
  setv $c; sleep 3; q "fxcheck reset" >/dev/null
  for i in $(seq 0 32); do setv $(awk -v c=$c -v i=$i 'BEGIN{srand(i*7+c); printf "%.0f", c*(1 + 0.03*(2*rand()-1))}'); sleep 0.25; done
  r2=$(q "fxcheck reset")
  ok "R2 dithered +-3 % about $c m/s: no flicker" $(awk -v f="$(num "$r2" flicker)" -v p="$(num "$r2" pops)" 'BEGIN{print (f == 0 && p == 0) ? 1 : 0}') "$r2"
done

setv 0; q "gridhold $h 0" >/dev/null; q "encon on" >/dev/null
echo "== fxramp: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
