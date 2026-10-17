@echo off
rem Lightweight entrypoint: the whole pipeline (staging, payload.zip, NativeAOT publish,
rem signing, single-file validation, optional GitHub release) lives in build\FvsBuild.
rem Usage: Build.cmd [--beta] [--yes] [--no-publish]   Exit codes: 0=OK  1=build failed/refused  2=release not updated
rem RELGATE_01: an official release first proves the checkout is clean, on the default branch and
rem pushed, that docs\CHANGELOG.md has lint-clean "## Upcoming" notes, and asks you to type PUBLISH.
rem AI agents must NEVER run this file (docs\README.md). Use dev_build.cmd.
dotnet run --project "%~dp0build\FvsBuild\FvsBuild.csproj" -c Release -- %*
exit /b %ERRORLEVEL%
