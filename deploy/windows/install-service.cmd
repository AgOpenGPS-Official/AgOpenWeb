@echo off
rem AgOpenWeb - install / manage the Windows Service (the headless guidance host).
rem
rem Right-click this and choose "Run as administrator" (or run it from a console). It
rem self-elevates via UAC and bypasses PowerShell's script-execution policy, then hands
rem off to install-service.ps1. Pass-through args work too, e.g.:
rem     install-service.cmd -Action uninstall
rem     install-service.cmd -Action status
rem No args = install + start the service.

rem net session only succeeds with admin rights -> use it as the elevation check.
net session >nul 2>&1
if "%errorlevel%"=="0" goto run

echo Requesting administrator privileges...
if "%~1"=="" powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if not "%~1"=="" powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
exit /b

:run
rem Elevated: run the installer with the execution policy bypassed for this call only.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-service.ps1" %*
echo.
pause
