#!/usr/bin/env bash
# usage: orb.sh [--shot [settleSec] | --eshot [settleSec]] "command" ["command" ...]
# Writes commands for the Orbital Mod DevHarness, waits until the mod consumes them, prints status.
#   --shot   desktop capture via the OrbitalShotSE2 scheduled task (needs the display awake)
#   --eshot  ENGINE screenshot via the harness `shot` command (works with the display asleep);
#            the PNG lands in the game's temp folder and its path is printed.
SP="$(dirname "$0")"; D=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$D"
shot=0; settle=4
if [ "$1" = "--shot" ] || [ "$1" = "--eshot" ]; then [ "$1" = "--shot" ] && shot=1 || shot=2; shift; if [[ "$1" =~ ^[0-9]+$ ]]; then settle=$1; shift; fi; fi
send() { printf '%s\n' "$@" > "$D/cmd.txt"; for i in $(seq 1 120); do [ -f "$D/cmd.txt" ] || return 0; sleep 0.5; done; echo "WARN: command not consumed after 60 s (game frozen or harness off)"; }
# (2026-10-26: a script whose lease had run out kept sending tp / cam commands into the next holder's game. With
#  GAME_OWNER set, commands go out only while that session holds the game slot or rides on it - slot.py's state file)
if [ -n "$GAME_OWNER" ] && [ -z "$ORB_ANY_GAME" ]; then
  python -c "
import json,os,sys
s=json.load(open(os.path.join(os.environ['LOCALAPPDATA'],'OrbitalModBuild','slots.json')))['game']
who=os.environ['GAME_OWNER']
sys.exit(0 if (s.get('holder') or {}).get('who')==who or any(r.get('who')==who for r in s.get('riders',[])) else 1)" 2>/dev/null   || { echo "REFUSED: $GAME_OWNER doesn't hold the game slot - nothing sent (slot.py take game first; ORB_ANY_GAME=1 overrides)"; exit 3; }
fi
send "$@"
# (the game slot's heartbeat: a holder still working keeps its lease - slot.py beat, in the background)
[ -n "$GAME_OWNER" ] && { python "${AERO_TOOLS:-$SP/../../../Aerodynamics Mod/tools/shared}/slot.py" beat game --who "$GAME_OWNER" >/dev/null 2>&1 & }
sleep 1; tail -n 14 "$D/status.txt" 2>/dev/null
if [ $shot = 1 ]; then
  sleep "$settle"; rm -f "$SP/shot-result.txt"
  powershell -NoProfile -Command "Start-ScheduledTask -TaskName 'OrbitalShotSE2'" >/dev/null
  for i in $(seq 1 20); do [ -f "$SP/shot-result.txt" ] && break; sleep 0.5; done
  cat "$SP/shot-result.txt"
elif [ $shot = 2 ]; then
  # (wait for a NEW shot path: read once after 1 s, a slow game's status still named the previous shot, and every view
  #  after it got the one before's picture - 2026-10-26 water shots)
  old=$(grep -a '^shot ' "$D/status.txt" 2>/dev/null | sed 's/^shot //' | tr -d '\r')
  sleep "$settle"; send "shot"; p=""
  for i in $(seq 1 90); do
    p=$(grep -a '^shot ' "$D/status.txt" 2>/dev/null | sed 's/^shot //' | tr -d '\r')
    [ -n "$p" ] && [ "$p" != "$old" ] && [ -f "$(cygpath -u "$p" 2>/dev/null || echo "$p")" ] && break; p=""; sleep 0.5
  done
  echo "engine shot: $p"
fi
