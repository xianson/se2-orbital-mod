#!/usr/bin/env bash
# usage: play_transfer.sh -- a played session for random-time screenshots (run randshots.sh alongside):
# EVA in a Verdure orbit, the map opened and closed at random, warp up and down, third-person views,
# then a planned transfer to Palatine flown by auto-burn (jetpack) through the SOI change.
SP="$(dirname "$0")"; O="$SP/orb.sh"
S=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
say() { echo "$(date +%T) $*"; }
maybe_map() { if [ $(( RANDOM % 2 )) = 0 ]; then $O "map on" >/dev/null; else $O "map off" >/dev/null; fi; }
# third person: the game's own camera toggle (its V key), now and then
tp()  { if [ $(( RANDOM % 3 )) = 0 ]; then $O "camview" >/dev/null; fi; }

say "orbit"; $O "orbit Verdure 250 250" >/dev/null; sleep 12
say "coast, map and camera at random"
for k in 1 2 3 4; do maybe_map; tp; sleep $(( 5 + RANDOM % 10 )); done
say "warp up"; $O "map off" >/dev/null
for k in 1 2 3; do $O "key period" >/dev/null; sleep $(( 3 + RANDOM % 6 )); maybe_map; done
sleep 10; $O "key slash" >/dev/null
say "plan the transfer"; $O "map on" >/dev/null; sleep 3
$O "node findenc Palatine" >/dev/null; sleep 4; grep "> node findenc" $S | tail -1 | cut -c10-200
$O "node auto 0 on" >/dev/null; sleep 2
say "warp to the burn (warp stops by itself)"
for k in 1 2 3 4; do $O "key period" >/dev/null; sleep 2; done
for k in $(seq 1 12); do maybe_map; tp; sleep $(( 5 + RANDOM % 8 )); grep "> flight" $S >/dev/null; done
$O "flight" >/dev/null; sleep 1; grep "> flight" $S | tail -1 | cut -c10-200
say "after the burn: warp toward the encounter"
for k in 1 2 3 4 5; do $O "key period" >/dev/null; sleep 2; done
for k in $(seq 1 12); do maybe_map; tp; sleep $(( 5 + RANDOM % 8 )); done
$O "key slash" "map off" >/dev/null
$O "node list" >/dev/null; sleep 1; grep "> node list" $S | tail -1 | cut -c10-200
say "done"
