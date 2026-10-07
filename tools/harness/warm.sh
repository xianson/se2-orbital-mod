#!/usr/bin/env bash
# warm.sh - warm berths: no engine stall when a frame takes a berth. RELAUNCHES the game; never saves. In Concordia:
#  W1 after the load, at least 3 free warm berths
#  W2 a ship split off into a fresh berth: the server tick's longest gap there is far under the cold ~230 ms
#  W3 the placeholders are no grids of ours (no frame, no gridinfo, no contact)
#  W4 the split ship's berth gets its own placeholder (warm when it leaves), and the pool is topped up again
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
newclone() { local before; before=$(ids); q "gridclone $1 1 $2" >/dev/null; sleep 10; scan; comm -13 <(echo "$before") <(ids) | head -1; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
num() { echo "$1" | grep -o "$2 [0-9]*" | grep -o "[0-9]*" | head -1; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 30; echo "log $(LOG)"
w=$(q "warm"); echo "  $w"
grep -a "warm berths:" "$(LOG)" | head -5 | cut -c12-200
fw=$(echo "$w" | grep -o "([0-9]* free" | grep -o "[0-9]*")
ok "W1 at least 3 free warm berths after the load" $([ "${fw:-0}" -ge 3 ] && echo 1 || echo 0) "$w"

scan; bf=$(ids | head -1); echo "ship: Blue Fighter $bf"
q "encon off" >/dev/null
q "unseat" >/dev/null; sleep 3
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 10
c=$(newclone $bf 3000); echo "copy: $c ($(q "frameof $c"))"
sleep 5
q "hitchms 40" >/dev/null   # (every gap over 40 ms logged: the split's own cost measured, not just "under 150")
M=$(date +%H:%M:%S)
echo "  $(q "gridnudge $c 25000 0 0")"; sleep 8
sp=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -a "SPLIT grid $c" | head -1); echo "  $(echo "$sp" | cut -c12-200)"
st=$(echo "$sp" | cut -c12-23)
# the longest server tick gap from the split until the pool's next refill (the nudge before it moved the copy 25 km into
# cold space - its own stall - and the refill warms a cold berth: both excluded, the refill shown for scale)
win=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | sed -n "/SPLIT grid $c/,/warm berths: slot [0-9]* being warmed/p")
gap=$(echo "$win" | grep -a "\[HITCH\] server tick gap" | grep -o "gap [0-9]*" | grep -o "[0-9]*" | sort -n | tail -1)
refill=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | sed -n '/warm berths: slot [0-9]* being warmed/,$p' | grep -a "\[HITCH\] server tick gap" | head -1 | grep -o "gap [0-9]*")
echo "  the split's window: longest gap ${gap:-none over 40 ms} | a cold berth warmed after it: ${refill:-none}"
ok "W2 the split into a warm berth: no stall (longest gap ${gap:-under 40} ms; a cold one ~230)" $([ -n "$sp" ] && awk -v g="${gap:-0}" 'BEGIN{print (g < 120) ? 1 : 0}' || echo 0) "$(echo "$sp" | cut -c60-200)"

# W3 - placeholders are no grids of ours
pl=$(grep -ac "'Orbital warm berth'" "$(LOG)")
ok "W3 no placeholder ever listed as a grid (gridinfo, frames)" $([ "$pl" = 0 ] && echo 1 || echo 0) "mentions: $pl"
sleep 5
w2=$(q "warm"); echo "  $w2"
ok "W4 the split ship's berth warm too, the pool topped up" $(echo "$w2" | grep -q "added\|placeholder" && [ "$(echo "$w2" | grep -o "([0-9]* free" | grep -o "[0-9]*")" -ge 3 ] && echo 1 || echo 0) "$w2"
q "encon on" >/dev/null
echo "== warm: $P passed, $F failed"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
