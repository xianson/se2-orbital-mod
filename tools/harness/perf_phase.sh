#!/usr/bin/env bash
# perf_phase.sh NAME SECONDS: sample the server's frame times (status 'frame' line) every 2 s for SECONDS and
# print the phase's mean of the per-second worst gap, its max, and the mean tick (ms). For A/B load tests.
ST=/c/Users/slob/AppData/Local/Temp/OrbitalMod/status.txt
name="$1"; secs="${2:-20}"; ws=(); ms=()
end=$(( $(date +%s) + secs ))
until [ "$(date +%s)" -ge "$end" ]; do
    l=$(grep -a "^frame server" "$ST")
    w=$(echo "$l" | sed -n 's/.*server worst=\([0-9]*\)ms mean=\([0-9.]*\)ms.*/\1/p'); m=$(echo "$l" | sed -n 's/.*server worst=[0-9]*ms mean=\([0-9.]*\)ms.*/\1/p')
    [ -n "$w" ] && ws+=("$w") && ms+=("$m")
    sleep 2
done
printf '%s\n' "${ws[@]}" | awk -v n="$name" -v ms="${ms[*]}" '{s+=$1; if($1>mx)mx=$1; c++} END{split(ms,a," "); t=0; for(i in a)t+=a[i]; printf "%-28s worst avg %6.1f ms, max %5d ms, mean tick %5.2f ms (%d samples)\n", n, s/c, mx, t/length(a), c}'
grep -a "^frame server" "$ST" | sed 's/.*| stress /   stress: /'
