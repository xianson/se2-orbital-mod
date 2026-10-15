#!/usr/bin/env bash
# cullab.sh - far grids' block effects drawn on the sky, with EffectCull off then on (5 minutes or less; RELAUNCHES the
# game - the Steam launch, its last world, never saved - and closes it after): loaded, 'fxcull off', a screenshot; 'fxcull
# on', a screenshot - the same view a few seconds apart.
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s)
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off"; hold 15
echo "== off: $(reply "fxcull off" | sed 's/.*=>  //')"; hold 3
echo "SHOT off $(shot cull_off)"
echo "== on: $(reply "fxcull on" | sed 's/.*=>  //')"; hold 3
echo "SHOT on $(shot cull_on)"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== cullab: $(( $(date +%s)-T0 ))s (game closed)"; summary
