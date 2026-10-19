#!/usr/bin/env bash
# test_blocksave.sh - the aero blocks' per-block saved data survives save -> reload (Aerodynamics Mod Blocks/AeroBlockSave.cs,
# 2026-10-15: each block's state in its own saved entity; the old position-matched lines still read for one release).
# Two launches of an 'Orbital Test ...' copy of the SSTO's world; PASS / FAIL / SKIP from the aero mod's own counters and
# dumps (aeroget AeroBlockSave.*), the harness's save reply and the game log - nothing judged by eye.
#
# usage: test_blocksave.sh               the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1 closes it
#                                        - only when nobody is playing. REFUSES unless the launch world is an 'Orbital Test...'
#                                        world (the -start: of the launch args file - set by whoever runs this; never
#                                        edited here): the harness `save` writes only into 'Orbital Test...' containers, and
#                                        this script saves into the very world it loaded.
#        test_blocksave.sh --release     only release the dev overrides in the running game (after a killed run)
# env: GRID (SSTO: the grid's name as findgrids prints it) DEADLINE s (290) ARGS (the launch args file; default
#      $SE2_LAUNCH_ARGS, else se2-launch-args.txt in the Aero mod's tools/shared - AERO_TOOLS, else the sibling
#      checkout's: what tools/shared/launch-se2.bat, Steam's launch, reads)
#      DRY=1 (offline: ORB / LOGDIR / ST at a mock harness - no game, no launch, no kill)
#
# TIMELINE (5 minutes end to end; past DEADLINE what is left is skipped and the releases run):
#   0 s      guards (the world, no game running or KILL_GAME=1), the Steam launch
#   ~5-120   wait: a NEW log with the world loaded, its -start: the args file's, the harness answering findgrids
#   +5       A  dump GRID's aero blocks (AeroBlockSave.DevDump, answered on the server step); counters of the first load
#   +5       B  set one surface (trim, max), one gear (up) and its Airshock (off), one airbrake (max), one thruster (0.73);
#               dump again: each change took
#   +5..30   C  `save <the world>` into the SAME container: "save -> '<world>': Success", Written grew
#   ~5-120   D  KILL, relaunch into it, wait as above
#   +5       E  dump again: every changed value back (rb:1 - from the block's own data), every other block as before;
#               Restored > 0, LegacyClaimed = 0 (no old line claimed anything: the first save wrote none)
#   +3       F  both runs' logs: no exception in our code, no mod fault, no Havok assertion; the game alive
#   +3       release (also from the EXIT trap): the overrides back, the changed blocks back to what they were (not saved)
#   typical ~200-260 s, cap 290 s.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
GRID=${GRID:-SSTO}; DEADLINE=${DEADLINE:-290}
ARGS=${ARGS:-${SE2_LAUNCH_ARGS:+$(cygpath -u "$SE2_LAUNCH_ARGS")}}
ARGS=${ARGS:-${AERO_TOOLS:-$SP/../../../Aerodynamics Mod/tools/shared}/se2-launch-args.txt}
if [ -n "$DRY" ]; then alive() { true; }; launch() { "$ORB" __launch >/dev/null; }; fi   # (offline: the mock harness)
RELEASED=""; notes=(); incomplete=""; L=""; L1=""; ORIG=()

el() { echo $(( $(date +%s) - T0 )); }
budget() { [ $(( $(date +%s) - T0 + $1 )) -le "$DEADLINE" ]; }
note() { notes+=("$1"); echo "   $1"; }
poll() { local end=$(( $(date +%s) + $1 )); shift; while :; do "$@" >/dev/null 2>&1 && return 0; alive || return 1; [ "$(date +%s)" -ge "$end" ] && return 1; sleep 1; done; }
since() { tail -n +"$((MARK + 1))" "$L"; }
mark() { MARK=$(wc -l < "$L"); }
num() { awk -v x="$1" 'BEGIN{exit !(x ~ /^-?[0-9.]+([eE][-+]?[0-9]+)?$/)}'; }

# a -start: value up to the next " -option" (a world name may hold '-': "Tu-134")
startof() { grep -ao '\-start:[^"]*' | head -1 | sed -E 's/^-start://; s/[[:space:]]+-[a-zA-Z]+(:.*|[[:space:]].*)?$//; s/[[:space:]]*$//'; }
# the world the launch loads: the -start: of the args file (quoted or not); the one the game was given, from its log
startworld() { startof < "$1" 2>/dev/null; }
logworld() { grep -a -m1 'Running application with arguments' "$1" | startof; }

