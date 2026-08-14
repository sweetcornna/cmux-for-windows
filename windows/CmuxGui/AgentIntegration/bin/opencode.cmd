@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0invoke-provider.ps1" opencode %*
exit /b %errorlevel%
