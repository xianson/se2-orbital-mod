#!/usr/bin/env bash
# havok_repro.sh - the 2026-10-17 crash, reproduced on purpose: "There are still active bodies in native world N"
# (HavokLargeWorldManager.RemoveCluster). Both crash sessions had a high-speed structural breakup and an Orbital grid
# teleport shortly before. Two runs on one grid, each watched by the cluster audit ([ORBIT-HAVOK], HavokAudit) and the
# engine's own assertion (testlib's bad()):
#  A. a breakup at speed, no teleport: held at 900 m/s, damage on, a hard pull - pieces split off at speed;
#  B. the same with a teleport first: seated, FlyTest (Ctrl+T: the grid put 1000 m over the ground at cruise speed -
#     the dev teleport of the 00:14 crash), then the hard pull; then a second FlyTest while the pieces are still flying.
# One launch, under 5 minutes (RELAUNCHES the game - the Steam launch, its last world, never saved - and closes it after;
# NOLAUNCH=1: the running game). NAME: the grid (default the Tu-134 world's). Overrides released after.
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
g=$("$SP/orb.sh" findgrids | grep -a " => " | grep -o "[0-9]\+=${NAME:-Tupolev_Tu-134_Big}" | head -1 | cut -d= -f1)
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
echo "      seat: $(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80)"; hold 1
mark
send "gridhold $g 0" "key flytest"; hold 3
since | grep -a "flytest:" | sed 's/.*\[ORBIT-FRAME\] //' | head -2 | sed 's/^/      /'
breakup "$g"
send "key flytest"; hold 10   # (a second teleport of the seated grid while its pieces are still in flight)
havoklines; audit
never "B: no cluster mismatch" "\[ORBIT-HAVOK\] cluster [0-9]"
check "B: no Havok assertion, the game alive" "flytest:" 5 15

echo "== release"
send "aeroset StructureLoads.Damage false" "gridspin $g 0 0 0" "gridhold $g 0" "unseat"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
MARK=0; never "whole run: no static refused by a teleport path" "not moved: it is static (MoveGrid\|not moved: it is static (SetPosition"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== havok_repro: $(( $(date +%s)-T0 ))s"; summary
