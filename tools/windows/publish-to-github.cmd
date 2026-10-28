@echo off
rem One-time setup: turns this folder into a git repository with the full project history and pushes it to
rem https://github.com/mustafa-V12/LudoCrown. GitHub sign-in happens in your browser (Git Credential Manager);
rem no password is stored in this script. Log: tools\windows\publish-log.txt
setlocal
cd /d "%~dp0\..\.."
set "LOG=%~dp0publish-log.txt"
set "REMOTE=https://github.com/mustafa-V12/LudoCrown.git"
echo Publish started %date% %time% > "%LOG%"

if exist "%ProgramFiles%\Git\cmd\git.exe" set "PATH=%ProgramFiles%\Git\cmd;%PATH%"
where git >nul 2>&1
if errorlevel 1 (
  echo Git was not found. Installing Git for Windows with winget...
  echo INSTALLING-GIT >> "%LOG%"
  winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements >> "%LOG%" 2>&1
  set "PATH=%ProgramFiles%\Git\cmd;%PATH%"
)
where git >nul 2>&1
if errorlevel 1 (
  echo GIT-MISSING >> "%LOG%"
  echo Git could not be installed. See %LOG%
  goto end
)
git --version >> "%LOG%" 2>&1

if not exist .git (
  git init -b main >> "%LOG%" 2>&1
)
if exist LudoCrown.bundle (
  git fetch LudoCrown.bundle main:refs/remotes/bundle/main >> "%LOG%" 2>&1
  if errorlevel 1 ( echo BUNDLE-FETCH-FAILED >> "%LOG%" & goto end )
  rem Puts the project history in place. Files tracked by git are replaced by their committed versions
  rem (same content); ignored files such as bin\ and obj\ are left alone.
  git checkout -f -B main refs/remotes/bundle/main >> "%LOG%" 2>&1
  if errorlevel 1 ( echo CHECKOUT-FAILED >> "%LOG%" & goto end )
  git update-ref -d refs/remotes/bundle/main
)

git remote remove origin >nul 2>&1
git remote add origin %REMOTE%
echo.
echo Pushing to %REMOTE% ...
echo If a GitHub sign-in window opens, sign in as mustafa-V12.
git push -u origin main >> "%LOG%" 2>&1
if errorlevel 1 (
  echo PUSH-FAILED >> "%LOG%"
  echo Push failed. See %LOG%
  goto end
)
echo PUSH-OK >> "%LOG%"
if exist LudoCrown.bundle del LudoCrown.bundle
echo Done: https://github.com/mustafa-V12/LudoCrown

:end
git status --short >> "%LOG%" 2>&1
git log --oneline -3 >> "%LOG%" 2>&1
echo PUBLISH-SCRIPT-DONE %date% %time% >> "%LOG%"
echo.
pause
endlocal
