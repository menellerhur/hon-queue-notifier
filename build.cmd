@echo off
setlocal
cd /d "%~dp0"
title HoN Queue Notifier - build

where dotnet >nul 2>&1
if errorlevel 1 (
    echo The .NET SDK is needed to build this program. It is free:
    echo https://dotnet.microsoft.com/download
    echo Players do not need it: the finished exe runs on .NET Framework 4.8, which Windows already has.
    goto :fail
)

rem The exe cannot be replaced while it is running.
tasklist /fi "imagename eq HonQueueNotifier.exe" 2>nul | find /i "HonQueueNotifier.exe" >nul
if not errorlevel 1 (
    echo HonQueueNotifier.exe is running - closing it so it can be rebuilt...
    taskkill /im HonQueueNotifier.exe /f >nul 2>&1
    ping -n 3 127.0.0.1 >nul
)

echo Building...
dotnet build HonQueueNotifier.sln -c Release --nologo -v q
if errorlevel 1 goto :fail
copy /y "src\HonQueueNotifier\bin\Release\net48\HonQueueNotifier.exe" "HonQueueNotifier.exe" >nul
if errorlevel 1 goto :fail
echo Built HonQueueNotifier.exe

echo.
echo Running the tests...
dotnet test HonQueueNotifier.sln -c Release --no-build --nologo -v q
if errorlevel 1 goto :fail

echo.
echo Next: double-click HonQueueNotifier.exe. It shows up as an icon in the tray, near the clock.
echo Double-click the icon for the settings; right-click it for a test alert.
echo.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED. Please copy everything above and send it back.
echo.
pause
exit /b 1
