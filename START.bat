@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title BeaverSearch Launcher v0.4.1 FixSteamID

set "LOG_PATH=%LOCALAPPDATA%\BeaverSearch\startup.log"
set "PROJECT=.\src\BeaverSearch\BeaverSearch.csproj"
set "APP_EXE=.\src\BeaverSearch\bin\Debug\net8.0-windows\BeaverSearch.exe"
set "OBJ_DIR=.\src\BeaverSearch\obj"

echo ========================================
echo        BeaverSearch - Launcher v0.4.1 FixSteamID
echo ========================================
echo.

set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
where dotnet >nul 2>&1
if not errorlevel 1 set "DOTNET_EXE=dotnet"

if exist "%ProgramFiles%\dotnet\dotnet.exe" goto build_app
where dotnet >nul 2>&1
if not errorlevel 1 goto build_app

echo [ERROR] .NET 8 SDK was not found.
where winget >nul 2>&1
if errorlevel 1 goto no_winget

echo Installing .NET 8 SDK via winget...
winget install --id Microsoft.DotNet.SDK.8 --source winget --accept-package-agreements --accept-source-agreements
if errorlevel 1 goto install_failed
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
    goto build_app
)

echo Close this window and run START.bat again.
pause
exit /b 1

:no_winget
echo winget was not found. Install .NET 8 SDK x64 first.
call :show_log
pause
exit /b 1

:install_failed
echo .NET 8 SDK installation failed.
call :show_log
pause
exit /b 1

:build_app
echo Running static preflight...
powershell -NoProfile -ExecutionPolicy Bypass -File ".\STATIC-PREFLIGHT.ps1"
if errorlevel 1 goto preflight_failed

rem WPF creates temporary *_wpftmp projects under obj. Always clear obj so an
rem old/corrupt Win32 resource or generated BAML cannot poison the next build.
if exist "%OBJ_DIR%" (
    echo Cleaning generated WPF build state...
    rmdir /s /q "%OBJ_DIR%" >nul 2>&1
    if exist "%OBJ_DIR%" (
        echo [WARN] Could not fully remove %OBJ_DIR%. Close Visual Studio/dotnet processes if build fails.
    )
)

echo Building BeaverSearch...
"%DOTNET_EXE%" build "%PROJECT%" -c Debug --nologo
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" goto build_failed

if not exist "%APP_EXE%" (
    echo.
    echo [ERROR] Build reported success, but BeaverSearch.exe was not found.
    call :show_log
    pause
    exit /b 2
)

echo.
echo Starting BeaverSearch...
"%APP_EXE%"
set "APP_EXIT=%ERRORLEVEL%"

if not "%APP_EXIT%"=="0" (
    echo.
    echo [ERROR] BeaverSearch exited with code %APP_EXIT%.
    call :show_log
    pause
    exit /b %APP_EXIT%
)

endlocal
exit /b 0

:preflight_failed
echo.
echo [ERROR] STATIC-PREFLIGHT failed. See STATIC-PREFLIGHT.txt.
call :show_log
pause
exit /b 3

:build_failed
echo.
echo [ERROR] BeaverSearch failed to build. Exit code: %BUILD_EXIT%
echo NOTE: startup.log below is a runtime log and may belong to an older successful/failed launch.
call :show_log
pause
exit /b %BUILD_EXIT%

:show_log
echo.
echo Startup log: %LOG_PATH%
if exist "%LOG_PATH%" (
    echo ===== LAST RUNTIME LOG (MAY BE FROM A PREVIOUS RUN) =====
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%LOG_PATH%' -Tail 80"
    echo ========================================================
) else (
    echo startup.log does not exist yet.
)
exit /b 0
