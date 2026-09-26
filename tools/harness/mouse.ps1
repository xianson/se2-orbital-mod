# usage: mouse.ps1 -X <fraction 0..1> -Y <fraction 0..1> [-Click]
# Moves the cursor to a fraction of the primary screen (the game runs fullscreen there); -Click left-clicks.
param([double]$X, [double]$Y, [switch]$Click)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
"@
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$px = [int]($b.X + $X * $b.Width); $py = [int]($b.Y + $Y * $b.Height)
[OrbMouse]::SetCursorPos($px - 3, $py - 3) | Out-Null; Start-Sleep -Milliseconds 50; [OrbMouse]::mouse_event(0x1, 3, 3, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50; [OrbMouse]::SetCursorPos($px, $py) | Out-Null
if ($Click) { Start-Sleep -Milliseconds 150; [OrbMouse]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [OrbMouse]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero) }
"cursor at $px,$py of $($b.Width)x$($b.Height)"
