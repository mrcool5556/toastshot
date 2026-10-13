# Installs Glance for the current user (no admin): %LOCALAPPDATA%\Programs\Glance, a Start menu shortcut,
# and an entry in Settings > Apps so it can be uninstalled. Running it again updates an existing install.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'Glance.exe'
if (-not (Test-Path $source)) { throw "Glance.exe isn't next to this script. Build the package first (installer\build.ps1)." }

$dir = Join-Path $env:LOCALAPPDATA 'Programs\Glance'
$exe = Join-Path $dir 'Glance.exe'

# Close a running copy so the file can be replaced.
Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $source $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dir -Force

# Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glance.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.IconLocation = "$exe,0"
$link.Description = 'Capture, mark up, read and translate screen regions'
$link.Save()

# Settings > Apps entry
$version = (Get-Item $exe).VersionInfo.ProductVersion
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\uninstall.ps1`""
@{
    DisplayName     = 'Glance'
    DisplayVersion  = $version
    Publisher       = 'Glance'
    DisplayIcon     = "$exe,0"
    InstallLocation = $dir
    UninstallString = $uninstall
    QuietUninstallString = "$uninstall -Quiet"
}.GetEnumerator() | ForEach-Object { Set-ItemProperty -Path $key -Name $_.Key -Value $_.Value }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord

# If "Start with Windows" was on for another copy, point it here.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $run -Name Glance -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $run -Name Glance -Value "`"$exe`" --tray"
}

Start-Process $exe
Write-Host "Glance $version is installed in $dir and running. Find it in the Start menu; uninstall it from Settings > Apps."
