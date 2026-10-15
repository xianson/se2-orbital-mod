#!/usr/bin/env bash
# hercules.sh - the player's own multi-grid ship, the SZ1 Hercules (a hull and six grids on hinges and rotors, its cockpit on
# a sub-grid; a scenario ship in a managed world area). RELAUNCHES the game; never saves. Every case ends requiring no
# invariant violations ('inv': Invariants - ship-together, anchor-alive, past-edge, warp-state, on-rails, seated-frame,
# prediction).
#  H1 the Hercules joins a Blue Fighter's frame (gridorbit 2 km behind it): every grid of it in that frame
#  H2 seated in the fighter (a member: the Hercules anchors), dampeners off, warp x10 for 90 s: the plot's prediction holds
#  H3 in the Hercules' own cockpit (on a sub-grid), out and back in three times: by the ship, in its frame, the HUD check
#  H4 seated in the Hercules (the frame's anchor), it is pushed 25 km: you stay seated, unsplit, the HUD on your ship's frame,
#     all its grids together, the fighter split off, no grid re-registered under a new id
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
named() { grep -a "gridinfo 10000" "$(LOG)" | grep -a "=> grid [0-9]* '$1' server" | grep -ao "=> grid [0-9]*" | grep -o "[0-9]\{10\}" | sort -u; }
P=0; F=0; FAILS=""
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); FAILS="$FAILS|$1"; echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3"; }
inv0() { local s; s=$(q "inv"); ok "$1: no invariant violated" $(echo "$s" | grep -q "inv: none" && echo 1 || echo 0) "$s"; q "inv reset" >/dev/null; }
frameno() { q "frameof $1" | grep -o "#[0-9]*" | head -1; }
seat() { q "tpgrid $1 15" >/dev/null; sleep 4; q "seatid $1" >/dev/null; sleep 6; }
# (the Hercules' cockpit is on a sub-grid: each of its grids tried until one has a seat within reach)
seatherc() { local g r; for g in "${herc[@]}" "${subs[@]}"; do q "tpgrid $g 15" >/dev/null; sleep 4; r=$(q "seatid $g"); if echo "$r" | grep -q "seating in"; then sleep 6; cockpit=$g; return 0; fi; done; return 1; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
. "$(dirname "$0")/launch.sh"; launch
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 20; echo "log $(LOG)"
scan
bf=$(named "Blue Fighter" | head -1)
herc=($(named "SZ1 - Hercules")); subs=($(named "Grid") $(named "Ramp Lights"))
hull=${herc[0]}; echo "Blue Fighter $bf | Hercules grids: ${herc[*]} (+ sub-grids ${subs[*]})"
q "encon off" >/dev/null; q "unseat" >/dev/null; sleep 3; q "inv reset" >/dev/null
# (the Hercules on its orbit first, the fighter after: the later frame hosts the merge - the fighter stays the anchor and the
#  Hercules is a member, as in play)
echo "hercules: $(q "gridorbit $hull Verdure 300 300 0 -0.3")"; sleep 8

# H1 - into a Blue Fighter's frame
echo "orbit: $(q "gridorbit $bf Verdure 300 300 0 0")"; sleep 15
echo "  anchor: $(q "frameof $bf")"
fb=$(frameno $bf); fh=$(frameno $hull); echo "  frames: fighter $fb, hull $fh"
together=1; for g in "${herc[@]}"; do [ "$(frameno $g)" = "$fh" ] || together=0; done
ok "H1 the Hercules in the fighter's frame" $([ -n "$fh" ] && [ "$fh" = "$fb" ] && echo 1 || echo 0) "hull $fh, fighter $fb"
ok "H1 ... every grid of it in the same frame" $together
inv0 "H1"

# H2 - the plot's prediction in warp: seated in the fighter (a member - the Hercules anchors), dampeners off, x10 for 90 s
seat $bf
echo "  seated: $(q "exitcheck $bf" | cut -c1-160)"
q "griddamp $bf off" >/dev/null; sleep 3
q "warp 10" >/dev/null; sleep 90; q "warp 1" >/dev/null; sleep 4
pd=$(grep -a "ORBIT-INV\] VIOLATION prediction" "$(LOG)" | tail -1 | cut -c60-240)
pdiag=$(grep -a "prediction after" "$(LOG)" | tail -1 | cut -c60-240)
ok "H2 in warp the plot's prediction held (no prediction violation)" $([ -z "$pd" ] && echo 1 || echo 0) "${pd:-$pdiag}"
ok "H2 ... the prediction was judged (a 30 s check ran)" $(q "inv" | grep -q "predictions judged [1-9]" && echo 1 || echo 0) "$(q "inv" | grep -o "predictions judged [0-9]*")"
inv0 "H2"
q "unseat" >/dev/null; sleep 4

# H3 - in its own cockpit (on a sub-grid), out and back in three times
seatherc; echo "  cockpit on grid ${cockpit:-none}"
ok "H3 seated in the Hercules' cockpit" $([ -n "$cockpit" ] && q "exitcheck $hull" | grep -q "seated True" && echo 1 || echo 0)
for k in 1 2 3; do
  q "unseat" >/dev/null; sleep 5
  ec=$(q "exitcheck $hull"); d=$(echo "$ec" | grep -o "server d [0-9.]*" | grep -o "[0-9.]*$")
  me=$(echo "$ec" | grep -o "you #[0-9a-z]*" | sed 's/.*#//'); sh=$(echo "$ec" | grep -o "ship #[0-9a-z]*" | sed 's/.*#//')
  ok "H3.$k out: by the ship (${d:-?} m from its hull), in its frame (#$me / #$sh)" $(awk -v d="${d:-99999}" 'BEGIN{print (d < 150) ? 1 : 0}' | grep -q 1 && [ "$me" = "$sh" ] && [ "$me" != "none" ] && echo 1 || echo 0) "$ec"
  q "seatid $cockpit" >/dev/null; sleep 6
done
ok "H3 the HUD check, seated again" $(q "hudcheck" | grep -q "hudcheck PASS" && echo 1 || echo 0)
inv0 "H3"

# H4 - seated in the Hercules (the frame's anchor), it is pushed 25 km: you stay with your ship; the fighter splits off
fh=$(frameno $hull); M=$(date +%H:%M:%S)
q "gridnudge $hull 25000 0 0" >/dev/null; sleep 10
lg=$(awk -v m="$M" 'substr($2,1,8) >= m' "$(LOG)")
ec=$(q "exitcheck $hull"); echo "  after: $ec" | cut -c1-200
together=1; for g in "${herc[@]}"; do [ "$(frameno $g)" = "$(frameno $hull)" ] || together=0; done
ok "H4 still seated in it" $(echo "$ec" | grep -q "seated True" && echo 1 || echo 0)
ok "H4 you were not split off your ship" $(echo "$lg" | grep -aq "SPLIT player" && echo 0 || echo 1) "$(echo "$lg" | grep -a "SPLIT player" | head -1 | cut -c40-200)"
ok "H4 the HUD's frame is your ship's" $([ "$(echo "$ec" | grep -o "hud #[0-9a-z]*" | sed 's/.*#//')" = "$(echo "$ec" | grep -o "ship #[0-9a-z]*" | sed 's/.*#//')" ] && echo 1 || echo 0)
ok "H4 every grid of the Hercules still together" $together
ok "H4 the fighter split off (it is 25 km from your ship now)" $(echo "$lg" | grep -aq "SPLIT grid $bf" && echo 1 || echo 0)
ok "H4 no grid of the Hercules registered anew" $(echo "$lg" | grep -aq "new grid [0-9]* 'SZ1 - Hercules'" && echo 0 || echo 1)
inv0 "H4"
q "unseat" >/dev/null; q "encon on" >/dev/null
echo "== hercules: $P passed, $F failed"
echo "failed:$FAILS" | tr '|' '\n' | sed 's/^/  /' | head -20
echo "exceptions: $(grep -ac 'Exception occurred' "$(LOG)")"
