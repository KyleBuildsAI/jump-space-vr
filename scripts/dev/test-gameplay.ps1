<#
.SYNOPSIS
    Development helper: runs the mod on the OpenXR mock runtime and drives the game into the prologue level
    (welcome screen -> Play Prologue), then writes diagnostics and headset snapshots.
#>
[CmdletBinding()]
param(
    [int]$GameplayWaitSeconds = 75,
    [switch]$KeepRunning,
    [switch]$DebugAim
)

$ErrorActionPreference = 'Stop'
$keys = Join-Path $PSScriptRoot 'send-keys.ps1'

& (Join-Path $PSScriptRoot 'test-cycle.ps1') -Seconds 45 -KeepRunning -NoMockInput -DebugAim:$DebugAim | Out-Null
& $keys -Keys 0x0D -Delay 6          # accept the Early Access notice
Start-Sleep -Seconds 20              # intro cinematic
& $keys -Keys 0x1B -Delay 10         # skip it; the main menu appears
& $keys -Keys 0x28, 0x26, 0x0D -Delay 3   # select and start Play Prologue (first menu entry)
Start-Sleep -Seconds $GameplayWaitSeconds
& $keys -Keys 0x1B -Delay 3          # skip intro cinematic if one is still playing
& $keys -Keys 0x78, 0x79 -Delay 4    # F9 diagnostics, F10 snapshots

$log = Join-Path 'G:\SteamLibrary\steamapps\common\JumpSpace' 'MelonLoader\Latest.log'
$lines = Get-Content $log
$start = ($lines | Select-String 'diagnostics ====' | Select-Object -Last 1).LineNumber
if ($start) { $lines[($start - 1)..([Math]::Min($lines.Count - 1, $start + 40))] }
Select-String -Path $log -Pattern '\[ERROR\]|Exception' | Select-Object -Last 5 | ForEach-Object Line

if (-not $KeepRunning) { & (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null }
