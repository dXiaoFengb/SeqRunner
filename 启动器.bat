@echo off
setlocal
set "ROOT=%~dp0"
set "PYTHON=%ROOT%backend\.venv\Scripts\python.exe"
if not exist "%PYTHON%" (
  echo [Missing Python virtual environment]
  echo Run: py -3.13 -m venv backend\.venv
  echo Then: backend\.venv\Scripts\python.exe -m pip install -r backend\requirements.txt
  exit /b 1
)
if not exist "%ROOT%node_modules\electron\dist\electron.exe" (
  echo [Missing Electron runtime or frontend dependencies]
  echo Run from this project root: npm install
  exit /b 1
)
pushd "%ROOT%"
npm run dev
popd
