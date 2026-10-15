#!/usr/bin/env bash
# reparentab.sh - stranded block effects, before and after EffectReparent (5 minutes or less; RELAUNCHES the game - the
# Steam launch, its last world, never saved - and closes it after): loaded, a census (particles) and a screenshot; then
# 'reparent' (each pooled effect parented to its owner's render root again), a census and a screenshot.
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
shot() { send "shotui on"; timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off"; hold ${SETTLE:-20}
census() { "$SP/orb.sh" particles >/dev/null 2>&1; grep -a "ORBIT-PARTICLES" "$(LOGF)" | tail -1 | sed 's/ || /\n/g' | grep -a "Generic\|AeroFlight\|live effects" | sed 's/BlockEffectComponentBase Particle: //; s/Vanilla.System.Particles.//g; s/_ParticleEffect\(Flame\)\?\.def//g' | head -${1:-12}; }
echo "== before"; census 14
echo "SHOT before $(shot reparent_before)"
echo "== reparent: $(reply "reparent" | sed 's/.*=>  //')"
hold 3
echo "SHOT after $(shot reparent_after)"
echo "== after"; census 6
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== reparentab: $(( $(date +%s)-T0 ))s (game closed)"; summary
