@echo off
certutil -user -addstore Root "%~dp0fvs-dev-root-ca.cer"
exit /b %ERRORLEVEL%
