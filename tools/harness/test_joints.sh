#!/usr/bin/env bash
# test_joints.sh — self-checking test of jointed grids (wheels, rotors, docked) moving through frames.
# Relaunches the game, runs each scenario with the harness, and checks the game's own log:
#   the expected event appeared, no Havok migration assertion, no fatal crash, the game still running.
# Prints PASS / FAIL per step and a summary. Uses 'Orbital Test Campaign 2' (grid 1000000033 is a small
# grid with one jointed part; 1000000030 the Cargo Truck, joined to the static barn).
# Scenarios run just inside Verdure's border (30 km), where the spawn guard keeps encounters out.
SP="$(dirname "$0")"
LOGDIR=/c/Users/slob/AppData/Roaming/SpaceEngineers2/Temp/Logs
J=1000000033; TRUCK=1000000030
pass=0; fail=0; results=()

log() { ls -t "$LOGDIR"/SpaceEngineers2_*.log | grep -v -e Render -e Mission | head -1; }
mark() { MARK=$(wc -l < "$(log)"); }
since() { tail -n +"$((MARK + 1))" "$(log)"; }
alive() { tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2; }
# wait until a pattern shows in the log since the mark (or timeout s)
waitfor() { local pat="$1" to="$2"; for i in $(seq 1 "$to"); do since | grep -aq -- "$pat" && return 0; alive || return 1; sleep 1; done; return 1; }
hold() { for i in $(seq 1 "$1"); do alive || return 1; sleep 1; done; return 0; }
bad() { since | grep -a -e "not migrated" -e "Crash Handler\]: Fatal" | head -1; }
check() {   # name, expected pattern, timeout, settle
    local name="$1" pat="$2" to="$3" settle="${4:-15}" why=""
    if ! waitfor "$pat" "$to"; then why="expected '$pat' not seen"; fi
    [ -z "$why" ] && ! hold "$settle" && why="game died after"
    local b; b=$(bad); [ -n "$b" ] && why="${why:+$why; }$(echo "$b" | cut -c1-160)"
    alive || why="${why:-game not running}"
    if [ -z "$why" ]; then pass=$((pass + 1)); results+=("PASS  $name"); else fail=$((fail + 1)); results+=("FAIL  $name: $why"); fi
    echo "${results[-1]}"
}
send() { "$SP/orb.sh" "$@" >/dev/null; }

echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15

# 1. A grid joined to something static never moves.
mark; send "gridmove $TRUCK 0 300000 0 group"
check "refuse: truck joined to a static base" "not moved: joined to something static" 20 5

# 2. Stow a jointed grid into a berth (thousands of km): the whole joined set, before the physics step.
mark; send "gridorbit $J Verdure 30 30 0 40"
check "gridorbit: jointed grid into a berth" "moved [0-9]* grid(s) in the teleport phase" 30 20

# 3. Re-pin: the jointed anchor pushed off its berth is shifted back (with its joined parts, before physics).
mark; send "gridmove $J 25000 0 0 group"
check "re-pin: jointed anchor shifted back to its berth" "moved [0-9]* grid(s) in the teleport phase" 20 20

# 4. Merge: two jointed grids on one orbit a few seconds apart; the incomer's jointed grid is carried over.
J2=1000000031
mark; send "gridorbit $J2 Verdure 30 30 0 40"; hold 6
mark; send "gridorbit $J Verdure 30 30 0 40"
check "merge: jointed grid carried into the other's frame" "MERGE frame #[0-9]* -> #[0-9]*: 1 grid(s) moved" 90 20
check "merge: its joined part went with it" "and 1 joined entit" 5 5

# 5. Split: the grid that is not the anchor pushed past the frame's reach splits off, its joined set with it.
ST=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
anc=$(grep -a "^  frame #.*$J" "$ST" | grep -a "$J2" | head -1 | sed -n 's/.*anchor=\([0-9]*\).*/\1/p')
mover=$([ "$anc" = "$J" ] && echo $J2 || echo $J)
echo "   (anchor $anc; pushing $mover)"
mark; send "gridmove $mover 30000 0 0 group"
check "split: jointed grid splits off to its own berth" "SPLIT grid $mover" 40 20
check "split: its joined part went with it" "grid $mover 'Grid' and 1 joined entit" 5 5

# 6. Arrival: a jointed grid's own frame dips under the handover shell and drops into Verdure's space.
mark; send "gridorbit $J Verdure 30 3 0 0"; hold 3; send "warp 10"
check "arrival: jointed grid's frame arrives at Verdure" "ARRIVE grid frame" 200 20
check "arrival: its joined part went with it" "grid $J 'Grid' and 1 joined entit" 5 5
send "warp 1"

echo; echo "== summary: $pass passed, $fail failed"
for r in "${results[@]}"; do echo "  $r"; done
[ "$fail" -eq 0 ]
