#!/usr/bin/env bash
# usage: sweep.sh <prefix> [count] -- randomized UI sweep: random map zoom/angle/pan/focus, hovers,
# clicks, context menus, maneuvers, warp, flight camera views; one engine screenshot per step into
# docs/shots-work/<prefix>_NN.jpg, and a log of what each step did (<prefix>.log). For finding what
# curated shots miss.
SP="$(dirname "$0")"; P=$1; N=${2:-24}
LOG="$SP/../../docs/shots-work/$P.log"; : > "$LOG"
r() { echo $(( RANDOM % $1 )); }                         # 0..n-1
f() { printf '0.%02d' $(( $1 + RANDOM % ($2 - $1) )); }  # a fraction between .a and .b
bodies=(Verdure Kemik Palatine Caligo Delfos)
zooms=(0.09 0.12 0.2 0.35 0.6 0.9 1.4 2.2 2.8 3.5)
"$SP/orb.sh" "shotui on" >/dev/null
for i in $(seq -w 1 $N); do
  cmds=()
  case $(r 7) in
    0|1|2) # map: random view
      cmds+=("map on" "mapcam ${RANDOM:0:3} $(( 10 + RANDOM % 80 ))" "mapcam zoom ${zooms[$(r ${#zooms[@]})]}")
      [ $(r 2) = 0 ] && cmds+=("focus ${bodies[$(r ${#bodies[@]})]}")
      [ $(r 2) = 0 ] && cmds+=("mapcam pan $(( RANDOM % 5 - 2 )).$(r 9) $(( RANDOM % 5 - 2 )).$(r 9)")
      [ $(r 2) = 0 ] && cmds+=("pickat $(f 30 75) $(f 20 80)");;
    3) # map: a click somewhere (double-click, right-click menu)
      cmds+=("map on")
      if [ $(r 2) = 0 ]; then cmds+=("dblclick $(f 30 75) $(f 20 80)"); else cmds+=("rclickat $(f 30 75) $(f 20 80)"); fi;;
    4) # maneuvers: add one somewhere on the path, pull a handle
      cmds+=("map on" "node clickat $(( 2 + RANDOM % 30 ))")
      [ $(r 2) = 0 ] && cmds+=("node pull P $(( 20 + RANDOM % 80 )) 1.5");;
    5) # warp up a few levels, then look
      cmds+=("map $([ $(r 2) = 0 ] && echo on || echo off)")
      for k in $(seq 1 $(( 1 + RANDOM % 4 ))); do cmds+=("key period"); done;;
    6) # flight: third-person around the player / look at a body
      cmds+=("map off" "mapcam reset")
      if [ $(r 2) = 0 ]; then cmds+=("cam player 0.0$(( 1 + RANDOM % 9 )) ${RANDOM:0:3} $(( RANDOM % 120 - 60 ))"); else cmds+=("cam off" "lookat ${bodies[$(r 4)]}"); fi;;
  esac
  echo "$i: ${cmds[*]}" >> "$LOG"
  "$SP/orb.sh" "${cmds[@]}" >/dev/null
  sleep $(( 1 + RANDOM % 3 ))
  timeout 90 "$SP/eshot.sh" "${P}_$i" 1 "flight" 2>/dev/null | tail -1
  "$SP/orb.sh" "cam off" "key slash" >/dev/null
done
"$SP/orb.sh" "map off" "mapcam reset" "node clear" "key slash" "pickat off" >/dev/null
echo "log: $LOG"
