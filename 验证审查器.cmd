@echo off
setlocal
cd /d "%~dp0"
set "RUNNER_V3_DATA_DIR=%~dp0.build\preview-%RANDOM%-%RANDOM%"
if exist "%RUNNER_V3_DATA_DIR%" (
  echo Preview data directory already exists. Run the launcher again.
  pause
  exit /b 1
)
set "DOTNET_CLI_HOME=%RUNNER_V3_DATA_DIR%\dotnet-home"
set "NUGET_PACKAGES=%RUNNER_V3_DATA_DIR%\packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
dotnet run --project "src\Runner\Runner.csproj" -p:RestoreSources="%~dp0."
if errorlevel 1 (
  echo Preview failed. Check the .NET 10 SDK and project build output.
  pause
  exit /b 1
)
