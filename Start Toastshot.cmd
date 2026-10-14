@echo off
setlocal
set "EXE=%~dp0src\Toastshot\bin\Release\net8.0-windows10.0.19041.0\Toastshot.exe"
if not exist "%EXE%" (
    echo Building Toastshot for the first time...
    dotnet build "%~dp0src\Toastshot\Toastshot.csproj" -c Release -nologo -v quiet || (pause & exit /b 1)
)
start "" "%EXE%" %*
