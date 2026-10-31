@echo off
REM ===================================================================
REM  Stops and removes the MM Server Agent service and its firewall rule.
REM  Your agent.jsonc and agent.log are left in this folder.
REM  Right-click this file and choose "Run as administrator".
REM ===================================================================
net session >nul 2>&1
if errorlevel 1 (
    echo [!] Right-click uninstall-agent.bat and choose "Run as administrator".
    pause
    exit /b 1
)
cd /d "%~dp0"
MMServerAgent.exe uninstall
pause
