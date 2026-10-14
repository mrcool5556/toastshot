# Removes Toastshot installed by install.ps1. Settings in %LOCALAPPDATA%\Toastshot are kept unless -RemoveSettings is given.
param([switch]$Quiet, [switch]$RemoveSettings)
$ErrorActionPreference = 'SilentlyContinue'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Toastshot'

Get-Process Toastshot | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Toastshot
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Toastshot.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Toastshot' -Recurse -Force
if ($RemoveSettings) { Remove-Item (Join-Path $env:LOCALAPPDATA 'Toastshot') -Recurse -Force }

# This script lives in the folder being removed, so remove it from a separate process once this one exits.
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dir`"" -WindowStyle Hidden

if (-not $Quiet) { Write-Host 'Toastshot is uninstalled.'; Start-Sleep -Seconds 2 }
