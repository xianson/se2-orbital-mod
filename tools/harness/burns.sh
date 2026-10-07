#!/usr/bin/env bash
# burns.sh - armed maneuvers fly themselves. RELAUNCHES the game; never saves. Seated in a Blue Fighter on a 300 km Verdure orbit:
#  B1 a node 2 min ahead, +10 m/s prograde, armed, warp x10: warp stops for it, it burns, completes; the apoapsis rose
#  B2 armed late (its burn already started): it flies what is left (RendezvousPlot.RemainingBurn), and completes
#  B3 a manual node left behind (passed) does not hide a later armed one: that one fires
#  B4 nodes across a save and load (in memory: the save's lines, the load's parser) keep armed, and their target orbits
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
ap() { q "orbitnow" | grep -o "Ap [0-9.]* km" | grep -o "[0-9.]*" | head -1; }
waitdone() {   # waitdone <max s>: until the auto-burn says complete
  for i in $(seq 1 $(( $1 / 3 ))); do fl=$(q "flight"); echo "$fl" | grep -q "auto: burn complete" && return 0; sleep 3; done; return 1; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
scan; bf=$(ids | head -1); echo "ship: Blue Fighter $bf"
q "encon off" >/dev/null
q "unseat" >/dev/null; sleep 3
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 8
echo "seat: $(q "seatid $bf")"; sleep 6
q "node clear" >/dev/null

# B1
a0=$(ap); echo "  Ap before: $a0 km"
echo "  $(q "node add 2 10 0 0")"
echo "  $(q "node auto 0 on")"
q "warp 10" >/dev/null
done1=0; waitdone 400 && done1=1
a1=$(ap); fl=$(q "flight"); echo "  after: Ap $a1 km | $fl" | cut -c1-260
ws=$(grep -a "warp x10 -> x1\|Warp stopped" "$(LOG)" | tail -1 | cut -c40-200)
ok "B1 warp stopped before the burn" $([ -n "$ws" ] && echo 1 || echo 0) "$ws"
ok "B1 the armed burn completed" $done1
ok "B1 ... and raised the apoapsis (by over 10 km)" $(awk -v a="${a0:-0}" -v b="${a1:-0}" 'BEGIN{print (b - a > 10) ? 1 : 0}') "Ap $a0 -> $a1 km"
q "warp 1" >/dev/null; q "node clear" >/dev/null; sleep 3

# B2 - armed after its burn started (a 20 m/s burn 10 s ahead: it starts before the node)
a0=$(ap)
echo "  $(q "node add 0.17 20 0 0")"; sleep 14
echo "  $(q "node auto 0 on")"
done2=0; waitdone 200 && done2=1
a1=$(ap); echo "  after: Ap $a0 -> $a1 km"
ok "B2 armed late: it flew what was left, and completed" $done2 "$(q "flight" | cut -c1-200)"
ok "B2 ... the apoapsis rose" $(awk -v a="${a0:-0}" -v b="${a1:-0}" 'BEGIN{print (b - a > 5) ? 1 : 0}') "Ap $a0 -> $a1 km"
q "node clear" >/dev/null; sleep 3

# B3 - a passed manual node left in the list, a later armed one
echo "  $(q "node add 0.05 5 0 0")"; sleep 75   # (manual, not flown: passed and over)
echo "  $(q "node add 1 5 0 0")"
echo "  $(q "node auto 1 on")"
done3=0; waitdone 200 && done3=1
ok "B3 a passed manual node does not hide a later armed one" $done3 "$(q "node list" | cut -c1-200)"
q "node clear" >/dev/null
# B4 - armed nodes across a save and load (written as a save writes them, read back as a load reads them; nothing saved)
q "node add 30 7 0 0" >/dev/null; q "node add 45 3 1 0" >/dev/null; q "node auto 0 on" >/dev/null; sleep 4   # (planned: their target orbits)
nr=$(q "noderound"); echo "  $nr" | cut -c1-300
ok "B4 armed and manual nodes, with their target orbits, come back as they were" $(echo "$nr" | grep -q "noderound SAME" && echo "$nr" | grep -q "auto/target" && echo 1 || echo 0)
q "node clear" >/dev/null; q "encon on" >/dev/null
echo "== burns: $P passed, $F failed"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
