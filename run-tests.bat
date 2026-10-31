@echo off
REM ===================================================================
REM  MM Launcher - engine self-check
REM  With no arguments: tests against fake servers on this PC. Start
REM  tools\fake_dayz_servers.py first (needs Python).
REM  Against a real host:
REM     run-tests.bat game.misfitmercenaries.com 2000 3000
REM  Prints PASS/FAIL lines. Send the output to Claude if anything fails.
REM ===================================================================
setlocal
cd /d "%~dp0"
dotnet run -c Release --project src\MMLauncher.Tests -- %*
pause
