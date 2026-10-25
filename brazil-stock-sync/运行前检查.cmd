@echo off
setlocal
title Brazil Stock Preflight
set "ROOT=%~dp0"
set "PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if not exist "%PYTHON%" set "PYTHON=python"
pushd "%ROOT%"
echo Running read-only checks. Keep this window open.
echo.
"%PYTHON%" -u preflight_check.py
set "EXIT_CODE=%ERRORLEVEL%"
:finish
echo.
if "%EXIT_CODE%"=="0" (
  echo Checks passed. You can run today's inventory update.
) else (
  echo Checks failed. Resolve the failed items first.
)
echo.
pause
popd
endlocal & exit /b %EXIT_CODE%
