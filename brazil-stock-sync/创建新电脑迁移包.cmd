@echo off
setlocal
set "ROOT=%~dp0"
set "DEST=%USERPROFILE%\Desktop\巴西库存更新自动化平台-新电脑迁移包"
if exist "%DEST%" (
  echo Target already exists: %DEST%
  echo Move or rename it before creating a new package.
  pause
  exit /b 1
)
robocopy "%ROOT%" "%DEST%" /E /XD runtime .git __pycache__ tests /XF *.pyc >nul
if errorlevel 8 goto failed
mkdir "%DEST%\runtime"
copy /Y "%ROOT%runtime\stock_order_template_fixed_layout.xlsx" "%DEST%\runtime\stock_order_template_fixed_layout.xlsx" >nul
if errorlevel 1 goto failed
echo Package created: %DEST%
echo No Cookie, MCP authorization, Webhook, download files, logs, or historical Stock files were copied.
pause
exit /b 0
:failed
echo Package creation failed. The source project was not changed.
pause
exit /b 1
