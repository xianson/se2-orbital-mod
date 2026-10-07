#!/usr/bin/env bash
# rv_views.sh - rendezvous from every seat, every target, every view, in game. RELAUNCHES the game; never saves.
# A Blue Fighter on a 300 km circular Verdure orbit (the frame's anchor), a clone 3 km off, a target site 30 km ahead on
# the same orbit (devtarget: no sector needed). For each seat (the anchor's pilot, the clone's pilot, on foot by the clone)
# and target (none, the site, a planet): hudcheck, then engine shots with the HUD - third person, first person, two random
# spectator angles, the map, the map's Rendezvous tab (docs/shots-work/rvv_*.jpg; contact sheets rvv_sheet_*.jpg).
# Then an elliptic orbit (300 x 600 km): the anchor plot from the clone and the target plot again.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; E="$H/eshot.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
P=0; F=0
check() { local r; r=$(q "hudcheck"); echo "[$1] $r" | sed 's/ || /\n      /g'; case "$r" in "hudcheck PASS"*) P=$((P+1));; *) F=$((F+1));; esac; }
shots() {   # shots <tag>: every view
  local tag=$1
  timeout 90 "$E" "rvv_${tag}_1third" 2 "shotui on" >/dev/null 2>&1
  q "camview" >/dev/null; sleep 2
  timeout 90 "$E" "rvv_${tag}_2first" 2 "shotui on" >/dev/null 2>&1
  q "camview" >/dev/null; sleep 2
  for k in 1 2; do
    b=$((RANDOM % 360)); el=$(( (RANDOM % 120) - 60 )); d=$(awk -v r=$RANDOM 'BEGIN{printf "%.2f", 0.04 + (r % 260) / 1000}')
    timeout 90 "$E" "rvv_${tag}_3cam${k}_b${b}_e${el}" 3 "cam player $d $b $el" >/dev/null 2>&1
  done
  q "cam off" >/dev/null; sleep 2
  q "map on" >/dev/null; sleep 4
  timeout 90 "$E" "rvv_${tag}_4map" 3 "shotui on" >/dev/null 2>&1
  q "rvtab on" >/dev/null; sleep 3
  timeout 90 "$E" "rvv_${tag}_5rvtab" 3 "shotui on" >/dev/null 2>&1
  q "rvtab off" >/dev/null; q "map off" >/dev/null; sleep 3
}
case_set() {   # case_set <seat tag> <seat label>
  for tg in none site planet; do
    case $tg in none) q "target off" >/dev/null;; site) q "target TestStation" >/dev/null;; planet) q "target Kemik" >/dev/null;; esac
    sleep 3; check "$2, target $tg"; shots "$1_$tg"
  done
  q "target off" >/dev/null
}
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
rm -f "$H/../../docs/shots-work/"rvv_*.jpg
scan; before=$(ids); bf=$(echo "$before" | head -1); echo "anchor: Blue Fighter $bf"
q "shotui on" >/dev/null
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $bf 30")"; sleep 4
echo "seat: $(q "tpgrid $bf 15" >/dev/null; sleep 4; q "seatid $bf")"; sleep 5
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 8
echo "target: $(q "devtarget TestStation Verdure 300 30")"; sleep 6
echo "sites: $(q "sites")"
case_set A "the anchor's pilot"
echo "clone: $(q "gridclone $bf 1 3000")"; sleep 10
scan; cl=$(comm -13 <(echo "$before") <(ids) | head -1); echo "clone: $cl"
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $cl 30")"; sleep 4
echo "seat: $(q "tpgrid $cl 15" >/dev/null; sleep 4; q "seatid $cl")"; sleep 6
case_set B "the clone's pilot 3 km off"
q "unseat" >/dev/null; sleep 5
case_set C "on foot by the clone"
# elliptic: 300 x 600 km (the frame's orbit; the clone and you ride it)
echo "seat: $(q "tpgrid $cl 15" >/dev/null; sleep 4; q "seatid $cl")"; sleep 5
echo "orbit: $(q "gridorbit $bf Verdure 600 300 0 0")"; sleep 8
q "target off" >/dev/null; sleep 3; check "E1 elliptic, the clone's pilot, no target"
timeout 90 "$E" "rvv_E_none_1third" 2 "shotui on" >/dev/null 2>&1
q "target TestStation" >/dev/null; sleep 3; check "E2 elliptic, the clone's pilot, the site targeted"
timeout 90 "$E" "rvv_E_site_1third" 2 "shotui on" >/dev/null 2>&1
q "target off" >/dev/null
echo "== rendezvous views: $P passed, $F failed"
for s in A B C; do powershell -NoProfile -ExecutionPolicy Bypass -File "$H/montage.ps1" -Pattern "rvv_${s}_*.jpg" -Out "$(cygpath -m "$H/../../docs/shots-work")/rvv_sheet_${s}.jpg" -Cols 6 -W 480 | tail -1; done
powershell -NoProfile -ExecutionPolicy Bypass -File "$H/montage.ps1" -Pattern "rvv_E_*.jpg" -Out "$(cygpath -m "$H/../../docs/shots-work")/rvv_sheet_E.jpg" -Cols 2 -W 800 | tail -1
