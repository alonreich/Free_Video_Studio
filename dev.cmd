@echo off
setlocal
cd /d "%~dp0"
REM SYS-VERIFYTOOL / DEVVERIFY_01 / VERIFYHALT_01: verification precedes cleanup.
dotnet run --project "build\FvsVerify\FvsVerify.csproj" -v q -- "%CD%"
if errorlevel 1 exit /b 1
if /I "%~1"=="verify" exit /b 0
REM DEVDATA_01 / BUILDKILL_01: preserve settings; never kill a process by name.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\DevRun.ps1" -Mode "%~1"
exit /b %errorlevel%
