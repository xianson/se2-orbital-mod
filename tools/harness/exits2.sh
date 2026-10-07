#!/usr/bin/env bash
# exits2.sh - getting out of vehicles where exits.sh does not look. RELAUNCHES the game; never saves.
#  X1 a ship in the world's own SITE (its stations among the members): out, by the ship, in the site's frame
#  X2 a ship in a PLANET's frame (materialized, 12 km up, hovering on its dampeners): out, by the ship, no frame jump
#  X3 out of a ship, then away on the jetpack past the frame's edge: you split off cleanly (one move, your own frame,
#     your speed kept), and the HUD agrees
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
newclone() { local before; before=$(ids); q "gridclone $1 1 $2" >/dev/null; sleep 10; scan; comm -13 <(echo "$before") <(ids) | head -1; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
val() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | grep -o "[0-9.]*$"; }
fr() { echo "$1" | grep -o "$2 #[0-9a-z]*" | head -1 | sed 's/.*#//'; }
seat() { q "tpgrid $1 15" >/dev/null; sleep 4; q "seatid $1" >/dev/null; sleep 6; }
exitcase() {   # exitcase <tag> <ship>: out; 8 s sampled; judged (both in no frame is fine: a planet's frame)
  local tag="$1" ship="$2" M first last d0 d8 dv8 me sh hud ev s
  M=$(date +%H:%M:%S)
  echo "  [$tag] before: $(q "exitcheck $ship")"
  q "unseat" >/dev/null
  first=""; for t in 0.5 1 1 2 4; do sleep $t; s=$(q "exitcheck $ship"); [ -z "$first" ] && first="$s"; last="$s"; done
  echo "     after: $last" | cut -c1-230
  hud=$(q "hudcheck")
  d0=$(val "$first" "server d"); d8=$(val "$last" "server d")
  dv8=$(echo "$last" | grep -o "server d [0-9.]* m dv [0-9.]*" | grep -o "[0-9.]*$")
  me=$(fr "$last" "you"); sh=$(fr "$last" "ship")
  ev=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -a "SPLIT player\|player split\|ORBIT: left frame\|STOW\|teleport\|materializ\|observer lost" | head -3 | cut -c40-200)
  ok "$tag: by the ship (drift $(awk -v a="${d0:-0}" -v b="${d8:-0}" 'BEGIN{printf "%.1f", b-a}') m in 8 s, at ${d8:-?} m)" $(awk -v a="${d0:-0}" -v b="${d8:-99999}" 'BEGIN{print (b - a < 20 && b < 300) ? 1 : 0}')
  ok "$tag: at its speed (${dv8:-?} m/s relative)" $(awk -v v="${dv8:-99}" 'BEGIN{print (v < 1.5) ? 1 : 0}')
  ok "$tag: in its frame (you #$me, ship #$sh; HUD $(fr "$last" "hud"))" $([ "$me" = "$sh" ] && [ "$(fr "$last" "hud")" = "$me" ] && echo 1 || echo 0)
  ok "$tag: nothing moved you" $([ -z "$ev" ] && echo 1 || echo 0) "$ev"
  ok "$tag: the HUD check" $(echo "$hud" | grep -q "hudcheck PASS" && echo 1 || echo 0) "$(echo "$hud" | grep -o "FAIL [^|]*" | head -2 | tr '\n' ' ')"
}

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 20; echo "log $(LOG)"
scan; bf=$(ids | head -1); echo "ship: Blue Fighter $bf"
st=$(grep -a "gridinfo 10000" "$(LOG)" | grep -a "dyn False" | grep -ao "=> grid [0-9]*" | grep -o "[0-9]\{10\}" | head -1); echo "station: $st ($(q "gridinfo $st" | cut -c1-120))"
q "encon off" >/dev/null
q "unseat" >/dev/null; sleep 3

# X1 - in the world's own site (its stations among the members, a virtual anchor): a ship of it, out
c1=$(newclone $bf 3000); echo "  ship: $c1 ($(q "frameof $c1")) | station: $(q "frameof $st")"
seat $c1; q "griddamp $c1 on" >/dev/null; sleep 3
exitcase "X1 in a site, by its stations" $c1

# X2 - in a planet's frame, 60 km up
c2=$(newclone $bf 3000); echo "  ship 2: $c2"
q "griddamp $c2 on" >/dev/null
echo "  launch: $(q "gridlaunch $c2 12 Verdure 0")"; sleep 10   # (under the planet frame's border, still: it hovers on its dampeners)
echo "  ship 2 frame: $(q "frameof $c2")"
seat $c2; sleep 3
exitcase "X2 in a planet's frame, 12 km up" $c2

# X3 - out, then away on the jetpack past the frame's edge
c3=$(newclone $bf 3000); echo "  ship 3: $c3"
seat $c3; q "griddamp $c3 on" >/dev/null; sleep 3
q "unseat" >/dev/null; sleep 4
f0=$(q "frameof player"); echo "  out: $f0 | $(q "exitcheck $c3" | cut -c1-160)"
q "player dampeners off" >/dev/null
M=$(date +%H:%M:%S)
echo "  $(q "player vel 400 0 0")"
for i in $(seq 1 30); do sleep 4; awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -aq "SPLIT player" && break; done
sleep 8
lg=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)")
sp=$(echo "$lg" | grep -a "SPLIT player" | head -1); echo "  $(echo "$sp" | cut -c12-200)"
f1=$(q "frameof player"); hc=$(q "hudcheck"); echo "  after: $f1 | $(echo "$hc" | cut -c1-200)"
ok "X3 split off past the frame's edge" $([ -n "$sp" ] && echo 1 || echo 0) "$(echo "$sp" | cut -c60-200)"
ok "X3 ... into a frame of your own" $(echo "$f1" | grep -q "#" && [ "$(echo "$f1" | grep -o '#[0-9]*')" != "$(echo "$f0" | grep -o '#[0-9]*')" ] && echo 1 || echo 0) "$f0 -> $f1"
nsplit=$(echo "$lg" | grep -ac "SPLIT player")
ok "X3 ... once (no split back and forth)" $([ "$nsplit" = 1 ] && echo 1 || echo 0) "splits: $nsplit"
ok "X3 ... the HUD check" $(echo "$hc" | grep -q "hudcheck PASS" && echo 1 || echo 0) "$(echo "$hc" | grep -o "FAIL [^|]*" | head -2 | tr '\n' ' ')"
q "player dampeners on" >/dev/null
q "encon on" >/dev/null
echo "== exits2: $P passed, $F failed"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -20
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
