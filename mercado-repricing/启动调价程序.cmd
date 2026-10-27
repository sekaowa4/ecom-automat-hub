@echo off
if exist "%~dp0dist\MercadoRepricing-0.5.38\Repricing.Api.exe" (
  start "" "%~dp0dist\MercadoRepricing-0.5.38\Repricing.Api.exe"
) else (
  echo Please build the distribution package first. See README.md.
  pause
)
