@echo off
rem Double-click to install (or update) Glance for this Windows user. No admin rights needed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 pause
