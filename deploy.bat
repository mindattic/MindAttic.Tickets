@echo off
rem Builds Workflow Desktop, installs it to C:\Apps\MindAttic.Workflow.Desktop and makes its desktop icon (deploy.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
