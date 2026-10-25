@echo off
setlocal
set "ROOT=%~dp0"
set "PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if not exist "%PYTHON%" set "PYTHON=python"
pushd "%ROOT%"
echo Step 1/2: Configure formal Stock_Order and group Webhook.
"%PYTHON%" portable_setup.py
if errorlevel 1 goto failed
echo.
echo Step 2/2: Configure UpSeller Cookie.
"%PYTHON%" setup_upseller_api.py
if errorlevel 1 goto failed
echo.
echo Next: initialize and authorize the WeCom document MCP on this computer.
echo Then run "运行前检查.cmd". It must pass before a formal update.
pause
popd
endlocal & exit /b 0
:failed
echo Initialization stopped. No inventory update was run.
pause
popd
endlocal & exit /b 1
