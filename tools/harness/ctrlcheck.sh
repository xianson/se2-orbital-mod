#!/usr/bin/env bash
# ctrlcheck.sh - the control audit's fixes in game (aero ThrustTorque probe, [AERO-THRUST] lines), one launch, 5 minutes or
# less (RELAUNCHES unless NOLAUNCH=1):
#  H1 a ship hovering in gravity: the thrust the mod reads includes the gravity hold (it read ~0 - the game cancels gravity
#     in place); mode damp (unpiloted), flames lit
#  D1 client against server, seated in the Hercules doing thrust and yaw manoeuvres, flames ON then OFF: the client copy's
#     position against the server's (both logged with their velocities and times) - do the flames' overrides on the client
#     copy (forced thrust there) pull its prediction off?
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
G=$(q "findgrids"); echo "grids: $(echo "$G" | cut -c1-300)"
h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1)
q "encon off" >/dev/null; q "unseat" >/dev/null

# H1 - the Hercules unpiloted in Verdure's gravity, dampeners on, released: the thrust it reads
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; q "griddamp $h on" >/dev/null; sleep 1
q "aeroset ThrustTorque.Probe true" >/dev/null; q "aeroset ThrustTorque.ProbeHz 2" >/dev/null
mark hover-unpiloted; sleep 6
q "aeroset ThrustTorque.Probe false" >/dev/null

# D1 - seated, manoeuvring in space, flames on then off (SKIPD=1: not)
if [ "$SKIPD" != 1 ]; then
for s in $(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 400000" >/dev/null; sleep 3; q "griddamp $h on" >/dev/null; sleep 2
q "aeroset ThrustTorque.ProbeHz 10" >/dev/null
for fl in true false; do
  q "aeroset ThrustTorque.FlamesEnabled $fl" >/dev/null; sleep 1
  q "aeroset ThrustTorque.Probe true" >/dev/null; mark "flames-$fl"
  q "thrust 0 0 -1 3" >/dev/null; sleep 3.5; q "thrust 1 0 0 2" >/dev/null; sleep 2.5; q "thrust 0 0 1 3" >/dev/null; sleep 4
  q "aeroset ThrustTorque.Probe false" >/dev/null; mark "end-$fl"
done
fi
q "aeroset ThrustTorque.FlamesEnabled true" >/dev/null; q "aeroset ThrustTorque.ProbeHz 1" >/dev/null
q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== ctrlcheck in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
echo "LOG $(LOG)"
