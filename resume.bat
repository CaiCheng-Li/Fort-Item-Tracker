@echo off
rem Continues the most recent session, keeping its timer. Pass a session ID to pick another.
cd /d "%~dp0"
call run.bat --resume %*
