@echo off
chcp 65001 >nul
cd /d "%~dp0"
python -c "import PIL, pypdf" >nul 2>nul
if errorlevel 1 (
  python -m pip install -r requirements.txt
  if errorlevel 1 goto failure
)
if not exist "node_modules\react\package.json" (
  echo 正在安装网页工作台所需组件...
  call npm.cmd install --no-audit --no-fund
  if errorlevel 1 goto failure
)
call npm.cmd run build
if errorlevel 1 goto failure
python -m webapp.server
if errorlevel 1 goto failure
exit /b 0
:failure
echo 启动未完成，请查看上方提示。
pause
