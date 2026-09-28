#!/usr/bin/env bash
# usage: play_grid.sh -- a played session seated in a ship, for random-time screenshots (run
# randshots.sh alongside). World 2 (the Blue Fighter; it has no colonization map): seat, onto rails,
# a 250 km orbit, warp, third person now and then, then a prograde maneuver flown by auto-burn
# (the ship turns its main engines onto the burn and fires them).
SP="$(dirname "$0")"; O="$SP/orb.sh"
S=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
say() { echo "$(date +%T) $*"; }
tp()  { if [ $(( RANDOM % 3 )) = 0 ]; then $O "camview" >/dev/null; fi; if [ $(( RANDOM % 2 )) = 0 ]; then $O "map on" >/dev/null; else $O "map off" >/dev/null; fi; }

say "seat"; $O "seat Blue Fighter" >/dev/null; sleep 6; grep "> seat" $S | tail -1 | cut -c10-120
$O "legacy on" >/dev/null; sleep 8
$O "orbit Verdure 250 250" >/dev/null; sleep 6
$O "flight" >/dev/null; sleep 1; grep "> flight" $S | tail -1 | cut -c10-120
say "coast"; for k in 1 2 3 4; do tp; sleep $(( 5 + RANDOM % 8 )); done
say "warp"; for k in 1 2 3; do $O "key period" >/dev/null; sleep $(( 4 + RANDOM % 6 )); tp; done
$O "key slash" >/dev/null; sleep 4
say "maneuver in 1 min, 30 m/s prograde, auto-burn"
$O "node add 1 30 0 0" >/dev/null; sleep 2; $O "node auto 0 on" >/dev/null
for k in $(seq 1 14); do tp; sleep $(( 5 + RANDOM % 6 )); $O "flight" >/dev/null; done
grep "> flight" $S | tail -1 | cut -c10-200
$O "node list" >/dev/null; sleep 1; grep "> node list" $S | tail -1 | cut -c10-160
say "done"
