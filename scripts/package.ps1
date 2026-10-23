<#
.SYNOPSIS
    Builds a release zip (dist/JumpSpaceVR-<version>.zip) that installs without the .NET SDK.

.DESCRIPTION
    The zip contains the prebuilt mod, the install/uninstall scripts, a double-click installer and the README.
    MelonLoader and Unity's OpenXR runtime are still downloaded by the installer from their official sources.
#>
[CmdletBinding()]
param(
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $RepoRoot 'src\JumpSpaceVR\JumpSpaceVR.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$buildDir = Join-Path $RepoRoot 'dist\build'
dotnet build $project -c Release "-p:GamePath=$GamePath" -o $buildDir -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$staging = Join-Path $RepoRoot "dist\JumpSpaceVR-$version"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path (Join-Path $staging 'scripts') | Out-Null

Copy-Item (Join-Path $buildDir 'JumpSpaceVR.dll') $staging
Copy-Item (Join-Path $RepoRoot 'README.md') $staging
Copy-Item (Join-Path $RepoRoot 'scripts\install.ps1'), (Join-Path $RepoRoot 'scripts\uninstall.ps1') (Join-Path $staging 'scripts')
Set-Content -Path (Join-Path $staging 'Install.bat') -Value @'
@echo off
rem Installs Jump Space VR. Close the game first.
rem A PowerShell 7 window leaks its module path into Windows PowerShell; start from the defaults.
set PSModulePath=
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
pause
'@
Set-Content -Path (Join-Path $staging 'Uninstall.bat') -Value @'
@echo off
rem Removes Jump Space VR (keeps MelonLoader so other mods keep working).
set PSModulePath=
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" %*
pause
'@
Copy-Item (Join-Path $RepoRoot 'docs\QUICKSTART.txt') $staging

$zip = Join-Path $RepoRoot "dist\JumpSpaceVR-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# Entries are added one by one with '/' separators: Windows PowerShell 5 (Compress-Archive and ZipFile) writes '\',
# which some extraction tools reject.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Write-Host "Created $zip"
