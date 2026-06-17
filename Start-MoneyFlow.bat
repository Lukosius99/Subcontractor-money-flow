@echo off
REM Start-MoneyFlow.bat - Start the Monthly Money Flow service
REM Requires Administrator (self-elevates on double-click)

setlocal
set SERVICE=MoneyFlow

REM --- Self-elevate to Administrator if needed ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
    exit /b
)

REM --- Check the service exists ---
sc query "%SERVICE%" >nul 2>&1
if %errorlevel% neq 0 (
    echo Service '%SERVICE%' not found. Run the install script first.
    goto :end
)

REM --- Already running? ---
sc query "%SERVICE%" | find "RUNNING" >nul
if %errorlevel% equ 0 (
    echo MoneyFlow is already running.
    goto :end
)

REM --- Start it ---
net start "%SERVICE%"
if %errorlevel% neq 0 (
    echo MoneyFlow failed to start.
    goto :end
)

echo MoneyFlow started successfully.

REM --- HTTP check (curl ships with Windows 10/11) ---
timeout /t 2 /nobreak >nul
for /f %%C in ('curl -s -o nul -w "%%{http_code}" http://127.0.0.1:5000/ 2^>nul') do set CODE=%%C
if defined CODE (
    echo HTTP check: %CODE%
) else (
    echo HTTP check failed - service may still be warming up.
)

:end
echo.
pause
endlocal
