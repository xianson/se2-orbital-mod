#!/usr/bin/env bash
# testlib.sh — shared helpers for the self-checking harness suites (source it).
# check NAME PATTERN TIMEOUT [SETTLE]: the pattern appears in the game's log since the last `mark`, the game
# survives SETTLE seconds after, no Havok migration assertion or fatal crash since the mark.
# expect NAME CONDITION-DESCRIPTION COMMAND...: PASS when the command succeeds.
SP="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# (LOGDIR, ST, ORB: overridable - the offline dry runs point them at canned files and a mock harness)
LOGDIR=${LOGDIR:-/c/Users/slob/AppData/Roaming/SpaceEngineers2/Temp/Logs}
ST=${ST:-/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt}
ORB=${ORB:-$SP/orb.sh}
pass=0; fail=0; skipped=0; results=()

log() { ls -t "$LOGDIR"/SpaceEngineers2_*.log | grep -v -e Render -e Mission | head -1; }
mark() { MARK=$(wc -l < "$(log)"); }
since() { tail -n +"$((MARK + 1))" "$(log)"; }
alive() { tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2; }
waitfor() { local pat="$1" to="$2"; for i in $(seq 1 "$to"); do since | grep -aq -- "$pat" && return 0; alive || return 1; sleep 1; done; return 1; }
hold() { for i in $(seq 1 "$1"); do alive || return 1; sleep 1; done; return 0; }
bad() { since | grep -a -e "not migrated" -e "still active bodies in native world" -e "Crash Handler\]: Fatal" | head -1; }
record() { if [ -z "$2" ]; then pass=$((pass + 1)); results+=("PASS  $1"); else fail=$((fail + 1)); results+=("FAIL  $1: $2"); fi; echo "${results[-1]}"; }
check() {
    local name="$1" pat="$2" to="$3" settle="${4:-15}" why=""
    if ! waitfor "$pat" "$to"; then why="expected '$pat' not seen"; fi
    [ -z "$why" ] && ! hold "$settle" && why="game died after"
    local b; b=$(bad); [ -n "$b" ] && why="${why:+$why; }$(echo "$b" | cut -c1-160)"
    alive || why="${why:-game not running}"
    record "$name" "$why"
}
# never: the pattern must NOT appear in the log since the mark
never() { local name="$1" pat="$2" hit; hit=$(since | grep -a -- "$pat" | head -1 | cut -c60-200); record "$name" "${hit:+unexpected: $hit}"; }
expect() { local name="$1" why="$2"; shift 2; if "$@"; then record "$name" ""; else record "$name" "$why"; fi; }
send() { "$ORB" "$@" >/dev/null; }
reply() { "$ORB" "$@" | grep -a " => " | tail -1; }
stat() { grep -a -- "$1" "$ST" | head -1; }
summary() { echo; echo "== summary: $pass passed, $fail failed$([ "$skipped" -gt 0 ] && echo ", $skipped skipped")"; for r in "${results[@]}"; do echo "  $r"; done; [ "$fail" -eq 0 ]; }
# skip NAME WHY: a check that could not run here (not a pass, not a failure; listed in the summary with its reason)
skip() { skipped=$((skipped + 1)); results+=("SKIP  $1: $2"); echo "${results[-1]}"; }

# LOGF: the newest game log as log() finds it, but stricter (the name ends in a digit; the stats log left out too).
# waitloaded: the opening of a one-launch script - the world loaded or failed (up to 300 s), then the
# harness answering with grids (up to 60 s); waits only, no verdict.
# gridsup: the findgrids REPLY names a grid (orb.sh prints the whole status tail, whose other lines hold '=' too: a bare
# grep '=' passed at once - 2026-10-18, a baseline read 0 grids while the server sat in a 14 s GC hitch, its views unpublished)
gridsup() { "$ORB" findgrids | grep -a '> findgrids  =>' | tail -1 | grep -aq ' [0-9][0-9]*='; }
export -f gridsup; export ORB   # (contactshot / targetshot call it inside bash -c)
LOGF() { ls -t "$LOGDIR"/SpaceEngineers2_*[0-9].log | grep -v "Render\|Mission\|Stats" | head -1; }
waitloaded() {
    for i in $(seq 1 300); do grep -aq 'beacon Root Entity\|Loading Failed' "$(LOGF)" 2>/dev/null && break; sleep 1; done
    for i in $(seq 1 60); do gridsup && break; sleep 1; done
}

