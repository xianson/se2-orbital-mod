#!/usr/bin/env bash
# spinfx.sh - the effects while a ship rotates fast through its airflow: the Hercules levelled in Verdure's air, puppet
# airflow at entry speed FROZEN in the world (AeroEntryFx.TestWorld freeze), then yawed at increasing rates; per rate the
# effects recorder (fxcheck): rebuilds while lit, pops, flicker, spot lights made. 5 minutes or less; RELAUNCHES the game
# unless NOLAUNCH=1 (not while someone plays). Releases every override at the end.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
SPEED=${SPEED:-900}; RATES=${RATES:-"0.2 0.6 1.5"}; HOLD=${HOLD:-8}
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1); echo "Hercules $h"
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
# the air from 30 deg below the nose, frozen in the world
q "aeroset AeroEntryFx.TestAxis 0,-0.5,-0.866" >/dev/null; q "aeroset AeroEntryFx.TestWorld freeze" >/dev/null
q "aeroset AeroEntryFx.TestSpeed $SPEED" >/dev/null; sleep 5
echo "still: $(q "fxcheck reset" | cut -c1-60)"; sleep $HOLD; echo "  still $HOLD s: $(q "fxcheck reset")"
for r in $RATES; do
  q "gridspin $h yaw $r" >/dev/null; sleep $HOLD
  echo "  yaw $r rad/s: $(q "fxcheck reset")"
done
q "gridspin $h 0 0 0" >/dev/null
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "aeroset AeroEntryFx.TestWorld \"\"" >/dev/null; q "aeroset AeroEntryFx.TestAxis -z" >/dev/null
q "gridhold $h 0" >/dev/null; q "cam off" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== spinfx in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
