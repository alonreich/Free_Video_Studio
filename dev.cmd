@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "build\FvsVerify\FvsVerify.csproj" -v q -- "%CD%"
if errorlevel 1 exit /b 1
if /I "%~1"=="verify" exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\DevRun.ps1" -Mode "%~1"
exit /b %errorlevel%
