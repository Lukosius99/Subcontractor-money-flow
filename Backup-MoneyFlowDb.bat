@echo off
rem MoneyFlow DB backup - paleidzia PowerShell skripta (pasiprasys administratoriaus teisiu)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Backup-MoneyFlowDb.ps1" %*
