@echo off
rem MoneyFlow DB backup - paleidzia PowerShell skripta be administratoriaus teisiu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Backup-MoneyFlowDb.ps1" %*
