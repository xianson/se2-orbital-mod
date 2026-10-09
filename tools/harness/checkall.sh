#!/usr/bin/env bash
# checkall.sh - the round of 2026-10-09 in one launch, 5 minutes or less (RELAUNCHES the game unless NOLAUNCH=1: not while
# someone plays):
#  A1 seated in the Hercules on its orbit, 'hold prograde': it turns onto prograde (under 5 deg off within 25 s) and is
#     still holding (no release without steering)
#  T1 the flames during that turn: thrusters lit, their records found (hits / lookups over 90 %)
#  E1 the effects through a ramp 200 -> 900 -> 200 m/s (puppeted, in Verdure's air): no pops, no flicker, emitters reused
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
AX=${AX:-0,-0.0872,-0.9962}
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-330; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }
val() { q "aeroget $1" | sed 's/.*=//'; }
setv() { "$O" "aeroset AeroEntryFx.TestSpeed $1" >/dev/null 2>&1 & }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1); echo "Hercules $h"
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "griddamp $h on" >/dev/null; sleep 1

# A1 + T1 - the prograde hold, and the flames while it turns
l0=$(val ThrustTorque.FlameLookups); h0=$(val ThrustTorque.FlameHits)
q "hold prograde" >/dev/null
until_ 50 "q 'hold' | grep -q 'holding Prograde: [0-4]\.[0-9] deg'"
hs=$(q "hold"); lit=$(val ThrustTorque.FlameLit); l1=$(val ThrustTorque.FlameLookups); h1=$(val ThrustTorque.FlameHits)
ok "A1 hold prograde: on prograde (under 5 deg) and still holding" $(echo "$hs" | grep -q "holding Prograde: [0-4]\.[0-9] deg" && echo 1 || echo 0) "$hs"
ok "T1 flames lit while turning, their records found" $(awk -v lit="${lit:-0}" -v dl=$(( ${l1:-0} - ${l0:-0} )) -v dh=$(( ${h1:-0} - ${h0:-0} )) 'BEGIN{print (lit > 0 && dl > 0 && dh / dl > 0.9) ? 1 : 0}') "lit $lit, hits $(( ${h1:-0} - ${h0:-0} )) of $(( ${l1:-0} - ${l0:-0} )) lookups"
q "hold off" >/dev/null

# E1 - the effects ramp
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "aeroset AeroEntryFx.TestAxis $AX" >/dev/null; setv 200; sleep 4; q "fxcheck reset" >/dev/null
for i in $(seq 0 40); do setv $(( 200 + 700 * i / 40 )); sleep 0.25; done; sleep 2
for i in $(seq 0 40); do setv $(( 900 - 700 * i / 40 )); sleep 0.25; done; sleep 3
e1=$(q "fxcheck reset")
ok "E1 the effects ramp: no pops, no flicker" $(awk -v p="$(num "$e1" pops)" -v f="$(num "$e1" flicker)" -v s="$(num "$e1" max-step)" 'BEGIN{print (p == 0 && f == 0 && s < 0.05) ? 1 : 0}') "$e1"

setv 0; q "gridhold $h 0" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== checkall: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
