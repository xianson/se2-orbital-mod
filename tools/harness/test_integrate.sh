#!/usr/bin/env bash
# test_integrate.sh - the ONE in-game check of an integration batch (both mods' reorg, fixes and features) before it is merged.
# One launch, then PASS / FAIL lines from counters (aeroget: statics the mods count per tick), the game log and the harness
# status - nothing judged by eye. A check that cannot run here is SKIP with its reason (neither pass nor fail).
#
# usage: test_integrate.sh               the run. REFUSES while a game is running (it may be the user's): KILL_GAME=1 closes it
#                                        - only when nobody is playing. NOLAUNCH=1: the game already running, no launch.
#        test_integrate.sh --release     only release every dev override in the running game (after a killed run)
#        test_integrate.sh --audit-log [LOG]   the world-reset audit (one reset per world change) on a game log, the newest
#                                        by default - no game needed: run it on the user's own log after they changed worlds
# env: STRENGTH (1.5) ALT km (6) V m/s (300) WARP (10) WARPSECS (6) DEADLINE s (285)
# The launch is the Steam one (launch.sh): the game's LAST world and the Steam launch options' mods. It must be the SSTO's
# world (Concordia Research Facility 7) with both mods (Aerodynamics Mod + Orbital Mod) deployed from the integration branches.
#
# TIMELINE (the user's limit: 5 minutes end to end; past DEADLINE the phases left are skipped and the releases run):
#   0 s      guard (no game running, or KILL_GAME=1), close the game, the Steam launch
#   ~5-120   wait (polled): a NEW game log, the world in it ("beacon Root Entity" / "Loading Failed"), the harness answering
#            findgrids; cap 150 s
#   +5       A  load: both mods' log tags, AeroApi.Version 10 / OrbitalApi.Version 3, WorldReset.Count 0 in a fresh process
#   +15      B  the SSTO: found by name, teleported to, seated (polled), launched (ALT km, V m/s over Verdure), thrust forward
#   +10         perf, in that level flight: aerocost on, the status read twice 8 s apart (modcost, GC alloc), [AERO-COST]
#   +12..35  C  structure: Strength STRENGTH, damage on, attitude hold radial-out; the "failed whole ... cracked across it" line
#               polled (12 s; else turntest 90 z, 8 s more); the cracks finished (polled 12 s, the debris counter with them);
#               counters before/after; Strength 7 back at once
#   +30      D  warp: unseat (polled 8 s), 300 km up (clear of the debris), the player alone onto a 300 km Verdure orbit
#               (polled 20 s: seated=False, its own frame, Pe/Ap ~300 km; else SKIP), xWARP for WARPSECS: WarpBill's counters
#               against the rails clock (billed seconds) and the server's ticks (bills a tick)
#   +3       E  settings UI (`settingsui`, when the verb exists)
#   +3       F  the whole log since launch: our exceptions / faults, Havok, the game alive
#   +5       release (also from the EXIT trap): every dev override back; the summary
#   typical ~200 s, cap 285 s.
source "$(dirname "$0")/testlib.sh"
T0=$(date +%s)
STRENGTH=${STRENGTH:-1.5}; ALT=${ALT:-6}; V=${V:-300}; WARP=${WARP:-10}; WARPSECS=${WARPSECS:-6}; DEADLINE=${DEADLINE:-285}
ORBMOD="/c/Users/slob/Documents/SpaceEngineers2/Mods/Orbital Mod"; AEROMOD="/c/Users/slob/Documents/SpaceEngineers2/Mods/Aerodynamics Mod"
[ -n "$DRY" ] && alive() { true; }   # (offline dry runs: a mock harness, no game)
G=""; RELEASED=""; notes=(); incomplete=""

