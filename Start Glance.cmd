@echo off
setlocal
set "EXE=%~dp0src\Glance\bin\Release\net8.0-windows10.0.19041.0\Glance.exe"
if not exist "%EXE%" (
    echo Building Glance for the first time...
    dotnet build "%~dp0src\Glance\Glance.csproj" -c Release -nologo -v quiet || (pause & exit /b 1)
)
start "" "%EXE%" %*
