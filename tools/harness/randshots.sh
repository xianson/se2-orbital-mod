#!/usr/bin/env bash
# usage: randshots.sh <prefix> <seconds> -- while a scenario runs, take an engine screenshot at random
# moments (every 4-25 s) for <seconds>; each shot is logged with the game clock and the player's state
# (docs/shots-work/<prefix>.log), so what was on screen can be matched to what was going on.
SP="$(dirname "$0")"; P=$1; T=${2:-300}
S=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
LOG="$SP/../../docs/shots-work/$P.log"; : > "$LOG"
"$SP/orb.sh" "shotui on" >/dev/null
end=$(( $(date +%s) + T )); n=0
while [ $(date +%s) -lt $end ]; do
  sleep $(( 4 + RANDOM % 22 ))
  n=$(( n + 1 )); id=$(printf '%02d' $n)
  timeout 90 "$SP/eshot.sh" "${P}_$id" 0 "flight" >/dev/null 2>&1
  st=$(grep "> flight" $S | tail -1 | cut -c1-200)
  echo "$id $(date +%T) $st" >> "$LOG"
done
echo "shots: $n  log: $LOG"
