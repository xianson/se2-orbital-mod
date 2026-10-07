#!/usr/bin/env bash
# scenarios.sh - frame events in game, each PASS/FAIL from the mod's own measurements and log. RELAUNCHES the game; never
# saves. A Blue Fighter on a 300 km Verdure orbit, copies of it:
#  S1 the anchor destroyed: a new anchor elected, the frame does not jump (a member stays on its own orbit, celcheck)
#  S2 a ship moved 150 km out of its berth (fast travel's stand-in): it leaves the frame quietly, no split
#  S3 seated in a ship that splits off: the old membership left while seated; standing up joins the ship's new frame
#  S4 encounters off, a new grid pasted beside a frame: it joins the frame
#  S5 warp x1000 towards a rendezvous: the frames merge, and the merged ship stays on its own orbit (velocity not cut by N)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
newclone() {   # newclone <of> <spacing m>: the id of a fresh copy
  local before; before=$(ids); q "gridclone $1 1 $2" >/dev/null; sleep 10; scan; comm -13 <(echo "$before") <(ids) | head -1
}
since() { sed -n "/$1/,\$p" "$(LOG)"; }   # (the log from a marker line on)
mark() { q "frameof player" >/dev/null; echo "SCN-MARK $1" >/dev/null; MARK=$(date +%H:%M:%S); }
after() { awk -v m="$MARK" '{ if (substr($2,1,8) >= m) print }' "$(LOG)"; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
num() { echo "$1" | grep -o "[0-9.]* m from its orbit" | grep -o "^[0-9.]*"; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
scan; bf=$(ids | head -1); echo "anchor: Blue Fighter $bf"
q "unseat" >/dev/null; sleep 4
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 8
cl=$(newclone $bf 3000); echo "clone: $cl ($(q "frameof $cl"))"

# S1 - the anchor destroyed
q "celmark $cl" >/dev/null
echo "  before: $(q "frameof $cl")"
q "griddel $bf" >/dev/null; sleep 6
fo=$(q "frameof $cl"); cc=$(q "celcheck $cl"); d=$(num "$cc")
echo "  after: $fo | $cc"
ok "S1 the anchor destroyed: $cl is the new anchor" $(echo "$fo" | grep -q "anchor $cl" && echo 1 || echo 0)
ok "S1 the frame did not jump: $cl within 200 m of its own orbit" $(awk -v d="${d:-99999}" 'BEGIN{print (d < 200) ? 1 : 0}') "$cc"

# S2 - a ship moved far out (fast travel's stand-in)
c2=$(newclone $cl 3000); echo "clone2: $c2 ($(q "frameof $c2"))"
MARK=$(date +%H:%M:%S)
echo "  $(q "gridnudge $c2 150000 0 0")"; sleep 6
lg=$(after)
ok "S2 moved 150 km out: it left its frame quietly" $(echo "$lg" | grep -aq "grid $c2 .*left frame .*moved by something else" && echo 1 || echo 0) "$(q "frameof $c2")"
ok "S2 ... and was not split onto an orbit" $(echo "$lg" | grep -aqi "split.*$c2\|$c2.*split" && echo 0 || echo 1)

# S3 - seated in a ship that splits off
c3=$(newclone $cl 3000); echo "clone3: $c3"
q "tpgrid $c3 30" >/dev/null; sleep 5
echo "  on foot: $(q "frameof player")"
q "seatid $c3" >/dev/null; sleep 6
old=$(q "frameof player"); echo "  seated: $old"
MARK=$(date +%H:%M:%S)
echo "  $(q "gridnudge $c3 25000 0 0")"; sleep 8
lg=$(after)
ok "S3 the ship split off with you seated in it" $(echo "$lg" | grep -aqi "split" && echo 1 || echo 0) "$(q "frameof $c3")"
ok "S3 your membership of the old frame left while seated" $(echo "$lg" | grep -aq "seated: left frame\|$old" && echo 1 || echo 0) "$(echo "$lg" | grep -a "seated: left frame" | tail -1 | cut -c1-160)"
q "unseat" >/dev/null; sleep 6
lg=$(after); pf=$(q "frameof player"); sf=$(q "frameof $c3")
ok "S3 standing up: in the ship's frame" $([ "${pf#*: }" != "none" ] && [ "$(echo "$pf" | grep -o '#[0-9]*')" = "$(echo "$sf" | grep -o '#[0-9]*')" ] && echo 1 || echo 0) "player $pf | ship $sf"
ok "S3 standing up: not thrown (no player split)" $(echo "$lg" | grep -aq "player split\|SPLIT player\|player .*split off" && echo 0 || echo 1)

# S4 - encounters off: a pasted grid joins the frame
q "encon off" >/dev/null
c4=$(newclone $cl 600); sleep 4
ok "S4 encounters off: a pasted grid joins the frame" $(q "frameof $c4" | grep -q "#" && echo 1 || echo 0) "$(q "frameof $c4")"
q "encon on" >/dev/null

# S5 - warp into a rendezvous
q "seatid $cl" >/dev/null; sleep 4
c5=$(newclone $cl 4000); echo "clone5: $c5"
q "encon off" >/dev/null   # (no encounter spawned beside the new frames: it anchored one and capped the warp)
for g in $c2 $c3 $c4; do q "griddel $g" >/dev/null; done; sleep 3   # (the earlier scenarios' ships: their drift capped the warp)
q "gridorbit $cl Verdure 300 300 0 0" >/dev/null
q "gridorbit $c5 Verdure 299 299 0 -1.7" >/dev/null; sleep 6
fa=$(q "frameof $cl" | grep -o "#[0-9]*"); fb=$(q "frameof $c5" | grep -o "#[0-9]*"); echo "  frames: $cl in $fa, $c5 in $fb"
q "griddamp $c5 off" >/dev/null; sleep 2   # (dampeners on, it station-keeps in its frame - by design - instead of following its own orbit)
q "celmark $c5" >/dev/null
MARK=$(date +%H:%M:%S)
q "warp 1000" >/dev/null
for i in $(seq 1 60); do after | grep -aq "MERGE frame \($fa\|$fb\) -> \($fa\|$fb\)" && break; sleep 3; done
q "warp 1" >/dev/null; sleep 5; q "encon on" >/dev/null
lg=$(after); cc=$(q "celcheck $c5"); d=$(num "$cc")
ok "S5 warp into a rendezvous: those two frames merged" $(echo "$lg" | grep -aq "MERGE frame \($fa\|$fb\) -> \($fa\|$fb\)" && echo 1 || echo 0) "$(echo "$lg" | grep -a "MERGE frame\|ahead of a rendezvous" | head -2 | cut -c40-200)"
ok "S5 the merged ship on its own orbit (within 1 km)" $(awk -v d="${d:-99999}" 'BEGIN{print (d < 1000) ? 1 : 0}') "$cc"
echo "== scenarios: $P passed, $F failed"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
