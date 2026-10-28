@echo off
rem Starts the app (with the local development server) in screenshot mode: the app saves PNGs of each screen into
rem tools\windows\shots. Click through: sign in as guest, open "2 Players", Offline, play a few turns, finish a game.
setlocal
cd /d "%~dp0\..\.."
if exist "%~dp0shots" rmdir /s /q "%~dp0shots"
mkdir "%~dp0shots"
set "LUDOCROWN_SHOTS=%~dp0shots"
call "%~dp0run-dev.cmd"
endlocal
