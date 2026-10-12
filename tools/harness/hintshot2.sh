#!/usr/bin/env bash
# hintshot2.sh - the build hints on a Blue Fighter seen by the spectator camera (clear of the station), overlay off /
# Mach 0.5 / Mach 2 (5 minutes or less; RELAUNCHES the game - the Steam launch, never saved - and closes it after)
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
shot() { send "shotui on"; local f; f=$(timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1)
  # (minimized, the engine may not draw: shown again without focus for the shot, then down)
  if [ -z "$f" ]; then Q restore; sleep 2; f=$(timeout 40 "$SP/orb.sh" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1); Q back; f="$f (window shown for it)"; fi; echo "$f"; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 3
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
Q() { [ -n "$NOQUIET" ] && return 0; powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$SP/quiet.ps1")" "$1" >/dev/null 2>&1; }
for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; [ $((i % 4)) = 0 ] && Q back; sleep 1; done
for i in $(seq 1 60); do "$SP/orb.sh" findgrids | grep -aq '=' && break; sleep 1; done
echo "loaded: $(LOGF)"; send "encon off"; hold 8
Q() { [ -n "$NOQUIET" ] && return 0; powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$SP/quiet.ps1")" "$1" >/dev/null 2>&1; }
Q back
bf=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=Blue_Fighter" | sed -n ${PICK:-2}p | cut -d= -f1)
echo "      Blue Fighter: $bf"
echo "      cam: $(reply "cam grid $bf ${KM:-0.03} ${BRG:-200} ${ELEV:-15}" | sed 's/.*=>  //' | cut -c1-160)"; hold 4
send "hints $bf"; hold 4
echo "      $(reply "hints status" | sed 's/.*=>  //' | cut -c1-260)"
echo "SHOT off $(shot hints0)"
send "hints overlay 1"; hold 5; echo "      $(reply "hints status" | grep -o "overlay [0-9] ([0-9]* faces)")"; echo "SHOT mach0.5 $(shot hints1)"
send "hints overlay 2"; hold 5; echo "SHOT mach2 $(shot hints2)"
send "hints overlay 0" "hints off" "cam off" "shotui off"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== hintshot2: $(( $(date +%s)-T0 ))s (game closed)"; summary
