<#
.SYNOPSIS
    Runs every scenario in scenarios/ (except the long soak test unless -IncludeSoak) and prints a summary.
    Exit code = number of scenarios with failures.
#>
param(
    [switch]$IncludeSoak,
    # Name filter, for example 'smoke*'.
    [string]$Filter = '*',
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace'
)

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$summary = @()
foreach ($file in Get-ChildItem (Join-Path $RepoRoot 'scenarios') -Filter "$Filter.txt" | Sort-Object Name) {
    if ($file.BaseName -eq 'soak' -and -not $IncludeSoak) { continue }
    $output = & (Join-Path $PSScriptRoot 'run-scenario.ps1') -Scenario $file.FullName -GamePath $GamePath -TimeoutSeconds 1500
    $done = "$($output | Select-String -CaseSensitive '\[scenario\] DONE')" -replace '.*\[scenario\] ', ''
    $passed = $done -match 'failed=0$'
    $summary += [pscustomobject]@{ Scenario = $file.BaseName; Result = $(if ($passed) { 'PASS' } else { 'FAIL' }); Details = $(if ($done) { $done } else { 'did not finish' }) }
    $output | Select-String -CaseSensitive '\] FAIL ' | ForEach-Object { Write-Host "  $($file.BaseName): $($_.Line)" }
}
$summary | Format-Table -AutoSize | Out-String | Write-Host
exit ($summary | Where-Object Result -eq 'FAIL').Count
