<#
.SYNOPSIS
    Development helper: rebuilds and deploys the mod, launches the game against the OpenXR Mock Runtime,
    waits, then prints the mod's log lines and any crash reported by Windows.

.PARAMETER Seconds
    How long to let the game run before collecting results.

.PARAMETER KeepRunning
    Leave the game running afterwards (default: stop it).
#>
[CmdletBinding()]
param(
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace',
    [int]$Seconds = 60,
    [switch]$KeepRunning,
    [switch]$RealRuntime
)

$ErrorActionPreference = 'Stop'

function Set-RuntimeOverride([string]$RuntimeJson) {
    $configFile = Join-Path $GamePath 'UserData\MelonPreferences.cfg'
    # TOML literal string: backslashes in Windows paths need no escaping.
    $line = "OpenXRRuntimeJson = '$RuntimeJson'"
    $content = if (Test-Path $configFile) { Get-Content $configFile -Raw } else { '' }
    if ($content -match '(?m)^OpenXRRuntimeJson = .*$') {
        $content = [regex]::Replace($content, '(?m)^OpenXRRuntimeJson = .*$', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $line })
    } elseif ($content -match '(?m)^\[JumpSpaceVR\]') {
        $content = [regex]::Replace($content, '(?m)^\[JumpSpaceVR\]\r?$', "[JumpSpaceVR]`r`n$line")
    } else {
        $content += "`r`n[JumpSpaceVR]`r`n$line`r`n"
    }
    Set-Content -Path $configFile -Value $content -NoNewline
}

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

& (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null
Start-Sleep -Seconds 2

dotnet build (Join-Path $RepoRoot 'src\JumpSpaceVR\JumpSpaceVR.csproj') -c Release "-p:GamePath=$GamePath" -o (Join-Path $RepoRoot 'dist') -v quiet -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item (Join-Path $RepoRoot 'dist\JumpSpaceVR.dll') (Join-Path $GamePath 'Mods') -Force

$runtimeJson = ''
if (-not $RealRuntime) { $runtimeJson = & (Join-Path $PSScriptRoot 'prepare-mock-runtime.ps1') }
Set-RuntimeOverride $runtimeJson

$launchTime = Get-Date
Start-Process 'steam://rungameid/1757300'
Start-Sleep -Seconds $Seconds

$running = [bool](Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue)
Write-Host "Game still running: $running"
Write-Host '---- mod log ----'
Select-String -Path (Join-Path $GamePath 'MelonLoader\Latest.log') -Pattern 'JumpSpaceVR|\[ERROR\]|Exception' |
    ForEach-Object { $_.Line }

$crashes = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $launchTime } -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -in 1000, 1023, 1026 -and $_.Message -match 'Jump Space' }
foreach ($crash in $crashes) {
    Write-Host '---- crash ----'
    Write-Host $crash.Message
}

if (-not $KeepRunning) {
    & (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null
}
