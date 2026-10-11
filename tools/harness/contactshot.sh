#!/usr/bin/env bash
# contactshot.sh - the Contacts tab (the game's GPS tab over the mod's list) (5 minutes or less; RELAUNCHES the game and
# closes it after: not while someone plays).
#  I  installed into the map's GPS tab, named Contacts; its groups and rows (your ships, contacts, planets, sectors, places)
#  S  the screen's own search filters it
#  T  selecting a row targets it
#  P  its eye pins it (saved state), and a pinned ship of yours is marked on the HUD
#  G  your real GPS points untouched: the same before and after (none of ours leaked in)
#  plus engine shots with the UI, and no mod fault
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
J=1000000033
poll() {
    local name="$1" to="$2"; shift 2
    for i in $(seq 1 "$to"); do "$@" >/dev/null 2>&1 && { record "$name" ""; return 0; }; alive || break; sleep 1; done
    record "$name" "not within ${to}s"; return 1
}
logs() { since | grep -aq -- "$1"; }
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
ct() { reply "contactstab $*" | sed 's/.*=>  //'; }

echo "== launch"; "$SP/relaunch.sh" 2>&1 | tail -1
MARK=0
poll "load: planets and grids" 60 bash -c "grep -aq 'server planet prefab' \"$(log)\" && \"$SP/orb.sh\" findgrids | grep -aq '='"
gps0=$(reply gps | sed 's/.*=>  //' | grep -o "[0-9]* marker(s)" ); echo "      real GPS before: $gps0"
mark; send "gridlaunch $J 60"
poll "setup: the test grid goes on rails" 30 logs "STOW grid $J"
send "orbit Verdure 300 280"; sleep 3
send "map on"; sleep 3
c=$(ct); echo "      $(echo "$c" | cut -c1-400)"
expect "I installed into the GPS tab" "$c" bash -c "echo '$c' | grep -q '^installed'"
send "contactstab open"; sleep 3
c=$(ct); echo "      $(echo "$c" | cut -c1-600)"
for g in "Your ships" "Planets and moons"; do expect "I rows under '$g'" "$(echo "$c" | cut -c1-200)" bash -c "echo '$c' | grep -q '\[$g: '"; done
expect "I your launched ship is a row" "$(echo "$c" | cut -c1-200)" bash -c "echo '$c' | grep -q 'grid:$J'"
echo "      tab:  $(shot 'contactstab')"

s=$(ct search Verd); echo "      $s"
n=$(echo "$s" | sed -n 's/.*-> \([0-9]*\) top-level.*/\1/p'); expect "S the search narrows the list (to $n)" "$s" test "${n:-99}" -le 3
echo "      search: $(shot 'contactstab')"
ct search >/dev/null

s=$(ct select grid:$J); echo "      $s"
expect "T selecting a row targets it" "$s" bash -c "echo '$s' | grep -q 'target grid:$J'"
s=$(ct pin grid:$J); echo "      $s"
expect "P its eye pins it" "$s" bash -c "echo '$s' | grep -q 'pinned=True'"
echo "      tab:  $(shot 'contactstab')"

send "map off"; sleep 3
bm=$(grep -a "^bodymarkers" "$ST" | head -1); echo "      $bm"
expect "P the pinned ship is marked on the HUD" "$bm" bash -c "echo '$bm' | grep -q 'contacts: [1-9]'"
echo "      hud:  $(shot 'map off')"
send "map on"; sleep 2; send "contactstab open"; sleep 2
c=$(ct)
expect "L the tab is labelled Contacts" "$(echo "$c" | grep -o "| tab '[^']*'")" bash -c "echo \"$c\" | grep -q \"| tab 'Contacts'\""
# Delete: a contact to Hidden, and back
D=1000000067
ct select grid:$D >/dev/null; send "key contactdel"; sleep 2
c=$(ct); expect "D Del puts a contact away under Hidden" "$(echo "$c" | grep -o "\[[^]]*grid:$D\]")" bash -c "echo \"$c\" | grep -q 'Hidden: [^]]*grid:$D\]'"
send "key contactdel"; sleep 2
c=$(ct); expect "D ... Del again brings it back" "$(echo "$c" | grep -o "\[[^]]*grid:$D\]")" bash -c "echo \"$c\" | grep -q 'Contacts: [^]]*grid:$D\]'"
# Rename sticks
s=$(ct rename grid:$J Mothership); echo "      $s"
sleep 1; c=$(ct)
expect "R a rename sticks" "$(echo "$c" | grep -o "\[[^]]*grid:$J\]")" bash -c "echo \"$c\" | grep -q \"Mothership' [a-z ]*grid:$J\""
# A place's eye is its GPS point's
pk=$(echo "$c" | grep -o "place:[^]]*" | head -1); echo "      place: $pk"
if [ -n "$pk" ]; then
  s1=$(ct unpin "$pk"); s2=$(ct pin "$pk"); echo "      $s1 / $s2"
  expect "E a place's eye hides and shows its GPS point" "$s1 / $s2" bash -c "echo \"$s1\" | grep -q 'hidden=True' && echo \"$s2\" | grep -q 'hidden=False'"
fi
echo "      tab:  $(shot 'contactstab')"
send "map off"; sleep 2
gps1=$(reply gps | sed 's/.*=>  //' | grep -o "[0-9]* marker(s)"); echo "      real GPS after: $gps1"
expect "G your real GPS points untouched" "before '$gps0' after '$gps1'" test "$gps0" = "$gps1"

MARK=0
never "whole run: no mod fault" "\[ORBIT-FAULT\]"
expect "whole run: game still running" "the game is not running" alive
send "target off" "map off" "cam off" "shotui off"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== contactshot: $(( $(date +%s)-T0 ))s (game closed)"
summary