el() { echo $(( $(date +%s) - T0 )); }
# budget N: true while N more seconds still fit before the deadline
budget() { [ $(( $(date +%s) - T0 + $1 )) -le "$DEADLINE" ]; }
note() { notes+=("$1"); echo "   $1"; }
# poll SECONDS COMMAND...: true as soon as COMMAND succeeds (tried about once a second), false when SECONDS have passed by the
# clock (a try that asks the harness takes 1-2 s itself) or the game is gone
poll() { local end=$(( $(date +%s) + $1 )); shift; while :; do "$@" >/dev/null 2>&1 && return 0; alive || return 1; [ "$(date +%s)" -ge "$end" ] && return 1; sleep 1; done; }
logs() { since | grep -aq -- "$1"; }
ngrids() { ask findgrids | tr ' ' '\n' | grep -c '^[0-9][0-9]*='; }
num() { awk -v x="$1" 'BEGIN{exit !(x ~ /^-?[0-9.]+([eE][-+]?[0-9]+)?$/)}'; }
# d NEW OLD: NEW - OLD, or "?" when either is not a number
d() { if num "$1" && num "$2"; then awk -v a="$1" -v b="$2" 'BEGIN{printf "%g", a - b}'; else echo "?"; fi; }
ge() { num "$1" && awk -v a="$1" -v b="$2" 'BEGIN{exit !(a >= b)}'; }

# THE RELEASES: every override this run (or any harness script) may leave on, back to what the user plays with. Run once,
# from the EXIT trap too (a FAIL, ^C, an error); skipped when no game answers. A killed run (Stop-Process) runs no trap:
# then `test_integrate.sh --release`.
release() {
    [ -n "$RELEASED" ] && return; RELEASED=1
    alive || { echo "== release: no game running"; return; }
    local r=("cam off" "hold off" "thrust 0 0 0 0" "warp 1" "aerocost off" "statusfast off"
             "aeroset AeroEntryFx.TestSpeed 0" "aeroset FlightField.TrailDebug false"
             "aeroset ShipStructure.Strength 7" "aeroset StructureLoads.Damage true" "aeroset StructureLoads.Trace false")
    [ -n "$G" ] && r+=("gridhold $G 0" "griddamp $G on")
    echo "== release: ${#r[@]} overrides"
    ask "${r[@]}" | paste -d'|' <(printf '%s\n' "${r[@]}") - | sed 's/^/   /; s/|/  =>  /' | cut -c1-140
}

summary_all() {
    echo; echo "== test_integrate: $(el) s (deadline $DEADLINE s)"
    [ ${#notes[@]} -gt 0 ] && { echo "-- numbers"; for n in "${notes[@]}"; do echo "  $n"; done; }
    summary; local r=$?
    [ -n "$incomplete" ] && echo "== INCOMPLETE: phases were cut by the deadline - not a verdict on those checks (exit 3 when nothing failed)"
    return $r
}
# (this run's log, not testlib's newest-file guess: the stats log is written as often)
since() { tail -n +"$((MARK + 1))" "$L"; }
mark() { MARK=$(wc -l < "$L"); }

case "$1" in
    --release) release; exit 0 ;;
    --audit-log) f="${2:-$(LOGF)}"; echo "== world reset audit: $f"; worldreset_audit "$f"; exit $? ;;
esac

# ---- launch ------------------------------------------------------------------------------------------------------------------
if [ -z "$NOLAUNCH" ] && [ -z "$DRY" ]; then
    if alive && [ -z "$KILL_GAME" ]; then
        echo "REFUSED: Space Engineers 2 is running - it may be the user's game. KILL_GAME=1 closes it (only when nobody plays)."
        exit 2
    fi
fi
trap 'release; summary_all' EXIT
trap 'exit 130' INT TERM

OLDLOG=$(LOGF 2>/dev/null)
echo "== deployed: orbital $(git -C "$ORBMOD" log -1 --format='%h %s' 2>/dev/null | cut -c1-80) | aero $(git -C "$AEROMOD" log -1 --format='%h %s' 2>/dev/null | cut -c1-80)"
if [ -n "$NOLAUNCH" ] || [ -n "$DRY" ]; then
    L=$(LOGF); MARK=$(wc -l < "$L"); [ -n "$DRY" ] && MARK=0; MARK0=$MARK
    echo "== no launch: the running game, log $L from line $MARK"
