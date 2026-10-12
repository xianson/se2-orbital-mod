# quiet.ps1 [back|restore] - SpaceEngineers2 behind whatever the user is doing: below-normal priority, its window sent to
# the bottom of the stack without activating it (minimized, the game stops serving the harness - its frame loop idles)
param([string]$mode = "back")
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
"@
foreach ($p in Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) {
  try { $p.PriorityClass = "BelowNormal" } catch {}
  $h = $p.MainWindowHandle
  if ($h -ne 0) {
    [W]::ShowWindow($h, 4) | Out-Null                                   # (shown, not activated: undo a minimize)
    [W]::SetWindowPos($h, [IntPtr]1, 0, 0, 0, 0, 0x0013) | Out-Null     # HWND_BOTTOM; NOSIZE | NOMOVE | NOACTIVATE
  }
}
"ok $mode"
