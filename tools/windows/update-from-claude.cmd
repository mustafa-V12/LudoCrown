@echo off
rem Applies an update bundle from Claude (LudoCrown.bundle in the project folder), builds the Windows app and starts it
rem with the local development server. Does not upload anything. Stops if you have unsaved changes or commits that
rem are not in the update. Log: tools\windows\update-log.txt (build details: app-build-log.txt)
rem The update replaces files in this folder (possibly this script), so it runs from a temporary copy.
if not "%~1"=="--from-temp" (
  copy /y "%~f0" "%TEMP%\ludocrown-update.cmd" >nul
  call "%TEMP%\ludocrown-update.cmd" --from-temp "%~dp0"
  exit /b
)
setlocal
set "TOOLS=%~2"
cd /d "%TOOLS%\..\.."
set "LOG=%TOOLS%update-log.txt"
echo Update started %date% %time% > "%LOG%"
if exist "%ProgramFiles%\Git\cmd\git.exe" set "PATH=%ProgramFiles%\Git\cmd;%PATH%"
if not exist LudoCrown.bundle ( echo NO-BUNDLE >> "%LOG%" & echo No LudoCrown.bundle found. & goto end )

rem Changes to the helper scripts in tools\windows are ignored here (Claude may deliver a newer copy of this script).
for /f %%c in ('git status --porcelain --untracked-files^=no -- . ":(exclude)tools/windows" ^| find /c /v ""') do set CHANGES=%%c
if not "%CHANGES%"=="0" (
  echo LOCAL-CHANGES >> "%LOG%"
  git status --short >> "%LOG%"
  echo You have changed project files. Nothing was updated. Tell Claude, or commit your changes first.
  goto end
)
git fetch LudoCrown.bundle +main:refs/remotes/bundle/main >> "%LOG%" 2>&1
if errorlevel 1 ( echo BUNDLE-FETCH-FAILED >> "%LOG%" & goto end )
git merge-base --is-ancestor HEAD refs/remotes/bundle/main
if errorlevel 1 (
  echo LOCAL-COMMITS >> "%LOG%"
  echo This folder has commits that are not in the update. Nothing was updated. Tell Claude.
  goto end
)
git checkout -f -B main refs/remotes/bundle/main >> "%LOG%" 2>&1
if errorlevel 1 ( echo CHECKOUT-FAILED >> "%LOG%" & goto end )
echo UPDATED >> "%LOG%"
git log --format="%%h %%s" -1 >> "%LOG%"

echo Building the Windows app...
call "%TOOLS%build-windows-app.cmd"
findstr /c:"EXITCODE windows-app 0" "%TOOLS%app-build-log.txt" >nul
if errorlevel 1 ( echo BUILD-FAILED >> "%LOG%" & echo Build failed. See tools\windows\app-build-log.txt & goto end )
echo BUILD-OK >> "%LOG%"
rem Start in screenshot mode: the app saves a PNG of each screen to tools\windows\shots (development only).
call "%TOOLS%screenshots.cmd"

:end
echo UPDATE-SCRIPT-DONE %date% %time% >> "%LOG%"
endlocal
