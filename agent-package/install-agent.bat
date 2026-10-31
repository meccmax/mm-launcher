@echo off
REM ===================================================================
REM  Installs MM Server Agent as a Windows service on THIS server machine.
REM  - Starts automatically with Windows, restarts itself if it ever stops
REM  - Opens its TCP port (from agent.jsonc, default 2999) in Windows Firewall
REM  Put this folder somewhere permanent FIRST (e.g. C:\Apps\MMServerAgent):
REM  the service runs the exe from wherever it is when you install.
REM  Right-click this file and choose "Run as administrator".
REM ===================================================================
net session >nul 2>&1
if errorlevel 1 (
    echo [!] Right-click install-agent.bat and choose "Run as administrator".
    pause
    exit /b 1
)
cd /d "%~dp0"
MMServerAgent.exe install
pause
