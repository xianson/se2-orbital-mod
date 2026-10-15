#!/usr/bin/env bash
# launch: the game started ONCE (2026-10-12: two SpaceEngineers2 processes were found running together - a Steam launch
# overlapping one still starting or closing - and the machine ran short of memory). Every game process closed, none left
# (up to 30 s), the Steam launch, then for 90 s any second INSTANCE closed, the first one kept. An instance is a top-level
# SpaceEngineers2 process with its tree: the game starts as a small launcher whose child (same name, same start second) is
# the game itself - counting processes instead of instances killed the real game (2026-10-12).
launch() {
  powershell -NoProfile -Command "Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force"
  for i in $(seq 1 30); do [ -z "$(powershell -NoProfile -Command "(Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue).Id")" ] && break; sleep 1; done
  sleep 2
  powershell -NoProfile -Command "Start-Process 'steam://rungameid/1133870'"
  ( for i in $(seq 1 45); do sleep 2
      powershell -NoProfile -Command "\$all = @(Get-CimInstance Win32_Process -Filter \"name='SpaceEngineers2.exe'\"); \$ids = \$all | ForEach-Object { \$_.ProcessId }; \$top = @(\$all | Where-Object { \$ids -notcontains \$_.ParentProcessId } | Sort-Object CreationDate); if (\$top.Count -gt 1) { \$top | Select-Object -Skip 1 | ForEach-Object { taskkill /T /F /PID \$_.ProcessId | Out-Null }; 'closed a second game instance' }"
    done ) &
}
