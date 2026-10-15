#!/usr/bin/env bash
# bakedcheck.sh - the baked flight field end to end, one launch, 5 minutes or less (RELAUNCHES unless NOLAUNCH=1):
#  K0 cold start: how long the field takes to bake all its directions, the on-demand bakes, the worst frame
#  K1 plasma ramp 200 -> 900 -> 200 m/s (fixed air): no pops, no flicker, no re-layouts, spot made once at most
#  K2 vapour at Mach ~0.95, PITCHING 1 rad/s through a world-fixed flow: the sheets on, no re-layouts; shots
#  K3 plasma at 900 m/s pitching 1 rad/s: no re-layouts, no spot remade; shots
# The Hercules held 2 km up in Verdure's air; every override released at the end (the ship then falls - unsaved).
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
shot() { timeout 30 "$O" --eshot 0 "fxcheck" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }
num() { echo "$1" | grep -o "$2 [0-9.]*" | head -1 | awk '{print $NF}'; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-420; }
setv() { "$O" "aeroset AeroEntryFx.TestSpeed $1" >/dev/null 2>&1 & }

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
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null
q "cam grid $h 0.05 30 15" >/dev/null
q "aeroset AeroEntryFx.TestAxis 0,-0.0872,-0.9962" >/dev/null

# K0 - cold start: the field made and baking at entry speed for 10 s - the worst frame, the bakes
q "fxcheck reset" >/dev/null; setv 900; sleep 10
k0=$(q "fxcheck reset")
ok "K0 cold start: frames under 8 ms while baking" $(awk -v m="$(num "$k0" max)" 'BEGIN{print (m < 8) ? 1 : 0}') "$(echo "$k0" | grep -o 'bake ms.*baked-ms avg [0-9.]* max [0-9.]*')"
setv 200; sleep 3; q "fxcheck reset" >/dev/null

# K1 - the ramp
for i in $(seq 0 40); do setv $(( 200 + 700 * i / 40 )); sleep 0.25; done; sleep 2
for i in $(seq 0 40); do setv $(( 900 - 700 * i / 40 )); sleep 0.25; done; sleep 3
k1=$(q "fxcheck reset")
ok "K1 ramp 200-900-200: no pops, no flicker, no re-layouts, one spot (the main grid), frames under 8 ms" $(awk -v p="$(num "$k1" pops)" -v f="$(num "$k1" flicker)" -v r="$(num "$k1" relayouts)" -v s="$(num "$k1" spots-made)" -v m="$(num "$k1" max)" 'BEGIN{print (p == 0 && f == 0 && r == 0 && s <= 1 && m < 8) ? 1 : 0}') "$(echo "$k1" | sed 's/| trail.*//')"

# K2 - transonic vapour, pitching through a world-fixed flow
q "aeroset AeroEntryFx.TestWorld freeze" >/dev/null; setv 320; sleep 4
q "gridspin $h pitch 1" >/dev/null; sleep 1; q "fxcheck reset" >/dev/null; sleep 6
k2=$(q "fxcheck reset"); st=$(q 'aeroget AeroEntryFx.FlightStatus' | sed 's/.*FlightStatus=//')
echo "  K2 status: $(echo "$st" | cut -c1-200)"
echo "  K2 shot: $(shot)"; sleep 0.7; echo "  K2 shot: $(shot)"
ok "K2 vapour while pitching: sheets on, no re-layouts" $(awk -v e="$(echo "$st" | grep -o 'edge [0-9.]*' | awk '{print $2}')" -v r="$(num "$k2" relayouts)" 'BEGIN{print (e > 0.2 && r == 0) ? 1 : 0}') "$(echo "$k2" | sed 's/| trail.*//')"

# K3 - plasma, pitching
setv 900; sleep 4; q "fxcheck reset" >/dev/null; sleep 6
k3=$(q "fxcheck reset")
echo "  K3 shot: $(shot)"; sleep 0.7; echo "  K3 shot: $(shot)"
ok "K3 plasma while pitching: no re-layouts, no spot remade" $(awk -v r="$(num "$k3" relayouts)" -v s="$(num "$k3" spots-made)" 'BEGIN{print (r == 0 && s == 0) ? 1 : 0}') "$(echo "$k3" | sed 's/| trail.*//')"

q "gridspin $h 0 0 0" >/dev/null; q "cam off" >/dev/null
setv 0; sleep 1; q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; q "aeroset AeroEntryFx.TestWorld \"\"" >/dev/null; q "aeroset AeroEntryFx.TestAxis -z" >/dev/null
q "gridhold $h 0" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== bakedcheck: $P passed, $F failed in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -6
