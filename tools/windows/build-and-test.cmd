@echo off
rem Ludo Crown: restore, run all tests and build the Windows app. Writes everything to build-log.txt next to this file.
rem Safe to run again. Installs nothing; if the .NET SDK or the MAUI workload is missing, the log says so.
setlocal EnableDelayedExpansion
set "PATH=%ProgramFiles%\dotnet;%PATH%"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /d "%~dp0\..\.."
taskkill /im LudoCrown.Mobile.exe /f >nul 2>&1
set LOG=%~dp0build-log.txt
echo Ludo Crown build started %date% %time% > "%LOG%"
echo. >> "%LOG%"

echo ===== dotnet --version ===== >> "%LOG%"
dotnet --version >> "%LOG%" 2>&1 || (echo NO-DOTNET-SDK >> "%LOG%" & goto done)
echo ===== dotnet --list-sdks ===== >> "%LOG%"
dotnet --list-sdks >> "%LOG%" 2>&1
echo ===== dotnet workload list ===== >> "%LOG%"
dotnet workload list >> "%LOG%" 2>&1

for %%P in (LudoCrown.Core.Tests LudoCrown.Server.Tests LudoCrown.App.Tests LudoCrown.Postgres.Tests) do (
  echo. >> "%LOG%"
  echo ===== TEST %%P ===== >> "%LOG%"
  dotnet test "tests\%%P\%%P.csproj" -nologo -v:minimal >> "%LOG%" 2>&1
  echo EXITCODE %%P !ERRORLEVEL! >> "%LOG%"
)

echo. >> "%LOG%"
echo ===== BUILD server ===== >> "%LOG%"
dotnet build "src\LudoCrown.Server\LudoCrown.Server.csproj" -nologo -v:minimal >> "%LOG%" 2>&1
echo EXITCODE server %ERRORLEVEL% >> "%LOG%"

echo. >> "%LOG%"
echo ===== BUILD Windows app ===== >> "%LOG%"
dotnet build "src\LudoCrown.Mobile\LudoCrown.Mobile.csproj" -p:LudoWindowsOnly=true -f net10.0-windows10.0.19041.0 -nologo -v:minimal >> "%LOG%" 2>&1
echo EXITCODE windows-app %ERRORLEVEL% >> "%LOG%"

:done
echo. >> "%LOG%"
echo BUILD-SCRIPT-DONE %date% %time% >> "%LOG%"
endlocal
