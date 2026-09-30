#!/usr/bin/env bash
# test_regress.sh — self-checking regression suite for the orbital mod's verified behaviours.
# Relaunches the game ('Orbital Test Campaign 2'), runs each scenario, checks the log and the status
# numbers itself, prints PASS / FAIL and a summary. Nothing to do by hand.
source "$(dirname "$0")/testlib.sh"

echo "== relaunch"; "$SP/relaunch.sh" 2>&1 | tail -1; hold 15
mark; START=$MARK

# 1. A low orbit (20 x 20 km, on Verdure's border) stows clean: no rock captured, no slot moves, no split,
#    the spawn guard up, the orbit unchanged.
mark; send "orbit Verdure 20 20"; hold 60
never "border stow: no split" "SPLIT player"
never "border stow: no rock captured as anchor" "anchor -> an asteroid"
never "border stow: no dirty-slot move" "moved to clear slot"
expect "border stow: spawn guard up" "no spawn guard zone" grep -aq "spawn guard: frame" <(since)
expect "border stow: orbit still 20 x 20" "orbit changed: $(stat '^orbit frame' | cut -c1-120)" grep -aq "Pe 20.0 km  Ap 20.0 km" <(stat "^orbit frame")

# 4. Dampeners hold station exactly: the offset from the anchor stays put through x1 and x10.
send "target off" "orbit Verdure 400 400" "devrider on 2" "player dampeners on"; hold 20
r() { stat "^relnow" | awk '{print sqrt($3*$3 + $6*$6)}'; }
r1=$(r); send "warp 10"; hold 30; send "warp 1"; hold 3; r2=$(r)
d=$(awk -v a="${r1:-0}" -v b="${r2:-99999}" 'BEGIN{d=a-b; if(d<0)d=-d; print d}')
expect "dampeners: hold station exactly (<10 m through x10)" "offset ${r1:-?} -> ${r2:-?} m" awk -v d="$d" -v a="${r1:-0}" 'BEGIN{exit !(a>0 && d<10)}'
send "devrider off"

# 2. Ring rocks: a path crossing Verdure's ring predicts rendezvous.
send "orbit Verdure 90 40 0"; hold 8
n=$(reply "ringrocks" | sed -n 's/.* \([0-9]*\) pass(es) on the plan.*/\1/p')
expect "ring rocks: a crossing predicts rendezvous" "passes: '${n:-none}'" test "${n:-0}" -ge 1

# 3. Ring rocks: co-orbiting inside the ring, the nearest rock becomes a site and its rocks come out.
mark; send "orbit Verdure 65 65 0"
check "ring rocks: a near rock becomes a site" "ring rock Oblivara #[0-9]* near" 40 5
check "ring rocks: its rocks come out" "ROCKS out: Oblivara #" 40 5

# 3b. The orbit command takes you out of a rock's frame (a site) onto the orbit you asked for.
send "orbit Verdure 300 300"; hold 10
expect "orbit command: leaves a rock's frame for the asked orbit" "orbit now: $(stat '^orbit frame' | cut -c1-120)" awk '/Pe 30[0-9.]* km  Ap 30[0-9.]* km/ && $0 !~ /alt [0-9]{1,2}\./ {ok=1} END{exit !ok}' <(stat "^orbit frame" | sed -n 's/.*\(alt [0-9.]* km\).*\(Pe [0-9.]* km  Ap [0-9.]* km\).*/\1 \2/p')

# 5. The map: the ring on Verdure's globe (the game's own texture), the Rendezvous tab, the porkchop.
send "orbit Verdure 160 140" "map on"; hold 6
send "mapcam at Verdure" "mapcam 20 35" "mapcam zoom 0.03"; hold 6
expect "map: Verdure's ring on its globe" "$(reply mapring | cut -c1-120)" grep -aq "shown" <(reply "mapring")
tab=$(reply "rvtab on")
expect "map: a real Rendezvous tab" "$(echo "$tab" | cut -c1-160)" grep -aq -e "tab added" -e "tab already there" <(echo "$tab")
send "target Kemik"; hold 10
expect "map: porkchop to Kemik solved" "$(reply 'rvtab on' | cut -c1-200)" grep -aq "porkchop best" <(reply "rvtab on")

# 5b. Nodes on the Rendezvous tab's relative plot (the map's own editor, driven like a mouse): click the
#     path to add one, drag its prograde handle, slide it along the path.
send "target Oblivara" "node clear" "rvtab on"; hold 6
send "node clickat 10"; hold 4
nl=$(reply "node")
expect "rendezvous tab: a click on the path adds a node" "$(echo "$nl" | cut -c1-160)" grep -aq "\[0\] in" <(echo "$nl")
send "node select 0" "node pull P 80 2"; hold 5
pro=$(reply "node" | sed -n 's/.*\[0\] in [^P]*P \(-\{0,1\}[0-9.]*\).*/\1/p')
expect "rendezvous tab: dragging the prograde handle changes the burn" "prograde '${pro:-?}'" awk -v p="${pro:-0}" 'BEGIN{exit !(p>0.5 || p<-0.5)}'
send "node slide 0 5"; hold 5
tm=$(reply "node" | sed -n 's/.*\[0\] in \([0-9]*\):.*/\1/p')
expect "rendezvous tab: sliding a node moves it along the path" "node now in '${tm:-?}' min" awk -v m="${tm:-0}" 'BEGIN{exit !(m>=3 && m<=6)}'
send "node clear"

# 6. In flight with a target: the HUD's relative plot about it.
send "rvtab off" "map off" "target Oblivara"; hold 6
expect "hud: relative plot about the target" "no relative plot in the status" grep -aq "^relnow" "$ST"
send "target off"

# The whole run: no Havok migration assertion, no fatal crash, still running.
MARK=$START
never "whole run: no Havok migration assertion" "not migrated"
never "whole run: no fatal crash" "Crash Handler\]: Fatal"
expect "whole run: game still running" "the game is not running" alive
summary
