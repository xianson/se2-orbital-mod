#!/usr/bin/env bash
# hintshot.sh - the build hints on the jetliner (5 minutes or less; RELAUNCHES the game - the Steam launch, the aero mod's
# world, never saved - and closes it after). The harness cannot hold a block: `hints <grid>` shows them on a grid as if
# building. Standing beside it, facing it (the wind from the camera):
#  P  the panel's figures come (top speed, cruise, thrust, mass)
#  O  the drag overlay finds the faces the camera sees, at Mach 0.5 and at Mach 2
#  plus engine shots (overlay off / subsonic / supersonic) and no mod fault
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s); G=f7a663bd
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off" "unseat"; hold 3
fg=$("$SP/orb.sh" findgrids | grep -a " => ")
id=$(echo "$fg" | grep -o "[0-9]\+=[^ ]*" | grep -i "$G" | head -1 | cut -d= -f1)
[ -z "$id" ] && echo "      grids: $(echo "$fg" | cut -c1-600)"
echo "      jetliner: $id"
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
send "tpgrid $id 70"; hold 4
send "hints $id" "hints overlay 0"; hold 4
s=$(reply "hints status"); echo "      $(echo "$s" | cut -c1-400)"
expect "P the panel's figures (top speed, thrust, mass)" "$s" bash -c "echo '$s' | grep -q 'top [0-9]' && echo '$s' | grep -q 'mass [1-9]'"
echo "      off:        $(shot hints0)"
send "hints overlay 1"; hold 3
s1=$(reply "hints status"); n1=$(echo "$s1" | grep -o "overlay 1 ([0-9]* faces)" | grep -o "[0-9]* faces")
echo "      subsonic:   $(shot hints1)  ($n1)"
send "hints overlay 2"; hold 3
s2=$(reply "hints status"); n2=$(echo "$s2" | grep -o "overlay 2 ([0-9]* faces)" | grep -o "[0-9]* faces")
echo "      supersonic: $(shot hints2)  ($n2)"
expect "O the drag overlay has faces at Mach 0.5 and Mach 2" "$n1 / $n2" bash -c "[ '${n1%% *}' -gt 0 ] 2>/dev/null && [ '${n2%% *}' -gt 0 ]"
send "hints overlay 0" "hints off" "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== hintshot: $(( $(date +%s)-T0 ))s (game closed)"; summary
