#!/usr/bin/env bash
# globeshot.sh - the proxy globe on the planet's own overlay maps (PlanetMesh + tools/globes/bake_globes.py) against the
# map model and the real planet, same views (5 minutes or less; RELAUNCHES the game and closes it after: not while
# someone plays).
#  M  the globe built for every planet in the world: baked colour faces + normal maps found, materials made, textures pinned, no warning
#  G  each planet from 250 km at a random bearing: the real planet, then the overlay globe - engine shots
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"; T0=$(date +%s)
q() { timeout 20 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=>  //'; }
# (the last command's reply only: status.txt holds the recent ones, each from its "=>" line)
qa() { timeout 20 "$O" "$@" >/dev/null; awk '/=>/{buf=""} {buf=buf $0 "\n"} END{printf "%s", buf}' /c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt; }
LOG() { ls -t "$APPDATA/SpaceEngineers2/Temp/Logs/"SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
until_() { local n=$1; shift; for i in $(seq 1 $n); do eval "$@" && return 0; sleep 0.5; done; return 1; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then P=$((P+1)); echo "PASS $1"; else F=$((F+1)); echo "FAIL $1"; fi; [ -n "$3" ] && echo "      $3" | cut -c1-300; }
shot() { timeout 40 "$O" --eshot 3 "$1" | grep -a -o "[A-Za-z]:[^ ]*\.png" | tail -1; }

powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"; sleep 2
. "$(dirname "$0")/launch.sh"; launch
until_ 300 "grep -aq 'beacon Root Entity\|Loading Failed' \"\$(LOG)\" 2>/dev/null"; until_ 60 "q 'findgrids' | grep -q '='"
echo "loaded in $(( $(date +%s) - T0 )) s: $(LOG)"
q "encon off" >/dev/null; q "unseat" >/dev/null

# every planet in the world (the dev status's "planet <i> <name>" lines), same view: real planet, overlay globe
q "status" >/dev/null
PL=$(grep -a "^planet [0-9]* " /c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt | awk '{print $3}' | sort -u | tr '\n' ' ')
[ -z "$PL" ] && PL="Verdure Kemik"
echo "planets: $PL"
for planet in $PL; do
  b=$((RANDOM % 360)); e=$(( (RANDOM % 60) - 30 ))
  q "cam planet $planet 250 $b $e" >/dev/null
  q "mode AlwaysReal" >/dev/null; sleep 5; echo "G $planet b$b e$e  real:   $(shot 'mode AlwaysReal')"
  q "mode AlwaysProxy" >/dev/null; sleep 6; echo "G $planet b$b e$e  globe:  $(shot 'hiresglobe status')"
done
st=$(qa "hiresglobe status")
echo "$st" | grep "materials=\|\[" | sed 's/^/      /' | cut -c1-200
BAKED=$(for p in $PL; do [ -d "$H/../../Content/PlanetGlobes/$p" ] && echo -n "$p "; done)
echo "baked: $BAKED (the others keep the map model)"
for planet in $BAKED; do
  blk=$(echo "$st" | awk -v p="[$planet]" 'index($0,p){f=1;next} f&&/\[[A-Z]/{f=0} f')
  nf=$(echo "$blk" | grep -c "_globe_cm_.*normal=yes")
  ok "M $planet: 6 baked faces with normal maps, materials made, mesh built, 12 textures pinned" \
     $([ "$nf" = 6 ] && echo "$blk" | grep -q "materials=ok" && echo "$blk" | grep -q "mesh=ok" && echo "$blk" | grep -q "pinned 12" && echo 1 || echo 0) "$(echo "$blk" | grep -o "error=.*")"
done
# (an unbaked planet's "0/6 baked globe faces" is expected: it keeps the map model)
w=$(grep -a "hi-res globe for" "$(LOG)" | grep -v "0/6 baked globe faces" | tail -2 | sed 's/.*\[ORBIT\] //')
ok "M no globe warnings for the baked planets" $([ -z "$w" ] && echo 1 || echo 0) "$w"

# restore, close
q "mode Frame" >/dev/null; q "cam off" >/dev/null; q "encon on" >/dev/null
ex=$(grep -ac 'Exception occurred' "$(LOG)")
powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
echo "== globeshot: $P passed, $F failed in $(( $(date +%s) - T0 )) s; exceptions: $ex (game closed)"
