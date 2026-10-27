@echo off
rem Builds Tickets Desktop, installs it to C:\Apps\MindAttic.Tickets.Desktop and makes its desktop icon (run.ps1).
set "APPS=C:\Apps"
if not exist "%APPS%" set "APPS=%LOCALAPPDATA%\Programs"
set "APP_EXE=%APPS%\MindAttic.Tickets.Desktop\MindAttic.Tickets.Desktop.exe"
echo.
echo   MindAttic Tickets
echo     1^) Deploy ^& Run
echo     2^) Run
echo.
choice /c 12 /n /m "  Choose 1 or 2: "
if errorlevel 2 goto run
:deploy
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Republish -Launch %*
if errorlevel 1 (
    echo Deploy failed.
    if exist "%APP_EXE%" (
        echo Opening the installed copy.
        start "" "%APP_EXE%"
    )
    pause
)
exit /b 0
:run
if not exist "%APP_EXE%" (
    echo MindAttic Tickets is not installed yet - deploying it first.
    goto deploy
)
start "" "%APP_EXE%"
exit /b 0
