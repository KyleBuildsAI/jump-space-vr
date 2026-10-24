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

# The game can save its settings mid-run (for example when a scenario changes one in the VR menu), which would write
# the test runtime and developer options back to disk; remember the user's runtime and reset them afterwards.
$configFile = Join-Path $GamePath 'UserData\MelonPreferences.cfg'
$userRuntime = ''
if ((Test-Path $configFile) -and ((Get-Content $configFile -Raw) -match "(?m)^OpenXRRuntimeJson = '(.*)'") -and
    -not $Matches[1].Contains('mock-runtime')) { $userRuntime = $Matches[1] }

# Keep a copy of the whole settings file: tests start from default settings, scenarios may change settings in the VR
# menu (even reset them all), and the game saves whatever it has in memory. The copy goes back once the game is stopped.
$settingsBackup = Join-Path ([System.IO.Path]::GetTempPath()) 'JumpSpaceVR-MelonPreferences.backup.cfg'
if (Test-Path $configFile) { Copy-Item $configFile $settingsBackup -Force }

& (Join-Path $PSScriptRoot 'test-cycle.ps1') -GamePath $GamePath -Seconds 5 -KeepRunning -NoMockInput -Scenario $scenarioPath -DefaultSettings | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (-not (Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue)) { Write-Warning 'The game exited.'; break }
    if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match '\[scenario\] DONE') { $done = $true; break }
}

$results = Select-String -Path $log -Pattern '\[scenario\]' | ForEach-Object { $_.Line }

# MelonLoader keeps only its last few logs; keep each scenario's own copy for looking into failures later.
$logArchive = Join-Path ([System.IO.Path]::GetTempPath()) 'JumpSpaceVR-scenario-logs'
New-Item -ItemType Directory -Force -Path $logArchive | Out-Null
Copy-Item $log (Join-Path $logArchive ("{0}-{1:yyyyMMdd-HHmmss}.log" -f [System.IO.Path]::GetFileNameWithoutExtension($scenarioPath), (Get-Date))) -ErrorAction SilentlyContinue
$results
if (-not $done) { Write-Warning "Scenario did not finish within $TimeoutSeconds s." }

if (-not $KeepRunning) {
    & (Join-Path $PSScriptRoot 'stop-game.ps1') | Out-Null
    if (Test-Path $settingsBackup) { Copy-Item $settingsBackup $configFile -Force }
}
& (Join-Path $PSScriptRoot 'reset-dev-settings.ps1') -GamePath $GamePath -RuntimeJson $userRuntime

$failed = ($results | Select-String 'FAIL').Count
if (-not $done) { $failed++ }
exit $failed
