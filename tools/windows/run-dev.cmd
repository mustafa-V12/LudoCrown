@echo off
rem Local development: builds the Windows app, starts the server (Development, in-memory store unless you set
rem ConnectionStrings__Postgres) in its own window, waits until it is healthy, then starts the app.
setlocal
set "PATH=%ProgramFiles%\dotnet;%PATH%"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /d "%~dp0\..\.."
set ROOT=%CD%
set LOG=%~dp0run-log.txt
echo Run started %date% %time% > "%LOG%"
rem Close a previous app instance so the build can replace its files.
taskkill /im LudoCrown.Mobile.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
dotnet build "src\LudoCrown.Mobile\LudoCrown.Mobile.csproj" -p:LudoWindowsOnly=true -f net10.0-windows10.0.19041.0 -nologo -v:minimal >> "%LOG%" 2>&1
echo EXITCODE windows-app %ERRORLEVEL% >> "%LOG%"
rem Restart the development server so it runs the code that was just built (in-memory data resets).
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":5080" ^| findstr "LISTENING"') do taskkill /pid %%p /f >nul 2>&1
timeout /t 2 /nobreak >nul
start "Ludo Crown server (Development)" /min cmd /c "cd /d %ROOT% && set ASPNETCORE_ENVIRONMENT=Development&& set ASPNETCORE_URLS=http://localhost:5080&& dotnet run --project src\LudoCrown.Server --no-launch-profile > %~dp0server-log.txt 2>&1"
for /l %%i in (1,1,60) do (
  curl.exe -s http://localhost:5080/health >nul 2>&1 && goto healthy
  timeout /t 2 /nobreak >nul
)
echo SERVER-NOT-HEALTHY >> "%LOG%"
goto app
:healthy
echo Server healthy >> "%LOG%"
curl.exe -s http://localhost:5080/info >> "%LOG%" 2>&1
echo. >> "%LOG%"
:app
set "LUDOCROWN_APP_LOG=%~dp0app-log.txt"
start "" "%ROOT%\src\LudoCrown.Mobile\bin\Debug\net10.0-windows10.0.19041.0\win-x64\LudoCrown.Mobile.exe"
echo App started >> "%LOG%"
echo RUN-SCRIPT-DONE %date% %time% >> "%LOG%"
endlocal
