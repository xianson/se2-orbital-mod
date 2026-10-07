#!/usr/bin/env bash
# rv_cases.sh - rendezvous from every seat, in game: what the HUD draws (hudcheck: card vs truth, plot chosen, target
# line) and an engine screenshot of each. RELAUNCHES the game; never saves. A Blue Fighter on a 300 km Verdure orbit
# (the frame's anchor), a clone of it 3 km off; a sector site round Verdure as the target, and a planet.
#  R1 anchor's pilot, no target (the disc)        R2 anchor's pilot, a site targeted (the target plot)
#  R3 anchor's pilot, a planet targeted (disc)    R4 pilot of the clone 3 km off, no target (the anchor plot)
#  R5 the clone, a site targeted (target plot)    R6 on foot by the clone, a site targeted (card only)
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; OUT="${OUT:-$APPDATA/../Local/Temp/OrbitalMod/rv}"; mkdir -p "$OUT"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
scan() { local cmds=(); for i in $(seq 1000000000 1000001500); do cmds+=("gridinfo $i"); done; timeout 200 "$O" "${cmds[@]}" >/dev/null; sleep 3; }
ids() { grep -a "gridinfo 10000" "$(LOG)" | grep -ao "=> grid [0-9]* 'Blue Fighter' server" | grep -o "[0-9]\{10\}" | sort -u; }
shot() { local p; p=$(timeout 60 "$O" --eshot 2 "hudcheck" | grep -a "engine shot" | sed 's/engine shot: //' | tr -d '\r'); [ -n "$p" ] && cp "$p" "$OUT/$1.png" 2>/dev/null && echo "      shot $OUT/$1.png"; }
check() { local r; r=$(q "hudcheck"); echo "[$1] $r" | sed 's/ || /\n      /g'; case "$r" in "hudcheck PASS"*) P=$((P+1));; *) F=$((F+1));; esac; shot "$2"; }
P=0; F=0
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 4
powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"; sleep 15
for i in $(seq 1 80); do L=$(LOG); grep -aq "beacon Root Entity\|Loading Failed" "$L" 2>/dev/null && break; sleep 3; done
sleep 15; echo "log $(LOG)"
scan; before=$(ids); bf=$(echo "$before" | head -1); echo "anchor: Blue Fighter $bf"
q "shotui on" >/dev/null
sites=$(q "sites"); echo "$sites" | cut -c1-300
site=$(echo "$sites" | sed 's/^sites [0-9]*: //' | tr ';' '\n' | grep "@Verdure#" | head -1 | sed 's/@.*//')
if [ -z "$site" ]; then
  # none: a third Blue Fighter made a site of a sector homed at Verdure (devsite)
  homes=$(q "sites homes"); echo "$homes" | cut -c1-300
  sec=$(echo "$homes" | sed 's/^homes [0-9]*: //' | tr ';' '\n' | grep "@Verdure:" | head -1 | sed 's/@.*//')
  if [ -n "$sec" ]; then
    q "gridclone $bf 1 6000" >/dev/null; sleep 10; scan; sg=$(comm -13 <(echo "$before") <(ids) | head -1); before=$(ids)
    echo "devsite: $(q "devsite $sg $sec")"; sleep 6
    site=$(q "sites" | sed 's/^sites [0-9]*: //' | tr ';' '\n' | grep "@Verdure#" | head -1 | sed 's/@.*//')
  fi
fi
if [ -z "$site" ]; then
  # still none: the ring rocks are sites (their ringed planet the host)
  q "roids on" >/dev/null; sleep 8
  all=$(q "sites"); echo "$all" | cut -c1-300
  site=$(echo "$all" | sed 's/^sites [0-9]*: //' | tr ';' '\n' | grep "@Verdure#" | head -1 | sed 's/@.*//')
  # none round Verdure: any - a target only the star is shared with (no target plot: the disc / anchor plot stays)
  [ -z "$site" ] && { site=$(echo "$all" | sed 's/^sites [0-9]*: //' | tr ';' '\n' | grep -v "^$" | head -1 | sed 's/@.*//'); FAR=1; }
fi
echo "target site: ${site:-none}${FAR:+ (not round Verdure)}"
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $bf 30")"; sleep 4
echo "seat: $(q "tpgrid $bf 15" >/dev/null; sleep 4; q "seatid $bf")"; sleep 5
echo "orbit: $(q "gridorbit $bf Verdure 300 300")"; sleep 8
q "target off" >/dev/null; sleep 2
check "R1 the anchor's pilot, no target" R1
if [ -n "$site" ]; then echo "target: $(q "target $site")"; sleep 3; check "R2 the anchor's pilot, a site targeted" R2; fi
echo "target: $(q "target Kemik")"; sleep 3
check "R3 the anchor's pilot, a planet targeted" R3
q "target off" >/dev/null
echo "clone: $(q "gridclone $bf 1 3000")"; sleep 10
scan; cl=$(comm -13 <(echo "$before") <(ids) | head -1); echo "clone: $cl"
q "unseat" >/dev/null; sleep 4
echo "to it: $(q "tpgrid $cl 30")"; sleep 4
echo "seat: $(q "tpgrid $cl 15" >/dev/null; sleep 4; q "seatid $cl")"; sleep 6
check "R4 the clone's pilot 3 km off, no target" R4
if [ -n "$site" ]; then
  echo "target: $(q "target $site")"; sleep 3; check "R5 the clone's pilot, a site targeted" R5
  q "unseat" >/dev/null; sleep 5; check "R6 on foot by the clone, a site targeted" R6
fi
q "target off" >/dev/null
echo "== rendezvous cases: $P passed, $F failed"
