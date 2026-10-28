@echo off
rem Builds only the Windows app (no iOS/Android workloads needed). Log: app-build-log.txt next to this file.
setlocal
set "PATH=%ProgramFiles%\dotnet;%PATH%"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /d "%~dp0\..\.."
set LOG=%~dp0app-build-log.txt
echo Windows app build started %date% %time% > "%LOG%"
dotnet build "src\LudoCrown.Mobile\LudoCrown.Mobile.csproj" -p:LudoWindowsOnly=true -f net10.0-windows10.0.19041.0 -nologo -v:minimal >> "%LOG%" 2>&1
echo EXITCODE windows-app %ERRORLEVEL% >> "%LOG%"
echo APP-BUILD-DONE %date% %time% >> "%LOG%"
endlocal
