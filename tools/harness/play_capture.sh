#!/usr/bin/env bash
# usage: play_capture.sh -- a played transfer that arrives: a Verdure orbit, a planned Palatine pass
# with its periapsis 20-80 km up (findarr), flown by auto-burn, warp to the SOI, then a capture burn at
# periapsis (retrograde until the orbit closes), then coast in Palatine orbit. Map and third person at
# random all along. Run randshots.sh alongside.
SP="$(dirname "$0")"; O="$SP/orb.sh"
S=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
say() { echo "$(date +%T) $*"; }
maybe_map() { if [ $(( RANDOM % 2 )) = 0 ]; then $O "map on" >/dev/null; else $O "map off" >/dev/null; fi; }
tp()  { if [ $(( RANDOM % 3 )) = 0 ]; then $O "camview" >/dev/null; fi; }
flight() { $O "flight" >/dev/null; sleep 1; grep "> flight" $S | tail -1 | cut -c10-110; }

say "orbit"; $O "orbit Verdure 250 250" >/dev/null; sleep 12
say "plan an arrival at Palatine, Pe 20-80 km up"
$O "map on" >/dev/null; sleep 3
$O "node findarr Palatine 40 100" >/dev/null; sleep 8; grep "> node findarr" $S | tail -1 | cut -c10-200
$O "node auto 0 on" >/dev/null
say "warp to the burn and fly it"
for k in 1 2 3 4 5; do $O "key period" >/dev/null; sleep 1; done
for k in $(seq 1 10); do maybe_map; tp; sleep $(( 5 + RANDOM % 6 )); done
flight
say "warp to Palatine's sphere"
for k in 1 2 3 4 5; do $O "key period" >/dev/null; sleep 1; done
for k in $(seq 1 60); do sleep 3; $O "flight" >/dev/null; grep -q "SOI Verdure -> Palatine" $(ls -t /c/Users/slob/AppData/Roaming/SpaceEngineers2/Temp/Logs/SpaceEngineers2_*[0-9].log | grep -v "Render\|Stats\|Mission" | head -1) && break; done
$O "key slash" >/dev/null; sleep 2
$O "node list" >/dev/null; sleep 1; grep "> node list" $S | tail -1 | cut -c10-200
say "capture: a burn at periapsis, retrograde"
$O "map on" "focus Palatine" >/dev/null; sleep 3
$O "node capture" >/dev/null; sleep 3; grep "> node capture" $S | tail -1 | cut -c10-200
$O "node auto 0 on" >/dev/null
for k in 1 2 3 4; do $O "key period" >/dev/null; sleep 1; done
for k in $(seq 1 12); do maybe_map; tp; sleep $(( 5 + RANDOM % 6 )); done
flight
$O "node list" >/dev/null; sleep 1; grep "> node list" $S | tail -1 | cut -c10-200
say "coast in orbit"
for k in 1 2 3; do $O "key period" >/dev/null; sleep 1; done
for k in 1 2 3 4 5; do maybe_map; tp; sleep $(( 5 + RANDOM % 6 )); done
$O "key slash" "map off" >/dev/null
flight
say "done"
