@echo off
dotnet run --project "%~dp0build\FvsBuild\FvsBuild.csproj" -c Release -- --dev %*
exit /b %ERRORLEVEL%
