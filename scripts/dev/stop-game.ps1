<#
.SYNOPSIS
    Development helper: closes Jump Space and waits until the process has exited (so its files are unlocked).
#>
$processes = Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue
$processes | Stop-Process -Force
foreach ($process in $processes) {
    if (-not $process.WaitForExit(15000)) { Write-Warning "Jump Space (pid $($process.Id)) did not exit within 15 s." }
}
Write-Host 'Jump Space stopped.'
