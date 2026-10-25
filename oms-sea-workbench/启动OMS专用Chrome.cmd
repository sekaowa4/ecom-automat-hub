@echo off
setlocal
pushd "%~dp0"
python oms_chrome.py
set "RESULT=%ERRORLEVEL%"
popd
if not "%RESULT%"=="0" pause
exit /b %RESULT%
