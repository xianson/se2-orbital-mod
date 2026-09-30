#!/usr/bin/env bash
# testlib.sh — shared helpers for the self-checking harness suites (source it).
# check NAME PATTERN TIMEOUT [SETTLE]: the pattern appears in the game's log since the last `mark`, the game
# survives SETTLE seconds after, no Havok migration assertion or fatal crash since the mark.
# expect NAME CONDITION-DESCRIPTION COMMAND...: PASS when the command succeeds.
SP="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOGDIR=/c/Users/slob/AppData/Roaming/SpaceEngineers2/Temp/Logs
ST=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
pass=0; fail=0; results=()

log() { ls -t "$LOGDIR"/SpaceEngineers2_*.log | grep -v -e Render -e Mission | head -1; }
mark() { MARK=$(wc -l < "$(log)"); }
since() { tail -n +"$((MARK + 1))" "$(log)"; }
alive() { tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2; }
waitfor() { local pat="$1" to="$2"; for i in $(seq 1 "$to"); do since | grep -aq -- "$pat" && return 0; alive || return 1; sleep 1; done; return 1; }
hold() { for i in $(seq 1 "$1"); do alive || return 1; sleep 1; done; return 0; }
bad() { since | grep -a -e "not migrated" -e "Crash Handler\]: Fatal" | head -1; }
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
send() { "$SP/orb.sh" "$@" >/dev/null; }
reply() { "$SP/orb.sh" "$@" | grep -a " => " | tail -1; }
stat() { grep -a -- "$1" "$ST" | head -1; }
summary() { echo; echo "== summary: $pass passed, $fail failed"; for r in "${results[@]}"; do echo "  $r"; done; [ "$fail" -eq 0 ]; }
