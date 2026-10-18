#!/usr/bin/env bash
# havok_repro.sh - the 2026-10-17 crash, reproduced on purpose: "There are still active bodies in native world N"
# (HavokLargeWorldManager.RemoveCluster). Both crash sessions had a high-speed structural breakup and an Orbital grid
# teleport shortly before. Two runs on one grid, each watched by the cluster audit ([ORBIT-HAVOK], HavokAudit) and the
# engine's own assertion (testlib's bad()):
#  A. a breakup at speed, no teleport: held at 900 m/s, damage on, a hard pull - pieces split off at speed;
#  B. the same with a teleport first: seated, FlyTest (Ctrl+T: the grid put 1000 m over the ground at cruise speed -
#     the dev teleport of the 00:14 crash), then the hard pull; then a second FlyTest while the pieces are still flying.
# One launch, under 5 minutes (RELAUNCHES the game - the Steam launch, its last world, never saved - and closes it after;
# NOLAUNCH=1: the running game). NAME: the grid (default the Tu-134 world's; of several, the first that seats the player -
# SEATSCAN=0 skips that scan). With no seat, B's teleport is 'gridmove' (the Orbital move path) 1000 m out, twice. Overrides released after.
# DO NOT run while the user plays (it relaunches the game).
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
get() { reply "aeroget $1" | sed 's/.*=>  //' | cut -c1-${2:-200}; }
audit() { echo "      $(reply "havok" | sed 's/.*=>  //' | cut -c1-220)"; }
havoklines() { since | grep -a "\[ORBIT-HAVOK\]" | sed 's/.*\[ORBIT-HAVOK\]/[ORBIT-HAVOK]/' | cut -c1-400 | head -${1:-4} | sed 's/^/      /'; }
breakup() {   # breakup <grid>: damage on, held fast, a hard pull; pieces split at speed
    send "aeroset StructureLoads.Damage true" "gridhold $1 ${SPEED:-900}"; hold 4
    send "gridhold $1 0" "gridspin $1 pitch ${PULL:-0.8}"; hold 4
    send "gridspin $1 pitch ${PULL:-0.8}"; hold 3
    since | grep -a "\[AERO-STRUCT\].*\(split\|broke\)" | sed 's/.*\[AERO-STRUCT\] //' | head -4 | sed 's/^/      /'
}
[ -z "$NOLAUNCH" ] && launch
waitloaded
echo "loaded: $(LOGF)"; send "encon off" "havok on"
# (the grid: of those with the name - the Tu-134 world has five, its pieces among them - the first that seats the player:
#  B's FlyTest teleports the SEATED grid; 2026-10-18 the first id was a piece with no seat within 60 m and B never ran)
ids=$(ask findgrids | tr ' ' '\n' | grep -a "^[0-9]*=${NAME:-Tupolev_Tu-134_Big}$" | cut -d= -f1)
g=""; [ "${SEATSCAN:-1}" = 0 ] && ids_scan="" || ids_scan=$ids   # (SEATSCAN=0: no scan - ~9 s a grid)
for c in $ids_scan; do
    send "unseat" "gridhold $c 1"; hold 2; send "tpgrid $c 15"; hold 1
    r=$(reply "seatid $c" | sed 's/.*=>  //' | cut -c1-80); echo "      grid $c: $r"
    send "gridhold $c 0"
    case "$r" in seating*) g=$c; break ;; esac
done
[ -n "$g" ] || g=$(echo "$ids" | head -1)
send "unseat"; hold 2
echo "      grid: $g"
[ -n "$g" ] || { record "grid found" "no grid named ${NAME:-Tupolev_Tu-134_Big}"; summary; exit 1; }
audit
s=$(val OrbitalMod.HavokAudit.Samples)
expect "the cluster audit runs" "HavokAudit.Samples=$s (audit off or not bound: see '[ORBIT-HAVOK] cluster audit off')" test "${s:-0}" -gt 0

echo "== A: breakup at speed, no teleport"
mark
breakup "$g"
hold 10
havoklines; audit
never "A: no cluster mismatch" "\[ORBIT-HAVOK\] cluster [0-9]"
check "A: no Havok assertion, the game alive" "AERO-STRUCT" 5 10

echo "== B: teleport (FlyTest), then a breakup, then another teleport"
send "aeroset StructureLoads.Damage false" "gridspin $g 0 0 0"; hold 2
send "unseat" "gridhold $g 1"; hold 2; send "tpgrid $g 15"; hold 1
st=$(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80); echo "      seat: $st"; hold 1
# (no seat on the grid - the Tu-134 world's five 'Tupolev_Tu-134_Big' grids, 2026-10-18: FlyTest needs the player seated -
#  then the teleport is the Orbital move path itself: gridmove, 1000 m out from the nearest planet, twice)
seated=""; case "$st" in seating*) seated=1 ;; esac
upmove() {   # upmove <grid>: "dx dy dz" (m) - 1000 m away from the nearest planet's centre (status 'planet N ... center=')
    local p; p=$(reply "gridinfo $1" | grep -o 'pos ([^)]*) km' | tr -d 'pos(),km')
    [ -n "$p" ] || return 0   # (the grid gone - 2026-10-18 the 900 m/s hold flew the Tu-134 into the ground: no move)
    grep -a '^planet [0-9]' "$ST" | sed 's/.*center=\([^ ]*\).*/\1/' | tr ',' ' ' | awk -v p="$p" 'BEGIN { split(p, q, " "); gx = q[1] * 1000; gy = q[2] * 1000; gz = q[3] * 1000; best = -1 }
        { dx = gx - $1; dy = gy - $2; dz = gz - $3; d = sqrt(dx * dx + dy * dy + dz * dz); if (best < 0 || d < best) { best = d; ux = dx / d; uy = dy / d; uz = dz / d } }
        END { if (best > 0) printf "%.0f %.0f %.0f", ux * 1000, uy * 1000, uz * 1000 }'
}
mark
if [ -n "$seated" ]; then
    send "gridhold $g 0" "key flytest"; hold 3
    since | grep -a "flytest:" | sed 's/.*\[ORBIT-FRAME\] //' | head -2 | sed 's/^/      /'
else
    send "unseat"; hold 1; mv=$(upmove "$g")
    echo "      no seat: the teleport is 'gridmove $g $mv group' (the Orbital move path, 1000 m out)"
    [ -n "$mv" ] && send "gridhold $g 0" "gridmove $g $mv group"; hold 3
fi
breakup "$g"
# (a second teleport while its pieces are still in flight)
if [ -n "$seated" ]; then send "key flytest"; else [ -n "$mv" ] && send "gridmove $g $mv group"; fi; hold 10
havoklines; audit
never "B: no cluster mismatch" "\[ORBIT-HAVOK\] cluster [0-9]"
check "B: no Havok assertion, the game alive" "$([ -n "$seated" ] && echo flytest: || echo AERO-STRUCT)" 5 15

echo "== release"
send "aeroset StructureLoads.Damage false" "gridspin $g 0 0 0" "gridhold $g 0" "unseat"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
MARK=0; never "whole run: no static refused by a teleport path" "not moved: it is static (MoveGrid\|not moved: it is static (SetPosition"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== havok_repro: $(( $(date +%s)-T0 ))s"; summary
