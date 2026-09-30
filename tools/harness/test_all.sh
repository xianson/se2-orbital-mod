#!/usr/bin/env bash
# test_all.sh — every self-checking suite, one after the other; exit 0 only if all pass.
cd "$(dirname "$0")"
rc=0
for t in test_joints.sh test_regress.sh test_soak.sh; do
    echo "######## $t"
    ./"$t" > "/tmp/orbital_$t.out" 2>&1 || rc=1
    grep -a -e "^PASS" -e "^FAIL" -e "== summary" "/tmp/orbital_$t.out"
done
echo; [ $rc -eq 0 ] && echo "ALL SUITES PASSED" || echo "SOME SUITES FAILED"
exit $rc
