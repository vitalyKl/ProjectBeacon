@echo off
REM Double-click this file. It starts Postgres, API, web, and worker and keeps the window open.
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" -NoPause %*
set EXITCODE=%ERRORLEVEL%
echo %CMDCMDLINE% | find /I /C "/c" >nul
if not errorlevel 1 (
  echo.
  pause
)
exit /b %EXITCODE%
