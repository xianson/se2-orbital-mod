#!/usr/bin/env bash
# test_save.sh — save / load round trip. Sets up a jointed grid on rails, an orbit and a maneuver node, saves
# into the harness's own 'Orbital Test World' (the save command refuses any other), reloads that world, and
# checks the frame's orbit, the grids and the node came back with no fault or Havok assertion. world.txt is
# put back to the harness world after the reload.
# (The HUD's Pe / Ap is not compared: holding station off the anchor with dampeners is not a free orbit,
# so the osculating reading drifts; the frame's own elements must not.)
source "$(dirname "$0")/testlib.sh"
J=1000000033
echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15
# (a stand-in telescope where you are: the harness world has no sensor block built; eyes alone see 20 km)
send "contacts sensor telescope"

mark; send "gridlaunch $J 60"   # (inside its cell: the dev launch teleports, and past the cell nothing stows)
check "setup: jointed grid on rails" "STOW grid $J" 40 10
send "orbit Verdure 300 280"; hold 12
send "node clear" "node add 20 25 0 0"; hold 4
send "testship" "sensornopower on"; hold 3; send "radarpower 0.25"   # (a real radar block, turned down)
# the player's frame: its id from the orbit line, its a (km) and e from the frame list
felem() {
    local id; id=$(stat "^orbit frame" | sed -n 's/^orbit frame #\([0-9]*\).*/\1/p')
    [ -n "$id" ] && stat "^frame #$id " | sed -n 's/.* a=\([0-9.]*\)km e=\([0-9.]*\).*/\1 \2/p'
}
orbit0=$(felem)
node0=$(reply "node" | grep -o "\[0\] in [0-9:]* P [0-9.]*")
known0=$(reply "contacts" | grep -o "known: [0-9]* grid(s), [0-9]* rock(s)")
echo "   before: frame a/e '$orbit0'  node '$node0'"

mark; send "save"
check "save: written to the test world" "save -> 'Orbital Test World': " 60 5
check "save: the mod's state captured" "saved state captured: [1-9][0-9]* frame" 5 1

KEEP="$(cat "$SP/world.txt")"
printf 'Orbital Test World' > "$SP/world.txt"
echo "== reload the saved world"; "$SP/relaunch.sh" 2>&1 | tail -1
printf '%s' "$KEEP" > "$SP/world.txt"
MARK=0; hold 20
check "load: the mod's state found" "saved state found" 5 1
check "load: frames restored" "RESTORE from save: [1-9][0-9]* frame" 30 5
orbit1=$(felem)
node1=$(reply "node" | grep -o "\[0\] in [0-9:]* P [0-9.]*")
echo "   after:  frame a/e '$orbit1'  node '$node1'"
same() { awk -v a="$1" -v b="$2" 'BEGIN{split(a,x," "); split(b,y," "); exit !(x[1] != "" && (x[1]-y[1])^2 < 0.01 && (x[2]-y[2])^2 < 1e-6)}'; }
expect "load: the frame's own orbit unchanged (a, e)" "'$orbit0' -> '$orbit1'" same "$orbit0" "$orbit1"
expect "load: the maneuver node came back" "'$node0' -> '$node1'" test -n "$node1"
check "load: the radar's setting came back" "radar setting restored: transmit power 25" 20 1
# (right after load, before anything new is in sight: what you had seen is still known)
known1=$(reply "contacts" | grep -o "known: [0-9]* grid(s), [0-9]* rock(s)")
rocks() { echo "$1" | sed -n 's/.* \([0-9]*\) rock(s).*/\1/p'; }
expect "load: what you had seen is still known" "'$known0' -> '$known1'" test "$(rocks "$known1")" -ge "$(rocks "$known0")" -a "$(rocks "$known0")" -gt 0
rst=$(since | grep -a "RESTORE from save" | tail -1 | sed -n 's/.*HighSpeed, \([0-9]*\) grid(s) missing.*/\1/p')
expect "load: every saved grid found" "missing: '$rst'" test "${rst:-1}" = 0
hold 20
never "load: no mod fault" "\[ORBIT-FAULT\]"
never "load: no Havok migration assertion" "not migrated"
expect "load: game still running" "the game is not running" alive

# Phase 2: saved while in a site (a ring rock you came up to): it is saved as a plain frame; you load on
# its orbit (not stranded where it was), and the rock's site comes back and joins you.
echo "== phase 2: save beside a ring rock"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15
mark; send "orbit Verdure 65 65 0"
check "site: a ring rock's rocks are out beside you" "ROCKS out: Oblivara #" 60 15
mark; send "save"
check "site: saved as a plain frame" "site frame #[0-9]* kept as a plain frame" 60 5
KEEP="$(cat "$SP/world.txt")"
printf 'Orbital Test World' > "$SP/world.txt"
echo "== reload"; "$SP/relaunch.sh" 2>&1 | tail -1
printf '%s' "$KEEP" > "$SP/world.txt"
MARK=0; hold 20
check "site load: your frame restored" "RESTORE from save: [1-9][0-9]* frame" 30 5
expect "site load: you are on an orbit (not stranded)" "no orbit line: $(stat '^orbit' | cut -c1-100)" grep -aq "^orbit frame #" "$ST"
check "site load: the rock's rocks come back" "ROCKS out: Oblivara #" 90 10
check "site load: the rock's site joins your frame" "MERGE frame #[0-9]* -> #[0-9]*\|player joins site" 60 5
hold 20
never "site load: rocks stay out (no out / in loop)" "ROCKS away: .*composition is the game's again"
never "site load: no mod fault" "\[ORBIT-FAULT\]"
never "site load: no Havok migration assertion" "not migrated"
expect "site load: game still running" "the game is not running" alive
echo "   world.txt: $(cat "$SP/world.txt")"
summary
