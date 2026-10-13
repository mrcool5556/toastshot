# Removes Glance installed by install.ps1. Settings in %LOCALAPPDATA%\Glance are kept unless -RemoveSettings is given.
param([switch]$Quiet, [switch]$RemoveSettings)
$ErrorActionPreference = 'SilentlyContinue'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Glance'

Get-Process Glance | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Glance
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Glance.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance' -Recurse -Force
if ($RemoveSettings) { Remove-Item (Join-Path $env:LOCALAPPDATA 'Glance') -Recurse -Force }

# This script lives in the folder being removed, so remove it from a separate process once this one exits.
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dir`"" -WindowStyle Hidden

if (-not $Quiet) { Write-Host 'Glance is uninstalled.'; Start-Sleep -Seconds 2 }