else
    echo "== launch ($(el) s)"
    mkdir -p "$(dirname "$ST")"; touch "$(dirname "$ST")/dev.flag"   # (the DevHarness runs only where this flag exists)
    rm -f "$(dirname "$ST")/cmd.txt"
    launch
    loaded=""
    for i in $(seq 1 150); do
        L=$(LOGF 2>/dev/null)
        if [ -n "$L" ] && [ "$L" != "$OLDLOG" ] && grep -aq 'beacon Root Entity\|Loading Failed' "$L" 2>/dev/null; then loaded=1; break; fi
        sleep 1
    done
    MARK=0; MARK0=0
    [ -z "$loaded" ] && { record "launch: the world loaded within 150 s" "no new log with a loaded world (log: ${L:-none})"; exit 1; }
    grep -aq 'Loading Failed' "$L" && { record "launch: the world loaded" "Loading Failed in $L"; exit 1; }
    poll 60 gridsup || { record "launch: the harness answers" "findgrids unanswered 60 s after the load"; exit 1; }
fi
echo "   world: $(grep -a -m1 'Running application with arguments' "$L" | grep -o '\-start:[^-]*' | head -1) log $(basename "$L") ($(el) s)"
NPLANETS=$(since | grep -ac '\[ORBIT\] beacon Root Entity')

# ---- A: load -----------------------------------------------------------------------------------------------------------------
echo "== A load ($(el) s)"
expect "load: Orbital Mod running (its planet beacons)" "no '[ORBIT] beacon Root Entity' line" logs '\[ORBIT\] beacon Root Entity'
expect "load: Aerodynamics Mod running (its prefab injections)" "no '[AERO] Injected' line" logs '\[AERO\] Injected'
read -r av ov wr < <(val AeroApi.Version OrbitalMod.OrbitalApi.Version OrbitalMod.WorldReset.Count | tr '\n' ' ')
expect "load: AeroApi.Version = 10" "read '$av'" test "$av" = 10
expect "load: OrbitalApi.Version = 3" "read '$ov'" test "$ov" = 3
if [ -z "$NOLAUNCH" ] && [ -z "$DRY" ]; then
    expect "world reset: none at a fresh process's first load (WorldReset.Count 0, no reset line)" "Count '$wr'; $(since | grep -a -m1 'world reset:' | cut -c60-200)" \
        bash -c "[ '$wr' = 0 ] && ! grep -aq '\[ORBIT\] world reset:' '$L'"
else
    note "world reset: WorldReset.Count $wr in this process (not a fresh one: the audit below judges it)"
    worldreset_audit "$L" | sed 's/^/   /'
fi
skip "world reset: one '[ORBIT] world reset:' per quit-and-load" "no harness verb loads a world in-process (input injection does not reach the menus); after the user's next world change run 'test_integrate.sh --audit-log'"

