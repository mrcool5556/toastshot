# Installs Toastshot for the current user (no admin): %LOCALAPPDATA%\Programs\Toastshot, a Start menu shortcut,
# and an entry in Settings > Apps so it can be uninstalled. Running it again updates an existing install.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'Toastshot.exe'
if (-not (Test-Path $source)) { throw "Toastshot.exe isn't next to this script. Build the package first (installer\build.ps1)." }

$dir = Join-Path $env:LOCALAPPDATA 'Programs\Toastshot'
$exe = Join-Path $dir 'Toastshot.exe'

# Close a running copy so the file can be replaced (Glance was this app's name before 0.2.0).
Get-Process Toastshot, Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Replace an install from when the app was called Glance: its folder, shortcut and Apps entry go; settings and
# "Start with Windows" carry over.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startWithWindows = [bool](Get-ItemProperty -Path $run -Name Glance -ErrorAction SilentlyContinue)
Remove-ItemProperty -Path $run -Name Glance -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Glance.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Glance') -Recurse -Force -ErrorAction SilentlyContinue
$oldSettings = Join-Path $env:LOCALAPPDATA 'Glance\settings.json'
$newSettings = Join-Path $env:LOCALAPPDATA 'Toastshot\settings.json'
if ((Test-Path $oldSettings) -and -not (Test-Path $newSettings)) {
    New-Item -ItemType Directory -Force (Split-Path $newSettings) | Out-Null
    Copy-Item $oldSettings $newSettings
}

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $source $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dir -Force

# Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Toastshot.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.IconLocation = "$exe,0"
$link.Description = 'Capture, mark up, read and translate screen regions'
$link.Save()

# Settings > Apps entry
$version = (Get-Item $exe).VersionInfo.ProductVersion
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Toastshot'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\uninstall.ps1`""
@{
    DisplayName     = 'Toastshot'
    DisplayVersion  = $version
    Publisher       = 'Toastshot'
    DisplayIcon     = "$exe,0"
    InstallLocation = $dir
    UninstallString = $uninstall
    QuietUninstallString = "$uninstall -Quiet"
}.GetEnumerator() | ForEach-Object { Set-ItemProperty -Path $key -Name $_.Key -Value $_.Value }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord

# If "Start with Windows" was on (for another copy, or for Glance), point it here.
if ($startWithWindows -or (Get-ItemProperty -Path $run -Name Toastshot -ErrorAction SilentlyContinue)) {
    Set-ItemProperty -Path $run -Name Toastshot -Value "`"$exe`" --tray"
}

Start-Process $exe
Write-Host "Toastshot $version is installed in $dir and running. Find it in the Start menu; uninstall it from Settings > Apps."
