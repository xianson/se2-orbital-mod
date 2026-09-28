#!/usr/bin/env bash
# usage: sweep.sh <prefix> [count] -- randomized interaction sweep on the map and in flight: random map
# views (zoom/angle/pan/focus), hovers, clicks, double-clicks, right-click menus (and picking an item
# or pressing Esc), maneuvers (add, pull a handle, slide, arm), warp, third person. One engine
# screenshot per step into docs/shots-work/<prefix>_NN.jpg; each step's commands in <prefix>.log.
SP="$(dirname "$0")"; P=$1; N=${2:-24}
LOG="$SP/../../docs/shots-work/$P.log"; : > "$LOG"
r() { echo $(( RANDOM % $1 )); }                         # 0..n-1
f() { printf '0.%02d' $(( $1 + RANDOM % ($2 - $1) )); }  # a fraction between .a and .b
bodies=(Verdure Kemik Palatine Caligo Delfos)
zooms=(0.09 0.12 0.2 0.35 0.6 0.9 1.4 2.2 2.8 3.5)
handles=(P R N AN RO RI)
for i in $(seq -w 1 $N); do
  cmds=("shotui on")
  case $(r 9) in
    0|1) # map: random view
      cmds+=("map on" "mapcam ${RANDOM:0:3} $(( 10 + RANDOM % 80 ))" "mapcam zoom ${zooms[$(r ${#zooms[@]})]}")
      [ $(r 2) = 0 ] && cmds+=("focus ${bodies[$(r ${#bodies[@]})]}")
      [ $(r 2) = 0 ] && cmds+=("pickat $(f 30 75) $(f 20 80)");;
    2) # map: double-click somewhere
      cmds+=("map on" "dblclick $(f 30 75) $(f 20 80)");;
    3) # map: right-click menu, then pick an item, press Esc, or leave it open
      cmds+=("map on" "rclickat $(f 30 75) $(f 20 80)")
      case $(r 3) in 0) cmds+=("menu $(r 3)");; 1) cmds+=("key escape");; esac;;
    4) # maneuvers: add one on the path, pull a handle
      cmds+=("map on" "mapcam reset" "node clickat $(( 2 + RANDOM % 40 ))")
      [ $(r 2) = 0 ] && cmds+=("node pull ${handles[$(r ${#handles[@]})]} $(( 20 + RANDOM % 80 )) 1.2");;
    5) # maneuvers: slide the first one, arm it
      cmds+=("map on" "node slide 0 $(( 2 + RANDOM % 30 ))")
      [ $(r 2) = 0 ] && cmds+=("node auto 0 on");;
    6) # warp up a few levels (map or flight)
      cmds+=("map $([ $(r 2) = 0 ] && echo on || echo off)")
      for k in $(seq 1 $(( 1 + RANDOM % 4 ))); do cmds+=("key period"); done;;
    7) # flight, maybe third person
      cmds+=("map off" "mapcam reset")
      [ $(r 2) = 0 ] && cmds+=("camview");;
    8) # clear the plan now and then
      cmds+=("node clear");;
  esac
  echo "$i: ${cmds[*]}" >> "$LOG"
  "$SP/orb.sh" "${cmds[@]}" >/dev/null
  sleep $(( 1 + RANDOM % 3 ))
  timeout 90 "$SP/eshot.sh" "${P}_$i" 1 "shotui on" 2>/dev/null | tail -1
  "$SP/orb.sh" "key slash" "pickat off" >/dev/null
done
"$SP/orb.sh" "map off" "mapcam reset" "node clear" "key slash" "pickat off" >/dev/null
echo "log: $LOG"
