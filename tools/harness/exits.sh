#!/usr/bin/env bash
# exits.sh - getting out of vehicles, every way the frames can catch you. RELAUNCHES the game; never saves. A Blue Fighter
# on a 300 km Verdure orbit (the anchor of its frame) and a copy 3 km off (a member). Each case: seated, the condition
# set up, stood up; then for 8 s: you stay by the ship (no jump, drift under 20 m), at its speed (under 1.5 m/s
# relative, server), in its frame (the HUD's too), no split or move of yours; the HUD check passes.
#  E1 the anchor ship, x1                 E2 a member holding station, x1      E3 a member drifting, x1
#  E4 the anchor ship in warp x10         E5 a member drifting in warp x10     E6 a member coasting at ~10 m/s
#  E7 in and out three times, quickly     E8 a member that split off with you in it (its own frame now)
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

# exitcase <tag> <ship id>: stand up now; sample 8 s; judge
exitcase() {
  local tag="$1" ship="$2" M first last d0 d8 dv8 me sh hud ev s
  M=$(date +%H:%M:%S)
  echo "  [$tag] before: $(q "exitcheck $ship")"
  q "unseat" >/dev/null
  first=""; for t in 0.5 1 1 2 4; do sleep $t; s=$(q "exitcheck $ship"); [ -z "$first" ] && first="$s"; last="$s"; echo "     $s" | cut -c1-230; done
  hud=$(q "hudcheck")
  d0=$(val "$first" "server d"); d8=$(val "$last" "server d"); dv8=$(val "$last" "server d [0-9.]* m dv")
  dv8=$(echo "$last" | grep -o "server d [0-9.]* m dv [0-9.]*" | grep -o "[0-9.]*$")
  me=$(fr "$last" "you"); sh=$(fr "$last" "ship")
  ev=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)" | grep -a "SPLIT player\|player split\|ORBIT: left frame\|STOW\|teleport\|materializ" | head -3 | cut -c40-200)
  ok "$tag: by the ship (drift $(awk -v a="${d0:-0}" -v b="${d8:-0}" 'BEGIN{printf "%.1f", b-a}') m in 8 s, at ${d8:-?} m)" $(awk -v a="${d0:-0}" -v b="${d8:-99999}" 'BEGIN{print (b - a < 20 && b < 300) ? 1 : 0}')
  ok "$tag: at its speed (${dv8:-?} m/s relative)" $(awk -v v="${dv8:-99}" 'BEGIN{print (v < 1.5) ? 1 : 0}')
  ok "$tag: in its frame (you #$me, ship #$sh; HUD $(fr "$last" "hud"))" $([ -n "$me" ] && [ "$me" != "none" ] && [ "$me" = "$sh" ] && [ "$(fr "$last" "hud")" = "$me" ] && echo 1 || echo 0)
  ok "$tag: nothing moved you" $([ -z "$ev" ] && echo 1 || echo 0) "$ev"
  ok "$tag: the HUD check" $(echo "$hud" | grep -q "hudcheck PASS" && echo 1 || echo 0) "$(echo "$hud" | grep -o "FAIL [^|]*" | head -2 | tr '\n' ' ')"
}
seat() { q "tpgrid $1 15" >/dev/null; sleep 4; q "seatid $1" >/dev/null; sleep 6; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 20; echo "log $(LOG)"
scan; bf=$(ids | head -1); echo "anchor: Blue Fighter $bf"
q "encon off" >/dev/null
q "unseat" >/dev/null; sleep 3
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 10
c=$(newclone $bf 3000); echo "member: $c ($(q "frameof $c"))"

seat $bf;              exitcase "E1 anchor, x1" $bf
seat $c; q "griddamp $c on" >/dev/null; sleep 3;   exitcase "E2 member holding station" $c
seat $c; q "griddamp $c off" >/dev/null; sleep 3;  exitcase "E3 member drifting" $c
seat $bf; q "warp 10" >/dev/null; sleep 5;         exitcase "E4 anchor in warp x10" $bf; q "warp 1" >/dev/null; sleep 3
seat $c; q "griddamp $c off" >/dev/null; q "warp 10" >/dev/null; sleep 5; exitcase "E5 member drifting in warp x10" $c; q "warp 1" >/dev/null; sleep 3
seat $c; q "griddamp $c off" >/dev/null; q "thrust 0 0 -1 3" >/dev/null; sleep 5; exitcase "E6 member coasting" $c
# E7 - in and out three times, quickly
for k in 1 2; do q "seatid $c" >/dev/null; sleep 1.5; q "unseat" >/dev/null; sleep 1.5; done
seat $c;               exitcase "E7 member, after quick in-and-out" $c
# E8 - a member that split off with you in it
c2=$(newclone $bf 3000); echo "member 2: $c2"
seat $c2; q "griddamp $c2 on" >/dev/null; q "gridnudge $c2 25000 0 0" >/dev/null; sleep 8
echo "  split: $(q "frameof $c2")"
exitcase "E8 split off with you in it" $c2
q "encon on" >/dev/null
echo "== exits: $P passed, $F failed"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -20
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
