@echo off
rem MoneyFlow diegimas arba atnaujinimas. PowerShell scenarijus pats paprasys administratoriaus teisiu.
setlocal
set "LATEST_BACKUP=%~dp0db-backups\monthly-money-flow-latest.mfbackup"
set "LIVE_DB=%ProgramData%\PADS\MoneyFlow\monthly-money-flow.db"
if exist "%LIVE_DB%" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-MoneyFlow.ps1"
) else if exist "%LATEST_BACKUP%" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-MoneyFlow.ps1" -InitialDatabase "%LATEST_BACKUP%"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-MoneyFlow.ps1"
)
exit /b %errorlevel%