# ---- B: the SSTO, seated and flying; perf in level flight -------------------------------------------------------------------
echo "== B the SSTO ($(el) s)"
if budget 120; then
    ask unseat >/dev/null
    G0=$(ask findgrids | tr ' ' '\n' | grep -i '=SSTO$' | head -1 | cut -d= -f1)
    if [ -z "$G0" ]; then
        record "setup: a grid named SSTO in this world" "none (findgrids) - launch the SSTO's world (Concordia Research Facility 7)"
    else
        ask "tpgrid $G0 15" "seat SSTO" >/dev/null
        seated() { G=$(ask hudcheck | grep -o "seated in grid [0-9]*" | grep -o "[0-9]*$"); [ -n "$G" ]; }
        poll 10 seated; seated
        expect "setup: seated in the SSTO" "hudcheck: $(ask hudcheck | cut -c1-120)" test -n "$G"
    fi
    if [ -n "$G" ]; then
        note "launch: $(ask "gridlaunch $G $ALT Verdure $V" | cut -c1-120)"
        sleep 2; ask "griddamp $G off" "thrust 0 0 -1 40" >/dev/null
        # perf: level flight (numbers only, no verdict: the old baseline - mods 140 ms/s, 1.74 MB/s - was a dotnet-trace
        # profile of the mods' frames, these are the mods' own timers and the process's GC total, a cruder measure)
        PM=$(wc -l < "$L"); ask "aerocost on" >/dev/null
        s1=$(grep -a '^modcost' "$ST"); w1=$(date +%s.%N); hold 8
        ask "aeroget StructureLoads.Breaks" >/dev/null   # (a command: the status is rewritten now)
        s2=$(grep -a '^modcost' "$ST"); w2=$(date +%s.%N); tl=$(grep -a '^time ' "$ST")
        ask "aerocost off" >/dev/null
        mb() { echo "$1" | sed -n 's/.* alloc \([0-9]*\)MB.*/\1/p'; }
        part() { echo "$1" | sed -n "s/.* $2 \([0-9.]*\)\/\([0-9.]*\)\/.*/\1 \2/p"; }
        tps() { echo "$tl" | sed -n "s/.* $1=\([0-9.]*\).*/\1/p"; }
        read -r ca cm <<<"$(part "$s2" client)"; read -r sa sm <<<"$(part "$s2" server)"
        allocr=$(awk -v a="$(mb "$s1")" -v b="$(mb "$s2")" -v t1="$w1" -v t2="$w2" 'BEGIN{ if (a == "" || b == "") print "?"; else printf "%.2f", (b - a) / (t2 - t1) }')
        orbms=$(awk -v c="${ca:-0}" -v s="${sa:-0}" -v tc="$(tps client)" -v ts="$(tps server)" 'BEGIN{printf "%.1f", c * tc + s * ts}')
        aero=$(tail -n +"$PM" "$L" | grep -a '\[AERO-COST\]' | awk '{
                 n++; for (i = 1; i <= NF; i++) if ($i ~ /^(blocks|patch|sim|draw)$/ && $(i + 1) ~ /ms\/s$/) { v = $(i + 1); sub(/ms\/s/, "", v); t[$i] += v }
               } END { if (!n) { print "no [AERO-COST] lines"; exit }
                 printf "%.1f ms/s (blocks %.1f patch %.1f sim %.1f draw %.1f; %d s)", (t["blocks"] + t["patch"] + t["sim"] + t["draw"]) / n, t["blocks"] / n, t["patch"] / n, t["sim"] / n, t["draw"] / n, n }')
        note "perf: aero $aero"
        note "perf: orbital client ${ca:-?} ms avg (worst ${cm:-?}) x $(tps client)/s + server ${sa:-?} ms (worst ${sm:-?}) x $(tps server)/s ~ $orbms ms/s"
        note "perf: process GC alloc $allocr MB/s (whole game) | baseline (trace, mods only): 140 ms/s, 1.74 MB/s"
    fi
else
    skip "setup: the SSTO" "out of time ($(el) s)"; NOSSTO="the SSTO's setup skipped (out of time)"; incomplete=1
fi

# ---- C: structure, the alerts, debris drag ----------------------------------------------------------------------------------
echo "== C structure ($(el) s)"
SC=(AeroGridComponent.CracksStarted AeroGridComponent.CracksDone AeroGridComponent.CracksRunning AeroGridComponent.CutsForced
    StructureTear.AnchorsMade StructureTear.AnchorsPeeled StructureTear.Faults AeroDebrisDrag.Steps OrbitalMod.StructureAlerts.Sections
    StructureLoads.Breaks)
