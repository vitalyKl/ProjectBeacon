@echo off
REM Double-click this file. It keeps the window open and asks for a project token.
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" -NoPause %*
set EXITCODE=%ERRORLEVEL%
echo %CMDCMDLINE% | find /I /C "/c" >nul
if not errorlevel 1 (
  echo.
  pause
)
exit /b %EXITCODE%
