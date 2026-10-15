#!/usr/bin/env bash
# fuzz.sh [seed] [steps] - random play against the invariants. RELAUNCHES the game; never saves. A Blue Fighter on a 300 km
# Verdure orbit, a copy of it beside it, and the SZ1 Hercules (a multi-grid ship) in the same frame; then N random actions -
# warp, getting in and out of a random ship, dampeners, thrust, a ship pushed 2-30 km, a jetpack velocity, waits - each
# followed by the invariants' count ('inv'): every violation is reported with the action that came before it. The seed is
# printed: the same seed replays the same run.
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
SEED=${1:-$RANDOM}; STEPS=${2:-40}; RANDOM=$SEED
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
named() { grep -a "gridinfo 10000" "$(LOG)" | grep -a "=> grid [0-9]* '$1' server" | grep -ao "=> grid [0-9]*" | grep -o "[0-9]\{10\}" | sort -u; }
pick() { local a=("$@"); echo "${a[$((RANDOM % ${#a[@]}))]}"; }

echo "fuzz: seed $SEED, $STEPS steps"
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
. "$(dirname "$0")/launch.sh"; launch
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 20; echo "log $(LOG)"
scan
bf=$(named "Blue Fighter" | head -1); hull=$(named "SZ1 - Hercules" | head -1)
q "encon off" >/dev/null; q "unseat" >/dev/null; sleep 3
q "gridorbit $bf Verdure 300 300 0 0" >/dev/null; sleep 8
before=$(named "Blue Fighter"); q "gridclone $bf 1 3000" >/dev/null; sleep 10; scan
cl=$(comm -13 <(echo "$before") <(named "Blue Fighter") | head -1)
q "gridorbit $hull Verdure 300 300 0 -0.3" >/dev/null; sleep 15
ships=($bf $cl $hull); echo "ships: fighter $bf, copy $cl, Hercules $hull"
q "inv reset" >/dev/null
seated=0; viol=0; trail=()
for n in $(seq 1 $STEPS); do
  r=$((RANDOM % 9)); s=$(pick "${ships[@]}"); act=""
  case $r in
    0) w=$(pick 1 1 10 50); act="warp $w"; q "warp $w" >/dev/null ;;
    1) if [ $seated = 0 ]; then act="seat in $s"; q "tpgrid $s 15" >/dev/null; sleep 4; q "seatid $s" >/dev/null; seated=1; else act="unseat"; q "unseat" >/dev/null; seated=0; fi ;;
    2) d=$(pick on off); act="dampeners $d on $s"; q "griddamp $s $d" >/dev/null ;;
    3) if [ $seated = 1 ]; then ax=$(pick "1 0 0" "-1 0 0" "0 1 0" "0 0 -1" "0 0 1"); act="thrust $ax 2 s"; q "thrust $ax 2" >/dev/null; else act="jetpack $(pick 5 20 60) m/s"; q "player dampeners off" >/dev/null; v=${act#jetpack }; q "player vel ${v% m/s} 0 0" >/dev/null; fi ;;
    4) km=$(pick 2 8 15 30); act="push $s by $km km"; q "gridnudge $s $((km*1000)) 0 0" >/dev/null ;;
    5) act="rendezvous: $s onto the fighter's orbit"; q "gridorbit $s Verdure 300 300 0 -$(pick 0.1 0.3 0.6)" >/dev/null ;;
    *) act="wait" ;;
  esac
  sleep $(pick 3 6 10 20)
  trail+=("$n: $act")
  iv=$(q "inv")
  if ! echo "$iv" | grep -q "inv: none"; then
    viol=$((viol+1)); echo "VIOLATION after step $n ($act): $iv" | cut -c1-400
    q "inv reset" >/dev/null
  fi
done
q "warp 1" >/dev/null; q "unseat" >/dev/null; q "encon on" >/dev/null
echo "trail:"; printf '  %s\n' "${trail[@]}"
echo "== fuzz (seed $SEED): $viol step(s) with violations of $STEPS"
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