sc() { mapfile -t "$1" < <(val "${SC[@]}"); }
if [ -n "$G" ] && budget 75; then
    sc B0; n0=$(ngrids); mark; MS=$MARK
    note "before: grids $n0 | $(paste -d= <(printf '%s\n' "${SC[@]}" | sed 's/.*\.//') <(printf '%s\n' "${B0[@]}") | tr '\n' ' ')"
    ask "aeroset ShipStructure.Strength $STRENGTH" "aeroset StructureLoads.Damage true" "hold radialout" >/dev/null
    CR='failed whole - [0-9]* of [0-9]* members cracked across it'
    if ! poll 12 logs "$CR"; then
        ask "hold off" "turntest 90 z" >/dev/null; poll 8 logs "$CR"
    fi
    ask "hold off" >/dev/null
    # the cracks finished (none running, as many done as started) - the debris steps polled alongside
    fin() { sc B1; [ "$(d "${B1[0]}" "${B0[0]}")" != 0 ] && [ "${B1[0]}" = "${B1[1]}" ] && [ "${B1[2]}" = 0 ]; }
    poll 12 fin; sc B1
    ask "aeroset ShipStructure.Strength 7" "thrust 0 0 0 0" >/dev/null
    debris() { local s; s=$(val AeroDebrisDrag.Steps); ge "$(d "$s" "${B0[7]}")" 1; }
    ge "$(d "${B1[7]}" "${B0[7]}")" 1 || poll 8 debris
    sc B1; apart() { n1=$(ngrids); [ "$n1" -gt "$n0" ]; }; poll 5 apart; n1=$(ngrids)
    note "after:  grids $n1 | $(paste -d= <(printf '%s\n' "${SC[@]}" | sed 's/.*\.//') <(printf '%s\n' "${B1[@]}") | tr '\n' ' ')"
    MARK=$MS
    expect "structure: a failed cut cracked across ('failed whole ... cracked across it')" "no such line; cuts: $(since | grep -a 'failed whole' | head -2 | cut -c60-200)" logs "$CR"
    expect "structure: every crack finished (CracksDone = CracksStarted, more than before; CracksRunning 0)" \
        "started ${B0[0]}->${B1[0]} done ${B0[1]}->${B1[1]} running ${B1[2]}" bash -c "[ '$(d "${B1[0]}" "${B0[0]}")' != 0 ] && [ '$(d "${B1[0]}" "${B0[0]}")' != '?' ] && [ '${B1[0]}' = '${B1[1]}' ] && [ '${B1[2]}' = 0 ]"
    expect "structure: tear anchors made (StructureTear.AnchorsMade +3 or more)" "+$(d "${B1[4]}" "${B0[4]}")" ge "$(d "${B1[4]}" "${B0[4]}")" 3
    expect "structure: tear anchors peeled (StructureTear.AnchorsPeeled +1 or more)" "+$(d "${B1[5]}" "${B0[5]}")" ge "$(d "${B1[5]}" "${B0[5]}")" 1
    expect "structure: no cut needed forcing apart (AeroGridComponent.CutsForced +0)" "+$(d "${B1[3]}" "${B0[3]}"); $(since | grep -a 'did not separate' | head -1 | cut -c60-200)" test "$(d "${B1[3]}" "${B0[3]}")" = 0
    expect "structure: no tear fault (StructureTear.Faults +0)" "+$(d "${B1[6]}" "${B0[6]}"); $(since | grep -a '\[AERO-TEAR\].*failed' | head -1 | cut -c60-200)" test "$(d "${B1[6]}" "${B0[6]}")" = 0
    expect "structure: the ship came apart (server grids more than before)" "grids $n0 -> $n1" test "$n1" -gt "$n0"
    expect "alerts: the seated ship's failure reported ([ORBIT-STRUCT] line; StructureAlerts.Sections +$(d "${B1[8]}" "${B0[8]}"))" \
        "no line; StructureAlerts.Status '$(val OrbitalMod.StructureAlerts.Status)'" logs '\[ORBIT-STRUCT\] seated ship:'
    expect "debris: drag acts on the fracture pieces (AeroDebrisDrag.Steps +$(d "${B1[7]}" "${B0[7]}"))" \
        "no steps - no loose pieces in air, or the job is not running (Fx.DebrisDrag / Debris.Drag off?)" ge "$(d "${B1[7]}" "${B0[7]}")" 1
    note "structure: breaks +$(d "${B1[9]}" "${B0[9]}"); $(since | grep -a 'failed whole' | wc -l) failed cut(s); last: $(val AeroGridComponent.CrackLast | cut -c1-160)"
    note "alerts: toast text = the [ORBIT-STRUCT] line (GameUi.Toast is called right after it unless the map is open; not seen on screen)"
