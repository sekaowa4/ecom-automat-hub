@echo off
setlocal
set "ROOT=%~dp0"
set "PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if not exist "%PYTHON%" set "PYTHON=python"
pushd "%ROOT%"
"%PYTHON%" -u daily_update.py
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" (
  echo Update completed and a success notification was sent.
) else (
  echo Update failed. No group notification was sent.
)
pause
popd
endlocal & exit /b %EXIT_CODE%
