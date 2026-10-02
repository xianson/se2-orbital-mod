#!/usr/bin/env bash
# test_perf.sh — what the mod's own code costs per frame (ms, average / worst over a second) in its heaviest
# scenes; FAIL when the average passes a budget. Reads the 'modcost' status line (ModCost in ServerFrames.cs).
source "$(dirname "$0")/testlib.sh"
[ "$1" = "--no-relaunch" ] || { echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15
# (a stand-in telescope where you are: the harness world has no sensor block built; eyes alone see 20 km)
send "contacts sensor radar"   # (radar: tracks at once, so ring predictions are on from the start)
}
mark; START=$MARK
cost() { stat "^modcost" | sed -n "s/.* $1 \([0-9.]*\)\/\([0-9.]*\).*/\1 \2/p"; }   # part -> "avg max"
# measure NAME PART BUDGET_MS: the worst of three one-second averages, against the budget
measure() {
    local name="$1" part="$2" budget="$3" worst=0 peak=0 a m
    for i in 1 2 3; do hold 2; read -r a m <<<"$(cost "$part")"
        worst=$(awk -v w="$worst" -v a="${a:-0}" 'BEGIN{print (a>w)?a:w}'); peak=$(awk -v w="$peak" -v a="${m:-0}" 'BEGIN{print (a>w)?a:w}'); done
    printf "   %-44s %-6s avg %6.2f ms  worst frame %6.1f ms  (budget %s)\n" "$name" "$part" "$worst" "$peak" "$budget"
    expect "perf: $name ($part < $budget ms avg)" "avg $worst ms" awk -v w="$worst" -v b="$budget" 'BEGIN{exit !(w<b)}'
}
send "target off" "orbit Verdure 300 300"; hold 15
measure "in orbit, flying"                   client 1.5
measure "in orbit, flying"                   server 1.5
send "node add 5 120 0 0"; hold 4
measure "in orbit, a maneuver planned"       client 2.0
send "node clear" "orbit Verdure 90 40 0"; hold 10
measure "on a path through Verdure's ring"   client 2.0
measure "on a path through Verdure's ring"   server 2.0
send "map on"; hold 8; send "mapcam at Verdure" "mapcam 20 35" "mapcam zoom 0.03"; hold 5
measure "map: Verdure close, ring and rocks" map 4.0
send "mapcam zoom 3"; hold 5
measure "map: whole system"                  map 4.0
send "target Kemik" "rvtab on"; hold 8
measure "rendezvous tab: porkchop to Kemik"  map 6.0
send "target Oblivara"; hold 6
measure "rendezvous tab: relative plot"      map 4.0
send "rvtab off" "map off" "warp 25"; hold 6
measure "warp x25"                           client 2.0
measure "warp x25"                           server 2.0
send "warp 1" "target off"
summary
