@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title BeaverSearch Release Builder v0.4.1 FixSteamID

echo ========================================
echo     BeaverSearch Release Build v0.4.1 FixSteamID
echo ========================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File ".\build-release.ps1"
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" goto fail

echo.
echo Release created in release\win-x64
echo SHA-256 is in release\win-x64\BeaverSearch.exe.sha256.txt
pause
exit /b 0

:fail
echo.
echo [ERROR] Release build failed. Exit code: %CODE%
if exist "%LOCALAPPDATA%\BeaverSearch\startup.log" (
    echo ===== LAST STARTUP LOG =====
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%LOCALAPPDATA%\BeaverSearch\startup.log' -Tail 80"
    echo ============================
)
pause
exit /b %CODE%
