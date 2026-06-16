@echo off
REM ============================================================
REM  Reset the LOCAL PADS MoneyFlow database (double-click).
REM  Deletes the local-dev SQLite DB used by start-local.bat,
REM  so the next launch starts with an empty local database.
REM
REM  This only touches the LOCAL dev database at
REM  test-data\local-dev.db. It does NOT
REM  touch the live production database in ProgramData.
REM ============================================================

setlocal

REM Run from the folder this script lives in.
cd /d "%~dp0"

set "DB=%~dp0test-data\local-dev.db"
set "RESET_MARKER=%~dp0test-data\local-dev.reset"

echo.
echo  This will DELETE the local database:
echo    %DB%
echo  (plus its -wal and -shm files). You can then re-upload your files.
echo.
choice /m "Are you sure you want to clear the local database"
if errorlevel 2 goto cancelled

REM Delete the database and its SQLite WAL side files.
if not exist "%~dp0test-data" mkdir "%~dp0test-data"
if exist "%DB%"      del /f /q "%DB%"
if exist "%DB%-wal"  del /f /q "%DB%-wal"
if exist "%DB%-shm"  del /f /q "%DB%-shm"
echo reset > "%RESET_MARKER%"

if exist "%DB%" (
    echo.
    echo  ERROR: Could not delete the database. It may still be in use.
    echo  Close the app window/window running start-local.bat and try again.
    echo.
    pause
    goto end
)

echo.
echo  Local database cleared. A fresh empty DB will be created
echo  the next time you launch start-local.bat.
echo.
echo  Next steps:
echo    1. Double-click start-local.bat
echo    2. Re-upload test files only if you need to test imports locally.
echo.
pause
goto end

:cancelled
echo.
echo  Cancelled. Nothing was deleted.
echo.
pause

:end
endlocal
