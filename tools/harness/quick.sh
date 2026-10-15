#!/usr/bin/env bash
# quick.sh [seed] - the in-game check, 5 minutes or less end to end (one launch; waits poll the condition, never sleep blind).
# The invariant checker (Server/Invariants.cs) runs every second throughout; the run ends requiring it saw nothing.
#  Q1 the SZ1 Hercules (a multi-grid ship) and a Blue Fighter in one frame: all of the Hercules together
#  Q2 seated in the fighter (a member), dampeners off, warp x10: the plot's prediction judged and held
#  Q3 in the Hercules' own cockpit: the HUD check; out (by the ship, in its frame) and back in
#  Q4 seated in the Hercules, it is pushed 25 km: you stay with it (no split of yours, the HUD on its frame), its grids
#     together, the fighter split off
#  Q5 random play: N steps (warp, seats, dampeners, thrust, pushes), 2 s apart
#  Q6 on foot, jetpack dampeners on (holding station): your orbit shown, no drift plot
#  Q7 coming down through a planet's air: no orbit graph
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
SEED=${1:-$RANDOM}; RANDOM=$SEED; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }   # until_ <tries x 0.5 s> <condition>
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-260; }
frameno() { q "frameof $1" | grep -o "#[0-9]*" | head -1; }
seated() { q "exitcheck $1" | grep -q "seated True"; }
sit() { q "tpgrid $1 15" >/dev/null; until_ 10 "q 'seatid $1' | grep -q 'seating in'" && until_ 12 "seated $1"; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
G=$(q "findgrids")
id() { echo "$G" | tr ' ' '\n' | grep "=$1\$" | head -1 | cut -d= -f1; }
bf=$(id Blue_Fighter); herc=($(echo "$G" | tr ' ' '\n' | grep "=SZ1_-_Hercules$" | cut -d= -f1)); hull=${herc[0]}
subs=($(echo "$G" | tr ' ' '\n' | grep "=Grid$\|=Ramp_Lights$" | cut -d= -f1))
q "encon off" >/dev/null; q "unseat" >/dev/null; q "inv reset" >/dev/null

# Q1 - the Hercules, then the fighter (the later frame hosts: the fighter a member, the Hercules the anchor)
q "gridorbit $hull Verdure 300 300 0 -0.3" >/dev/null; until_ 20 "[ -n \"\$(frameno $hull)\" ]"
q "gridorbit $bf Verdure 300 300 0 0" >/dev/null
until_ 20 "[ \"\$(frameno $bf)\" = \"\$(frameno $hull)\" ]"
fh=$(frameno $hull); together=1; for g in "${herc[@]}"; do [ "$(frameno $g)" = "$fh" ] || together=0; done
ok "Q1 the Hercules and the fighter in one frame ($fh)" $([ -n "$fh" ] && [ "$(frameno $bf)" = "$fh" ] && echo 1 || echo 0)
ok "Q1 ... every grid of the Hercules in it" $together

# Q2 - the prediction in warp, seated in the fighter
sit $bf; q "griddamp $bf off" >/dev/null; j0=$(q "inv" | grep -o "judged [0-9]*" | grep -o "[0-9]*")
q "warp 10" >/dev/null; until_ 15 "[ \"\$(q 'inv' | grep -o 'judged [0-9]*' | grep -o '[0-9]*')\" -ge $(( ${j0:-0} + 2 )) ]"; q "warp 1" >/dev/null
iv=$(q "inv")
ok "Q2 the prediction judged in warp ($(echo "$iv" | grep -o "judged [0-9]*"))" $(echo "$iv" | grep -o "judged [0-9]*" | grep -o "[0-9]*" | awk -v j="${j0:-0}" '{print ($1 >= j + 2) ? 1 : 0}')
ok "Q2 ... and held (no prediction violation)" $(echo "$iv" | grep -q "prediction " && echo 0 || echo 1) "$iv"
q "unseat" >/dev/null; until_ 10 "! seated $bf"

# Q3 - the Hercules' cockpit (on one of its grids)
cockpit=""; for g in "${herc[@]}" "${subs[@]}"; do q "tpgrid $g 15" >/dev/null; sleep 1; if q "seatid $g" | grep -q "seating in"; then cockpit=$g; break; fi; done
until_ 12 "seated $hull"
hc=$(q "hudcheck")
ok "Q3 seated in the Hercules' cockpit (grid ${cockpit:-none}): the HUD check" $(echo "$hc" | grep -q "hudcheck PASS" && echo 1 || echo 0) "$(echo "$hc" | grep -o "FAIL [^|]*" | head -3 | tr '\n' ' ')"
q "unseat" >/dev/null; until_ 10 "! seated $hull"
ec=$(q "exitcheck $hull"); d=$(echo "$ec" | grep -o "server d [0-9.]*" | grep -o "[0-9.]*$")
me=$(echo "$ec" | grep -o "you #[0-9a-z]*" | sed 's/.*#//'); sh=$(echo "$ec" | grep -o "ship #[0-9a-z]*" | sed 's/.*#//')
ok "Q3 out: by the ship (${d:-?} m), in its frame (#$me / #$sh)" $(awk -v d="${d:-99999}" 'BEGIN{exit !(d < 150)}' && [ "$me" = "$sh" ] && [ "$me" != "none" ] && echo 1 || echo 0)
q "seatid $cockpit" >/dev/null; until_ 12 "seated $hull"

# Q4 - pushed 25 km with you in it
M=$(date +%H:%M:%S); q "gridnudge $hull 25000 0 0" >/dev/null
until_ 20 "awk -v m=$M 'substr(\$2,1,8) >= m' \"\$(LOG)\" | grep -aq 'SPLIT grid $bf'"
lg=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)"); ec=$(q "exitcheck $hull")
together=1; nf=$(frameno $hull); for g in "${herc[@]}"; do [ "$(frameno $g)" = "$nf" ] || together=0; done
ok "Q4 still seated, not split off your ship" $(echo "$ec" | grep -q "seated True" && ! echo "$lg" | grep -aq "SPLIT player" && echo 1 || echo 0) "$(echo "$lg" | grep -a "SPLIT player" | head -1 | cut -c40-160)"
ok "Q4 the HUD on your ship's frame" $([ "$(echo "$ec" | grep -o "hud #[0-9a-z]*" | sed 's/.*#//')" = "$(echo "$ec" | grep -o "ship #[0-9a-z]*" | sed 's/.*#//')" ] && echo 1 || echo 0)
ok "Q4 the Hercules together, the fighter split off" $([ $together = 1 ] && echo "$lg" | grep -aq "SPLIT grid $bf" && echo 1 || echo 0)

# Q6 - on foot beside your ship, jetpack dampeners on (holding station): your orbit shown, no drift plot
q "unseat" >/dev/null; until_ 10 "! seated $hull"; q "player dampeners on" >/dev/null; sleep 2
hc=$(q "hudcheck")
ok "Q6 holding station on foot: the HUD check (your orbit, no drift plot)" $(echo "$hc" | grep -q "hudcheck PASS" && echo 1 || echo 0) "$(echo "$hc" | grep -o "FAIL [^|]*" | head -3 | tr '
' ' ')"
q "seatid $cockpit" >/dev/null; until_ 12 "seated $hull"

# Q7 - coming down through the air (seated in the fighter, 8 km up in Verdure's air): no orbit graph
q "unseat" >/dev/null; until_ 10 "! seated $hull"; sit $bf; q "griddamp $bf off" >/dev/null
q "gridlaunch $bf 8 Verdure 150" >/dev/null
until_ 16 "q 'exitcheck $bf' | grep -q 'graph hidden True'"
ec=$(q "exitcheck $bf")
ok "Q7 coming down through the air: no orbit graph" $(echo "$ec" | grep -q "graph hidden True" && echo 1 || echo 0) "$(echo "$ec" | grep -o "graph hidden [A-Za-z]*")"
q "unseat" >/dev/null

# Q5 - random play
STEPS=${STEPS:-10}; ships=($bf $hull); seat=1; trail=""
for n in $(seq 1 $STEPS); do
  s=${ships[$((RANDOM % 2))]}
  case $((RANDOM % 6)) in
    0) w=$((RANDOM % 2 ? 1 : 10)); a="warp $w"; q "warp $w" >/dev/null ;;
    1) if [ $seat = 1 ]; then a="unseat"; q "unseat" >/dev/null; seat=0; else a="seat $s"; sit $s; seat=1; fi ;;
    2) d=$((RANDOM % 2)); a="damp $s $d"; q "griddamp $s $([ $d = 1 ] && echo on || echo off)" >/dev/null ;;
    3) a="thrust"; q "thrust 0 0 -1 1" >/dev/null ;;
    4) km=$((2 + RANDOM % 25)); a="push $s ${km}km"; q "gridnudge $s $((km*1000)) 0 0" >/dev/null ;;
    *) a="wait" ;;
  esac
  sleep 2; trail="$trail $n:$a"
done
q "warp 1" >/dev/null
iv=$(q "inv")
ok "Q5 random play ($STEPS steps, seed $SEED): no invariant violated" $(echo "$iv" | grep -q "inv: none" && echo 1 || echo 0) "$iv | $trail"
q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== quick: $P passed, $F failed in $(( $(date +%s) - T0 )) s"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -12
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
