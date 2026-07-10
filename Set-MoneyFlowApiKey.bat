@echo off
rem MoneyFlow mutaciju API rakto nustatymas
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-MoneyFlowApiKey.ps1" %*
