<#
.SYNOPSIS
    Installs the Jump Space VR mod.

.DESCRIPTION
    0. Makes sure the .NET 6 runtime MelonLoader needs is installed (via winget when missing).
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
    if (-not $steamPath) { return $null }

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
    return $null
}

function Request-GamePath {
    Write-Host 'Could not find Jump Space automatically.'
    $answer = Read-Host 'Paste the Jump Space install folder (Steam > Jump Space > Manage > Browse local files)'
    return $answer.Trim('"', ' ')
}

function Test-DotNet6 {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        return [bool]($runtimes | Select-String 'Microsoft\.NETCore\.App 6\.')
    } catch {
        return $false
    }
}

function Install-DotNet6 {
    if (Test-DotNet6) {
        Write-Host '.NET 6 runtime found.'
        return
    }
    Write-Host 'MelonLoader needs the .NET 6 runtime; installing it...'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Microsoft.DotNet.Runtime.6 --exact --silent --accept-package-agreements --accept-source-agreements
    }
    if (-not (Test-DotNet6)) {
        Write-Warning 'Could not install .NET 6 automatically. Install ".NET Runtime 6" (x64) from https://dotnet.microsoft.com/download/dotnet/6.0 and run this installer again.'
        throw '.NET 6 runtime missing.'
    }
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
    Write-Host "MelonLoader $MelonLoaderVersion installed."
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
    # Release zips ship a prebuilt JumpSpaceVR.dll next to this script's folder; source checkouts build it.
    $prebuilt = Join-Path $RepoRoot 'JumpSpaceVR.dll'
    $dist = if (Test-Path $prebuilt) { $RepoRoot } else { Join-Path $RepoRoot 'dist' }
    if (-not $SkipBuild -and -not (Test-Path $prebuilt)) {
        dotnet build (Join-Path $RepoRoot 'src\JumpSpaceVR\JumpSpaceVR.csproj') -c Release "-p:GamePath=$Game" -o $dist
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $modsDir = Join-Path $Game 'Mods'
    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
    Copy-Item (Join-Path $dist 'JumpSpaceVR.dll') $modsDir -Force
    Write-Host "JumpSpaceVR.dll installed to $modsDir"
}

if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath) { $GamePath = Request-GamePath }
if (-not (Test-Path (Join-Path $GamePath 'GameAssembly.dll'))) { throw "$GamePath does not look like a Jump Space install." }
if (Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue) { throw 'Close Jump Space before installing.' }

New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null
Write-Host "Game: $GamePath"

Install-DotNet6
Install-MelonLoader $GamePath
Install-OpenXRRuntime $GamePath
$hasPrebuilt = Test-Path (Join-Path $RepoRoot 'JumpSpaceVR.dll')
if ($hasPrebuilt -or (Test-Path (Join-Path $GamePath 'MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll'))) {
    Install-Mod $GamePath
} else {
    Write-Warning 'MelonLoader has not generated game assemblies yet. Start the game once, close it, then run this script again.'
}

Write-Host ''
Write-Host 'Done! Start your VR app (SteamVR / Steam Link, Virtual Desktop or Quest Link), then launch Jump Space from Steam.'
Write-Host 'The first launch after installing takes a few extra minutes while MelonLoader prepares the game.'
Write-Host 'In game: Insert = VR settings menu, F7 = recenter, F8 = VR on/off.'
