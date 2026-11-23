@echo off
dotnet run --project "%~dp0build\FvsBuild\FvsBuild.csproj" -c Release -- %*
exit /b %ERRORLEVEL%
