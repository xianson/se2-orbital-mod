#!/usr/bin/env bash
# test_soak.sh [steps] [seed] — safety soak: a long random sequence of real harness actions (orbits of every
# body at random heights and inclinations, warp, the map and its tabs, targets, nodes, dampeners, rider mode,
# grid launches), then checks: no mod fault ([ORBIT-FAULT]), no Havok migration assertion, no fatal crash,
# the game still running. Reports memory growth and the mod's warnings. The seed replays a run exactly.
source "$(dirname "$0")/testlib.sh"
STEPS=${1:-80}; SEED=${2:-$RANDOM}; RANDOM=$SEED
echo "== soak: $STEPS steps, seed $SEED"
echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15
mark; START=$MARK
mem() { tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" //FO CSV //NH 2>/dev/null | tail -1 | awk -F'","' '{gsub(/[^0-9]/,"",$5); print $5}'; }
m0=$(mem)
pick() { local a=("$@"); echo "${a[$((RANDOM % ${#a[@]}))]}"; }
BODIES=(Verdure Verdure Kemik Palatine Caligo)
TARGETS=(off Oblivara Kemik Palatine Zarkon Caligo Nadirae)
mkdir -p /tmp/orbital_soak; SEQ="/tmp/orbital_soak/soak_$SEED.txt"; : > "$SEQ"
for i in $(seq 1 "$STEPS"); do
    alive || { echo "game died at step $i"; break; }
    case $((RANDOM % 14)) in
        0|1|2) b=$(pick "${BODIES[@]}"); pe=$((15 + RANDOM % 300)); ap=$((pe + RANDOM % 300)); inc=$((RANDOM % 40)); ph=$((RANDOM % 360))
               cmd="orbit $b $ap $pe $inc $ph" ;;
        3) cmd="warp $(pick 1 1 5 10 25)" ;;
        4) cmd="map $(pick on off)" ;;
        5) cmd="mapcam $((RANDOM % 360)) $((5 + RANDOM % 80))" ;;
        6) cmd="rvtab $(pick on off)" ;;
        7) cmd="target $(pick "${TARGETS[@]}")" ;;
        8) cmd="node add $((1 + RANDOM % 30)) $((RANDOM % 400 - 200)) $((RANDOM % 100 - 50)) $((RANDOM % 100 - 50))" ;;
        9) cmd="node clear" ;;
        10) cmd="player dampeners $(pick on off)" ;;
        11) cmd="devrider $(pick 'on 2' off)" ;;
        12) cmd="gridlaunch $(pick 1000000031 1000000033) $((20 + RANDOM % 300))" ;;
        13) cmd="ringrocks" ;;
    esac
    echo "$i $cmd" >> "$SEQ"
    send "$cmd"; hold $((2 + RANDOM % 8)) || { echo "game died after step $i: $cmd"; break; }
done
send "warp 1"; hold 10
m1=$(mem)
MARK=$START
never "soak: no mod fault" "\[ORBIT-FAULT\]"
never "soak: no Havok migration assertion" "not migrated"
never "soak: no fatal crash" "Crash Handler\]: Fatal"
expect "soak: game still running" "died (sequence in $SEQ)" alive
echo "   memory: ${m0:-?} K -> ${m1:-?} K"
echo "   mod warnings:"; since | grep -a "Warning: \[ORBIT" | cut -c60-200 | sort | uniq -c | sort -rn | head -15
echo "   sequence: $SEQ"
summary
