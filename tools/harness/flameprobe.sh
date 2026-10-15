#!/usr/bin/env bash
# flameprobe.sh - why thruster flames stick: the aero mod's flame probe ([AERO-FLAME] lines: the share published per
# thruster, the eased value, what was written, the override the game's thruster actually holds) through a scripted flight
# of the Hercules in space (5 minutes or less; RELAUNCHES the game unless NOLAUNCH=1: not while someone plays).
# Phases (marked in the output with their wall times): idle, thrust forward 4 s, released, hold retrograde (a turn), off.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
mark() { echo "PHASE $(date +%H:%M:%S) $1"; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
echo "seated: $(q "exitcheck $h" | grep -o "seated [A-Za-z]*") Hercules $h"
q "griddamp $h on" >/dev/null; sleep 3
q "aeroset AeroFlameComponent.Probe true" >/dev/null
mark idle; sleep 4
mark thrust-forward; echo "  thrust: $(q "thrust 0 0 -1 4")"; sleep 5
mark released; sleep 5
mark hold-retrograde; echo "  hold: $(q "hold retrograde")"; sleep 12; echo "  $(q hold)"
mark hold-off; q "hold off" >/dev/null; sleep 6
mark end
q "aeroset AeroFlameComponent.Probe false" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "probe lines: $(grep -ac 'AERO-FLAME' "$(LOG)")"
echo "== flameprobe in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
echo "LOG $(LOG)"
