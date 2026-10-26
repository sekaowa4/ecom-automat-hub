$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskExe=Join-Path $taskRoot 'dist\MercadoRepricing-0.5.29\Repricing.Api.exe'
if(!(Test-Path -LiteralPath $taskExe)){throw '请先运行 scripts/publish.ps1 生成程序包。'}
Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path $taskExe -Parent) -WindowStyle Hidden
