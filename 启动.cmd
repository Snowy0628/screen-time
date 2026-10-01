@echo off
rem ============================================================
rem  ScreenTime launcher
rem  Stops any leftover instance, starts the app, reports the result.
rem  Keep this file ASCII-only: cmd.exe reads batch in the OEM codepage.
rem ============================================================
setlocal
set "EXE=%~dp0dist\ScreenTime.App.exe"

echo [1/4] Checking program file...
if not exist "%EXE%" (
    echo     ERROR: not found: %EXE%
    echo     Run the publish command first.
    pause
    exit /b 1
)

echo [2/4] Stopping leftover instances...
taskkill /IM ScreenTime.App.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

echo [3/4] Starting...
start "" "%EXE%"

echo [4/4] Waiting for it to come up...
timeout /t 8 /nobreak >nul

tasklist /FI "IMAGENAME eq ScreenTime.App.exe" 2>nul | find /I "ScreenTime.App.exe" >nul
if errorlevel 1 (
    echo.
    echo     FAILED: the program is not running.
    echo     Check the log:
    echo       %~dp0dist\data\app.log
    echo       %~dp0dist\data\crash.log
    echo.
    pause
    exit /b 1
)

echo.
echo     OK: running. A status window should be on screen.
echo.
echo     Tray icon not visible? Windows 11 hides new tray icons.
echo     Click the ^ arrow (up-chevron) next to the taskbar clock,
echo     then drag the clock icon onto the taskbar to pin it.
echo.
timeout /t 10 /nobreak >nul
exit /b 0