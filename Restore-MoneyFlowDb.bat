@echo off
rem MoneyFlow DB atstatymas per veikianti API - administratoriaus teisiu nereikia
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-MoneyFlowDb.ps1" %*
