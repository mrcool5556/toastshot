# Builds the installable package: dist\Glance\ (Glance.exe + Install Glance.cmd + scripts) and dist\Glance.zip.
# Glance.exe is self-contained, so the PC it is installed on doesn't need .NET.
param([string]$Version) # e.g. 0.2.0; defaults to the version in Glance.csproj
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'dist\Glance'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'src\Glance\Glance.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out -nologo $(if ($Version) { "-p:Version=$Version" })
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'Install Glance.cmd') $out
Get-ChildItem $out -Exclude 'Glance.exe', '*.ps1', '*.cmd' | Remove-Item -Recurse -Force   # leftovers such as .pdb files

$zip = Join-Path $root 'dist\Glance.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host "Built $out and $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)."
