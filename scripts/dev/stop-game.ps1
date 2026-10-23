<#
.SYNOPSIS
    Development helper: closes Jump Space.
#>
Get-Process -Name 'Jump Space' -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'Jump Space stopped.'
