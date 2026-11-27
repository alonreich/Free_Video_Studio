@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sanitize\Sanitize.ps1" %*
set "FVS_RESULT=%ERRORLEVEL%"
echo %CMDCMDLINE% | find /i "%~nx0" >nul && pause
exit /b %FVS_RESULT%