else
    if [ -z "$G" ]; then why="${NOSSTO:-not seated in the SSTO}"; else why="out of time ($(el) s)"; incomplete=1; fi
    for c in "structure: crack / anchors / cuts / split" "alerts: [ORBIT-STRUCT]" "debris: AeroDebrisDrag.Steps"; do skip "$c" "$why"; done
fi

# ---- D: warp billing once a frame -------------------------------------------------------------------------------------------
echo "== D warp billing ($(el) s)"
# (2026-10-15: after C the player was still seated in the split SSTO in the air - `unseat` is async, so the `orbit` sent with it
#  in one batch found the player seated and was dropped ("seated: on-foot requests ... dropped"); the player stayed materialized
#  and warp fell back to x1 at once. Now: out of the seat (polled), 300 km straight up - clear of the debris, which a stow
#  within ServerFrames.AttachRadius (5 km) takes along - then the orbit, polled until the player alone is on rails.)
flightln() { ask flight | head -1; }
unseated() { flightln | grep -q '^seated=False'; }
# on rails: a frame of the player's own, its orbit card a ~300 km circle (the status's "orbit frame #N ... Pe X km  Ap Y km")
onrails() { flightln | grep -q '^seated=False frame=#' && stat '^orbit frame #' | sed -n 's/.*Pe \([0-9.]*\) km  Ap \([0-9.]*\) km.*/\1 \2/p' | awk '{ ok = $1 >= 250 && $1 <= 350 && $2 >= 250 && $2 <= 350 } END { exit !ok }'; }
if budget $((45 + WARPSECS)); then
    ask unseat >/dev/null
    railed=""; why=""
    if ! poll 8 unseated; then
        why="still seated 8 s after 'unseat' ($(flightln | cut -c1-100))"
    else
        r0=$(stat '^planet [0-9]* Verdure ' | sed -n 's/.* r0=\([0-9.]*\)km.*/\1/p')
        if num "$r0" && ge "$r0" 1; then ask "view Verdure $(awk -v r="$r0" 'BEGIN{printf "%.0f", r + 300}')" >/dev/null; sleep 2
        else note "warp: Verdure's radius not in the status ('$r0') - no hop away from the debris"; fi
        ask "orbit Verdure 300 300" >/dev/null
        if poll 20 onrails; then railed=1
        else why="not on rails 20 s after 'orbit Verdure 300 300': $(flightln | cut -c1-60) | $(stat '^orbit frame #' | cut -c1-90) | $(stat '^lastEvent' | cut -c1-120)"; fi
    fi
