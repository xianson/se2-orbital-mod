#!/usr/bin/env bash
# showshot.sh - screenshots of the new UI for the user (5 minutes or less; RELAUNCHES the game - the Steam launch, the
# aero mod's world, never saved - and closes it after):
#  1. the build hints where the player stands (holding a block): overlay off, Mach 0.5, Mach 2
#  2. seated in a Blue Fighter on a 300 km orbit: the orbit plot with its captions
#  3. the Contacts tab (no Edit / Remove), and F2's rename dialog
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s)
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off"; hold 8

echo "== 1 build hints (on the Blue Fighter, standing beside it: the harness cannot hold a block)"
bf=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=Blue_Fighter" | head -1 | cut -d= -f1)
send "unseat"; hold 3; send "tpgrid $bf 22"; hold 4; send "hints $bf"; hold 4
s=$(reply "hints status"); echo "      $(echo "$s" | cut -c1-300)"
echo "SHOT hints-off $(shot hints0)"
send "hints overlay 1"; hold 4; echo "SHOT hints-mach0.5 $(shot hints1)"
send "hints overlay 2"; hold 4; echo "SHOT hints-mach2 $(shot hints2)"
send "hints overlay 0" "hints off"

echo "== 2 the orbit plot"
echo "      Blue Fighter: $bf"
send "unseat"; hold 3; send "tpgrid $bf 30"; hold 3; send "tpgrid $bf 15"; hold 3
echo "      seat: $(reply "seatid $bf" | sed 's/.*=>  //')"; hold 4
echo "      orbit: $(reply "gridorbit $bf Verdure 300 300" | sed 's/.*=>  //')"; hold 10
echo "SHOT orbit-plot $(shot orbit)"

echo "== 3 the contacts tab"
send "map on"; hold 3; send "contactstab open"; hold 3
echo "SHOT contacts $(shot contacts)"
echo "      $(reply "contactstab select Verdure" | sed 's/.*=>  //' | cut -c1-160)"; hold 2
echo "SHOT contacts-selected $(shot contacts2)"
send "key contactrename"; hold 2
echo "SHOT contacts-rename $(shot rename)"
send "map off" "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|NoMatchingLocalPairing"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== showshot: $(( $(date +%s)-T0 ))s (game closed)"; summary
