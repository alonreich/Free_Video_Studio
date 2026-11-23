@echo off
certutil -user -delstore Root "FVS Local Development Root CA"
exit /b %ERRORLEVEL%
