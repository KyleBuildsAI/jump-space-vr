<#
.SYNOPSIS
    Development helper: focuses the Jump Space window and taps virtual-key codes (e.g. 0x0D Enter, 0x78 F9).
#>
param([int[]]$Keys, [int]$Delay = 2)
if (-not ('GameKeys' -as [type])) {
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class GameKeys {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
  public static void Tap(byte vk){ byte sc=(byte)MapVirtualKey(vk,0); keybd_event(vk,sc,0,UIntPtr.Zero); System.Threading.Thread.Sleep(120); keybd_event(vk,sc,2,UIntPtr.Zero); }
  public static bool Focus(IntPtr h){ keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); ShowWindow(h,9); return SetForegroundWindow(h); }
}
"@
}
$p = Get-Process 'Jump Space'
[GameKeys]::Focus($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
foreach ($k in $Keys) { [GameKeys]::Tap([byte]$k); Start-Sleep $Delay }
