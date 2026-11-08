@echo off
rem TESTGATE_01: full deterministic suites, real media exports and native decoder soak.
rem Deletes only verified previous test outputs. Does not install or publish a release.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-av-tests.ps1" all
exit /b %ERRORLEVEL%
