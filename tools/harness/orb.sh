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
send "$@"
sleep 1; tail -n 14 "$D/status.txt" 2>/dev/null
if [ $shot = 1 ]; then
  sleep "$settle"; rm -f "$SP/shot-result.txt"
  powershell -NoProfile -Command "Start-ScheduledTask -TaskName 'OrbitalShotSE2'" >/dev/null
  for i in $(seq 1 20); do [ -f "$SP/shot-result.txt" ] && break; sleep 0.5; done
  cat "$SP/shot-result.txt"
elif [ $shot = 2 ]; then
  sleep "$settle"; send "shot"; sleep 1
  p=$(grep -a '^shot ' "$D/status.txt" | sed 's/^shot //' | tr -d '\r')
  for i in $(seq 1 30); do [ -n "$p" ] && [ -f "$(cygpath -u "$p" 2>/dev/null || echo "$p")" ] && break; sleep 0.5; done
  echo "engine shot: $p"
fi
