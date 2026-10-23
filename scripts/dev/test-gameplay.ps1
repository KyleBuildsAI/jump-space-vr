<#
.SYNOPSIS
    Development helper: runs the mod on the OpenXR mock runtime and drives the game into the prologue level
    (welcome screen -> Play Prologue), then writes diagnostics and headset snapshots.
#>
[CmdletBinding()]
param(
    [int]$GameplayWaitSeconds = 75,
    [switch]$KeepRunning,
    [switch]$DebugAim,
    # Enter the Hangar (second menu entry) instead of the prologue, then equip the first weapon slot.
    [switch]$Hangar
)

$ErrorActionPreference = 'Stop'
$keys = Join-Path $PSScriptRoot 'send-keys.ps1'

& (Join-Path $PSScriptRoot 'test-cycle.ps1') -Seconds 45 -KeepRunning -NoMockInput -DebugAim:$DebugAim | Out-Null
& $keys -Keys 0x0D -Delay 6          # accept the Early Access notice
Start-Sleep -Seconds 20              # intro cinematic
& $keys -Keys 0x1B -Delay 10         # skip it; the main menu appears
if ($Hangar) {
    & $keys -Keys 0x28, 0x28, 0x0D -Delay 3   # focus the menu, move to Enter Hangar, confirm
} else {
    & $keys -Keys 0x28, 0x26, 0x0D -Delay 3   # select and start Play Prologue (first menu entry)
}
Start-Sleep -Seconds $GameplayWaitSeconds
if ($Hangar) { & $keys -Keys 0x31 -Delay 3 }   # equip weapon slot 1
& $keys -Keys 0x78, 0x79 -Delay 4    # F9 diagnostics, F10 snapshots

$log = Join-Path 'G:\SteamLibrary\steamapps\common\JumpSpace' 'MelonLoader\Latest.log'
$lines = Get-Content $log
$start = ($lines | Select-String 'diagnostics ====' | Select-Object -Last 1).LineNumber
if ($start) { $lines[($start - 1)..([Math]::Min($lines.Count - 1, $start + 40))] }
Select-String -Path $log -Pattern '\[ERROR\]|Exception' | Select-Object -Last 5 | ForEach-Object Line

if (-not $KeepRunning) { & (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null }
