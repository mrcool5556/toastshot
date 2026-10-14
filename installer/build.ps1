# Builds the installer: dist\ToastshotSetup.exe, a single per-user setup (no admin, nothing else needed on the PC).
# Toastshot.exe is self-contained, so the PC it is installed on doesn't need .NET. Needs Inno Setup 6 (jrsoftware.org).
param([string]$Version) # e.g. 0.4.0; defaults to the version in Toastshot.csproj
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'dist\publish'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src\Toastshot\Toastshot.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is needed to build the installer: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Toastshot\Toastshot.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publish -nologo "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

& $iscc /Q "/DAppVersion=$Version" "/DSourceExe=$publish\Toastshot.exe" (Join-Path $PSScriptRoot 'Toastshot.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $root 'dist\ToastshotSetup.exe'
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB), Toastshot $Version."
