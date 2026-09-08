@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title BeaverSearch Debug Launcher v0.4.1 FixSteamID
set "LOG_PATH=%LOCALAPPDATA%\BeaverSearch\startup.log"

call START.bat
set "CODE=%ERRORLEVEL%"

echo.
echo ========================================
echo BeaverSearch process finished.
echo Exit code: %CODE%
echo Startup log: %LOG_PATH%
echo ========================================
if exist "%LOG_PATH%" (
    echo ===== LAST 120 STARTUP LOG LINES =====
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%LOG_PATH%' -Tail 120"
    echo ======================================
) else (
    echo startup.log does not exist yet.
)

pause
exit /b %CODE%
