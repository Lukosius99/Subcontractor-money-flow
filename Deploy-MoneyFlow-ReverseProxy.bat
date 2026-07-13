@echo off
rem MoneyFlow diegimas tam paciam serveryje veikianciam IIS, Caddy arba Nginx reverse proxy.
call "%~dp0Deploy-MoneyFlow.bat" -NetworkMode ReverseProxy
exit /b %errorlevel%
