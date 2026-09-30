#!/usr/bin/env bash
# test_smoke.sh — the in-game smoke run (target ~3 min): ONE launch, then only what needs the engine, each
# step waiting for its own event (polled, with a timeout) instead of sleeping a fixed time. The logic is
# covered offline by tools/test_fast.sh; this checks the game side: the mod loads (components injected,
# sensor blocks present), an orbit stows, a jointed grid goes on rails, warp runs the clock, the map and its
# tabs draw, contacts are spotted, and nothing faults or trips Havok. The long suites (test_all.sh) stay
# for releases and bug hunts.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
J=1000000033
# poll NAME TIMEOUT COMMAND...: PASS as soon as COMMAND succeeds (checked every second), FAIL at the timeout.
poll() {
    local name="$1" to="$2"; shift 2
    for i in $(seq 1 "$to"); do "$@" >/dev/null 2>&1 && { record "$name" ""; return 0; }; alive || break; sleep 1; done
    record "$name" "not within ${to}s"; return 1
}
logs() { since | grep -aq -- "$1"; }
st() { grep -aq -- "$1" "$ST"; }

echo "== launch"; "$SP/relaunch.sh" 2>&1 | tail -1
MARK=0
poll "load: planets, grids, sensor blocks injected" 20 bash -c "grep -aq 'sensor block prefab.*Telescope' \"$(log)\" && grep -aq 'sensor block prefab.*Radar' \"$(log)\" && grep -aq 'server planet prefab' \"$(log)\""
poll "load: the sensor blocks are defined and unlocked" 20 bash -c "\"$SP/orb.sh\" sensorblocks | grep -aq 'Telescope: kind yes.*unlocked True.*Radar: kind yes.*unlocked True'"
send "contacts sensor telescope"

mark; send "gridlaunch $J 60"
poll "rails: a jointed grid goes on rails with its joined part" 30 logs "STOW grid $J"
poll "rails: ... its joined part moved with it" 5 logs "grid $J 'Grid' and 1 joined entit"

mark; send "orbit Verdure 300 280"
poll "orbit: the player stows onto the asked orbit" 20 bash -c "grep -a '^orbit frame' \"$ST\" | grep -aq 'Pe 2[78][0-9.]* km  Ap 30[0-9.]* km'"

t1=$(stat "^time " | sed -n 's/.*rails t=\([0-9.]*\).*/\1/p'); send "warp 100"
poll "warp: x100 runs the orbits' clock" 10 bash -c "t=\$(grep -a '^time ' \"$ST\" | sed -n 's/.*rails t=\([0-9.]*\).*/\1/p'); awk -v a=\"$t1\" -v b=\"\$t\" 'BEGIN{exit !(b-a>200)}'"
send "warp 1"

mark; send "contacts forget"
poll "contacts: ring rocks in sight are spotted (telescope)" 10 logs "ORBIT-CONTACT\] .*rocks spotted"

send "map on"
poll "map: opens" 15 bash -c "grep -a '^mapview' \"$ST\" | grep -avq 'map closed\|no map'"
send "mapcam at Verdure" "mapcam 20 35" "mapcam zoom 0.03"
poll "map: Verdure's ring on its globe" 15 bash -c "\"$SP/orb.sh\" mapring | grep -aq 'shown'"
send "target Kemik" "rvtab on"
poll "map: the Rendezvous tab and a solved porkchop" 20 bash -c "\"$SP/orb.sh\" 'rvtab on' | grep -a ' => ' | tail -1 | grep -aq 'porkchop best'"
send "rvtab off" "map off" "target Oblivara"
poll "hud: the relative plot about a target" 10 st "^relnow"
send "target off"

# The whole run: no fault, no Havok assertion, still running.
MARK=0
never "whole run: no mod fault" "\[ORBIT-FAULT\]"
never "whole run: no Havok migration assertion" "not migrated"
expect "whole run: game still running" "the game is not running" alive
echo "== smoke run: $(( $(date +%s)-T0 ))s"
summary
