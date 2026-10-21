@echo off
rem Builds Tickets Desktop, installs it to C:\Apps\MindAttic.Tickets.Desktop and makes its desktop icon (deploy.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
