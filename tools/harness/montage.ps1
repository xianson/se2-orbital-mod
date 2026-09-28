# usage: montage.ps1 -Pattern <glob in docs/shots-work> -Out <file.jpg> [-Cols 3] [-W 640]
# Tiles screenshots into one contact sheet (each labelled with its file name) for quick review.
param([string]$Pattern, [string]$Out, [int]$Cols = 3, [int]$W = 640)
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot "..\..\docs\shots-work"
$files = Get-ChildItem -Path $dir -Filter $Pattern | Sort-Object Name
if ($files.Count -eq 0) { "no files"; exit }
$first = [System.Drawing.Image]::FromFile($files[0].FullName)
$H = [int]($W * $first.Height / $first.Width); $first.Dispose()
$rows = [math]::Ceiling($files.Count / $Cols)
$sheet = New-Object System.Drawing.Bitmap ($W * $Cols), ($H * $rows)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$font = New-Object System.Drawing.Font "Arial", 14, ([System.Drawing.FontStyle]::Bold)
for ($i = 0; $i -lt $files.Count; $i++) {
  $img = [System.Drawing.Image]::FromFile($files[$i].FullName)
  $x = ($i % $Cols) * $W; $y = [math]::Floor($i / $Cols) * $H
  $g.DrawImage($img, $x, $y, $W, $H); $img.Dispose()
  $g.FillRectangle([System.Drawing.Brushes]::Black, $x, $y, 90, 24)
  $g.DrawString($files[$i].BaseName.Split('_')[-1], $font, [System.Drawing.Brushes]::Yellow, ($x + 4), ($y + 2))
}
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Jpeg); $g.Dispose(); $sheet.Dispose()
"saved $Out"