# dev ask/answer (the aero mod answers on its server step): set the request, poll the answer until it is no longer "asked"
dev() {   # dev DevDump|DevSet VALUE  -> the answer
    local m="$1" v="$2" a i
    ask "aeroset AeroBlockSave.$m $v" >/dev/null
    for i in $(seq 1 20); do a=$(val "AeroBlockSave.$m"); case "$a" in asked*|"?") sleep 0.5 ;; *) echo "$a"; return 0 ;; esac; done
    echo "${a:-no answer}"; return 1
}
# dodump: GRID's blocks into DUMPRAW (the header in DUMPHEAD); tokens: one a line ("kind@x,y,z=name:v;...")
dodump() { DUMPRAW=$(dev DevDump "$GRID"); DUMPHEAD=$(echo "$DUMPRAW" | cut -d: -f1-2 | cut -c1-120); }
tokens() { echo "$DUMPRAW" | tr ' ' '\n' | grep -a '^[a-z]*@-\?[0-9]*,-\?[0-9]*,-\?[0-9]*='; }
# tv TOKEN NAME: one value of a token
tv() { echo "$1" | sed 's/^[^=]*=//' | tr ';' '\n' | sed -n "s/^$2://p" | head -1; }
cellof() { echo "$1" | sed 's/^[a-z]*@\([^=]*\)=.*/\1/'; }
# pick a value unlike the current one
other() { awk -v c="$1" -v a="$2" -v b="$3" 'BEGIN{ if (c != "" && (c - a) ^ 2 < 1e-6) print b; else print a }'; }
ndump() { dodump; tokens | grep -q .; }

release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    local r=("cam off" "hold off" "thrust 0 0 0 0" "warp 1" "aerocost off" "statusfast off")
    echo "== release: ${#r[@]} overrides$([ ${#ORIG[@]} -gt 0 ] && echo ", ${#ORIG[@]} block value(s) back (this session only - not saved)")"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
    local o; for o in "${ORIG[@]}"; do echo "   $(dev DevSet "$o" | cut -c1-140)"; done
}
summary_all() {
    echo; echo "== test_blocksave: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for n in "${notes[@]}"; do echo "  $n"; done; }
    summary; local r=$?
    [ -n "$incomplete" ] && echo "== INCOMPLETE: phases were cut by the deadline - not a verdict on those checks (exit 3 when nothing failed)"
    return $r
}

[ "$1" = "--release" ] && { release; exit 0; }

# ---- guards ------------------------------------------------------------------------------------------------------------------
W=$(startworld "$ARGS")
case "$W" in
    "Orbital Test"*) ;;
    *) echo "REFUSED: the launch world is '$W' (-start: in $ARGS) - not an 'Orbital Test...' world. This script saves into the"
       echo "         world it loads; copy the SSTO's save to an 'Orbital Test ...' folder and point -start: at it."; exit 2 ;;
esac
if [ -z "$DRY" ] && alive && [ -z "$KILL_GAME" ]; then
    echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it (only when nobody plays)."
    exit 2
fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM

