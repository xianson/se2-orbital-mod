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
# The verdict on the audit (2026-10-18, run 1): every world logs [ORBIT-HAVOK] mismatches from its load on (bodies the
# engine itself never tracks - voxel and character bodies), so "no mismatch line" always failed. A phase passes when no
# cluster's unaccounted count grew over what it read before the phase (the bodies a removal would leave are new ones).
# And the 900 m/s hold flew the grid into the ground before it broke: it is levelled and lifted LIFT m (gridup, a move, so
# A waits SETTLE s after it) and held HOLDSECS s; the structure at STRENGTH (1.5, as test_integrate: the default 7 let the
# Tu-134 through the pull whole - 2026-10-18 run 2, "AERO-STRUCT not seen"), 7 again at the release.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
get() { reply "aeroget $1" | sed 's/.*=>  //' | cut -c1-${2:-200}; }
audit() { echo "      $(reply "havok" | sed 's/.*=>  //' | cut -c1-220)"; }
havoklines() { since | grep -a "\[ORBIT-HAVOK\]" | sed 's/.*\[ORBIT-HAVOK\]/[ORBIT-HAVOK]/' | cut -c1-400 | head -${1:-4} | sed 's/^/      /'; }
# havokbase: each cluster's last "K unaccounted" in the log up to the mark. havokgrew NAME: PASS unless a cluster known
# before ends the phase (its last line since the mark) more than max(5, 10 %) above that - the counts jitter by a few
# as voxels stream (run 1: 318 -> 323 -> 320 with nothing removed). A cluster new since the mark (a new physics world: a
# grid flew into a fresh region, its voxels unaccounted from the start) is listed, not judged - keyed by index AND world
# address (run 3: "cluster 9 31 -> 332" was index 9 reused for a new world elsewhere).
havokcounts() { grep -a "\[ORBIT-HAVOK\] cluster [0-9]" | sed -n 's/.*\[ORBIT-HAVOK\] cluster \([0-9]*\) world \(0x[0-9A-Fa-f]*\): .* - \([0-9]*\) unaccounted.*/\1@\2 \3/p'; }   # (index@world: an index is reused for a new world)
havokbase() { BASE=$(head -n "$MARK" "$(log)" | havokcounts | awk '{ k[$1] = $2 } END { for (c in k) print c, k[c] }' | tr '\n' ';'); }
havokgrew() {
    local r grew fresh
    r=$(since | havokcounts | awk -v base="$BASE" 'BEGIN { n = split(base, b, ";"); for (i = 1; i <= n; i++) { split(b[i], f, " "); if (f[1] != "") k[f[1]] = f[2] } }
        { last[$1] = $2 }
        END { for (c in last) { if (!(c in k)) printf "N cluster %s: %d; ", c, last[c]
                                else { t = k[c] * 0.1; if (t < 5) t = 5; if (last[c] > k[c] + t) printf "G cluster %s %d -> %d; ", c, k[c], last[c] } } }')
    grew=$(echo "$r" | tr ';' '\n' | sed -n 's/^ *G //p' | tr '\n' ';'); fresh=$(echo "$r" | tr ';' '\n' | sed -n 's/^ *N //p' | tr '\n' ';')
    echo "      mismatch lines since the mark: $(since | grep -ac "\[ORBIT-HAVOK\] cluster [0-9]"), clusters known before: $(echo "$BASE" | tr ';' '\n' | grep -c .)${fresh:+; new clusters (not judged): $fresh}"
    record "$1" "${grew:+unaccounted bodies grew: $grew}"
}
breakup() {   # breakup <grid>: damage on, held fast, a hard pull; pieces split at speed
    send "aeroset StructureLoads.Damage true" "aeroset ShipStructure.Strength ${STRENGTH:-1.5}" "gridhold $1 ${SPEED:-900}"; hold ${HOLDSECS:-2}
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

echo "== A: breakup at speed, no teleport (levelled, lifted ${LIFT:-2000} m first, ${SETTLE:-6} s before)"
send "gridlevel $g" "gridup $g ${LIFT:-2000}" "gridhold $g 1"; hold ${SETTLE:-6}
# (seated for the breakup: the structure is evaluated for the grid the player flies - run 2, unseated, never broke. Lifted
#  first, then walked up to: a seated character is never teleported, the seat's joint)
send "tpgrid $g 15"; hold 1; echo "      seat for A: $(reply "seatid $g" | sed 's/.*=>  //' | cut -c1-80)"; hold 2
mark; havokbase
breakup "$g"
hold 10
havoklines; audit
havokgrew "A: no cluster's unaccounted bodies grew (vs before the breakup)"
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
mark; havokbase
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
havokgrew "B: no cluster's unaccounted bodies grew (vs before the teleport)"
check "B: no Havok assertion, the game alive" "$([ -n "$seated" ] && echo flytest: || echo AERO-STRUCT)" 5 15

echo "== release"
send "aeroset StructureLoads.Damage false" "aeroset ShipStructure.Strength 7" "gridspin $g 0 0 0" "gridhold $g 0" "unseat"
MARK=0; never "no mod fault" "\[ORBIT-FAULT\]\|\[AERO-FAULT\]\|evaluation failed"
MARK=0; never "whole run: no static refused by a teleport path" "not moved: it is static (MoveGrid\|not moved: it is static (SetPosition"
[ -z "$NOLAUNCH" ] && powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== havok_repro: $(( $(date +%s)-T0 ))s"; summary
