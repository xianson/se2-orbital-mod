#!/usr/bin/env bash
# usage: eshot.sh <name> [settleSec] "commands..." -- run commands, take an engine screenshot, save a JPEG to docs/shots-work/<name>.jpg
SP="$(dirname "$0")"; name=$1; shift; settle=5; if [[ "$1" =~ ^[0-9]+$ ]]; then settle=$1; shift; fi
out="$SP/../../docs/shots-work"; mkdir -p "$out"
p=$("$SP/orb.sh" --eshot "$settle" "$@" | grep '^engine shot:' | sed 's/^engine shot: //' | tr -d '\r')
powershell -NoProfile -ExecutionPolicy Bypass -File "$SP/shrink.ps1" -Src "$p" -Dst "$(cygpath -m "$out")/$name.jpg" | tail -1
