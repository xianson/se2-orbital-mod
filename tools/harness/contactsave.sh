#!/usr/bin/env bash
# contactsave.sh - the target and the Contacts tab's pins, names and hidden contacts survive a save and a load (two
# launches, about 5 minutes; RELAUNCHES the game, saves only the "Orbital Test World" copy, closes the game after).
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
J=1000000033; D=1000000067
ct() { reply "contactstab $*" | sed 's/.*=>  //'; }

echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 10
mark; send "gridlaunch $J 60"
check "setup: the test grid on rails" "STOW grid $J" 40 5
send "orbit Verdure 300 280"; hold 5
send "map on"; hold 3; send "contactstab open"; hold 2
ct pin grid:$J >/dev/null; ct rename grid:$J Mothership >/dev/null
ct select grid:$D >/dev/null; send "key contactdel"; hold 2
send "target grid:$J"; hold 1
c0=$(ct); echo "      before: $(echo "$c0" | cut -c1-160)"
send "map off"; hold 2
mark; send "save"
check "save: written to the test world" "save -> 'Orbital Test World': " 60 5

KEEP="$(cat "$SP/world.txt")"
printf 'Orbital Test World' > "$SP/world.txt"
echo "== reload the saved world"; "$SP/relaunch.sh" 2>&1 | tail -1
printf '%s' "$KEEP" > "$SP/world.txt"
MARK=0; hold 20
check "load: frames restored" "RESTORE from save: [1-9][0-9]* frame" 30 5
send "map on"; hold 3; send "contactstab open"; hold 3
c1=$(ct); echo "      after:  $(echo "$c1" | tr "[" "
" | grep -a "Your ships\|Hidden" | head -8 | tr "
" " ")"
t1=$(reply targets | sed 's/.*=>  //' | cut -c1-120); echo "      $t1"
expect "load: the target came back (the ship, its id mapped)" "$t1" bash -c "echo \"$t1\" | grep -q 'target .*Mothership\|target .*grid:'"
expect "load: the pinned ship is still pinned" "$c1" bash -c "echo \"$c1\" | grep -q '[1-9] pinned'"
expect "load: its name came back" "$c1" bash -c "echo \"$c1\" | grep -q 'Mothership'"
expect "load: the hidden contact is still under Hidden" "$c1" bash -c "echo \"$c1\" | grep -q 'Hidden: '"
never "load: no mod fault" "\[ORBIT-FAULT\]"
expect "load: game still running" "the game is not running" alive
send "map off" "target off" "shotui off"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== contactsave: $(( $(date +%s)-T0 ))s (game closed)"
summary
