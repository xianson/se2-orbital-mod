#!/usr/bin/env bash
# (RELAUNCHES the game through Steam and moves the player into the Hercules: not while someone is playing; never saves)
# reentry, top to bottom (DevReentry): relaunch; the player seated in the Hercules; a 1000 x 3 km Verdure orbit (an entry
# over the cap); dampeners off (it falls); armed; warped to the entry (the recorder turns warp off at the handover);
# the report polled to the end (2 km up, or at rest); dampeners back on.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
# usage: reentry_e2e.sh [APO_KM=1000] [PERI_KM=3] [CAP=off|m/s] [SHIP name, as gridinfo shows it='SZ1 - Hercules'] [SEAT prefix=SZ1]
APO=${1:-1000}; PERI=${2:-3}; CAP=${3:-off}; SHIP=${4:-SZ1 - Hercules}; SEAT=${5:-SZ1}
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; L=$(LOG); echo "log $L failures $(grep -a -c 'Loading Failed' "$L")"
cmds=(); for i in $(seq 1000000000 1000000300); do cmds+=("gridinfo $i"); done
timeout 120 "$O" "${cmds[@]}" >/dev/null; sleep 3
herc=$(grep -a "gridinfo 10000" "$L" | grep -ao "=> grid [0-9]* '$SHIP' server" | head -1 | grep -o "[0-9]\{10\}")
echo "ship '$SHIP': $herc"
echo "out of any seat: $(q "unseat")"; sleep 4   # (a seated character teleported crashed Havok)
echo "to the ship: $(q "tpgrid $herc 30")"; sleep 5
echo "seat: $(q "seat $SEAT")"; sleep 6
echo "seated: $(q "aeroget OrbitalMod.FrameHost.Seated")"
echo "orbit: $(q "gridorbit $herc Verdure $APO $PERI")"; sleep 5
echo "info: $(q "gridinfo $herc" | cut -c1-260)"
echo "dampeners off: $(q "griddamp $herc off")"
echo "entry cap: $(q "entrycap $CAP")"   # (a test cap under the arrival speed makes the band brake it)
echo "arm: $(q "reentrytest $herc")"
echo "warp: $(q "warp 20")"
for i in $(seq 1 150); do
  sleep 10; r=$(q "reentrytest"); echo "[$((i*10)) s] $(echo "$r" | cut -c1-160)"
  case "$r" in *"[done"*) break;; esac
done
echo "warp off: $(q "warp 1")"; echo "entry cap: $(q "entrycap off")"; echo "dampeners on: $(q "griddamp $herc on")"
echo "REPORT: $(q "reentrytest")" | sed 's/ || /\n   /g'
grep -a "ORBIT-REENTRY\|ARRIVE\|arrival" "$L" | tail -20 | cut -c1-240
