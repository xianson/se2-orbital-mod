Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $out = Join-Path $PSScriptRoot ("shot_" + (Get-Date -Format 'HHmmss') + ".png")
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  Set-Content (Join-Path $PSScriptRoot 'shot-result.txt') "ok $out $($b.Width)x$($b.Height)"
} catch {
  Set-Content (Join-Path $PSScriptRoot 'shot-result.txt') "fail $($_.Exception.Message)"
}
