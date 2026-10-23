<#
.SYNOPSIS
    Runs a VR test scenario (scenarios/*.txt) against the OpenXR mock runtime and reports PASS/FAIL.

.DESCRIPTION
    Builds and deploys the mod, launches the game with the mock runtime and the scenario, waits for the scenario's
    "[scenario] DONE" line (or the timeout), prints every scenario result and exits with the number of failures.
    No headset or person is needed: the scenario moves the simulated head and hands and presses inputs itself.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\dev\run-scenario.ps1 -Scenario scenarios\menu-and-pointer.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Scenario,
    [int]$TimeoutSeconds = 420,
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scenarioPath = (Resolve-Path (Join-Path $RepoRoot $Scenario) -ErrorAction SilentlyContinue)
if (-not $scenarioPath) { $scenarioPath = Resolve-Path $Scenario }
$log = Join-Path $GamePath 'MelonLoader\Latest.log'

& (Join-Path $PSScriptRoot 'test-cycle.ps1') -GamePath $GamePath -Seconds 5 -KeepRunning -NoMockInput -Scenario $scenarioPath | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (-not (Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue)) { Write-Warning 'The game exited.'; break }
    if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match '\[scenario\] DONE') { $done = $true; break }
}

$results = Select-String -Path $log -Pattern '\[scenario\]' | ForEach-Object { $_.Line }
$results
if (-not $done) { Write-Warning "Scenario did not finish within $TimeoutSeconds s." }

if (-not $KeepRunning) { & (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null }

$failed = ($results | Select-String 'FAIL').Count
if (-not $done) { $failed++ }
exit $failed