fi
if [ -n "$railed" ] && budget $((10 + WARPSECS)); then
    tline() { grep -a '^time ' "$ST"; }
    rails() { tline | sed -n 's/.* rails t=\([0-9.]*\).*/\1/p'; }
    wall() { tline | awk '{ split($2, h, ":"); printf "%.3f", h[1] * 3600 + h[2] * 60 + h[3] }'; }
    mapfile -t W0 < <(ask "warp $WARP" "aeroget OrbitalMod.WarpBill.Bills" "aeroget OrbitalMod.WarpBill.BilledSeconds" | tail -2 | sed 's/^[^=]*=//')
    r0=$(rails); c0=$(wall); hold "$WARPSECS"
    mapfile -t W1 < <(val OrbitalMod.WarpBill.Bills OrbitalMod.WarpBill.BilledSeconds)
    r1=$(rails); c1=$(wall); ts=$(tline | sed -n 's/.* x\([0-9.]*\) clock.*/\1/p'); tps=$(tline | sed -n 's/.* server=\([0-9.]*\).*/\1/p')
    ask "warp 1" >/dev/null
    note "warp: x$WARP for $(d "$c1" "$c0") s wall, rails clock +$(d "$r1" "$r0") s, bills +$(d "${W1[0]}" "${W0[0]}") billed +$(d "${W1[1]}" "${W0[1]}") s | $NPLANETS planet beacon(s) | server $tps ticks/s | warpbill: $(grep -a '^warpbill' "$ST" | cut -c10-120)"
    if ! num "${W1[0]}"; then
        skip "warp: billed once a frame (WarpBill.Bills / BilledSeconds)" "the counters are not in this build ('${W1[0]}') - feat/o-ingame not merged"
    elif [ "$ts" != "$WARP" ] && [ "${ts%.*}" != "$WARP" ]; then
        record "warp: billed once a frame" "warp did not hold x$WARP (now x$ts): $(since | grep -a 'Warp stopped\|warp x[0-9]* -> x1' | tail -1 | cut -c60-200)"
    else
        ratio=$(awk -v b="$(d "${W1[1]}" "${W0[1]}")" -v r="$(d "$r1" "$r0")" -v w="$WARP" 'BEGIN{ e = r * (w - 1) / w; if (e <= 0) print "?"; else printf "%.2f", b / e }')
        per=$(awk -v n="$(d "${W1[0]}" "${W0[0]}")" -v s="$(d "$c1" "$c0")" -v t="$tps" 'BEGIN{ if (s <= 0 || t <= 0) print "?"; else printf "%.2f", n / (s * t) }')
        expect "warp: billed seconds = the rails clock's surplus ((N-1)/N of its advance; 2 = billed twice) - ratio $ratio" "ratio $ratio (want 0.85-1.15)" \
            awk -v x="$ratio" 'BEGIN{exit !(x != "?" && x >= 0.85 && x <= 1.15)}'
        expect "warp: one bill a server tick - $per" "$per bills a tick (want 0.7-1.3)" awk -v x="$per" 'BEGIN{exit !(x != "?" && x >= 0.7 && x <= 1.3)}'
        [ "${NPLANETS:-0}" -lt 2 ] && note "warp: only $NPLANETS planet beacon(s) here - the double bill needed two (the checks still measure the rate)"
    fi
    note "station keeping: $(val OrbitalMod.StationKeepCharge.Status)"
elif [ -n "$why" ]; then
    skip "warp: billed once a frame" "the player could not be put on rails alone: $why"
else
    skip "warp: billed once a frame" "out of time ($(el) s)"; incomplete=1
fi

# ---- E: settings UI ----------------------------------------------------------------------------------------------------------
echo "== E settings UI ($(el) s)"
# (the verb needs the screen open first: open, select the tab, then report - settingsui.sh's sequence; closed after)
ask "settingsui open" >/dev/null; sleep 3; ask "settingsui select" >/dev/null; sleep 2; ask "settingsui report" >/dev/null; sleep 1
su=$(ask "settingsui report" | tail -1)
ask "settingsui close" >/dev/null
if [ -z "$su" ] || [ "$su" = "unknown command" ]; then
    skip "settings UI: the tab and its rows" "no 'settingsui' harness verb in this build (the settings UI branch not merged)"
else
    rows=$(echo "$su" | grep -oiE '[0-9]+ rows?|rows?[ =:]+[0-9]+' | grep -o '[0-9]*' | head -1)
    expect "settings UI: the tab found, with rows ($rows) - '$(echo "$su" | cut -c1-100)'" "reply: $(echo "$su" | cut -c1-160)" \
        bash -c "echo \"\$1\" | grep -qiE 'tab[^|]*(found|true|yes|shown)' && ! echo \"\$1\" | grep -qiE 'not found|no tab' && [ \"\${2:-0}\" -gt 0 ]" _ "$su" "$rows"
fi

# ---- F: the whole run --------------------------------------------------------------------------------------------------------
echo "== F the whole run ($(el) s)"
MARK=$MARK0
ex=$(ourexc "$L" "$((MARK + 1))")
expect "whole run: no exception in our code, no mod fault (log since launch)" "$(echo "$ex" | grep -c .) hit(s): $(echo "$ex" | head -3 | tr '\n' ' ')" test -z "$ex"
never "whole run: no Havok migration assertion" "not migrated"
never "whole run: no fatal crash" "Crash Handler\]: Fatal"
expect "whole run: game still running" "the game is not running" alive

# exit: 0 all ran and passed (SKIPs listed), 1 a FAIL, 2 refused (a game running), 3 no FAIL but phases cut by the deadline
exit $([ "$fail" -gt 0 ] && echo 1 || { [ -n "$incomplete" ] && echo 3 || echo 0; })
