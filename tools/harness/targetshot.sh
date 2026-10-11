#!/usr/bin/env bash
# targetshot.sh - targeting (5 minutes or less; RELAUNCHES the game and closes it after: not while someone plays).
#  L  a ship of yours on rails (the jointed test grid, launched) is in the map's target list and drawn as a pin
#  N  the cursor on its pin and N: it is the target; N again: cleared, or the next ship there
#  R  a click on the target list's first row targets that row
#  A  the target line at any distance: a ship ("closest" / "rendezvous"), a planet ("closest ... up")
#  V  the Rendezvous tab about a ship target; the HUD's relative plot about it in flight
#  plus engine shots of the map (pins, brackets, list) and the HUD, and no mod fault
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
J=1000000033
poll() {
    local name="$1" to="$2"; shift 2
    for i in $(seq 1 "$to"); do "$@" >/dev/null 2>&1 && { record "$name" ""; return 0; }; alive || break; sleep 1; done
    record "$name" "not within ${to}s"; return 1
}
logs() { since | grep -aq -- "$1"; }
st() { grep -aq -- "$1" "$ST"; }
shot() { timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
# (the map is drawn over the game as its UI: the engine shot takes it only with shotui on)
dshot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
tgt() { reply targets | sed -n "s/.*=>  target \([^|]*\) | line \([^|]*\) |.*/\1|\2/p"; }

echo "== launch"; "$SP/relaunch.sh" 2>&1 | tail -1
MARK=0
poll "load: planets and grids" 60 bash -c "grep -aq 'server planet prefab' \"$(log)\" && \"$SP/orb.sh\" findgrids | grep -aq '='"

mark; send "gridlaunch $J 60"
poll "setup: the test grid goes on rails (a ship of yours in orbit)" 30 logs "STOW grid $J"
mark; send "orbit Verdure 300 280"
poll "setup: you are on rails too" 20 bash -c "grep -a '^orbit frame' \"$ST\" | grep -aq 'Pe'"
send "map on"
poll "setup: the map opens" 15 bash -c "grep -a '^mapview' \"$ST\" | grep -avq 'map closed\|no map'"
send "mapcam at Verdure" "mapcam 20 35" "mapcam zoom 0.05"
sleep 3

r=$(reply targets); echo "      $(echo "$r" | cut -c1-400)"
key=$(echo "$r" | grep -o "\[grid:$J '[^']*' yours" | head -1 | sed "s/\[\(grid:[0-9]*\) .*/\1/")
[ -z "$key" ] && key=$(echo "$r" | grep -o "\[grid:[0-9]* '[^']*' yours" | head -1 | sed "s/\[\(grid:[0-9]*\) .*/\1/")
expect "L your ship on rails is in the target list" "no own ship in: $(echo "$r" | grep -o '| ships[^|]*' | cut -c1-200)" test -n "$key"
pin=$(echo "$r" | grep -o "\[$key '[^']*' at [0-9.]*,[0-9.]*\]" | head -1 | grep -o "[0-9.]*,[0-9.]*\]" | tr -d ']')
expect "L ... and drawn on the map as a pin" "no pin for $key on screen" test -n "$pin"
echo "      map:  $(dshot 'targets')"

if [ -n "$pin" ]; then
  send "target off" "pickat ${pin%,*} ${pin#*,}"; sleep 1
  u=$(reply targets | grep -o "| under.*"); echo "      $u"
  send "key n"; sleep 1
  t1=$(tgt); echo "      after N: $t1"
  expect "N the cursor on the pin and N: the ship is the target" "target: $t1" bash -c "echo '$(reply targets)' | grep -q 'target .*\[$key\]'"
  echo "      map:  $(dshot 'targets')"
  # (re-aimed: on rails the ship moved on while the shot was taken)
  pin=$(reply targets | grep -o "\[$key '[^']*' at [0-9.]*,[0-9.]*\]" | head -1 | grep -o "[0-9.]*,[0-9.]*\]" | tr -d ']')
  [ -n "$pin" ] && send "pickat ${pin%,*} ${pin#*,}"; sleep 1
  u=$(reply targets | grep -o "| under.*")
  nu=$(echo "$u" | grep -o "\[[^]]*\]" | wc -l)
  send "key n"; sleep 1
  t2=$(tgt); echo "      N again ($nu under the cursor): $t2"
  # (alone under the cursor: cleared; with others there: the next of them)
  if [ "$nu" -le 1 ]; then expect "N ... N again: cleared (alone under the cursor)" "$t2" bash -c "echo \"$t2\" | grep -q '^none'"
  else expect "N ... N again: the next thing under the cursor" "$t2" bash -c "! echo \"$t2\" | grep -q '\[$key\]'"; fi
fi

send "pickat off" "target off" "targetrow 0"; sleep 1
r=$(reply targets); row0=$(echo "$r" | grep -o "| list \[[^ ]*" | sed 's/| list \[//')
expect "R a click on the list's first row targets it" "row $row0, $(echo "$r" | cut -c1-200)" bash -c "echo '$r' | grep -q \"target .*\[$row0\]\""

send "target Kemik"; sleep 2
t2=$(tgt); echo "      Kemik: $t2"
expect "A a planet: how high the path passes it, at any distance" "$t2" bash -c "echo '$t2' | grep -q 'closest .* up in\|impact in\|no approach on this path'"
if [ -n "$key" ]; then
  send "target $key"; sleep 2
  t3=$(tgt); echo "      ship: $t3"
  expect "A a ship: how close the path comes, at any distance" "$t3" bash -c "echo '$t3' | grep -q 'closest .* in\|rendezvous .* in\|no approach on this path'"
  echo "      map:  $(dshot 'targets')"
  send "rvtab on"; sleep 3
  rv=$(reply "rvtab on"); echo "      rvtab: $(echo "$rv" | cut -c1-200)"
  expect "V the Rendezvous tab about the ship" "$rv" bash -c "echo '$rv' | grep -qv 'target lost\|no target'"
  echo "      rv:   $(dshot 'rvtab on')"
  send "rvtab off" "map off"; sleep 2
  poll "V the HUD's relative plot about the ship in flight" 10 st "^relnow"
  echo "      hud:  $(shot 'map off')"
fi

MARK=0
never "whole run: no mod fault" "\[ORBIT-FAULT\]"
expect "whole run: game still running" "the game is not running" alive
send "target off" "pickat off" "map off" "cam off" "shotui off"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== targetshot: $(( $(date +%s)-T0 ))s (game closed)"
summary
