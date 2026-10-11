#!/usr/bin/env bash
# infoshot.sh - orbital elements on the map: the target's docked card and the hover card (a ship's pin, a planet) - engine
# shots with the UI (5 minutes or less; RELAUNCHES the game and closes it after: not while someone plays).
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s); J=1000000033
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
echo "== launch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 10
mark; send "gridlaunch $J 60"; check "setup: the test grid on rails" "STOW grid $J" 40 5
send "orbit Verdure 300 280"; hold 4
send "map on"; hold 3; send "mapcam at Verdure" "mapcam 20 35" "mapcam zoom 0.05"; hold 3
send "target grid:$J"; hold 2
r=$(reply targets | sed 's/.*=>  //')
card=$(echo "$r" | grep -o "| panel .*"); echo "      $(echo "$card" | cut -c1-400)"
expect "C the target panel takes the right column" "$card" bash -c "echo \"$card\" | grep -q 'panel shown'"
expect "C ... its orbit" "$card" bash -c "echo \"$card\" | grep -q 'Orbit: Apoapsis=.*Periapsis=.*Inclination='"
expect "C ... from you" "$card" bash -c "echo \"$card\" | grep -q 'From you: Closest approach='"
echo "      panel: $(shot targets)"
send "targetpanel more"; hold 1; echo "      panel+more: $(shot targets)"; send "targetpanel more"
pin=$(echo "$r" | grep -o "\[grid:$J '[^']*' at [0-9.]*,[0-9.]*\]" | grep -o "[0-9.]*,[0-9.]*\]" | tr -d ']')
[ -n "$pin" ] && { send "pickat ${pin%,*} ${pin#*,}"; hold 1; echo "      hover ship: $(shot targets)"; }
vb=$(echo "$r" | grep -o "\[Verdure at [0-9.]*,[0-9.]*\]" | grep -o "[0-9.]*,[0-9.]*")
[ -n "$vb" ] && { send "pickat ${vb%,*} ${vb#*,}"; hold 1; echo "      hover Verdure: $(shot targets)"; }
send "pickat off" "mapcam zoom 3"; hold 3
r=$(reply targets | sed 's/.*=>  //')
kb=$(echo "$r" | grep -o "\[Kemik at [0-9.]*,[0-9.]*\]" | grep -o "[0-9.]*,[0-9.]*")
[ -n "$kb" ] && { send "pickat ${kb%,*} ${kb#*,}"; hold 1; echo "      hover Kemik: $(shot targets)"; }
MARK=0; never "whole run: no mod fault" "\[ORBIT-FAULT\]"
send "pickat off" "target off" "map off" "shotui off" "targetpanel panel"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== infoshot: $(( $(date +%s)-T0 ))s (game closed)"; summary
