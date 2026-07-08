@echo off
rem MoneyFlow DB atstatymas is atsargines kopijos (pasiprasys administratoriaus teisiu)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-MoneyFlowDb.ps1" %*
