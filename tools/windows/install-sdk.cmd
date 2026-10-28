@echo off
rem Installs the .NET 10 SDK (winget, from Microsoft) and the .NET MAUI Windows workload. Needs administrator rights:
rem it asks Windows to restart itself elevated (you approve the UAC prompt). Log: install-log.txt next to this file.
setlocal
set LOG=%~dp0install-log.txt
if /i not "%~1"=="elevated" (
  echo Requesting administrator rights... > "%LOG%"
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList 'elevated' -Verb RunAs"
  exit /b
)
echo Ludo Crown SDK install started %date% %time% > "%LOG%"
echo ===== winget install .NET 10 SDK ===== >> "%LOG%"
winget install --id Microsoft.DotNet.SDK.10 -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity >> "%LOG%" 2>&1
echo EXITCODE winget %ERRORLEVEL% >> "%LOG%"
set DOTNET="%ProgramFiles%\dotnet\dotnet.exe"
echo ===== dotnet --list-sdks ===== >> "%LOG%"
%DOTNET% --list-sdks >> "%LOG%" 2>&1
echo ===== dotnet workload install maui-windows ===== >> "%LOG%"
%DOTNET% workload install maui-windows >> "%LOG%" 2>&1
echo EXITCODE workload %ERRORLEVEL% >> "%LOG%"
%DOTNET% workload list >> "%LOG%" 2>&1
echo INSTALL-SCRIPT-DONE %date% %time% >> "%LOG%"
endlocal
