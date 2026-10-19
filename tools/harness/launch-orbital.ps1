# the launch args file (2026-10-18, D:\aero retired): $env:SE2_LAUNCH_ARGS, else se2-launch-args.txt in the Aero
# mod's tools/shared ($env:AERO_TOOLS, else the sibling checkout's) - the file tools/shared/launch-se2.bat reads
$argsFile = $env:SE2_LAUNCH_ARGS
if (-not $argsFile) {
    $tools = $env:AERO_TOOLS
    if (-not $tools) { $tools = Join-Path $PSScriptRoot '..\..\..\Aerodynamics Mod\tools\shared' }
    $argsFile = Join-Path $tools 'se2-launch-args.txt'
}
$args0 = (Get-Content $argsFile -Raw).Trim()
$orb = 'C:\Users\slob\Documents\SpaceEngineers2\Mods\Orbital Mod\Orbital Mod.vrgproj'
if (Test-Path "$PSScriptRoot\orbital-only.flag") {
    # Replace the whole project list with just the Orbital Mod.
    $a = $args0 -replace '"-projectPaths:[^"]*"', ('"-projectPaths:' + $orb + '"')
} else {
    $a = $args0 -replace '"-projectPaths:', ('"-projectPaths:' + $orb + ';')
}
if (Test-Path "$PSScriptRoot\no-orbital.flag") { $a = $args0 }
if (Test-Path "$PSScriptRoot\world.txt") {
    $w = (Get-Content "$PSScriptRoot\world.txt" -Raw).Trim()
    # "content:<folder>" starts a NEW game from a shipped world (GameData\Vanilla\Worlds\<folder>).
    if ($w.StartsWith('content:')) { $a = $a -replace '"-start:[^"]*"', ('"-startContent:' + $w.Substring(8) + '"') }
    else { $a = $a -replace '"-start:[^"]*"', ('"-start:' + $w + '"') }
}
# Test launches never send crash reports (the harness provokes crashes while testing).
$a = $a + ' -forceDisableCrashReporting'
$game = 'C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2'
Set-Content -Path "$PSScriptRoot\last-launch-args.txt" -Value $a
Start-Process -FilePath "$game\SpaceEngineers2.exe" -ArgumentList $a -WorkingDirectory $game
