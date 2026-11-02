@echo off
setlocal
cd /d "%~dp0"
if not exist "backend\.venv\Scripts\python.exe" goto missing
if not exist "node_modules\electron\dist\electron.exe" goto missing
set "RUNNER_USER_DATA_DIR=%TEMP%\SeqRunnerV2-preview-%RANDOM%-%RANDOM%"
if exist "%RUNNER_USER_DATA_DIR%" goto collision
mkdir "%RUNNER_USER_DATA_DIR%" || goto failed
set "APPDATA=%RUNNER_USER_DATA_DIR%\appdata"
set "LOCALAPPDATA=%RUNNER_USER_DATA_DIR%\localappdata"
set "NO_PROXY=127.0.0.1,localhost"
set "no_proxy=127.0.0.1,localhost"
echo Isolated preview data: %RUNNER_USER_DATA_DIR%
set "RUNNER_PREVIEW_PORT="
for /f "delims=" %%P in ('powershell.exe -NoProfile -Command "$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $listener.Start(); $listener.LocalEndpoint.Port; $listener.Stop()"') do set "RUNNER_PREVIEW_PORT=%%P"
if not defined RUNNER_PREVIEW_PORT goto failed
call npm.cmd run dev:preview
if errorlevel 1 goto failed
exit /b 0
:missing
echo Missing Python environment or Electron dependencies. See README.md.
goto failed
:collision
echo Preview data directory already exists. Please run again.
goto failed
:failed
echo Preview failed. Please check the message above.
pause
exit /b 1
