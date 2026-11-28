@echo off
setlocal
cd /d "%~dp0"
if not "%~1"=="" echo [NOTE] %~n0.cmd takes no arguments. They were ignored.
set "PSModulePath=%ProgramFiles%\WindowsPowerShell\Modules;%SystemRoot%\system32\WindowsPowerShell\v1.0\Modules"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Bootstrap.ps1" -Command "%~n0"
exit /b %ERRORLEVEL%