# ask CMD...: the commands in one batch, then each one's reply (the text after "  =>  " in its status note), a line each,
# in order. (status.txt keeps the last 20 notes: at most 20 commands a batch.)
ask() { "$ORB" "$@" >/dev/null; local c; for c in "$@"; do grep -aF "> $c  =>  " "$ST" | tail -1 | sed 's/.*  =>  //' | tr -d '\r'; done; }
# val Type.Member...: aeroget values (the reply's text after "="), a line each; "?" when the static is missing or unread
val() {
    local m a=(); for m in "$@"; do a+=("aeroget $m"); done
    ask "${a[@]}" | awk '{ i = index($0, "="); if (i == 0 || $0 ~ /^failed/) print "?"; else print substr($0, i + 1) }'
}

# ourexc FILE [FROMLINE]: our mods' exceptions in a game log - a stack frame in our code (at AeroMod. / at OrbitalMod. /
# at SEAerospace.), a mod fault line ([AERO-FAULT] / [ORBIT-FAULT]), or one of our tagged lines naming an exception
# ([AERO...] / [ORBIT...] with "Exception"). Prints each hit (line number: text, cut); nothing when clean.
ourexc() {
    tail -n +"${2:-1}" "$1" | grep -an -e '^[[:space:]]*at \(AeroMod\|OrbitalMod\|SEAerospace\)\.' -e '\[\(AERO\|ORBIT\)-FAULT\]' \
        -e '\[\(AERO\|ORBIT\)[A-Z-]*\].*Exception' | cut -c1-220
}

# worldreset_audit FILE: one "[ORBIT] world reset:" per world LEFT, in a game log (fix/o-bugs). A world load is the first
# "[ORBIT] beacon Root Entity" line after the start or after an "UnloadAllScenes GameServer"; a load is left when an unload
# or another load follows it. Each reset counts for the load it follows (one whose reason is "another world's planets" for
# the load before that: it runs as the NEXT world's planets join). Exit 0 when every left load has exactly one and the
# current one none. Prints a line per load and the verdict.
worldreset_audit() {
    awk '
      /UnloadAllScenes GameServer/ { if (n > 0) left[n] = 1; gap = 1; next }
      /\[ORBIT\] beacon Root Entity/ { if (gap || n == 0) { if (n > 0) left[n] = 1; n++; at[n] = NR; gap = 0 } next }
      /\[ORBIT\] world reset:/ {
          k = n; if ($0 ~ /another world.s planets/ && k > 1) k--
          if (k > 0) { r[k]++; why[k] = why[k] " | " substr($0, index($0, "world reset:") + 13) } else early++
          next }
      END {
          bad = 0
          for (i = 1; i <= n; i++) {
              want = left[i] ? 1 : 0; got = r[i] + 0
              printf "  load %d (log line %d): %s, %d reset(s), want %d%s\n", i, at[i], left[i] ? "left" : "current", got, want, why[i]
              if (got != want) bad++
          }
          if (early) { printf "  %d reset(s) before any world loaded\n", early; bad++ }
          printf "  world reset audit: %d load(s), %s\n", n, bad ? bad " wrong" : "one reset per world change"
          exit bad ? 1 : 0
      }' "$1"
}

# launch(): the game started once - launch.sh
source "$(dirname "${BASH_SOURCE[0]}")/launch.sh"
