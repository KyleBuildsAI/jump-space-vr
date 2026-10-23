<#
.SYNOPSIS
    Development helper: puts the game's JumpSpaceVR settings back to normal play after a test run (no developer
    options, the given runtime override, empty by default). Other settings are left as they are.
#>
param(
    [string]$GamePath = 'G:\SteamLibrary\steamapps\common\JumpSpace',
    [string]$RuntimeJson = ''
)

$configFile = Join-Path $GamePath 'UserData\MelonPreferences.cfg'
if (-not (Test-Path $configFile)) { return }

$normal = [ordered]@{
    OpenXRRuntimeJson = "'$RuntimeJson'"
    SnapshotOnStartup = 'false'
    SyntheticControllers = 'false'
    ScenarioFile = "''"
    DebugAimOffset = 'false'
    MockControllerInput = 'false'
}
$content = Get-Content $configFile -Raw
foreach ($key in $normal.Keys) {
    $content = [regex]::Replace($content, "(?m)^$key = .*$", "$key = $($normal[$key])")
}
Set-Content -Path $configFile -Value $content -NoNewline
