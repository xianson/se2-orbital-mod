#!/usr/bin/env bash
# usage: play.sh -- relaunch for the user to play: World 2, seated in the Blue Fighter, in a 250 km
# Verdure orbit. world.txt is put back to the harness world ("Orbital Test Campaign 2") once loaded.
SP="$(dirname "$0")"; O="$SP/orb.sh"; S=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
KEEP="$(cat "$SP/world.txt")"
printf 'Orbital Test World 2' > "$SP/world.txt"
"$SP/relaunch.sh"
printf '%s' "$KEEP" > "$SP/world.txt"
$O "seat Blue Fighter" >/dev/null; sleep 6; grep "> seat" $S | tail -1 | cut -c10-140
$O "legacy on" >/dev/null; sleep 8
$O "orbit Verdure 250 250" >/dev/null; sleep 6
$O "flight" >/dev/null; sleep 1; grep "> flight" $S | tail -1 | cut -c10-140
