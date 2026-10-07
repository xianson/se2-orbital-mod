#!/usr/bin/env bash
# map_compare.sh - the same map views in a world with colonization sectors and in a sectorless one, side by side.
# RELAUNCHES the game (twice); never saves; world.txt is restored after. Shots: docs/shots-work/cmp_<world>_*.jpg and
# contact sheet cmp_sheet.jpg (rows: campaign, creative).
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; E="$H/eshot.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
OUT="$H/../../docs/shots-work"; rm -f "$OUT"/cmp_*.jpg
cp "$H/world.txt" "$H/world.txt.cmpbak"
run() {   # run <tag> <world> <a Lagrange target>
  printf '%s' "$2" > "$H/world.txt"
  powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
  powershell -NoProfile -ExecutionPolicy Bypass -File "$H/launch-orbital.ps1"; sleep 15
  for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
  sleep 20; echo "[$1] $2: log $(LOG)"
  q "shotui on" >/dev/null
  timeout 90 "$E" "cmp_$1_0hud" 3 "shotui on" >/dev/null 2>&1
  q "map on" >/dev/null; sleep 5
  timeout 90 "$E" "cmp_$1_1map" 3 "shotui on" >/dev/null 2>&1
  echo "  focus star: $(q "focus Delfos")"; sleep 3
  timeout 90 "$E" "cmp_$1_2system" 3 "shotui on" >/dev/null 2>&1
  echo "  focus Verdure: $(q "focus Verdure")"; sleep 3
  timeout 90 "$E" "cmp_$1_3verdure" 3 "shotui on" >/dev/null 2>&1
  echo "  focus Kemik: $(q "focus Kemik")"; sleep 3
  timeout 90 "$E" "cmp_$1_4moon" 3 "shotui on" >/dev/null 2>&1
  echo "  target: $(q "target $3")"; sleep 2
  q "rvtab on" >/dev/null; sleep 3
  timeout 90 "$E" "cmp_$1_5rvtab" 3 "shotui on" >/dev/null 2>&1
  q "rvtab off" >/dev/null; q "map off" >/dev/null; q "target off" >/dev/null; sleep 2
  echo "  sites: $(q "sites" | cut -c1-200)"
}
run a_campaign "Orbital Test Campaign 2" "Vantaris"
run b_creative "Concordia Research Facility 7" "Verdure L4"
mv -f "$H/world.txt.cmpbak" "$H/world.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "$H/montage.ps1" -Pattern "cmp_*.jpg" -Out "$(cygpath -m "$OUT")/cmp_sheet.jpg" -Cols 6 -W 480 | tail -1
echo "== compare done"
