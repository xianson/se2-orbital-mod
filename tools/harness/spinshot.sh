#!/usr/bin/env bash
# spinshot.sh - engine screenshots of the entry effects while the Hercules spins through a world-fixed airflow (spinfx's
# setup): SHOTS shots while still, then SHOTS while yawing at RATE. NOLAUNCH=1 uses the running game. 5 minutes or less;
# releases every override (camera, airflow, hold, spin) at the end.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
SPEED=${SPEED:-900}; RATE=${RATE:-1.5}; SHOTS=${SHOTS:-4}; DIST=${DIST:-0.07}   # (km)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shot() { timeout 30 "$O" --eshot 0 "fxcheck" | grep -a -o "[A-Za-z]:[^ ]*\.png\|/[^ ]*\.png" | tail -1; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "aeroset AeroEntryFx.TestAxis 0,-0.5,-0.866" >/dev/null; q "aeroset AeroEntryFx.TestWorld freeze" >/dev/null
q "aeroset AeroEntryFx.TestSpeed $SPEED" >/dev/null; q "cam grid $h $DIST 30 15" >/dev/null; sleep 6
for i in $(seq 1 $SHOTS); do echo "still $i: $(shot)"; done
q "gridspin $h yaw $RATE" >/dev/null; sleep 2
for i in $(seq 1 $SHOTS); do echo "yaw $RATE #$i: $(shot)"; done
q "gridspin $h 0 0 0" >/dev/null; q "cam off" >/dev/null
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "aeroset AeroEntryFx.TestWorld \"\"" >/dev/null; q "aeroset AeroEntryFx.TestAxis -z" >/dev/null
q "gridhold $h 0" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== spinshot in $(( $(date +%s) - T0 )) s"
