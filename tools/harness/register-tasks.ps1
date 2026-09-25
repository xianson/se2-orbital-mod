# Registers the two interactive scheduled tasks the harness needs (run once, as the logged-in user).
# The agent shell cannot open windows or capture the screen itself; tasks with an Interactive
# principal run in the user's desktop session and can.
$here = $PSScriptRoot
$pr = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$launch = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$here\launch-orbital.ps1`""
$shot   = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$here\shot.ps1`""
Register-ScheduledTask -TaskName 'OrbitalTestSE2' -Action $launch -Principal $pr -Force | Out-Null
Register-ScheduledTask -TaskName 'OrbitalShotSE2' -Action $shot -Principal $pr -Force | Out-Null
"registered OrbitalTestSE2 + OrbitalShotSE2 -> $here"
# Flags read by launch-orbital.ps1 (files next to it): orbital-only.flag (only this mod),
# no-orbital.flag (baseline without it), world.txt (save name to start instead of the default).
