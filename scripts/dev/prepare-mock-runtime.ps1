<#
.SYNOPSIS
    Development helper: copies Unity's OpenXR Mock Runtime out of the downloaded package and prints the
    path of its manifest. Requires scripts/install.ps1 to have run.
#>
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mockSource = Join-Path $RepoRoot 'vendor\com.unity.xr.openxr-1.18.0\package\Runtime\MockRuntime'
if (-not (Test-Path $mockSource)) { throw 'OpenXR package not found in vendor/. Run scripts/install.ps1 first.' }

$mockDir = Join-Path $RepoRoot 'vendor\mock-runtime'
New-Item -ItemType Directory -Force -Path (Join-Path $mockDir 'windows\x64') | Out-Null
Copy-Item (Join-Path $mockSource 'unity-mock-runtime.json') $mockDir -Force
Copy-Item (Join-Path $mockSource 'windows\x64\mock_runtime.dll') (Join-Path $mockDir 'windows\x64') -Force
Copy-Item (Join-Path $mockSource 'windows\x64\mock_api.dll') (Join-Path $mockDir 'windows\x64') -Force

Join-Path $mockDir 'unity-mock-runtime.json'
