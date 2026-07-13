@echo off
rem MoneyFlow rankinio redagavimo slaptafrazes nustatymas
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-MoneyFlowEditPassphrase.ps1" %*
