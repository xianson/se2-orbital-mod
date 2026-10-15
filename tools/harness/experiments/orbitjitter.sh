#!/usr/bin/env bash
# orbitjitter.sh - how much the HUD's orbit readout jumps from frame to frame in flight through air (5 minutes or less;
# RELAUNCHES the game - the Steam launch, its last world, never saved - and closes it after): seated as loaded (the
# world's ship in flight), the orbit readout (orbitnow) sampled N times as fast as the harness answers; Ap, Pe and speed
# per sample, and their sample-to-sample spread (orbitjitter_sum.py).
source "$(dirname "$0")/../testlib.sh"
T0=$(date +%s); OUT=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$OUT"
launch
waitloaded
echo "loaded"; send "encon off"; hold 10
f="$OUT/orbitjitter.txt"; : > "$f"
for i in $(seq 1 ${N:-40}); do echo "$(date +%s.%N) $(reply "orbitnow" | sed 's/.*=>  //')" >> "$f"; done
echo "      $(wc -l < "$f") samples"
python "$(cygpath -w "$(dirname "$0")/orbitjitter_sum.py")" "$(cygpath -w "$f")"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== orbitjitter: $(( $(date +%s)-T0 ))s (game closed)"
