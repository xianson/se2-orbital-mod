param([string]$Src, [string]$Dst, [int]$Width = 1280)
# Downscale an engine screenshot (8 MB PNG) to a JPEG for review.
Add-Type -AssemblyName System.Drawing
for ($i = 0; $i -lt 20; $i++) { try { $img = [System.Drawing.Image]::FromFile($Src); break } catch { Start-Sleep -Milliseconds 500 } }
$b = New-Object System.Drawing.Bitmap $img, $Width, ([int]($Width * $img.Height / $img.Width))
$b.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Jpeg); $img.Dispose(); $b.Dispose(); "saved $Dst"
