<#
.SYNOPSIS
    Development helper: optionally waits, presses keys in the game, then triggers diagnostics (F9) and headset
    snapshots (F10) and prints the newest diagnostics block.

.PARAMETER WaitSeconds
    Seconds to wait before doing anything (e.g. while a level loads).

.PARAMETER Keys
    Virtual-key codes to press first (e.g. 0x1B to skip a cinematic).
#>
[CmdletBinding()]
param(
    [int]$WaitSeconds = 0,
    [int[]]$Keys = @(),
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace'
)

$ErrorActionPreference = 'Stop'
$sendKeys = Join-Path $PSScriptRoot 'send-keys.ps1'

if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }
if ($Keys.Count -gt 0) { & $sendKeys -Keys $Keys -Delay 3 }
& $sendKeys -Keys 0x78, 0x79 -Delay 4

$lines = Get-Content (Join-Path $GamePath 'MelonLoader\Latest.log')
$start = ($lines | Select-String 'diagnostics ====' | Select-Object -Last 1).LineNumber
if ($start) { $lines[($start - 1)..([Math]::Min($lines.Count - 1, $start + 45))] }

$snapshots = Join-Path $GamePath 'UserData\JumpSpaceVR'
Get-ChildItem $snapshots -Filter 'headset-*.bmp' | Sort-Object LastWriteTime | Select-Object -Last 2 | ForEach-Object FullName
