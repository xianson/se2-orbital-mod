#!/usr/bin/env bash
# test_fast.sh — the fast gate (no game, ~20 s): the mod's scripts compiled exactly as the game compiles
# them (ModCheck), then every offline suite in Tests/ (the game-free core: orbits, frames, persistence,
# rendezvous, time, sensing). Exit 0 only if everything passes. Run on every change; the in-game suites
# (tools/harness/test_*.sh) are for engine-facing work and releases.
cd "$(dirname "$0")/.."
# the shared dev tools live in the Aerodynamics Mod (tools/shared, 2026-10-18: D:/aero retired): the sibling checkout's,
# AERO_TOOLS=<a tools/shared> overrides it; MODCHECK=<a ModCheck.csproj> overrides the ModCheck alone
AERO_TOOLS=${AERO_TOOLS:-../Aerodynamics Mod/tools/shared}
MODCHECK=${MODCHECK:-$AERO_TOOLS/ModCheck/ModCheck.csproj}   # (env override: a private ModCheck copy, own obj/)
rc=0; t0=$(date +%s)

s=$(date +%s)
out=$(dotnet build "$MODCHECK" -nologo -v q "-p:ModDir=$(cygpath -w "$PWD")" 2>&1)   # (this checkout, not the live mod folder)
if echo "$out" | grep -q "Build succeeded"; then echo "PASS  compile (ModCheck)  $(( $(date +%s)-s ))s"
else rc=1; echo "FAIL  compile (ModCheck)"; [ -f "$MODCHECK" ] || echo "      no $MODCHECK: set AERO_TOOLS (the Aero repo's tools/shared) or MODCHECK"; echo "$out" | grep -E " error " | sort -u | head -20; fi

out=$(tools/check_contract.sh 2>&1) || rc=1; echo "$out"

for d in Tests/*/; do
    [ -n "$(ls "$d"*.csproj 2>/dev/null)" ] || continue
    s=$(date +%s)
    out=$(dotnet run --project "$d" -c Release 2>&1); r=$?
    line=$(echo "$out" | grep -a -i "passed" | tail -1 | tr -d '=' | sed 's/^ *//')
    if [ $r -eq 0 ]; then echo "PASS  $(basename "$d")  $(( $(date +%s)-s ))s  $line"
    else rc=1; echo "FAIL  $(basename "$d")  $line"; echo "$out" | grep -a "FAIL\|error" | head -10 | sed 's/^/      /'; fi
done
echo "== fast gate: $([ $rc -eq 0 ] && echo PASSED || echo FAILED) in $(( $(date +%s)-t0 ))s"
exit $rc
