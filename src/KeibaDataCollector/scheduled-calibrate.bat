@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for calibrate mode (weekly, Monday 03:30).
REM
REM Re-fits the pre-sale scoring weights from the past year of races in
REM keiba-ai-data-system's history database (read-only) and saves them to
REM data\scoring-weights.json, which the next predict run uses. Each group
REM keeps its current weights if the new ones do worse on the last four weeks.
REM
REM It reads only the local SQLite database: no JV-Link / UmaConn and no
REM WordPress, so secrets.local.bat is not needed.
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if not exist logs mkdir logs

REM %DATE% is locale-dependent and wmic is absent on newer Windows builds,
REM so ask PowerShell for an unambiguous yyyyMMdd.
for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\calibrate-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] calibrate start >> "%LOGFILE%"

set EXE=%~dp0bin\Debug\net48\KeibaDataCollector.exe
if not exist "%EXE%" (
    echo [ERROR] Not built yet: %EXE% >> "%LOGFILE%"
    echo         Run: dotnet build -c Debug >> "%LOGFILE%"
    exit /b 1
)

pushd "%~dp0bin\Debug\net48"
"%EXE%" calibrate >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] calibrate end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
