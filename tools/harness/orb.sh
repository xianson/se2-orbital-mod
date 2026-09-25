#!/usr/bin/env bash
# usage: orb.sh [--shot [settleSec]] "command" ["command" ...]
# Writes commands for the Orbital Mod DevHarness, waits until the mod consumes them, prints status,
# optionally captures the screen after settleSec.
SP="$(dirname "$0")"; D=/c/Users/slob/AppData/Local/Temp/OrbitalMod; mkdir -p "$D"
shot=0; settle=4
if [ "$1" = "--shot" ]; then shot=1; shift; if [[ "$1" =~ ^[0-9]+$ ]]; then settle=$1; shift; fi; fi
printf '%s\n' "$@" > "$D/cmd.txt"
for i in $(seq 1 120); do [ -f "$D/cmd.txt" ] || break; sleep 0.5; done
[ -f "$D/cmd.txt" ] && echo "WARN: command not consumed after 60 s (game frozen or harness off)"
sleep 1; tail -n 12 "$D/status.txt" 2>/dev/null
if [ $shot = 1 ]; then
  sleep "$settle"; rm -f "$SP/shot-result.txt"
  powershell -NoProfile -Command "Start-ScheduledTask -TaskName 'OrbitalShotSE2'" >/dev/null
  for i in $(seq 1 20); do [ -f "$SP/shot-result.txt" ] && break; sleep 0.5; done
  cat "$SP/shot-result.txt"
fi
