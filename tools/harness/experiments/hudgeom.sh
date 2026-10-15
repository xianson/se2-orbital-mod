#!/usr/bin/env bash
# hudgeom.sh - what HudGeom reads off the game's HUD (2 minutes or less; RELAUNCHES the game - the Steam launch, its last
# world, never saved - and closes it after): loaded and seated as saved, alignbtn's status line, and a screenshot.
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded"; send "encon off"; hold 12
echo "STATUS $(reply "alignbtn" | sed 's/.*=>  //' | cut -c1-600)"
for t in CockpitHUDToggles HUDSpeedometer HUDAltimeter; do send "hudtree $t 40"; hold 2; echo "TREE $t: $(reply "hudtree" | sed 's/.*=>  //')"; done
send "hudtree root 400"; hold 2; echo "ROOT: $(reply "hudtree" | sed 's/.*=>  //' | tr '|' '
' | grep -a "Speed\|Altim\|Toggle\|Flight\|Screen" | head -20 | tr '
' '|')"
echo "SHOT $(shot hudgeom)"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== hudgeom: $(( $(date +%s)-T0 ))s (game closed)"
