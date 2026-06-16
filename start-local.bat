@echo off
REM ============================================================
REM  Start PADS MoneyFlow API + Web app locally (double-click).
REM  Serves both the API and the web UI from one process.
REM  Uses the Development environment with a local database.
REM  First launch seeds it from ProgramData; reset-local-db.bat
REM  makes the next launch start empty.
REM ============================================================

setlocal

REM Run from the folder this script lives in.
cd /d "%~dp0"

REM Local web settings.
set "ASPNETCORE_ENVIRONMENT=Development"
set "APP_PORT=5000"
set "APP_URL=http://localhost:%APP_PORT%"

REM Use the local database copy. If it does not exist, seed it once from the
REM installed app/service database. reset-local-db.bat creates RESET_MARKER so
REM the next launch starts empty instead of copying ProgramData back in.
set "LOCAL_DB=%~dp0test-data\local-dev.db"
set "RESET_MARKER=%~dp0test-data\local-dev.reset"
set "PRODUCTION_DB=C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db"
if not exist "%~dp0test-data" mkdir "%~dp0test-data"
if exist "%RESET_MARKER%" (
    del /f /q "%RESET_MARKER%" >nul 2>&1
    set "MONEY_FLOW_DB_PATH=%LOCAL_DB%"
) else if exist "%LOCAL_DB%" (
    set "MONEY_FLOW_DB_PATH=%LOCAL_DB%"
) else if exist "%PRODUCTION_DB%" (
    copy /y "%PRODUCTION_DB%" "%LOCAL_DB%" >nul
    if exist "%PRODUCTION_DB%-wal" (
        copy /y "%PRODUCTION_DB%-wal" "%LOCAL_DB%-wal" >nul
    ) else if exist "%LOCAL_DB%-wal" (
        del /f /q "%LOCAL_DB%-wal"
    )
    if exist "%PRODUCTION_DB%-shm" (
        copy /y "%PRODUCTION_DB%-shm" "%LOCAL_DB%-shm" >nul
    ) else if exist "%LOCAL_DB%-shm" (
        del /f /q "%LOCAL_DB%-shm"
    )
    set "MONEY_FLOW_DB_PATH=%LOCAL_DB%"
) else (
    set "MONEY_FLOW_DB_PATH=%~dp0data\monthly-money-flow.db"
)

echo.
echo  Starting PADS MoneyFlow at %APP_URL%
echo  Database: %MONEY_FLOW_DB_PATH%
echo  (Press Ctrl+C in this window to stop.)
echo.

REM Do not open an already-running/stale server on the same port.
set "PORT_OWNER="
for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$port = [int]$env:APP_PORT; $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; if ($c) { $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue; if ($p) { Write-Output ($p.Id.ToString() + ' ' + $p.ProcessName + ' ' + $p.Path) } else { Write-Output $c.OwningProcess } }"`) do set "PORT_OWNER=%%P"
if defined PORT_OWNER (
    echo  ERROR: Port %APP_PORT% is already in use by:
    echo    %PORT_OWNER%
    echo.
    echo  Close the existing PADS window or stop the existing PADS process,
    echo  then run start-local.bat again.
    echo.
    pause
    exit /b 1
)

REM Open the web app in the default browser shortly after launch.
start "" /min cmd /c "timeout /t 4 >nul & start "" %APP_URL%"

REM Launch the app (blocks until you stop it with Ctrl+C).
dotnet run --urls %APP_URL%

endlocal
