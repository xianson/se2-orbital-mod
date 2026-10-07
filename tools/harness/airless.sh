#!/usr/bin/env bash
# airless.sh - arriving at an airless body over the speed cap. RELAUNCHES the game (the campaign world in world.txt: Palatine
# has no air); never saves. On foot, on a fast pass at Palatine from outside (periapsis 1 km, 1500 m/s from afar), warp x10:
#  A1 the border slows the frame to the cap there, with a jolt (EntryHost.AirlessBorder: "has no air")
#  A2 it arrives at the cap or under (no second clamp by the world)
#  A3 the jolt is announced to its client before the move (the arrival notice) and the crossing fade runs
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -ExecutionPolicy Bypass -File "$H/launch-orbital.ps1"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 20; echo "log $(LOG) (world: $(cat "$H/world.txt"))"
q "unseat" >/dev/null; sleep 3
q "player dampeners off" >/dev/null
echo "flyby: $(q "flyby Palatine 1 1500")"; sleep 6   # (no orbit about Palatine meets its border over the cap - escape there is ~480 m/s: an arrival from outside)
echo "frame: $(q "frameof player")"
q "warp 10" >/dev/null
for i in $(seq 1 200); do grep -aq "ARRIVE.* -> Palatine\|ARRIVE player" "$(LOG)" && break; sleep 3; done
sleep 4; q "warp 1" >/dev/null
lg=$(grep -a "Palatine" "$(LOG)" | grep -a "has no air\|ARRIVE\|arrival of frame\|crossing fade" | tail -8)
grep -a "has no air\|ARRIVE\|arrival of frame\|crossing fade" "$(LOG)" | tail -10 | cut -c12-220
na=$(grep -a "has no air" "$(LOG)" | tail -1)
ok "A1 the border slowed it to the cap, a jolt" $([ -n "$na" ] && echo 1 || echo 0) "$(echo "$na" | cut -c60-240)"
ar=$(grep -a "ARRIVE" "$(LOG)" | grep -a "Palatine" | tail -1); v=$(echo "$ar" | grep -o "|v|=[0-9]*" | grep -o "[0-9]*")
ok "A2 arrived at or under the cap" $(awk -v v="${v:-99999}" 'BEGIN{print (v <= 1000) ? 1 : 0}') "$(echo "$ar" | cut -c60-240)"
fd=$(grep -ac "crossing fade: fading in" "$(LOG)")
ok "A3 the crossing fade ran" $([ "$fd" -gt 0 ] && echo 1 || echo 0) "fades: $fd"
echo "== airless: $P passed, $F failed"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
