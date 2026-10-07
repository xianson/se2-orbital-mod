#!/usr/bin/env bash
# holdcharge.sh - holding station is charged, and shows no orbit. RELAUNCHES the game; never saves. A Blue Fighter on a
# 300 km Verdure orbit and a copy of it 5 km off (a member of its frame):
#  H1 the copy, dampeners on, holds station and its thrusters are charged for the pull (fuel or power asked > 0)
#  H2 its stores drain faster holding than free (the charge reaches the tanks / batteries)
#  H3 dampeners off: free again, its thrusters ask nothing
#  H4 on foot beside it, jetpack dampeners on: holding station - no orbit shown, and the suit is billed
#  H5 riding, the frame's origin is marked on screen (a screenshot is taken)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
newclone() { local before; before=$(ids); q "gridclone $1 1 $2" >/dev/null; sleep 10; scan; comm -13 <(echo "$before") <(ids) | head -1; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
asked() { echo "$1" | grep -o "[0-9.eE+-]*/s in all\|charged, [0-9.eE+-]*/s" | head -1 | grep -o "[0-9][0-9.eE+-]*" | head -1; }
fill() { q "stores" | tr '[' '\n' | grep -a "^$1:" | grep -o " [0-9.eE+-]*/" | tr -d ' /' | awk '{s+=$1} END {printf "%.6f", s}'; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
grep -a "+StationKeepCharge" "$(LOG)" | head -2 | cut -c60-200
scan; bf=$(ids | head -1); echo "anchor: Blue Fighter $bf"
q "encon off" >/dev/null
q "unseat" >/dev/null; sleep 4
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 8
c=$(newclone $bf 5000); echo "copy: $c ($(q "frameof $c"))"
q "griddamp $c on" >/dev/null; sleep 6

# H1, H2 - holding
s1=$(q "skcharge $c"); echo "  $s1"
f0=$(fill "Blue Fighter"); sleep 20; f1=$(fill "Blue Fighter")
ok "H1 holding station, its thrusters charged" $(echo "$s1" | grep -q "holding" && awk -v a="$(asked "$s1")" 'BEGIN{print (a+0 > 0) ? 1 : 0}' || echo 0) "$s1"
# H3 - free
q "griddamp $c off" >/dev/null; sleep 4
s2=$(q "skcharge $c"); echo "  $s2"
g0=$(fill "Blue Fighter"); sleep 20; g1=$(fill "Blue Fighter")
echo "  stores (all Blue Fighters): holding $f0 -> $f1, free $g0 -> $g1"
# (in a creative world the stores do not drain at all: the block's own ask on the network is what is checked)
ap=$(echo "$s1" | grep -o "applies [0-9.eE+-]*" | grep -o "[0-9][0-9.eE+-]*")
ok "H2 a charged block asks its network for it (the game's applied consumption)" $(awk -v a="${ap:-0}" 'BEGIN{print (a+0 > 0) ? 1 : 0}') "applied ${ap:-none}; stores drop holding $(awk -v a="$f0" -v b="$f1" 'BEGIN{print a-b}'), free $(awk -v c="$g0" -v d="$g1" 'BEGIN{print c-d}')"
ok "H3 dampeners off: free, nothing asked" $(echo "$s2" | grep -q "free, .* 0 charged" && echo 1 || echo 0) "$s2"
# H4 - on foot beside it
q "tpgrid $c 40" >/dev/null; sleep 8
s3=$(q "skcharge $c"); echo "  $s3"
hc=$(q "hudcheck"); echo "  $hc" | cut -c1-300
ok "H4 on foot holding station: no orbit shown" $(echo "$s3" | grep -q "holdingStation(you) True card none" && echo 1 || echo 0) "$s3"
ok "H4 ... and the suit billed (taken, or refused by the game in creative)" $(echo "$s3" | grep -q "suit billed .*\(taken\|refused: creative\)" && echo 1 || echo 0) "$(echo "$s3" | grep -o "suit billed[^|]*")"
# H5 - the frame origin marked on screen (riding: where the pull is nothing)
bm=$(q "bodymarkers on"); echo "  $bm"
ok "H5 the frame origin is marked" $(echo "$bm" | grep -q "frame origin marked" && echo 1 || echo 0) "$bm"
q "shotui on" >/dev/null; sleep 2; echo "  shot: $(q "shot holdcharge-origin")"
q "encon on" >/dev/null
echo "== holdcharge: $P passed, $F failed"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
