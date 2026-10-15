#!/usr/bin/env bash
# recsuper.sh [out.mp4] - a REAL-TIME recording of the Hercules going supersonic, PUPPETED (5 minutes or less; RELAUNCHES
# the game unless NOLAUNCH=1: not while someone plays). The ship is put in Verdure's air and held still (gridhold 0.5);
# the air is the aero mod's test airflow (AeroEntryFx.TestSpeed along TestAxis in the grid's own frame - a set angle of
# attack). The speed ramps TestFrom -> TestTo m/s over DUR seconds seated (the hold needs a pilot), a scripted camera orbiting it; the game window is captured
# live with ffmpeg (gdigrab of the desktop region the game fills - its window by title captures blank, DX12 - 30 fps) - true speed, unlike engine screenshots (~8 s each).
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
OUTMP4=${1:-$TEMP/supersonic.mp4}
DUR=${DUR:-14}; V0=${TestFrom:-300}; V1=${TestTo:-700}; AX=${AX:-0,-0.0872,-0.9962}; DIST=${DIST:-0.06}
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }

if [ "$NOLAUNCH" != 1 ]; then
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s"
fi
G=$(q "findgrids"); h=$(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules\$" | head -1 | cut -d= -f1); echo "Hercules $h"
q "encon off" >/dev/null; q "unseat" >/dev/null
# seated: the game's own third-person chase camera keeps the ship in frame (a scripted camera did not hold under live capture)
seated() { q "exitcheck $1" | grep -q "seated True"; }
for s in $(echo "$G" | tr ' ' '
' | grep "=SZ1_-_Hercules\$" | cut -d= -f1); do
  q "tpgrid $s 15" >/dev/null; sleep 1; q "seatid $s" | grep -q "seating in" && until_ 10 "seated $s" && break
done
q "gridlaunch $h 2 Verdure 150" >/dev/null; sleep 3; q "gridlevel $h" >/dev/null; sleep 2; q "gridhold $h 0.5" >/dev/null   # (levelled: top away from the planet, the world the right way up)
q "aeroset AeroEntryFx.TestAxis $AX" >/dev/null; q "aeroset AeroEntryFx.TestSpeed $V0" >/dev/null; q "cam grid $h $DIST ${BEAR:-30} ${ELEV:-12}" >/dev/null; sleep 6
# PRE: commands (';'-separated, harness syntax) run before the capture - e.g. "aeroset FlightField.TipLead 0"
if [ -n "$PRE" ]; then IFS=';' read -ra pre <<< "$PRE"; for c in "${pre[@]}"; do q "$c" >/dev/null; done; sleep 2; fi
echo "status: $(q 'aeroget AeroMod.AeroEntryFx.FlightStatus' | cut -c1-160)"
ffmpeg -hide_banner -loglevel error -y -f gdigrab -framerate 30 -offset_x ${CAPX:-0} -offset_y ${CAPY:-0} -video_size ${CAPSIZE:-2560x1440} -t $DUR -i desktop \
  -vf "scale=1920:-2" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart "$OUTMP4" &
FF=$!
# YAW (rad/s): the ship yawing about its own up axis through the take; the camera's bearing (measured from the nose)
# swung against it so the camera stays put in the world and the ship is seen turning
if [ -n "$YAW" ]; then q "gridspin $h yaw $YAW" >/dev/null; SWING=$(awk -v w=$YAW -v d=$DUR 'BEGIN{printf "%.1f", -w*d*57.2958}'); fi
steps=$(( DUR * 2 ))
for i in $(seq 0 $((steps-1))); do
  v=$(awk -v i=$i -v n=$steps -v a=$V0 -v b=$V1 'BEGIN{k=i/(n-1); printf "%.0f", a + (b-a)*k}')
  b=$(awk -v i=$i -v n=$steps -v s=${SWING:-0} -v b0=${BEAR:-30} 'BEGIN{printf "%.1f", b0 + s*i/(n-1)}')   # (SWING deg of orbit; 0: a fixed view, re-aimed each step)
  "$O" "aeroset AeroEntryFx.TestSpeed $v" "cam grid $h $DIST $b ${ELEV:-12}" >/dev/null 2>&1 &
  sleep 0.5
done
wait $FF
q "aeroset AeroEntryFx.TestSpeed 0" >/dev/null; [ -n "$YAW" ] && q "gridspin $h 0 0 0" >/dev/null; q "gridhold $h 0" >/dev/null; q "encon on" >/dev/null
ls -la "$OUTMP4"; echo "== recsuper in $(( $(date +%s) - T0 )) s; exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
