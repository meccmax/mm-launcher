@echo off
REM ===================================================================
REM  Updates MM Server Agent to the newest release on GitHub
REM  (github.com/meccmax/mm-launcher). The agent never updates itself;
REM  this is the only way it changes.
REM  - Downloads the new version and checks it against the published checksum
REM  - Stops the service, swaps the exe, starts it again
REM  - Keeps agent.jsonc, agent.log and the reports folder exactly as they are
REM  Right-click this file and choose "Run as administrator".
REM ===================================================================
net session >nul 2>&1
if errorlevel 1 (
    echo [!] Right-click update-agent.bat and choose "Run as administrator".
    pause
    exit /b 1
)
cd /d "%~dp0"
MMServerAgent.exe update
pause
