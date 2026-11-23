@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-av-tests.ps1" %*
exit /b %ERRORLEVEL%
