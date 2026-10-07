#!/usr/bin/env bash
# hud_cases.sh - the orbit card against the truth (hudcheck) in every seat a player can be in. RELAUNCHES the game;
# never saves. A Blue Fighter on a 300 km circular orbit about Verdure (the frame's anchor), clones of it near and far:
#  A seated in the anchor          B seated in a clone (not the anchor)     C the clone drifting at 20 m/s
#  D warp x10 in the clone         E on foot, then seated again             F a clone pasted 2 km off, seated
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
check() { local r; r=$(q "hudcheck"); echo "[$1] $r" | sed 's/ || /\n      /g'; case "$r" in "hudcheck PASS"*) P=$((P+1));; *) F=$((F+1));; esac; }
P=0; F=0
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
scan; before=$(ids); bf=$(echo "$before" | head -1); echo "anchor: Blue Fighter $bf"
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $bf 30")"; sleep 4
echo "seat: $(q "tpgrid $bf 15" >/dev/null; sleep 4; q "seatid $bf")"; sleep 5
echo "orbit: $(q "gridorbit $bf Verdure 300 300")"; sleep 8
check "A seated in the anchor"
echo "clone near: $(q "gridclone $bf 1 150")"; sleep 10
scan; near=$(comm -13 <(echo "$before") <(ids) | head -1); echo "near clone: $near"; before=$(ids)
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $near 30")"; sleep 4
echo "seat: $(q "tpgrid $near 15" >/dev/null; sleep 4; q "seatid $near")"; sleep 6
check "B seated in a clone, not the anchor"
echo "drift: $(q "gridhold $near 20")"; sleep 6
check "C the clone drifting at 20 m/s"
q "gridhold $near 0" >/dev/null; sleep 3
echo "warp: $(q "warp 10")"; sleep 6
check "D warp x10 in the clone"
q "warp 1" >/dev/null; sleep 3
echo "unseat: $(q "unseat")"; sleep 5
check "E1 on foot by the clone"
echo "seat: $(q "tpgrid $near 15" >/dev/null; sleep 4; q "seatid $near")"; sleep 6
check "E2 seated again, after the walk"
echo "clone far: $(q "gridclone $bf 1 2000")"; sleep 10
scan; far=$(comm -13 <(echo "$before") <(ids) | head -1); echo "far clone: $far"
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $far 30")"; sleep 4
echo "seat: $(q "tpgrid $far 15" >/dev/null; sleep 4; q "seatid $far")"; sleep 3
check "F1 seated in a clone pasted 2 km off, at once"
sleep 12
check "F2 the same, 12 s later"
echo "== hud cases: $P passed, $F failed"
