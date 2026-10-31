@echo off
REM ===================================================================
REM  Shows exactly what MM Server Agent sees right now: every running DayZ
REM  server on this machine, its ports, full name and client mod list.
REM  Run as administrator if it says it can't read a server's startup line.
REM ===================================================================
cd /d "%~dp0"
MMServerAgent.exe check
