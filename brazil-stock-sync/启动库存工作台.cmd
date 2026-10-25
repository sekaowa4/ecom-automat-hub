@echo off
setlocal
set ROOT=%~dp0
set PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe
if not exist "%PYTHON%" set PYTHON=python

netstat -ano | findstr /r /c:":8765 .*LISTENING" >nul
if not errorlevel 1 goto open_workbench
powershell -NoProfile -Command "Start-Process -FilePath '%PYTHON%' -ArgumentList 'server.py' -WorkingDirectory '%ROOT%' -WindowStyle Hidden"
timeout /t 3 /nobreak >nul

:open_workbench
start "" "http://127.0.0.1:8765/"
endlocal
exit /b 0
