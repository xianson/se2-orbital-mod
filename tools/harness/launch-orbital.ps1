$args0 = (Get-Content 'D:\aero\se2-launch-args.txt' -Raw).Trim()
$orb = 'C:\Users\slob\Documents\SpaceEngineers2\Mods\Orbital Mod\Orbital Mod.vrgproj'
if (Test-Path "$PSScriptRoot\orbital-only.flag") {
    # Replace the whole project list with just the Orbital Mod.
    $a = $args0 -replace '"-projectPaths:[^"]*"', ('"-projectPaths:' + $orb + '"')
} else {
    $a = $args0 -replace '"-projectPaths:', ('"-projectPaths:' + $orb + ';')
}
if (Test-Path "$PSScriptRoot\no-orbital.flag") { $a = $args0 }
if (Test-Path "$PSScriptRoot\world.txt") { $w = (Get-Content "$PSScriptRoot\world.txt" -Raw).Trim(); $a = $a -replace '"-start:[^"]*"', ('"-start:' + $w + '"') }
$game = 'C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2'
Set-Content -Path "$PSScriptRoot\last-launch-args.txt" -Value $a
Start-Process -FilePath "$game\SpaceEngineers2.exe" -ArgumentList $a -WorkingDirectory $game
