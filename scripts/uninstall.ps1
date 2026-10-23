<#
.SYNOPSIS
    Removes the Jump Space VR mod and its OpenXR runtime files. MelonLoader is kept unless -RemoveMelonLoader is passed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GamePath,
    [switch]$RemoveMelonLoader
)

$ErrorActionPreference = 'Stop'

$dataDir = Get-ChildItem -Path $GamePath -Directory -Filter '*_Data' | Select-Object -First 1
$targets = @(
    (Join-Path $GamePath 'Mods\JumpSpaceVR.dll'),
    (Join-Path $dataDir.FullName 'Plugins\x86_64\UnityOpenXR.dll'),
    (Join-Path $dataDir.FullName 'Plugins\x86_64\openxr_loader.dll'),
    (Join-Path $dataDir.FullName 'UnitySubsystems\UnityOpenXR')
)
if ($RemoveMelonLoader) {
    $targets += (Join-Path $GamePath 'MelonLoader'), (Join-Path $GamePath 'version.dll')
}

foreach ($target in $targets) {
    if (Test-Path $target) {
        Remove-Item -Recurse -Force $target
        Write-Host "Removed $target"
    }
}
Write-Host 'Done. Use Steam > Verify integrity of game files if anything still looks wrong.'
