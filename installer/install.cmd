@echo off
setlocal

rem ============================================================
rem  ScreenTime - Installation launcher
rem
rem  WHY THIS FILE EXISTS
rem    Windows Mandatory Integrity Control labels the Desktop as
rem    "Low integrity" and forces that label onto child items.
rem    If the zip is extracted onto the Desktop, the extracted
rem    installer inherits that label, and any process started from it
rem    is demoted to Low integrity -- which Windows then refuses
rem    permission to write the program directory ("Access denied").
rem
rem    A .cmd file does not carry the label, so this launcher copies
rem    the files into a temp folder and starts the installer there.
rem
rem  Just double-click this file; you do not need to care where the
rem  zip was extracted.
rem
rem  IMPORTANT IMPLEMENTATION NOTES
rem    1. Keep this file pure ASCII with CRLF line endings.
rem       cmd.exe reads .cmd using the console code page; a UTF-8 BOM
rem       or bare LF endings both make it mis-parse lines.
rem    2. It never writes a non-ASCII filename literally, because
rem       "if exist ...<chinese>.exe" fails when the console code page
rem       differs from the one the file was saved in. The installer is
rem       located through PowerShell, which handles Unicode natively.
rem ============================================================

set "SRC=%~dp0"
if "%SRC:~-1%"=="\" set "SRC=%SRC:~0,-1%"

if not exist "%SRC%\app\ScreenTime.App.exe" goto :nopayload

set "WORK=%TEMP%\ScreenTime-Setup-%RANDOM%%RANDOM%"

echo.
echo   ScreenTime - Installer
echo   ======================
echo.
echo   Preparing files ...
echo     from : %SRC%
echo     to   : %WORK%
echo.

robocopy "%SRC%" "%WORK%" /E /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (
    echo   [ERROR] Could not copy the setup files.
    echo.
    echo   Please extract the zip somewhere other than the Desktop
    echo   ^(for example C:\ScreenTime^) and run the installer there.
    echo.
    pause
    exit /b 1
)

rem Find the installer in the temp copy. It is the largest .exe there
rem that is not the application itself. Done in PowerShell so that the
rem non-ASCII filename never has to be written in this batch file.
set "ST_WORK=%WORK%"
set "PS_FIND=$d=$env:ST_WORK; $c=Get-ChildItem -LiteralPath $d -Filter *.exe | Where-Object { $_.Name -ne 'ScreenTime.App.exe' } | Sort-Object Length -Descending; if ($c) { $c[0].FullName }"
for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS_FIND%"`) do set "ST_SETUP=%%P"

if not defined ST_SETUP (
    echo   [ERROR] Could not find the installer inside the temp copy.
    echo.
    pause
    exit /b 1
)

echo   Running the installer ...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$w=$env:ST_WORK; $e=$env:ST_SETUP; Set-Location -LiteralPath $w; & $e"
set "RC=%ERRORLEVEL%"

set "ST_WORK="
set "ST_SETUP="

rem Remove the temp folder we created (the installer has exited by now)
if exist "%WORK%" rd /s /q "%WORK%" >nul 2>&1

if not "%RC%"=="0" (
    echo.
    echo   Installer exited with code %RC%.
    echo.
    pause
)

exit /b %RC%

:nopayload
echo.
echo   [ERROR] Program files ^(app\ScreenTime.App.exe^) are missing.
echo   The zip was not fully extracted. Please extract it again.
echo.
pause
exit /b 1