# start: the Steam launch (launch.sh: every game process closed first), a NEW log with the world loaded, the harness answering
start() {
    local old; old=$(LOGF 2>/dev/null); L=""
    mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"; rm -f "$(dirname "$ST")/cmd.txt"
    launch
    local i loaded=""
    for i in $(seq 1 150); do
        L=$(LOGF 2>/dev/null)
        if [ -n "$L" ] && [ "$L" != "$old" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null; then loaded=1; break; fi
        sleep 1
    done
    MARK=0
    [ -z "$loaded" ] && { record "$1: the world loaded within 150 s" "no new log with a loaded world (log: ${L:-none})"; return 1; }
    grep -aq 'Loading Failed' "$L" && { record "$1: the world loaded" "Loading Failed in $L"; return 1; }
    local lw; lw=$(logworld "$L")
    [ "$lw" = "$W" ] || { record "$1: the game loaded the test world" "its -start: is '$lw', the args file's '$W'"; return 1; }
    poll 60 gridsup || { record "$1: the harness answers" "findgrids unanswered 60 s after the load"; return 1; }
    # (the grids stream in after the first answer: wait for GRID itself, up to 60 s - a partial list missed the SSTO, 2026-10-15)
    poll 60 bash -c "\"$ORB\" findgrids | tr ' ' '\n' | grep -aiq '=$GRID\$'" || true
    record "$1: '$W' loaded, the harness answers ($(el) s)" ""
}

echo "== launch 1 ($(el) s): '$W'"
start "launch 1" || exit 1
L1=$L

# ---- A: the blocks as loaded -------------------------------------------------------------------------------------------------
echo "== A dump ($(el) s)"
if ! num "$(val AeroBlockSave.Restored)"; then
    record "aero: the per-block save's dev statics (AeroBlockSave.Restored, DevDump)" "not in this build: '$(val AeroBlockSave.Restored)' - deploy an aero build with Dev/AeroBlockSaveDev.cs"
    exit 1
fi
# (the grids stream in after the harness first answers: a partial list missed the SSTO - wait up to 60 s for it, 2026-10-15)
gid=""; for _i in $(seq 1 30); do gid=$(ask findgrids | tr ' ' '\n' | grep -i "=$GRID\$" | head -1 | cut -d= -f1); [ -n "$gid" ] && break; sleep 2; done
[ -z "$gid" ] && { record "setup: a grid named $GRID in this world" "none (findgrids) - the test world must be a copy of the SSTO's (Concordia Research Facility 7)"; exit 1; }
poll 30 ndump; dodump
mapfile -t D0 < <(tokens)
H0=$DUMPHEAD
note "load 1: $H0 | $(val AeroBlockSave.Counts) | last: $(val AeroBlockSave.Last | cut -c1-80)"
expect "dump: $GRID's aero blocks listed (${#D0[@]})" "$H0" test "${#D0[@]}" -gt 0
[ "${#D0[@]}" -gt 0 ] || exit 1

# ---- B: distinctive values on one block of each kind -------------------------------------------------------------------------
echo "== B set ($(el) s)"
S=$(printf '%s\n' "${D0[@]}" | grep -a '^surface@' | head -1)
Gr=$(printf '%s\n' "${D0[@]}" | grep -a '^gear@' | head -1)
A=$(printf '%s\n' "${D0[@]}" | grep -a '^airbrake@' | head -1)
T=$(printf '%s\n' "${D0[@]}" | grep -a '^thrust@' | head -1)
# WANT: "kind cell name value" - what the reload must bring back
WANT=()
setone() {   # setone KIND CELL REQKIND VALUE NAME WANTVALUE ORIGVALUE
    local r; r=$(dev DevSet "$GRID|$3|$2|$4")
    echo "   $3 $2 -> $4: $(echo "$r" | cut -c1-150)"
    if echo "$r" | grep -q '^set '; then WANT+=("$1 $2 $5 $6"); ORIG+=("$GRID|$3|$2|$7"); return 0; fi
    record "set: $3 at $2 -> $4" "answer: $(echo "$r" | cut -c1-160)"; return 1
}
if [ -n "$S" ]; then
    c=$(cellof "$S"); trim=$(other "$(tv "$S" trim)" 7.25 -6.5); max=$(other "$(tv "$S" max)" 27.5 13.5)
    setone surface "$c" surface-trim "$trim" trim "$trim" "$(tv "$S" trim)"
    setone surface "$c" surface-max "$max" max "$max" "$(tv "$S" max)"
else skip "surface: trim / max survive the reload" "no control surface on $GRID"; fi
if [ -n "$Gr" ]; then
    c=$(cellof "$Gr")
    if [ "$(tv "$Gr" down)" = 1 ]; then setone gear "$c" gear up down 0 down; else setone gear "$c" gear down down 1 up; fi
    if [ "$(tv "$Gr" shock)" = 1 ]; then setone gear "$c" airshock off shock 0 on; else setone gear "$c" airshock on shock 1 off; fi
else skip "gear: down / up and Airshock survive the reload" "no retractable gear on $GRID"; fi
if [ -n "$A" ]; then
    c=$(cellof "$A"); bm=$(other "$(tv "$A" max)" 12.5 17.5)
    setone airbrake "$c" airbrake-max "$bm" max "$bm" "$(tv "$A" max)"
else skip "airbrake: max opening survives the reload" "no airbrake on $GRID"; fi
if [ -n "$T" ]; then
    c=$(cellof "$T"); tf=$(other "$(tv "$T" f)" 0.73 0.27)
    setone thrust "$c" thrust "$tf" f "$tf" "$(tv "$T" f)"
else skip "thruster: attitude fraction survives the reload" "no thruster on $GRID"; fi
[ ${#WANT[@]} -gt 0 ] || { record "set: at least one block changed" "nothing to change on $GRID"; exit 1; }
# the dump right after: each change took (the values the save will write)
dodump; mapfile -t D1 < <(tokens)
tok() { local k="$1" c="$2"; shift 2; printf '%s\n' "$@" | grep -a "^$k@$c=" | head -1; }
same() { num "$1" && num "$2" && awk -v a="$1" -v b="$2" 'BEGIN{exit !((a - b) ^ 2 < 1e-6)}'; }
# okback TOKEN NAME VALUE: the token's value is VALUE and the block took it from its own data
okback() { [ "$(tv "$1" rb)" = 1 ] && same "$(tv "$1" "$2")" "$3"; }
bad=""; for w in "${WANT[@]}"; do read -r k c n v <<<"$w"; t=$(tok "$k" "$c" "${D1[@]}"); same "$(tv "$t" "$n")" "$v" || bad+=" $k@$c $n=$(tv "$t" "$n") (want $v)"; done
expect "set: every change took on the server blocks (${#WANT[@]} value(s))" "${bad# }" test -z "$bad"

# ---- C: save into the same container ------------------------------------------------------------------------------------------
echo "== C save ($(el) s)"
w0=$(val AeroBlockSave.Written); mark
ask "save $W" | sed 's/^/   /' | cut -c1-140
check "save: written to the test world ('save -> '$W': Success')" "save -> '$W': Success" 60 3
w1=$(val AeroBlockSave.Written)
note "save: Written $w0 -> $w1 | $(val AeroBlockSave.Counts) | last: $(val AeroBlockSave.Last | cut -c1-80)"
expect "save: the blocks wrote their keys (AeroBlockSave.Written grew: $w0 -> $w1)" "Written $w0 -> $w1" \
    awk -v a="$w0" -v b="$w1" 'BEGIN{exit !(b ~ /^[0-9]+$/ && a ~ /^[0-9]+$/ && b > a)}'
saved=$(since | grep -ac "save -> '$W': Success")

# ---- D: kill, relaunch into it -----------------------------------------------------------------------------------------------
if [ "$saved" -gt 0 ] && budget 130; then
    echo "== D relaunch ($(el) s): '$W'"
    start "launch 2" || exit 1
else
    [ "$saved" -gt 0 ] && { skip "reload: the saved values come back" "out of time ($(el) s)"; incomplete=1; }
    exit 1
fi

# ---- E: what came back -------------------------------------------------------------------------------------------------------
echo "== E compare ($(el) s)"
poll 30 ndump; dodump
mapfile -t D2 < <(tokens)
note "load 2: $DUMPHEAD | $(val AeroBlockSave.Counts) | last: $(val AeroBlockSave.Last | cut -c1-80)"
for w in "${WANT[@]}"; do
    read -r k c n v <<<"$w"; t=$(tok "$k" "$c" "${D2[@]}")
    expect "reload: $k@$c $n = $v (from its own data, rb:1)" "after: '${t:-no such block}'" okback "$t" "$n" "$v"
done
# every block not changed: as it was (values only - rb may differ: a first load of an old save takes the old lines)
unch=""; miss=""
for t0 in "${D1[@]}"; do
    key=${t0%%=*}; t2=$(printf '%s\n' "${D2[@]}" | grep -aF "$key=" | head -1)
    [ -z "$t2" ] && { miss+=" $key"; continue; }
    [ "$(echo "${t0#*=}" | sed 's/;rb:[01]//')" = "$(echo "${t2#*=}" | sed 's/;rb:[01]//')" ] || unch+=" $key: ${t0#*=} -> ${t2#*=}"
done
expect "reload: every aero block of $GRID came back (${#D1[@]} before, ${#D2[@]} after)" "missing:$(echo "$miss" | cut -c1-200)" test -z "$miss"
expect "reload: every block's state as saved (values)" "$(echo "${unch# }" | cut -c1-240)" test -z "$unch"
rs=$(val AeroBlockSave.Restored); lc=$(val AeroBlockSave.LegacyClaimed)
expect "reload: blocks restored from their own data (AeroBlockSave.Restored $rs > 0)" "Restored '$rs'" awk -v x="$rs" 'BEGIN{exit !(x ~ /^[0-9]+$/ && x > 0)}'
expect "reload: no old position-matched line claimed anything (AeroBlockSave.LegacyClaimed $lc = 0)" "LegacyClaimed '$lc'; $(grep -a -m1 '\[AERO\] .* restored\|settings loaded:' "$L" | cut -c60-200)" test "$lc" = 0

# ---- F: both runs' logs ------------------------------------------------------------------------------------------------------
echo "== F logs ($(el) s)"
for lf in "$L1" "$L"; do
    ex=$(ourexc "$lf")
    expect "log $(basename "$lf"): no exception in our code, no mod fault" "$(echo "$ex" | grep -c .) hit(s): $(echo "$ex" | head -3 | tr '\n' ' ')" test -z "$ex"
    hv=$(grep -a -m1 -e 'not migrated' -e 'Crash Handler\]: Fatal' "$lf" | cut -c1-160)
    expect "log $(basename "$lf"): no Havok migration assertion, no fatal crash" "$hv" test -z "$hv"
done
expect "game still running" "the game is not running" alive

# exit: 0 all ran and passed (SKIPs listed), 1 a FAIL, 2 refused, 3 no FAIL but phases cut by the deadline
exit $([ "$fail" -gt 0 ] && echo 1 || { [ -n "$incomplete" ] && echo 3 || echo 0; })
