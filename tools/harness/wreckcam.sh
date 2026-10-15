#!/usr/bin/env bash
# wreckcam.sh - a burning wreck's fires: on its hull, or left behind? (5 minutes or less; RELAUNCHES the game - the Steam
# launch, its last world, never saved - and closes it after): the spectator camera put DIST km off the wreck NAME (default
# the f7a663bd debris ship), a census and a screenshot; then 'reparent' (effects parented to their grids' roots again,
# the roots moved to their entities), a screenshot; the camera let go.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off"; hold 15
NAME=${NAME:-f7a663bd-c8cd-44a1-81ee-36fde0857747}
# (the census names the client copy; its server id from the census line)
"$SP/orb.sh" particles >/dev/null 2>&1
g=$(grep -a "ORBIT-PARTICLES" "$(LOGF)" | tail -1 | sed 's/ || /\n/g' | grep -a "owner '${NAME}'" | grep -o "server [0-9]*" | head -1 | cut -d' ' -f2)
[ -n "$G" ] && g=$G
echo "      wreck: $g  $(reply "gridinfo $g" | sed 's/.*=>  //' | cut -c1-200)"
census() { "$SP/orb.sh" particles >/dev/null 2>&1; grep -a "ORBIT-PARTICLES" "$(LOGF)" | tail -1 | sed 's/ || /\n/g' | grep -a "Generic\|live effects" | sed 's/BlockEffectComponentBase Particle: //; s/Vanilla.System.Particles.//g; s/_ParticleEffect\(Flame\)\?\.def//g' | head -${1:-8}; }
echo "      cam: $(reply "cam grid $g ${DIST:-0.12} 90 15" | sed 's/.*=>  //')"; hold 8
echo "== at the wreck"; census 8
echo "SHOT at-wreck $(shot wreck_before)"
echo "== reparent: $(reply "reparent" | sed 's/.*=>  //')"; hold 3
echo "SHOT at-wreck-after $(shot wreck_after)"
send "cam off"; hold 3
echo "SHOT player-after $(shot player_after)"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== wreckcam: $(( $(date +%s)-T0 ))s (game closed)"; summary
