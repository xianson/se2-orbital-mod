#!/usr/bin/env bash
# settingsui.sh [setting] - the Options screen's "Mods" tab (Scripts/Simulation/Ui/SettingsTab.cs) in a RUNNING world: the
# game is not relaunched (the dev harness must be on; never while the user plays). ~20 s. Opens Options (the harness's own
# SharedUIComponent.OpenOptionsScreen), selects the Mods tab, checks the tab is there and every section's rows counted by
# Tag equal the rows it declares (Orbital's against its registry too), flips one client switch through its CONTROL (as a
# click) and checks the setting followed and OrbitalMod.client.cfg was rewritten, flips it back, closes the screen.
# Then take the randomized screenshot sweep by hand (tabs, expanders open / shut, window sizes, gamepad tab cycling).
H="$(cd "$(dirname "$0")" && pwd)"; O="$H/orb.sh"
q() { timeout 30 "$O" "$@" | grep -a "=>" | tail -1 | sed 's/.*=> *//'; }
P=0; F=0
ok() { if [ "$2" = 1 ]; then echo "PASS  $1"; P=$((P+1)); else echo "FAIL  $1${3:+: $3}"; F=$((F+1)); fi; }
NAME=${1:-Hud.SpeedIcon}
CFG="$APPDATA/SpaceEngineers2/ModSettings/OrbitalMod.client.cfg"
mtime() { stat -c %Y "$CFG" 2>/dev/null || echo 0; }

echo "open: $(q "settingsui open")"; sleep 3
echo "select: $(q "settingsui select")"; sleep 2
q "settingsui report" >/dev/null; sleep 1
r=$(q "settingsui report"); echo "report: $r"
case "$r" in *"Mods tab: found"*) ok "the Mods tab is in the Options screen" 1 ;; *) ok "the Mods tab is in the Options screen" 0 "$r" ;; esac
for s in $(echo "$r" | grep -o '[A-Za-z]* rows [0-9?]*/[0-9?]*' | tr ' ' '_'); do
  id=${s%%_*}; n=${s##*_}; ok "$id: rows counted = rows declared ($n)" $([ "${n%/*}" = "${n#*/}" ] && [ "${n%/*}" != 0 ] && echo 1 || echo 0)
done
echo "$r" | grep -q "OrbitalMod rows" && ok "the Orbital section" 1 || ok "the Orbital section" 0
echo "$r" | grep -q "AerodynamicsMod rows" && ok "the Aerodynamics section" 1 || echo "NOTE  no Aerodynamics section (aero off, or no grid in the world: its tab ticks from the grids' effects jobs)"
b=$(echo "$r" | grep -o 'OrbitalMod: [0-9]* rows built, registry shows [0-9]*')
ok "Orbital: rows built = the registry's shown ($b)" $([ -n "$b" ] && [ "$(echo "$b" | awk '{print $2}')" = "$(echo "$b" | awk '{print $NF}')" ] && echo 1 || echo 0)
echo "$r" | grep -q "selected True" && ok "the tab is selected (the pump runs)" 1 || ok "the tab is selected (the pump runs)" 0

before=$(q "setting $NAME"); t0=$(mtime)
echo "flip: $(q "settingsui flip $NAME")"; sleep 4
after=$(q "setting $NAME")
ok "$NAME followed its control ($before -> $after)" $([ -n "$before" ] && [ "$before" != "$after" ] && echo 1 || echo 0)
ok "OrbitalMod.client.cfg rewritten" $([ "$(mtime)" != "$t0" ] && echo 1 || echo 0)
echo "flip back: $(q "settingsui flip $NAME")"; sleep 2
back=$(q "setting $NAME")
ok "$NAME back ($back)" $([ "$back" = "$before" ] && echo 1 || echo 0)
echo "close: $(q "settingsui close")"; sleep 2
echo "status: $(q "settingsui report")"
echo; echo "== settingsui: $P passed, $F failed"; [ $F -eq 0 ]
