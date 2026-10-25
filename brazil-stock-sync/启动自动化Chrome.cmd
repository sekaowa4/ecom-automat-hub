@echo off
setlocal
set ROOT=%~dp0
set PYTHON=C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe
set NODE=C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe

if not exist "%PYTHON%" goto missing_runtime
if not exist "%NODE%" goto missing_runtime
pushd "%ROOT%"

netstat -ano | findstr /r /c:":8765 .*LISTENING" >nul
if not errorlevel 1 goto open_pages
powershell -NoProfile -Command "Start-Process -FilePath 'C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' -ArgumentList 'server.py' -WorkingDirectory '.' -WindowStyle Hidden"
timeout /t 3 /nobreak >nul

:open_pages
"%NODE%" "automation\launch_portal_chrome.js"
if errorlevel 1 goto launch_failed
popd
endlocal
exit /b 0

:missing_runtime
echo The local runtime was not found.
pause
endlocal
exit /b 1

:launch_failed
echo Chrome launch failed. Keep this window open and send a screenshot.
pause
popd
endlocal
exit /b 1
