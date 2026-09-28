# usage: key.ps1 -Scan <hex scan code> [-HoldMs 80]
# Focuses the game window and presses one key by hardware scan code (SendInput), for testing the game
# without the mod's harness (e.g. V = 0x2F toggles the camera).
param([int]$Scan = 0x2F, [int]$HoldMs = 80)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbKey {
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static void Press(ushort scan, bool up) {
        var i = new INPUT[1]; i[0].type = 1; i[0].ki.wScan = scan; i[0].ki.dwFlags = 0x0008u | (up ? 0x0002u : 0u);
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@
$p = Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) { [OrbKey]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300 }
[OrbKey]::Press([uint16]$Scan, $false); Start-Sleep -Milliseconds $HoldMs; [OrbKey]::Press([uint16]$Scan, $true)
"pressed scan 0x{0:X2} (window: {1})" -f $Scan, ($(if ($p) { $p.MainWindowHandle } else { 'none' }))
