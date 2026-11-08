@echo off
rem AVREG_01 - playback/sync/export regression gate. See docs\README.md section 0.3a.
rem   tests\run-av-tests.cmd          full gate (required before declaring any change done)
rem   tests\run-av-tests.cmd quick    quick A/V subset while iterating (not the gate)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-av-tests.ps1" %*
exit /b %ERRORLEVEL%
