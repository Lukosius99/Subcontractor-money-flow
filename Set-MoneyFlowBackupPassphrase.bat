@echo off
rem MoneyFlow backup sifravimo frazes nustatymas
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-MoneyFlowBackupPassphrase.ps1" %*
