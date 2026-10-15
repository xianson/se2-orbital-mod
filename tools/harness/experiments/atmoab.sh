#!/usr/bin/env bash
# atmoab.sh - the proxy atmosphere against the real planet, same view: real, proxy unspherized, proxy spherized as the planet
# proxy (inner sphere at the hills radius) (5 minutes or less; RELAUNCHES the game and closes it after).
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s)
shot() { timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
echo "== launch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 8
send "encon off" "unseat"
for planet in Verdure Kemik; do
  for d in 400 1200; do
    b=$((RANDOM % 360)); e=$(( (RANDOM % 40) - 20 ))
    send "cam planet $planet $d $b $e"
    send "mode AlwaysReal"; hold 6; echo "G $planet ${d}km  real:   $(shot 'mode AlwaysReal')"
    send "mode AlwaysProxy" "atmosphz off"; hold 4; echo "G $planet ${d}km  flat:   $(shot 'atmosphz off')"
    send "atmosphz on"; hold 4; echo "G $planet ${d}km  sphz:   $(shot 'atmosphz on')"
  done
done
send "atmosphz on" "mode Frame" "cam off" "encon on"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== atmoab: $(( $(date +%s)-T0 ))s (game closed)"; summary
