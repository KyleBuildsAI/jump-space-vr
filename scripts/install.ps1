<#
.SYNOPSIS
    Installs the Jump Space VR mod.

.DESCRIPTION
    1. Installs MelonLoader (if missing) into the game folder.
    2. Downloads the Unity OpenXR plugin package from Unity's package registry and copies its native
       runtime (UnityOpenXR.dll + openxr_loader.dll) and subsystem manifest into the game's data folder.
    3. Builds JumpSpaceVR.dll and copies it into the game's Mods folder.

    Run with the game closed. Re-run after a game update if VR stops working.

.PARAMETER GamePath
    Jump Space install folder. Auto-detected from Steam when omitted.

.PARAMETER SkipBuild
    Use an existing build in dist/ instead of running dotnet build.
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$MelonLoaderVersion = '0.7.3'
$OpenXRPackageVersion = '1.18.0'
$SteamAppId = '1757300'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$VendorDir = Join-Path $RepoRoot 'vendor'

function Find-GamePath {
    $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steamPath) { throw 'Steam not found. Pass -GamePath explicitly.' }

    $libraryFile = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
    $libraries = @($steamPath) + (Select-String -Path $libraryFile -Pattern '"path"\s+"(.+)"' |
        ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' })

    foreach ($library in $libraries) {
        $manifest = Join-Path $library "steamapps\appmanifest_$SteamAppId.acf"
        if (Test-Path $manifest) {
            $installDir = (Select-String -Path $manifest -Pattern '"installdir"\s+"(.+)"').Matches[0].Groups[1].Value
            return Join-Path $library "steamapps\common\$installDir"
        }
    }
    throw 'Jump Space not found in any Steam library. Pass -GamePath explicitly.'
}

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$ExpectedSha1) {
    if (-not (Test-Path $Destination)) {
        Write-Host "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
    }
    if ($ExpectedSha1) {
        $actual = (Get-FileHash -Path $Destination -Algorithm SHA1).Hash.ToLowerInvariant()
        if ($actual -ne $ExpectedSha1.ToLowerInvariant()) {
            Remove-Item $Destination -Force
            throw "Checksum mismatch for $Destination (expected $ExpectedSha1, got $actual)."
        }
    }
}

function Install-MelonLoader([string]$Game) {
    if (Test-Path (Join-Path $Game 'MelonLoader\net6\MelonLoader.dll')) {
        Write-Host 'MelonLoader already installed.'
        return
    }
    $zip = Join-Path $VendorDir "MelonLoader-$MelonLoaderVersion.x64.zip"
    Get-VerifiedDownload "https://github.com/LavaGang/MelonLoader/releases/download/v$MelonLoaderVersion/MelonLoader.x64.zip" $zip $null
    Expand-Archive -Path $zip -DestinationPath $Game -Force
    Write-Host "MelonLoader $MelonLoaderVersion installed. Launch the game once so it can generate its assemblies before building."
}

function Install-OpenXRRuntime([string]$Game) {
    $registry = Invoke-RestMethod -Uri 'https://packages.unity.com/com.unity.xr.openxr' -UseBasicParsing
    $dist = $registry.versions.$OpenXRPackageVersion.dist
    if (-not $dist) { throw "Unity OpenXR $OpenXRPackageVersion not found in the Unity registry." }

    $archive = Join-Path $VendorDir "com.unity.xr.openxr-$OpenXRPackageVersion.tgz"
    Get-VerifiedDownload $dist.tarball $archive $dist.shasum

    $extractDir = Join-Path $VendorDir "com.unity.xr.openxr-$OpenXRPackageVersion"
    if (-not (Test-Path $extractDir)) {
        New-Item -ItemType Directory -Path $extractDir | Out-Null
        tar -xzf $archive -C $extractDir
        if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the OpenXR package.' }
    }

    $package = Join-Path $extractDir 'package'
    $dataDir = Get-ChildItem -Path $Game -Directory -Filter '*_Data' | Select-Object -First 1
    $pluginsDir = Join-Path $dataDir.FullName 'Plugins\x86_64'
    $manifestDir = Join-Path $dataDir.FullName 'UnitySubsystems\UnityOpenXR'
    New-Item -ItemType Directory -Force -Path $pluginsDir, $manifestDir | Out-Null

    Copy-Item (Join-Path $package 'Runtime\windows\x64\UnityOpenXR.dll') $pluginsDir -Force
    Copy-Item (Join-Path $package 'RuntimeLoaders\windows\x64\openxr_loader.dll') $pluginsDir -Force
    Copy-Item (Join-Path $package 'Runtime\UnitySubsystemsManifest.json') $manifestDir -Force
    Write-Host "Unity OpenXR $OpenXRPackageVersion runtime installed."
}

function Install-Mod([string]$Game) {
    $dist = Join-Path $RepoRoot 'dist'
    if (-not $SkipBuild) {
        dotnet build (Join-Path $RepoRoot 'src\JumpSpaceVR\JumpSpaceVR.csproj') -c Release "-p:GamePath=$Game" -o $dist
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $modsDir = Join-Path $Game 'Mods'
    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
    Copy-Item (Join-Path $dist 'JumpSpaceVR.dll') $modsDir -Force
    Write-Host "JumpSpaceVR.dll installed to $modsDir"
}

if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not (Test-Path (Join-Path $GamePath 'GameAssembly.dll'))) { throw "$GamePath does not look like a Jump Space install." }
if (Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue) { throw 'Close Jump Space before installing.' }

New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null
Write-Host "Game: $GamePath"

Install-MelonLoader $GamePath
Install-OpenXRRuntime $GamePath
if (Test-Path (Join-Path $GamePath 'MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll')) {
    Install-Mod $GamePath
} else {
    Write-Warning 'MelonLoader has not generated game assemblies yet. Start the game once, close it, then run this script again.'
}
