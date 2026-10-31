@echo off
REM ===================================================================
REM  MM Launcher - build script
REM  Builds into the "dist" folder:
REM    dist\MMLauncher.exe          the launcher players run
REM    dist\agent\MMServerAgent.exe the agent for server machines (+ helper .bat files)
REM  Both are self-contained: nothing else needs installing to run them.
REM  Only this PC needs the .NET 10 SDK to build:
REM    https://dotnet.microsoft.com/download/dotnet/10.0
REM ===================================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [!] The .NET SDK isn't installed. Get the .NET 10 SDK from:
    echo     https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

set PUBLISH=-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None

echo Building MM Launcher...
dotnet publish src\MMLauncher.App %PUBLISH% -o dist
if errorlevel 1 goto failed

echo Building MM Server Agent...
dotnet publish src\MMServerAgent %PUBLISH% -o dist\agent
if errorlevel 1 goto failed
copy /y agent-package\*.* dist\agent\ >nul

echo.
echo Done:
echo   %~dp0dist\MMLauncher.exe
echo   %~dp0dist\agent\  (copy this folder to each server machine)
pause
exit /b 0

:failed
echo.
echo [!] Build failed. Copy everything above this line and send it to Claude.
pause
exit /b 1
